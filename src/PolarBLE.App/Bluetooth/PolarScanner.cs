using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.Advertisement;

namespace PolarBLE.App.Bluetooth;

/// <summary>A Polar strap seen in a Bluetooth LE advertisement.</summary>
/// <param name="Address">The Bluetooth address, which is the stable identity of the strap.</param>
/// <param name="Name">The advertised local name.</param>
internal sealed record DiscoveredDevice(ulong Address, string Name)
{
    /// <summary>The address formatted the way it is shown to users and used in stream ids.</summary>
    public string AddressText => Address.ToString("X12");

    /// <summary>
    /// What the list box displays. The address travels with the object rather than being
    /// parsed back out of this string, which is what the original implementation did.
    /// </summary>
    public override string ToString() => $"{Name} [{AddressText}]";
}

/// <summary>
/// Watches for Polar advertisements and reports each distinct strap once.
/// </summary>
internal sealed class PolarScanner : IDisposable
{
    private readonly ConcurrentDictionary<ulong, DiscoveredDevice> _seen = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised once per distinct strap, on a background thread.</summary>
    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    /// <summary>True while a scan is running.</summary>
    public bool IsScanning => _watcher is not null;

    /// <summary>
    /// Starts a scan, replacing any scan already in progress. The original implementation
    /// created a fresh watcher on every click and never stopped the previous one, so handlers
    /// and watchers accumulated for the lifetime of the process.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();
        _seen.Clear();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        watcher.Received += OnAdvertisementReceived;
        _watcher = watcher;
        watcher.Start();
    }

    /// <summary>Stops the current scan, if any, and detaches its handler.</summary>
    public void Stop()
    {
        var watcher = _watcher;
        if (watcher is null)
        {
            return;
        }

        _watcher = null;
        watcher.Received -= OnAdvertisementReceived;

        try
        {
            watcher.Stop();
        }
        catch (Exception) when (!Environment.HasShutdownStarted)
        {
            // Stopping a watcher that the radio already tore down is not actionable.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var name = args.Advertisement.LocalName;

        // Match any Polar strap, not just the H10: an empty local name carries no identity, so
        // it is skipped rather than listed as a nameless entry.
        if (string.IsNullOrWhiteSpace(name) ||
            !name.StartsWith("Polar", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var device = new DiscoveredDevice(args.BluetoothAddress, name);
        if (_seen.TryAdd(args.BluetoothAddress, device))
        {
            DeviceDiscovered?.Invoke(this, device);
        }
    }
}
