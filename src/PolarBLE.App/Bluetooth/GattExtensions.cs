using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Small, leak-free wrappers over the WinRT GATT calls this application makes.
/// </summary>
/// <remarks>
/// <see cref="GattDeviceService"/> holds a native GATT session open until it is disposed, and
/// Windows refuses new GATT operations with <c>AccessDenied</c> once too many have leaked. Every
/// helper here therefore disposes whatever it discovered but does not hand back.
/// </remarks>
internal static class GattExtensions
{
    /// <summary>How long a best-effort teardown write may take before it is abandoned.</summary>
    public static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Discovers the first service with the given UUID.</summary>
    /// <returns>The service, owned by the caller, or null if the device does not offer it.</returns>
    public static async Task<GattDeviceService?> GetFirstServiceAsync(
        this BluetoothLEDevice device,
        Guid uuid,
        CancellationToken cancellationToken)
    {
        var result = await device.GetGattServicesForUuidAsync(uuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);

        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            return null;
        }

        for (var i = 1; i < result.Services.Count; i++)
        {
            result.Services[i].Dispose();
        }

        return result.Services[0];
    }

    /// <summary>Discovers the first characteristic with the given UUID.</summary>
    /// <returns>The characteristic, or null if the service does not offer it.</returns>
    public static async Task<GattCharacteristic?> GetFirstCharacteristicAsync(
        this GattDeviceService service,
        Guid uuid,
        CancellationToken cancellationToken)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken);

        return result.Status == GattCommunicationStatus.Success && result.Characteristics.Count > 0
            ? result.Characteristics[0]
            : null;
    }

    /// <summary>Enables notifications or indications, failing loudly if the strap refuses.</summary>
    /// <param name="characteristic">The characteristic to subscribe to.</param>
    /// <param name="value">Notify or Indicate.</param>
    /// <param name="what">What is being subscribed to, for the error message.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="PolarConnectionException">The strap refused the subscription.</exception>
    public static async Task SubscribeAsync(
        this GattCharacteristic characteristic,
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

    /// <summary>Disables notifications, ignoring any failure. Never throws.</summary>
    public static Task TryUnsubscribeAsync(this GattCharacteristic characteristic) =>
        BestEffortAsync(() => characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None)
            .AsTask());

    /// <summary>Writes a value, ignoring any failure. Never throws.</summary>
    public static Task TryWriteAsync(this GattCharacteristic characteristic, byte[] value) =>
        BestEffortAsync(() => characteristic
            .WriteValueAsync(CryptographicBuffer.CreateFromByteArray(value))
            .AsTask());

    /// <summary>Copies a WinRT buffer into a managed array.</summary>
    /// <returns>The bytes; empty for an empty or missing buffer.</returns>
    public static byte[] ToByteArray(this IBuffer? buffer)
    {
        if (buffer is null)
        {
            return [];
        }

        CryptographicBuffer.CopyToByteArray(buffer, out var bytes);
        return bytes ?? [];
    }

    /// <summary>
    /// Runs a teardown operation that must neither throw nor hang: the strap may already be out
    /// of range, in which case Windows can take far longer than a user will wait to give up.
    /// </summary>
    private static async Task BestEffortAsync(Func<Task> operation)
    {
        try
        {
            await operation().WaitAsync(TeardownTimeout);
        }
        catch (Exception)
        {
            // Teardown is best effort by definition; there is nobody left to report to.
        }
    }
}
