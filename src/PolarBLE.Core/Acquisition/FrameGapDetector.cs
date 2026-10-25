namespace PolarBLE.Core.Acquisition;

/// <summary>
/// Detects frames lost between the strap and this application by comparing consecutive PMD
/// device timestamps against the number of samples that actually arrived.
/// </summary>
/// <remarks>
/// <para>
/// Every PMD frame is stamped by the strap with the time of its last sample. Between two
/// consecutive frames, the timestamps should therefore advance by exactly the duration of the
/// second frame's samples. When they advance by noticeably more, whole notifications were
/// lost on the radio link, and the surplus says roughly how many samples went with them.
/// </para>
/// <para>
/// This is diagnostic only: it never alters or re-times data. Comparing each frame only with
/// its predecessor means a small mismatch between the nominal and the true sample rate cannot
/// accumulate into a false alarm. The class is not thread-safe; feed it from one thread, or
/// under a lock, in the order frames arrived.
/// </para>
/// </remarks>
public sealed class FrameGapDetector
{
    private const double NanosecondsPerSecond = 1e9;

    private readonly double _samplesPerNanosecond;
    private readonly double _toleranceFrames;
    private ulong? _previousTimestamp;

    /// <summary>Creates a detector for a stream with the given nominal rate.</summary>
    /// <param name="nominalSampleRateHz">The rate the strap was configured for.</param>
    /// <param name="toleranceFrames">
    /// How much longer than expected an interval may be, as a fraction of the arriving frame,
    /// before it counts as a gap. The default of half a frame sits well clear of timestamp
    /// jitter yet well below the loss of one whole notification.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An argument is not positive.</exception>
    public FrameGapDetector(double nominalSampleRateHz, double toleranceFrames = 0.5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nominalSampleRateHz);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toleranceFrames);

        _samplesPerNanosecond = nominalSampleRateHz / NanosecondsPerSecond;
        _toleranceFrames = toleranceFrames;
    }

    /// <summary>
    /// Records a frame and reports how many samples appear to be missing before it.
    /// </summary>
    /// <param name="timestampNanoseconds">The frame's device timestamp.</param>
    /// <param name="sampleCount">The number of samples the frame carried.</param>
    /// <returns>
    /// The estimated number of samples lost since the previous frame, or zero when the
    /// interval is consistent with the samples received. The first frame, and a frame whose
    /// timestamp does not advance (a device clock reset), only establish a new baseline.
    /// </returns>
    public int Observe(ulong timestampNanoseconds, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);

        var previous = _previousTimestamp;
        _previousTimestamp = timestampNanoseconds;

        if (previous is not { } baseline || timestampNanoseconds <= baseline)
        {
            return 0;
        }

        var samplesSpanned = (timestampNanoseconds - baseline) * _samplesPerNanosecond;
        var surplus = samplesSpanned - sampleCount;
        var threshold = Math.Max(1.0, _toleranceFrames * sampleCount);

        // Clamped so that a large forward jump of the device clock cannot overflow the count.
        return surplus > threshold ? (int)Math.Min(Math.Round(surplus), int.MaxValue) : 0;
    }

    /// <summary>Forgets the baseline, so the next frame starts a fresh comparison.</summary>
    public void Reset() => _previousTimestamp = null;
}
