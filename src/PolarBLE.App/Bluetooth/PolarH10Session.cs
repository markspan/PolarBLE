using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using PolarBLE.Core.Acquisition;
using PolarBLE.Core.Polar;
using PolarBLE.Core.Streaming;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// One connected Polar H10: owns the GATT connection, the measurement streams and their LSL
/// outlets, and tears all of them down deterministically.
/// </summary>
/// <remarks>
/// <para>
/// The session is built up step by step in <see cref="OpenAsync"/>, and every resource is
/// recorded in a field the moment it is acquired. <see cref="DisposeAsync"/> releases whatever
/// is present, so a failure at any step is rolled back through the same, single teardown path
/// that a normal disconnect uses.
/// </para>
/// <para>
/// Everything protocol-specific (what to request, how to decode, where samples go) lives in
/// PolarBLE.Core. This class only moves bytes between WinRT and that pipeline.
/// </para>
/// </remarks>
internal sealed class PolarH10Session : IAsyncDisposable
{
    private readonly BluetoothLEDevice _device;
    private readonly List<MeasurementStream> _streams = [];

    private IReadOnlyList<MeasurementConfiguration> _measurements = [];
    private GattDeviceService? _pmdService;
    private PmdControlPointChannel? _controlPoint;
    private GattCharacteristic? _data;
    private BatteryMonitor? _battery;
    private PmdDataRouter? _router;
    private bool _disposed;

    private PolarH10Session(BluetoothLEDevice device, string deviceName)
    {
        _device = device;
        DeviceName = deviceName;
    }

    /// <summary>Raised, on a background thread, when the strap disconnects unexpectedly.</summary>
    public event EventHandler? ConnectionLost;

    /// <summary>Raised, on a background thread, with each new battery percentage.</summary>
    public event EventHandler<int>? BatteryLevelChanged;

    /// <summary>Raised, on a background thread, when a notification could not be processed.</summary>
    public event EventHandler<Exception>? StreamError;

    /// <summary>The connected strap's name.</summary>
    public string DeviceName { get; }

    /// <summary>The measurement streams, in the order they were started.</summary>
    public IReadOnlyList<MeasurementStream> Streams => _streams;

    /// <summary>True while the radio link to the strap is up.</summary>
    public bool IsConnected => _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>The most recent battery percentage, or null if none has arrived yet.</summary>
    /// <remarks>
    /// The first reading arrives while <see cref="ConnectAsync"/> is still running, before a
    /// caller can have subscribed to <see cref="BatteryLevelChanged"/>; read this once after
    /// subscribing to catch up.
    /// </remarks>
    public int? BatteryPercent => _battery?.Percent;

    /// <summary>
    /// Connects to a strap and starts every measurement.
    /// </summary>
    /// <param name="device">The strap to connect to.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The live session.</returns>
    /// <exception cref="PolarConnectionException">The strap could not be set up for streaming.</exception>
    /// <exception cref="OperationCanceledException">The attempt was cancelled.</exception>
    public static async Task<PolarH10Session> ConnectAsync(
        DiscoveredDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(device.Address)
            .AsTask(cancellationToken) ??
            throw new PolarConnectionException(
                "Windows could not open the device. Check that Bluetooth is on and the strap is worn.");

        var name = string.IsNullOrWhiteSpace(bleDevice.Name) ? device.Name : bleDevice.Name;
        var session = new PolarH10Session(bleDevice, name);
        try
        {
            await session.OpenAsync(device.AddressText, cancellationToken);
            return session;
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            if (ex is PolarConnectionException or OperationCanceledException)
            {
                throw;
            }

            throw new PolarConnectionException("Could not start streaming from the strap.", ex);
        }
    }

    /// <summary>
    /// Stops every measurement, unsubscribes, closes the outlets and releases the connection.
    /// Never throws, and returns promptly even when the strap is already out of range.
    /// </summary>
    /// <remarks>
    /// Stopping the measurements matters: without it the strap keeps streaming, and draining its
    /// battery, after the application has gone. The GATT services are disposed last, after
    /// everything obtained from them has finished being used.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        if (_data is not null)
        {
            _data.ValueChanged -= OnDataNotification;
        }

        // Radio traffic to a strap that is gone can only time out, so skip it.
        var connected = IsConnected;
        if (connected && _controlPoint is not null)
        {
            foreach (var measurement in _measurements)
            {
                await _controlPoint.TrySendAsync(measurement.BuildStopCommand());
            }
        }

        if (connected && _data is not null)
        {
            await _data.TryUnsubscribeAsync();
        }

        if (_controlPoint is not null)
        {
            await _controlPoint.CloseAsync(connected);
        }

        if (_battery is not null)
        {
            _battery.LevelChanged -= OnBatteryLevelChanged;
            await _battery.CloseAsync(connected);
        }

        // Closing the outlets removes the streams from the network; a recorder that is still
        // bound to them sees a clean end of stream rather than a silent one.
        foreach (var stream in _streams)
        {
            stream.Dispose();
        }

        _pmdService?.Dispose();
        _device.Dispose();
    }

    /// <summary>Discovers the services, creates the streams and starts every measurement.</summary>
    private async Task OpenAsync(string deviceAddress, CancellationToken cancellationToken)
    {
        _pmdService = await _device.GetFirstServiceAsync(PolarUuids.PmdService, cancellationToken) ??
            throw new PolarConnectionException(
                "The Polar measurement service was not available. " +
                "Make sure the strap is worn and not connected to another application.");

        var controlPoint =
            await GetRequiredCharacteristicAsync(_pmdService, PolarUuids.PmdControlPoint, cancellationToken);
        _data = await GetRequiredCharacteristicAsync(_pmdService, PolarUuids.PmdData, cancellationToken);

        _battery = await BatteryMonitor.TryOpenAsync(_device, cancellationToken);
        if (_battery is not null)
        {
            _battery.LevelChanged += OnBatteryLevelChanged;
        }

        // The outlets exist before notifications are enabled, so no sample can arrive ahead of
        // the stream that should carry it.
        _measurements = PolarH10Configuration.CreateMeasurements(DeviceName, deviceAddress);
        foreach (var measurement in _measurements)
        {
            AddStream(measurement);
        }

        _router = new PmdDataRouter(_streams);
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Responses arrive as indications; without them a refused configuration would be
        // indistinguishable from a stream that has simply not started yet.
        _controlPoint = new PmdControlPointChannel(controlPoint);
        await _controlPoint.OpenAsync(cancellationToken);

        _data.ValueChanged += OnDataNotification;
        await _data.SubscribeAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify,
            "PMD data",
            cancellationToken);

        foreach (var measurement in _measurements)
        {
            await StartMeasurementAsync(_controlPoint, measurement, cancellationToken);
        }

        if (_battery is not null)
        {
            await _battery.StartAsync(cancellationToken);
        }
    }

    private void AddStream(MeasurementConfiguration measurement)
    {
        var sink = new LslSampleSink(measurement.Stream);
        try
        {
            _streams.Add(measurement.CreateStream(sink));
        }
        catch
        {
            sink.Dispose();
            throw;
        }
    }

    /// <summary>Starts one measurement and insists that the strap accepted it.</summary>
    private static async Task StartMeasurementAsync(
        PmdControlPointChannel controlPoint,
        MeasurementConfiguration measurement,
        CancellationToken cancellationToken)
    {
        var start = measurement.StartCommand.ToArray();
        var what = $"{measurement.DisplayName} start";

        var response = await controlPoint.SendAsync(start, what, cancellationToken);
        if (response.Error == PmdError.AlreadyInState)
        {
            // The strap is still streaming from a session that ended without stopping it.
            // Restart, so that it runs with exactly the settings requested now.
            await controlPoint.SendAsync(
                measurement.BuildStopCommand(),
                $"{measurement.DisplayName} stop",
                cancellationToken);
            response = await controlPoint.SendAsync(start, what, cancellationToken);
        }

        if (!response.IsSuccess)
        {
            throw new PolarConnectionException(
                $"The strap refused the {measurement.DisplayName} configuration: {response.Error}.");
        }
    }

    private static async Task<GattCharacteristic> GetRequiredCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        CancellationToken cancellationToken) =>
        await service.GetFirstCharacteristicAsync(uuid, cancellationToken) ??
        throw new PolarConnectionException("A required Polar measurement characteristic was missing.");

    private void OnDataNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            _router?.Route(args.CharacteristicValue.ToByteArray());
        }
        catch (Exception ex)
        {
            // A throw here would cross a WinRT callback boundary and take the process down in
            // the middle of a recording.
            StreamError?.Invoke(this, ex);
        }
    }

    private void OnBatteryLevelChanged(object? sender, int percent) =>
        BatteryLevelChanged?.Invoke(this, percent);

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }
}
