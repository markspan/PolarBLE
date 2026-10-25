using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using PolarBLE.Core.Polar;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Reports the strap's battery level for the duration of a recording.
/// </summary>
/// <remarks>
/// Battery reporting is a convenience: every failure here is swallowed rather than allowed to
/// stop a recording.
/// </remarks>
internal sealed class BatteryMonitor
{
    private readonly GattDeviceService _service;
    private readonly GattCharacteristic _level;
    private bool _subscribed;
    private bool _closed;

    private BatteryMonitor(GattDeviceService service, GattCharacteristic level)
    {
        _service = service;
        _level = level;
    }

    /// <summary>Raised with the new percentage whenever a reading arrives.</summary>
    public event EventHandler<int>? LevelChanged;

    /// <summary>The most recent percentage, or null until the first reading arrives.</summary>
    /// <remarks>
    /// The first reading is taken while connecting, before anyone can have subscribed to
    /// <see cref="LevelChanged"/>. Callers that subscribe afterwards read this once to catch up.
    /// </remarks>
    public int? Percent { get; private set; }

    /// <summary>Discovers the standard battery service.</summary>
    /// <returns>A monitor, or null if the strap does not expose a usable battery level.</returns>
    public static async Task<BatteryMonitor?> TryOpenAsync(
        BluetoothLEDevice device,
        CancellationToken cancellationToken)
    {
        GattDeviceService? service = null;
        try
        {
            service = await device.GetFirstServiceAsync(PolarUuids.BatteryService, cancellationToken);
            var level = service is null
                ? null
                : await service.GetFirstCharacteristicAsync(PolarUuids.BatteryLevel, cancellationToken);

            if (service is not null && level is not null)
            {
                return new BatteryMonitor(service, level);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fall through: no battery level is not a reason to refuse a connection.
        }

        service?.Dispose();
        return null;
    }

    /// <summary>Takes a first reading and subscribes to changes where the strap supports it.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var read = await _level.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
            if (read.Status == GattCommunicationStatus.Success)
            {
                Report(read.Value.ToByteArray());
            }

            // Subscribing means a long recording shows the battery draining rather than the
            // value captured at connect time.
            if (_level.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
            {
                _level.ValueChanged += OnNotification;
                _subscribed = true;
                await _level
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // See the class remarks.
        }
    }

    /// <summary>Unsubscribes, when still possible, and releases the service.</summary>
    /// <param name="deviceConnected">False skips the radio traffic that can no longer succeed.</param>
    public async Task CloseAsync(bool deviceConnected)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _level.ValueChanged -= OnNotification;
        if (_subscribed && deviceConnected)
        {
            await _level.TryUnsubscribeAsync();
        }

        _service.Dispose();
    }

    private void OnNotification(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        Report(args.CharacteristicValue.ToByteArray());

    private void Report(byte[] value)
    {
        if (value.Length == 0)
        {
            return;
        }

        Percent = value[0];
        LevelChanged?.Invoke(this, value[0]);
    }
}
