using System.Buffers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using PolarBLE.Core.Polar;
using PolarBLE.Core.Streaming;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Raised when the connection sequence fails in a way the user needs to know about.
/// </summary>
internal sealed class PolarConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// One connected Polar H10: owns the GATT connection, the decoders and the LSL outlets, and
/// tears all of them down deterministically.
/// </summary>
internal sealed class PolarH10Session : IAsyncDisposable
{
    private static readonly TimeSpan ControlPointTimeout = TimeSpan.FromSeconds(10);

    private readonly BluetoothLEDevice _device;
    private readonly GattDeviceService _pmdService;
    private readonly GattDeviceService? _batteryService;
    private readonly GattCharacteristic _controlPoint;
    private readonly GattCharacteristic _data;
    private readonly GattCharacteristic? _batteryLevel;

    private readonly ISampleSink _ecgSink;
    private readonly ISampleSink _accSink;

    private TaskCompletionSource<PmdControlPointResponse>? _pendingResponse;
    private bool _disposed;

    private PolarH10Session(
        BluetoothLEDevice device,
        GattDeviceService pmdService,
        GattDeviceService? batteryService,
        GattCharacteristic controlPoint,
        GattCharacteristic data,
        GattCharacteristic? batteryLevel,
        ISampleSink ecgSink,
        ISampleSink accSink)
    {
        _device = device;
        _pmdService = pmdService;
        _batteryService = batteryService;
        _controlPoint = controlPoint;
        _data = data;
        _batteryLevel = batteryLevel;
        _ecgSink = ecgSink;
        _accSink = accSink;
    }

    /// <summary>Raised when the strap disconnects unexpectedly.</summary>
    public event EventHandler? ConnectionLost;

    /// <summary>Raised when a battery level is read or notified, with the percentage.</summary>
    public event EventHandler<int>? BatteryLevelChanged;

    /// <summary>Raised when a notification could not be processed.</summary>
    public event EventHandler<Exception>? StreamError;

    /// <summary>ECG counters.</summary>
    public SessionStatistics EcgStatistics { get; } = new("ECG");

    /// <summary>Accelerometer counters.</summary>
    public SessionStatistics AccStatistics { get; } = new("ACC");

    /// <summary>The connected strap's name.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>
    /// The most recently observed battery percentage, or null if none has arrived yet.
    /// </summary>
    /// <remarks>
    /// The initial battery read happens as part of connecting, which finishes before a caller
    /// has had a chance to subscribe to <see cref="BatteryLevelChanged"/>. Callers that connect
    /// and only then subscribe should read this property immediately afterwards to pick up a
    /// reading that arrived before they were listening.
    /// </remarks>
    public int? BatteryPercent { get; private set; }

    /// <summary>
    /// Connects to a strap and starts both measurements.
    /// </summary>
    /// <remarks>
    /// The ordering here is deliberate and is the fix for two defects in the original code.
    /// The LSL outlets are created <em>before</em> notifications are enabled, so no sample can
    /// arrive while the outlet field is still null; and every GATT status and every
    /// control-point response is checked, so a refused configuration fails loudly instead of
    /// advertising a stream that will never carry data.
    /// </remarks>
    /// <param name="device">The strap to connect to.</param>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    /// <returns>The live session.</returns>
    /// <exception cref="PolarConnectionException">The strap could not be set up for streaming.</exception>
    public static async Task<PolarH10Session> ConnectAsync(
        DiscoveredDevice device,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        var bleDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(device.Address)
            .AsTask(cancellationToken) ??
            throw new PolarConnectionException(
                "Windows could not open the device. Check that Bluetooth is on and the strap is worn.");

        PolarH10Session session;
        try
        {
            session = await CreateSessionAsync(bleDevice, device, cancellationToken);
        }
        catch (PolarConnectionException)
        {
            bleDevice.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            bleDevice.Dispose();
            throw new PolarConnectionException("Could not start streaming from the strap.", ex);
        }

        // From here every acquired resource is owned by `session`, so a failure must be torn
        // down through DisposeAsync rather than released piecemeal a second time.
        try
        {
            await session.StartStreamingAsync(cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Discovers the GATT services and characteristics this session needs and wraps them in a
    /// session object, rolling back everything it acquired if any step fails.
    /// </summary>
    /// <remarks>
    /// <see cref="GattDeviceService"/> holds a native GATT session open until disposed. The
    /// original implementation never disposed the services it discovered, so each connection
    /// leaked one and, after a few connect/disconnect cycles, Windows started refusing new GATT
    /// operations with <c>AccessDenied</c> until the strap itself was power-cycled.
    /// </remarks>
    private static async Task<PolarH10Session> CreateSessionAsync(
        BluetoothLEDevice bleDevice,
        DiscoveredDevice device,
        CancellationToken cancellationToken)
    {
        GattDeviceService? pmdService = null;
        GattDeviceService? batteryService = null;
        ISampleSink? ecgSink = null;
        ISampleSink? accSink = null;

        try
        {
            pmdService = await GetServiceAsync(bleDevice, PolarUuids.PmdService, cancellationToken);
            var controlPoint =
                await GetCharacteristicAsync(pmdService, PolarUuids.PmdControlPoint, cancellationToken);
            var data = await GetCharacteristicAsync(pmdService, PolarUuids.PmdData, cancellationToken);

            var (battery, discoveredBatteryService) =
                await TryGetBatteryCharacteristicAsync(bleDevice, cancellationToken);
            batteryService = discoveredBatteryService;

            var name = string.IsNullOrWhiteSpace(bleDevice.Name) ? device.Name : bleDevice.Name;
            var address = device.AddressText;

            // Outlets first: a sample that arrives before they exist would be lost silently.
            ecgSink = new LslSampleSink(PolarH10Configuration.EcgStream(name, address));
            accSink = new LslSampleSink(PolarH10Configuration.AccStream(name, address));

            return new PolarH10Session(
                bleDevice, pmdService, batteryService, controlPoint, data, battery, ecgSink, accSink)
            {
                DeviceName = name,
            };
        }
        catch
        {
            ecgSink?.Dispose();
            accSink?.Dispose();
            batteryService?.Dispose();
            pmdService?.Dispose();
            throw;
        }
    }

    private async Task StartStreamingAsync(CancellationToken cancellationToken)
    {
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        // Control-point responses arrive as indications. Without this the sensor's error codes
        // are invisible, which is how a rejected configuration used to masquerade as success.
        _controlPoint.ValueChanged += OnControlPointResponse;
        await SetNotificationAsync(
            _controlPoint,
            GattClientCharacteristicConfigurationDescriptorValue.Indicate,
            "PMD control point",
            cancellationToken);

        _data.ValueChanged += OnDataNotification;
        await SetNotificationAsync(
            _data,
            GattClientCharacteristicConfigurationDescriptorValue.Notify,
            "PMD data",
            cancellationToken);

        // One command at a time: the control point services a single request, so pipelining
        // the two start commands could get the second one refused outright.
        await RequestAsync(PolarH10Configuration.BuildEcgStart(), "ECG", cancellationToken);
        await RequestAsync(PolarH10Configuration.BuildAccStart(), "accelerometer", cancellationToken);

        await StartBatteryMonitoringAsync(cancellationToken);
    }

    private async Task RequestAsync(byte[] command, string what, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PmdControlPointResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResponse = completion;

        try
        {
            var status = await _controlPoint
                .WriteValueAsync(CryptographicBuffer.CreateFromByteArray(command))
                .AsTask(cancellationToken);

            if (status != GattCommunicationStatus.Success)
            {
                throw new PolarConnectionException($"Writing the {what} start command failed ({status}).");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ControlPointTimeout);
            await using var registration = timeout.Token.Register(
                () => completion.TrySetCanceled(timeout.Token));

            PmdControlPointResponse response;
            try
            {
                response = await completion.Task;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PolarConnectionException(
                    $"The strap did not acknowledge the {what} start command within " +
                    $"{ControlPointTimeout.TotalSeconds:0} seconds.");
            }

            if (!response.IsSuccess)
            {
                throw new PolarConnectionException(
                    $"The strap refused the {what} configuration: {response.Error}.");
            }
        }
        finally
        {
            _pendingResponse = null;
        }
    }

    private void OnControlPointResponse(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        if (bytes is not null && PmdControlPoint.TryParseResponse(bytes, out var response))
        {
            _pendingResponse?.TrySetResult(response);
        }
    }

    private void OnDataNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
            if (bytes is null || !PmdFrameHeader.TryParse(bytes, out var header, out var payload))
            {
                return;
            }

            // A single handler dispatching on the header replaces the original arrangement, in
            // which two handlers were attached to the same characteristic and each decoded
            // every frame before discarding the ones addressed to the other.
            switch (header.MeasurementType)
            {
                case PmdMeasurementType.Ecg:
                    HandleEcgFrame(header, payload);
                    break;
                case PmdMeasurementType.Acc:
                    HandleAccFrame(header, payload);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            // A throw here would cross a WinRT callback boundary and take the process down in
            // the middle of a recording.
            StreamError?.Invoke(this, ex);
        }
    }

    private void HandleEcgFrame(PmdFrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.FrameType != EcgFrameDecoder.UncompressedFrameType)
        {
            EcgStatistics.RecordUnsupportedFrame();
            return;
        }

        var count = EcgFrameDecoder.SampleCount(payload);
        if (count == 0)
        {
            return;
        }

        var raw = ArrayPool<int>.Shared.Rent(count);
        var values = ArrayPool<float>.Shared.Rent(count);
        try
        {
            EcgFrameDecoder.Decode(payload, raw.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                values[i] = raw[i];
            }

            _ecgSink.PushChunk(values.AsSpan(0, count), count);
            EcgStatistics.RecordFrame(count);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(raw);
            ArrayPool<float>.Shared.Return(values);
        }
    }

    private void HandleAccFrame(PmdFrameHeader header, ReadOnlySpan<byte> payload)
    {
        int count;
        try
        {
            count = AccFrameDecoder.SampleCount(header.FrameType, payload);
        }
        catch (NotSupportedException)
        {
            // Unknown frame types are counted, never guessed at.
            AccStatistics.RecordUnsupportedFrame();
            return;
        }

        if (count == 0)
        {
            return;
        }

        var axes = count * AccFrameDecoder.Axes;
        var raw = ArrayPool<short>.Shared.Rent(axes);
        var values = ArrayPool<float>.Shared.Rent(axes);
        try
        {
            AccFrameDecoder.Decode(header.FrameType, payload, raw.AsSpan(0, axes));
            for (var i = 0; i < axes; i++)
            {
                values[i] = raw[i];
            }

            _accSink.PushChunk(values.AsSpan(0, axes), count);
            AccStatistics.RecordFrame(count);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(raw);
            ArrayPool<float>.Shared.Return(values);
        }
    }

    private async Task StartBatteryMonitoringAsync(CancellationToken cancellationToken)
    {
        if (_batteryLevel is null)
        {
            return;
        }

        try
        {
            var read = await _batteryLevel.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (read.Status == GattCommunicationStatus.Success)
            {
                CryptographicBuffer.CopyToByteArray(read.Value, out var bytes);
                if (bytes is { Length: > 0 })
                {
                    SetBatteryPercent(bytes[0]);
                }
            }

            // Subscribe as well, so a long recording shows the battery actually draining
            // rather than the value captured at connect time.
            if (_batteryLevel.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                _batteryLevel.ValueChanged += OnBatteryNotification;
                await _batteryLevel
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Battery reporting is a convenience; never fail a recording over it.
        }
    }

    private void OnBatteryNotification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out var bytes);
        if (bytes is { Length: > 0 })
        {
            SetBatteryPercent(bytes[0]);
        }
    }

    /// <summary>
    /// Records a battery reading and raises <see cref="BatteryLevelChanged"/>.
    /// </summary>
    /// <remarks>
    /// The very first reading happens while <see cref="ConnectAsync"/> is still running, before
    /// a caller can possibly have subscribed to the event yet. Caching the value here means a
    /// caller that subscribes right after <see cref="ConnectAsync"/> returns and then reads
    /// <see cref="BatteryPercent"/> still sees it, instead of the reading having been raised
    /// into an event with no listeners and lost.
    /// </remarks>
    private void SetBatteryPercent(int percent)
    {
        BatteryPercent = percent;
        BatteryLevelChanged?.Invoke(this, percent);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task<GattDeviceService> GetServiceAsync(
        BluetoothLEDevice device,
        Guid uuid,
        CancellationToken cancellationToken)
    {
        var result = await device.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);

        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            throw new PolarConnectionException(
                $"The Polar measurement service was not available ({result.Status}). " +
                "Make sure the strap is worn and not connected to another application.");
        }

        // Every service the caller does not keep would otherwise leak its GATT session.
        for (var i = 1; i < result.Services.Count; i++)
        {
            result.Services[i].Dispose();
        }

        return result.Services[0];
    }

    private static async Task<GattCharacteristic> GetCharacteristicAsync(
        GattDeviceService service,
        Guid uuid,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);

        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            throw new PolarConnectionException(
                $"A required Polar characteristic was missing ({result.Status}).");
        }

        return result.Characteristics[0];
    }

    private static async Task<(GattCharacteristic? Characteristic, GattDeviceService? Service)>
        TryGetBatteryCharacteristicAsync(BluetoothLEDevice device, CancellationToken cancellationToken)
    {
        GattDeviceService? service = null;
        try
        {
            var services = await device
                .GetGattServicesForUuidAsync(PolarUuids.BatteryService, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);

            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                return (null, null);
            }

            for (var i = 1; i < services.Services.Count; i++)
            {
                services.Services[i].Dispose();
            }

            service = services.Services[0];

            var characteristics = await service
                .GetCharacteristicsForUuidAsync(PolarUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);

            if (characteristics.Status == GattCommunicationStatus.Success &&
                characteristics.Characteristics.Count > 0)
            {
                return (characteristics.Characteristics[0], service);
            }

            service.Dispose();
            return (null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            service?.Dispose();
            return (null, null);
        }
    }

    private static async Task SetNotificationAsync(
        GattCharacteristic characteristic,
        GattClientCharacteristicConfigurationDescriptorValue value,
        string what,
        CancellationToken cancellationToken)
    {
        var status = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(value)
            .AsTask(cancellationToken);

        if (status != GattCommunicationStatus.Success)
        {
            throw new PolarConnectionException($"Could not subscribe to {what} ({status}).");
        }
    }

    /// <summary>
    /// Stops both measurements, unsubscribes, closes the outlets and releases the connection.
    /// The original implementation did none of this, so the strap kept streaming after the
    /// window closed and the outlets lingered on the network.
    /// </summary>
    /// <remarks>
    /// The GATT services are disposed last, after everything obtained from them (the
    /// characteristics) has finished being used. Leaving either service undisposed is what used
    /// to make every reconnect after the first fail with <c>AccessDenied</c>.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _data.ValueChanged -= OnDataNotification;
        _controlPoint.ValueChanged -= OnControlPointResponse;
        if (_batteryLevel is not null)
        {
            _batteryLevel.ValueChanged -= OnBatteryNotification;
        }

        await TryStopMeasurementAsync(PmdMeasurementType.Ecg);
        await TryStopMeasurementAsync(PmdMeasurementType.Acc);
        await TryUnsubscribeAsync(_data);
        await TryUnsubscribeAsync(_controlPoint);

        _ecgSink.Dispose();
        _accSink.Dispose();

        _batteryService?.Dispose();
        _pmdService.Dispose();
        _device.Dispose();
    }

    private async Task TryStopMeasurementAsync(PmdMeasurementType measurement)
    {
        try
        {
            await _controlPoint
                .WriteValueAsync(CryptographicBuffer.CreateFromByteArray(PmdControlPoint.BuildStop(measurement)))
                .AsTask();
        }
        catch (Exception)
        {
            // The strap may already be gone; teardown must not throw.
        }
    }

    private static async Task TryUnsubscribeAsync(GattCharacteristic characteristic)
    {
        try
        {
            await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None)
                .AsTask();
        }
        catch (Exception)
        {
            // Same: best effort.
        }
    }
}
