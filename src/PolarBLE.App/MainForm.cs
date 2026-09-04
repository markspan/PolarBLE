using System.Globalization;
using System.Reflection;
using PolarBLE.App.Bluetooth;

namespace PolarBLE.App;

/// <summary>
/// The application window: find a strap, connect to it, and show that data is really flowing.
/// </summary>
internal sealed partial class MainForm : Form
{
    private readonly PolarScanner _scanner = new();

    private PolarH10Session? _session;
    private CancellationTokenSource? _connectCancellation;
    private bool _busy;

    public MainForm()
    {
        InitializeComponent();
        LoadWindowIcon();

        _scanner.DeviceDiscovered += OnDeviceDiscovered;
    }

    /// <summary>
    /// Finds a monospace font so telemetry columns line up, preferring the faces most likely to
    /// already be installed on Windows and falling back to whatever the OS considers monospace.
    /// </summary>
    private static Font CreateMonospaceFont(float sizeInPoints)
    {
        string[] preferred = ["Consolas", "Cascadia Mono", "Courier New"];
        foreach (var name in preferred)
        {
            foreach (var family in FontFamily.Families)
            {
                if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new Font(name, sizeInPoints);
                }
            }
        }

        return new Font(FontFamily.GenericMonospace, sizeInPoints);
    }

    private bool IsConnected => _session is not null;

    private void LoadWindowIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PolarBLE.App.icon.ico");
            if (stream is not null)
            {
                Icon = new Icon(stream);
            }
        }
        catch (Exception)
        {
            // A missing icon is not worth failing startup over.
        }
    }

    private void BtnScan_Click(object? sender, EventArgs e)
    {
        if (_scanner.IsScanning)
        {
            _scanner.Stop();
            btnScan.Text = "&Scan";
            SetStatus("Scan stopped.");
            return;
        }

        listBoxDevices.Items.Clear();
        btnConnect.Enabled = false;

        try
        {
            _scanner.Start();
            btnScan.Text = "Stop &scanning";
            SetStatus("Scanning for Polar straps…");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start scanning: {ex.Message}");
        }
    }

    private void OnDeviceDiscovered(object? sender, DiscoveredDevice device) =>
        RunOnUi(() => listBoxDevices.Items.Add(device));

    private void ListBoxDevices_SelectedIndexChanged(object? sender, EventArgs e) =>
        btnConnect.Enabled = !_busy && (IsConnected || listBoxDevices.SelectedItem is DiscoveredDevice);

    private async void ListBoxDevices_DoubleClick(object? sender, EventArgs e)
    {
        if (!IsConnected && !_busy && listBoxDevices.SelectedItem is DiscoveredDevice)
        {
            await ToggleConnectionAsync();
        }
    }

    private async void BtnConnect_Click(object? sender, EventArgs e) => await ToggleConnectionAsync();

    /// <summary>
    /// Connecting is an explicit action. The original code connected as a side effect of the
    /// selection changing, so arrow-keying through the list fired connection attempts and a
    /// second selection silently created a duplicate set of outlets.
    /// </summary>
    private async Task ToggleConnectionAsync()
    {
        if (_busy)
        {
            return;
        }

        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        if (listBoxDevices.SelectedItem is not DiscoveredDevice device)
        {
            return;
        }

        _busy = true;
        btnConnect.Enabled = false;
        btnScan.Enabled = false;
        SetStatus($"Connecting to {device.Name}… this can take up to a minute.");

        _connectCancellation = new CancellationTokenSource();
        try
        {
            _scanner.Stop();
            btnScan.Text = "&Scan";

            var session = await PolarH10Session.ConnectAsync(device, _connectCancellation.Token);

            session.ConnectionLost += OnConnectionLost;
            session.BatteryLevelChanged += OnBatteryLevelChanged;
            session.StreamError += OnStreamError;
            _session = session;

            // The first battery reading happens during ConnectAsync, before this handler could
            // possibly have been subscribed, so it would otherwise be lost until the strap next
            // notifies a change (which, for a slowly-draining battery, can be a long wait).
            if (session.BatteryPercent is { } percent)
            {
                batteryBar.Value = percent;
            }

            uiTimer.Start();

            btnConnect.Text = "&Disconnect";
            SetStatus($"Streaming from {session.DeviceName}.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Connection cancelled.");
        }
        catch (PolarConnectionException ex)
        {
            SetStatus($"Could not connect: {ex.Message}");
        }
        catch (Exception ex)
        {
            SetStatus($"Unexpected error while connecting: {ex.Message}");
        }
        finally
        {
            _connectCancellation?.Dispose();
            _connectCancellation = null;
            _busy = false;
            btnScan.Enabled = true;
            btnConnect.Enabled = IsConnected || listBoxDevices.SelectedItem is DiscoveredDevice;
        }
    }

    /// <summary>
    /// Guards itself against re-entry rather than relying on each caller to remember to: it is
    /// reached from a button click, from a lost-connection notification and from the window
    /// closing, and the strap's own stop commands make this a genuinely awaited operation, not
    /// an instant one. Without the guard the button stayed enabled and labelled "Disconnect"
    /// for that whole stretch — because <see cref="_session"/> is cleared up front, a second
    /// click during it saw <see cref="IsConnected"/> already false and fell through to
    /// <see cref="ToggleConnectionAsync"/>'s connect branch instead of being ignored.
    /// </summary>
    private async Task DisconnectAsync()
    {
        if (_busy)
        {
            return;
        }

        var session = _session;
        if (session is null)
        {
            return;
        }

        _busy = true;
        btnConnect.Enabled = false;
        uiTimer.Stop();
        _session = null;

        try
        {
            session.ConnectionLost -= OnConnectionLost;
            session.BatteryLevelChanged -= OnBatteryLevelChanged;
            session.StreamError -= OnStreamError;

            SetStatus("Disconnecting…");
            await session.DisposeAsync();

            batteryBar.Value = -1;
            lblThroughput.Text = string.Empty;
            btnConnect.Text = "&Connect";
            SetStatus("Disconnected.");
        }
        finally
        {
            _busy = false;
            btnConnect.Enabled = IsConnected || listBoxDevices.SelectedItem is DiscoveredDevice;
        }
    }

    private void OnConnectionLost(object? sender, EventArgs e) => RunOnUi(async () =>
    {
        if (IsConnected)
        {
            await DisconnectAsync();
            SetStatus("The strap disconnected. Scan and reconnect to resume recording.");
        }
    });

    private void OnBatteryLevelChanged(object? sender, int percent) =>
        RunOnUi(() => batteryBar.Value = percent);

    private void OnStreamError(object? sender, Exception error) =>
        RunOnUi(() => SetStatus($"Error while decoding a frame: {error.Message}"));

    /// <summary>
    /// The user interface refreshes on a timer rather than from the BLE callbacks. The original
    /// code marshalled to the UI thread on every single notification just to animate an
    /// ellipsis and to set a colour that never changed again.
    /// </summary>
    /// <remarks>
    /// Both fields are padded to a fixed width, so the two rows never shift left or right as a
    /// sample count picks up another digit over the course of a recording — only the digits
    /// themselves change, never their position.
    /// </remarks>
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        lblThroughput.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"ECG {session.EcgStatistics.Samples,12:N0} samples\n" +
            $"ACC {session.AccStatistics.Samples,12:N0} samples");
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        _connectCancellation?.Cancel();
        _scanner.Dispose();

        if (IsConnected)
        {
            // Give the strap its stop commands before the window goes away, otherwise it keeps
            // streaming and draining its battery.
            e.Cancel = true;
            await DisconnectAsync();
            FormClosing -= MainForm_FormClosing;
            Close();
        }
    }

    private void SetStatus(string message) => lblStatus.Text = message;

    /// <summary>
    /// Marshals to the UI thread, tolerating the window being torn down underneath us.
    /// </summary>
    private void RunOnUi(Action action)
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // The handle was destroyed between the check and the call.
        }
    }
}
