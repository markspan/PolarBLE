using System.Reflection;
using Windows.Devices.Bluetooth;
using PolarBLE.App.Bluetooth;
using PolarBLE.App.Presentation;

namespace PolarBLE.App;

/// <summary>
/// The application window: find a strap, connect to it, and show that data is really flowing.
/// </summary>
/// <remarks>
/// <para>
/// The window is driven by one explicit <see cref="ConnectionState"/>. Every control's enabled
/// state and caption is derived from it in <see cref="UpdateControls"/>, so no code path can
/// leave, say, the connect button enabled halfway through a disconnect.
/// </para>
/// <para>
/// Closing the window while a strap is connected, connecting or disconnecting defers the close
/// until the strap has been sent its stop commands, otherwise it keeps streaming and draining
/// its battery after the application has gone.
/// </para>
/// </remarks>
internal sealed partial class MainForm : Form
{
    private static readonly Color HealthyTelemetryColor = SystemColors.GrayText;
    private static readonly Color ProblemTelemetryColor = Color.Firebrick;

    private readonly PolarScanner _scanner = new();

    private ConnectionState _state = ConnectionState.Idle;
    private PolarH10Session? _session;
    private CancellationTokenSource? _connectCancellation;
    private bool _closeRequested;

    public MainForm()
    {
        InitializeComponent();
        LoadWindowIcon();

        _scanner.DeviceDiscovered += OnDeviceDiscovered;
        _scanner.ScanAborted += OnScanAborted;
        UpdateControls();
    }

    private enum ConnectionState
    {
        Idle,
        Connecting,
        Connected,
        Disconnecting,
    }

    /// <summary>
    /// Finds a monospace font so telemetry columns line up, preferring the faces most likely to
    /// already be installed on Windows and falling back to whatever the OS considers monospace.
    /// </summary>
    private static Font CreateMonospaceFont(float sizeInPoints)
    {
        string[] preferred = ["Consolas", "Cascadia Mono", "Courier New"];
        var installed = FontFamily.Families.Select(family => family.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = preferred.FirstOrDefault(installed.Contains);
        return name is null
            ? new Font(FontFamily.GenericMonospace, sizeInPoints)
            : new Font(name, sizeInPoints);
    }

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

    private void SetState(ConnectionState state)
    {
        _state = state;
        UpdateControls();
    }

    /// <summary>Derives every control's enabled state and caption from the current state.</summary>
    private void UpdateControls()
    {
        var hasSelection = listBoxDevices.SelectedItem is DiscoveredDevice;

        btnScan.Enabled = _state is ConnectionState.Idle or ConnectionState.Connected;
        btnScan.Text = _scanner.IsScanning ? "Stop &scanning" : "&Scan";

        btnConnect.Enabled = _state switch
        {
            ConnectionState.Idle => hasSelection,
            ConnectionState.Connected => true,
            _ => false,
        };
        btnConnect.Text = _state is ConnectionState.Connected or ConnectionState.Disconnecting
            ? "&Disconnect"
            : "&Connect";
    }

    private void BtnScan_Click(object? sender, EventArgs e)
    {
        if (_scanner.IsScanning)
        {
            _scanner.Stop();
            SetStatus("Scan stopped.");
        }
        else
        {
            StartScan();
        }

        UpdateControls();
    }

    private void StartScan()
    {
        listBoxDevices.Items.Clear();

        try
        {
            _scanner.Start();
            SetStatus("Scanning for Polar straps…");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not start scanning: {ex.Message}", isProblem: true);
        }
    }

    private void OnDeviceDiscovered(object? sender, DiscoveredDevice device) =>
        RunOnUi(() => listBoxDevices.Items.Add(device));

    private void OnScanAborted(object? sender, BluetoothError error) => RunOnUi(() =>
    {
        SetStatus($"Scanning stopped by Windows ({error}). Check that Bluetooth is on.", isProblem: true);
        UpdateControls();
    });

    private void ListBoxDevices_SelectedIndexChanged(object? sender, EventArgs e) => UpdateControls();

    private async void ListBoxDevices_DoubleClick(object? sender, EventArgs e)
    {
        if (_state == ConnectionState.Idle && listBoxDevices.SelectedItem is DiscoveredDevice device)
        {
            await ConnectAsync(device);
        }
    }

    /// <summary>
    /// Connecting is always an explicit action, never a side effect of the selection changing,
    /// so arrow-keying through the list cannot fire connection attempts.
    /// </summary>
    private async void BtnConnect_Click(object? sender, EventArgs e)
    {
        switch (_state)
        {
            case ConnectionState.Idle when listBoxDevices.SelectedItem is DiscoveredDevice device:
                await ConnectAsync(device);
                break;
            case ConnectionState.Connected:
                await DisconnectAsync("Disconnected.");
                break;
            default:
                break;
        }
    }

    private async Task ConnectAsync(DiscoveredDevice device)
    {
        SetState(ConnectionState.Connecting);
        _scanner.Stop();
        SetStatus($"Connecting to {device.Name}… this can take up to a minute.");

        _connectCancellation = new CancellationTokenSource();
        try
        {
            var session = await PolarH10Session.ConnectAsync(device, _connectCancellation.Token);
            AttachSession(session);
            SetState(ConnectionState.Connected);
            SetStatus($"Streaming from {session.DeviceName}.");

            // A disconnect that happened before the handlers were attached raised no event.
            if (!session.IsConnected)
            {
                await DisconnectAsync("The strap disconnected while connecting. Scan and try again.", isProblem: true);
            }
        }
        catch (OperationCanceledException)
        {
            SetState(ConnectionState.Idle);
            SetStatus("Connection cancelled.");
        }
        catch (PolarConnectionException ex)
        {
            SetState(ConnectionState.Idle);
            SetStatus($"Could not connect: {ex.Message}", isProblem: true);
        }
        catch (Exception ex)
        {
            SetState(ConnectionState.Idle);
            SetStatus($"Unexpected error while connecting: {ex.Message}", isProblem: true);
        }
        finally
        {
            _connectCancellation.Dispose();
            _connectCancellation = null;
        }

        await CompletePendingCloseAsync();
    }

    /// <summary>
    /// Stops the strap and releases the session. Reached from the button, from a lost
    /// connection and from the window closing; only the first caller does the work.
    /// </summary>
    private async Task DisconnectAsync(string finalStatus, bool isProblem = false)
    {
        var session = _session;
        if (_state != ConnectionState.Connected || session is null)
        {
            return;
        }

        SetState(ConnectionState.Disconnecting);
        SetStatus("Disconnecting…");
        DetachSession(session);

        await session.DisposeAsync();

        SetState(ConnectionState.Idle);
        SetStatus(finalStatus, isProblem);
        await CompletePendingCloseAsync();
    }

    private void AttachSession(PolarH10Session session)
    {
        _session = session;
        session.ConnectionLost += OnConnectionLost;
        session.BatteryLevelChanged += OnBatteryLevelChanged;
        session.StreamError += OnStreamError;

        // The first battery reading arrived during ConnectAsync, before the handler existed.
        batteryBar.Value = session.BatteryPercent ?? -1;
        uiTimer.Start();
        RefreshTelemetry();
    }

    private void DetachSession(PolarH10Session session)
    {
        uiTimer.Stop();
        session.ConnectionLost -= OnConnectionLost;
        session.BatteryLevelChanged -= OnBatteryLevelChanged;
        session.StreamError -= OnStreamError;
        _session = null;

        batteryBar.Value = -1;
        lblThroughput.Text = string.Empty;
    }

    private void OnConnectionLost(object? sender, EventArgs e) => RunOnUi(async () =>
        await DisconnectAsync("The strap disconnected. Scan and reconnect to resume recording.", isProblem: true));

    private void OnBatteryLevelChanged(object? sender, int percent) =>
        RunOnUi(() => batteryBar.Value = percent);

    private void OnStreamError(object? sender, Exception error) =>
        RunOnUi(() => SetStatus($"Error while decoding a frame: {error.Message}", isProblem: true));

    /// <summary>
    /// The telemetry refreshes on a timer rather than from the Bluetooth callbacks, so the UI
    /// thread is never marshalled to once per notification.
    /// </summary>
    private void UiTimer_Tick(object? sender, EventArgs e) => RefreshTelemetry();

    private void RefreshTelemetry()
    {
        if (_session is not { } session)
        {
            return;
        }

        lblThroughput.Text = TelemetryText.Format(session.Streams, out var hasProblems);
        lblThroughput.ForeColor = hasProblems ? ProblemTelemetryColor : HealthyTelemetryColor;
    }

    private async void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_state == ConnectionState.Idle)
        {
            _scanner.Dispose();
            return;
        }

        // Defer: the close is completed by CompletePendingCloseAsync once the strap is idle.
        e.Cancel = true;
        _closeRequested = true;
        _connectCancellation?.Cancel();

        if (_state == ConnectionState.Connected)
        {
            await DisconnectAsync("Disconnected.");
        }
    }

    /// <summary>Finishes a close that was deferred while the strap was still busy.</summary>
    private async Task CompletePendingCloseAsync()
    {
        if (!_closeRequested)
        {
            return;
        }

        switch (_state)
        {
            case ConnectionState.Connected:
                // The connection completed just before cancellation could stop it.
                await DisconnectAsync("Disconnected.");
                break;
            case ConnectionState.Idle:
                // Cleared first so that a second completion cannot queue a second close.
                _closeRequested = false;
                RunOnUi(Close);
                break;
            default:
                break;
        }
    }

    private void SetStatus(string message, bool isProblem = false)
    {
        lblStatus.Text = message;
        lblStatus.ForeColor = isProblem ? ProblemTelemetryColor : SystemColors.ControlText;
    }

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

    /// <summary>
    /// Marshals an asynchronous handler to the UI thread. It runs as an async void delegate on
    /// purpose: a failure then reaches <see cref="Application.ThreadException"/> instead of
    /// vanishing with a discarded task.
    /// </summary>
    private void RunOnUi(Func<Task> action)
    {
        // Typed explicitly: an async lambda would otherwise bind back to this overload.
        Action handler = async () => await action();
        RunOnUi(handler);
    }
}
