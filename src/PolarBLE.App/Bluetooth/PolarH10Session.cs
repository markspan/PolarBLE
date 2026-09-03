using System.Buffers;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using PolarBLE.Core.Polar;
using PolarBLE.Core.Streaming;
using PolarBLE.Core.Timing;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Raised when the connection sequence fails in a way the user needs to know about.
/// </summary>
internal sealed class PolarConnectionException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// One connected Polar H10: owns the GATT connection, the decoders, the sample clocks and the
/// LSL outlets, and tears all of them down deterministically.
/// </summary>
internal sealed class PolarH10Session : IAsyncDisposable
{
    private static readonly TimeSpan ControlPointTimeout = TimeSpan.FromSeconds(10);

    private readonly BluetoothLEDevice _device;
    private readonly GattCharacteristic _controlPoint;
    private readonly GattCharacteristic _data;
    private readonly GattCharacteristic? _batteryLevel;

    private readonly ISampleSink _ecgSink;
    private readonly ISampleSink _accSink;
    private readonly FrameClock _ecgClock;
    private readonly FrameClock _accClock;

    private TaskCompletionSource<PmdControlPointResponse>? _pendingResponse;
    private bool _disposed;

    private PolarH10Session(
        BluetoothLEDevice device,
        GattCharacteristic controlPoint,
        GattCharacteristic data,
        GattCharacteristic? batteryLevel,
        ISampleSink ecgSink,
        ISampleSink accSink)
    {
        _device = device;
        _controlPoint = controlPoint;
        _data = data;
        _batteryLevel = batteryLevel;
        _ecgSink = ecgSink;
        _accSink = accSink;
        _ecgClock = new FrameClock(PolarH10Configuration.EcgSampleRateHz);
        _accClock = new FrameClock(PolarH10Configuration.AccSampleRateHz);
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

    /// <summary>Frames whose device timestamps imply data was lost in transit.</summary>
    public long DroppedFrames => _ecgClock.DroppedFrameCount + _accClock.DroppedFrameCount;

    /// <summary>Times a stream visibly skipped and the sample grid had to resynchronise.</summary>
    public long Discontinuities => _ecgClock.DiscontinuityCount + _accClock.DiscontinuityCount;

    /// <summary>The connected strap's name.</summary>
    public string DeviceName { get; private set; } = string.Empty;

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

        try
        {
            var pmd = await GetServiceAsync(bleDevice, PolarUuids.PmdService, cancellationToken);
            var controlPoint = await GetCharacteristicAsync(pmd, PolarUuids.PmdControlPoint, cancellationToken);
            var data = await GetCharacteristicAsync(pmd, PolarUuids.PmdData, cancellationToken);
            var battery = await TryGetBatteryCharacteristicAsync(bleDevice, cancellationToken);

            var name = string.IsNullOrWhiteSpace(bleDevice.Name) ? device.Name : bleDevice.Name;
            var address = device.AddressText;

            // Outlets first: a sample that arrives before they exist would be lost silently.
            ISampleSink ecgSink = new LslSampleSink(PolarH10Configuration.EcgStream(name, address));
            ISampleSink accSink;
            try
            {
                accSink = new LslSampleSink(PolarH10Configuration.AccStream(name, address));
            }
            catch
            {
                ecgSink.Dispose();
                throw;
            }

            var session = new PolarH10Session(bleDevice, controlPoint, data, battery, ecgSink, accSink)
            {
                DeviceName = name,
            };

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
            // Stamp arrival as early as possible: everything after this adds latency.
            var arrival = LSL.LSL.local_clock();

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
                    HandleEcgFrame(header, payload, arrival);
                    break;
                case PmdMeasurementType.Acc:
                    HandleAccFrame(header, payload, arrival);
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

    private void HandleEcgFrame(PmdFrameHeader header, ReadOnlySpan<byte> payload, double arrival)
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
        var stamps = ArrayPool<double>.Shared.Rent(count);
        try
        {
            EcgFrameDecoder.Decode(payload, raw.AsSpan(0, count));
            for (var i = 0; i < count; i++)
            {
                values[i] = raw[i];
            }

            _ecgClock.Stamp(count, header.TimestampNanoseconds, arrival, stamps.AsSpan(0, count));
            _ecgSink.PushChunk(values.AsSpan(0, count), stamps.AsSpan(0, count), count);
            EcgStatistics.RecordFrame(count);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(raw);
            ArrayPool<float>.Shared.Return(values);
            ArrayPool<double>.Shared.Return(stamps);
        }
    }

    private void HandleAccFrame(PmdFrameHeader header, ReadOnlySpan<byte> payload, double arrival)
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
        var stamps = ArrayPool<double>.Shared.Rent(count);
        try
        {
            AccFrameDecoder.Decode(header.FrameType, payload, raw.AsSpan(0, axes));
            for (var i = 0; i < axes; i++)
            {
                values[i] = raw[i];
            }

            _accClock.Stamp(count, header.TimestampNanoseconds, arrival, stamps.AsSpan(0, count));
            _accSink.PushChunk(values.AsSpan(0, axes), stamps.AsSpan(0, count), count);
            AccStatistics.RecordFrame(count);
        }
        finally
        {
            ArrayPool<short>.Shared.Return(raw);
            ArrayPool<float>.Shared.Return(values);
            ArrayPool<double>.Shared.Return(stamps);
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
                    BatteryLevelChanged?.Invoke(this, bytes[0]);
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
            BatteryLevelChanged?.Invoke(this, bytes[0]);
        }
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

    private static async Task<GattCharacteristic?> TryGetBatteryCharacteristicAsync(
        BluetoothLEDevice device,
        CancellationToken cancellationToken)
    {
        try
        {
            var services = await device
                .GetGattServicesForUuidAsync(PolarUuids.BatteryService, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);

            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                return null;
            }

            var characteristics = await services.Services[0]
                .GetCharacteristicsForUuidAsync(PolarUuids.BatteryLevel, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);

            return characteristics.Status == GattCommunicationStatus.Success &&
                   characteristics.Characteristics.Count > 0
                ? characteristics.Characteristics[0]
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
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
