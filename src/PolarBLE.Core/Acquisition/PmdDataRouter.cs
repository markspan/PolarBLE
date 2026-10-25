using PolarBLE.Core.Polar;

namespace PolarBLE.Core.Acquisition;

/// <summary>
/// Dispatches raw notifications from the PMD data characteristic to the stream that owns
/// their measurement type.
/// </summary>
/// <remarks>
/// All measurements share one characteristic, so a single router reads each header once and
/// hands the frame to exactly one stream.
/// </remarks>
public sealed class PmdDataRouter
{
    private readonly Dictionary<PmdMeasurementType, MeasurementStream> _streams;
    private long _ignoredNotifications;

    /// <summary>Creates a router over the given streams.</summary>
    /// <param name="streams">One stream per measurement type.</param>
    /// <exception cref="ArgumentException">Two streams carry the same measurement type.</exception>
    public PmdDataRouter(IEnumerable<MeasurementStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);

        _streams = [];
        foreach (var stream in streams)
        {
            if (!_streams.TryAdd(stream.MeasurementType, stream))
            {
                throw new ArgumentException(
                    $"More than one stream was registered for {stream.MeasurementType}.",
                    nameof(streams));
            }
        }
    }

    /// <summary>Number of notifications that were too short or had no matching stream.</summary>
    public long IgnoredNotifications => Interlocked.Read(ref _ignoredNotifications);

    /// <summary>Routes one notification.</summary>
    /// <param name="notification">The raw notification bytes.</param>
    /// <returns>True if a stream processed the frame.</returns>
    public bool Route(ReadOnlySpan<byte> notification)
    {
        if (!PmdFrameHeader.TryParse(notification, out var header, out var payload) ||
            !_streams.TryGetValue(header.MeasurementType, out var stream))
        {
            Interlocked.Increment(ref _ignoredNotifications);
            return false;
        }

        stream.Process(header, payload);
        return true;
    }
}
