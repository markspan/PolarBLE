using System.Collections.Concurrent;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Watches for Polar advertisements and reports each distinct strap once.
/// </summary>
/// <remarks>
/// At most one watcher exists at a time: starting a scan replaces the previous one and
/// detaches its handlers, so nothing accumulates over repeated scans.
/// </remarks>
internal sealed class PolarScanner : IDisposable
{
    private readonly ConcurrentDictionary<ulong, DiscoveredDevice> _seen = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised once per distinct strap, on a background thread.</summary>
    public event EventHandler<DiscoveredDevice>? DeviceDiscovered;

    /// <summary>
    /// Raised, on a background thread, when Windows ends a scan on its own, for example because
    /// the Bluetooth radio is off. Carries the reason; it is not raised for <see cref="Stop"/>.
    /// </summary>
    public event EventHandler<BluetoothError>? ScanAborted;

    /// <summary>True while a scan is running.</summary>
    public bool IsScanning => _watcher is not null;

    /// <summary>Starts a scan, replacing any scan already in progress.</summary>
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
        watcher.Stopped += OnWatcherStopped;
        _watcher = watcher;
        watcher.Start();
    }

    /// <summary>Stops the current scan, if any, and detaches its handlers.</summary>
    public void Stop()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is null)
        {
            return;
        }

        watcher.Received -= OnAdvertisementReceived;
        watcher.Stopped -= OnWatcherStopped;

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

        // Match any Polar strap, not just the H10. An empty local name carries no identity, so
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

    private void OnWatcherStopped(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        // Only a watcher that is still current was stopped by Windows rather than by Stop().
        if (Interlocked.CompareExchange(ref _watcher, null, sender) == sender)
        {
            sender.Received -= OnAdvertisementReceived;
            sender.Stopped -= OnWatcherStopped;
            ScanAborted?.Invoke(this, args.Error);
        }
    }
}
