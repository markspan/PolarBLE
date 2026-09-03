using System.Diagnostics;
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
    private readonly Stopwatch _sinceLastTick = Stopwatch.StartNew();

    private PolarH10Session? _session;
    private CancellationTokenSource? _connectCancellation;
    private bool _connecting;

    public MainForm()
    {
        InitializeComponent();
        LoadWindowIcon();

        _scanner.DeviceDiscovered += OnDeviceDiscovered;
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
        btnConnect.Enabled = !_connecting && (IsConnected || listBoxDevices.SelectedItem is DiscoveredDevice);

    private async void ListBoxDevices_DoubleClick(object? sender, EventArgs e)
    {
        if (!IsConnected && !_connecting && listBoxDevices.SelectedItem is DiscoveredDevice)
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
        if (_connecting)
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

        _connecting = true;
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

            _sinceLastTick.Restart();
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
            _connecting = false;
            btnScan.Enabled = true;
            btnConnect.Enabled = IsConnected || listBoxDevices.SelectedItem is DiscoveredDevice;
        }
    }

    private async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        uiTimer.Stop();

        if (session is not null)
        {
            session.ConnectionLost -= OnConnectionLost;
            session.BatteryLevelChanged -= OnBatteryLevelChanged;
            session.StreamError -= OnStreamError;

            SetStatus("Stopping measurement…");
            await session.DisposeAsync();
        }

        batteryBar.Value = -1;
        lblThroughput.Text = string.Empty;
        btnConnect.Text = "&Connect";
        SetStatus("Disconnected.");
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
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var elapsed = _sinceLastTick.Elapsed.TotalSeconds;
        _sinceLastTick.Restart();

        var ecgRate = session.EcgStatistics.ConsumeRate(elapsed);
        var accRate = session.AccStatistics.ConsumeRate(elapsed);

        var text = string.Create(
            CultureInfo.CurrentCulture,
            $"ECG {ecgRate:F0} Hz ({session.EcgStatistics.Samples:N0} samples)   " +
            $"ACC {accRate:F0} Hz ({session.AccStatistics.Samples:N0} samples)");

        var problems = session.DroppedFrames + session.Discontinuities +
                       session.EcgStatistics.UnsupportedFrames + session.AccStatistics.UnsupportedFrames;
        if (problems > 0)
        {
            text += string.Create(
                CultureInfo.CurrentCulture,
                $"   ·  {session.DroppedFrames} dropped, {session.Discontinuities} gaps");
        }

        lblThroughput.Text = text;
        lblThroughput.ForeColor = problems > 0 ? Color.Firebrick : SystemColors.GrayText;
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
