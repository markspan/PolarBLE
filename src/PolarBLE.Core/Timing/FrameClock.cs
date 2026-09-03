namespace PolarBLE.Core.Timing;

/// <summary>
/// Assigns a timestamp to every sample in a burst-delivered frame.
/// </summary>
/// <remarks>
/// <para>
/// A Polar strap delivers tens of samples in a single BLE notification. Stamping them all at
/// arrival time — which is what liblsl does when no timestamp is supplied — collapses an
/// entire frame into one instant and then leaves a gap until the next notification. At 130 Hz
/// with 73 samples per frame that misplaces samples by up to about 550 ms, and it destroys the
/// alignment between the ECG and accelerometer streams.
/// </para>
/// <para>
/// This class instead maintains a continuous sample grid. The frame's last sample is taken to
/// arrive now, the rest are back-dated at the nominal sample interval, and the grid is nudged
/// gently towards the observed arrival time so that it tracks host/device clock drift without
/// jittering. A large disagreement is treated as a real discontinuity: the grid resynchronises
/// and the event is counted rather than smoothed away.
/// </para>
/// </remarks>
public sealed class FrameClock
{
    private readonly double _sampleInterval;
    private readonly double _resyncThreshold;
    private readonly double _driftCorrection;

    private double _nextSampleTime;
    private double _lastEmitted = double.NegativeInfinity;
    private ulong _lastDeviceTimestamp;
    private bool _started;

    /// <summary>
    /// Creates a clock for a stream of a given nominal rate.
    /// </summary>
    /// <param name="nominalSampleRate">Sample rate in hertz; must be positive.</param>
    /// <param name="resyncThresholdSeconds">
    /// How far the grid may disagree with arrival time before it is treated as a discontinuity
    /// and resynchronised rather than nudged.
    /// </param>
    /// <param name="driftCorrection">
    /// Fraction of the observed offset folded into the grid on each frame. Small values track
    /// clock drift smoothly; 0 pins the grid to its original anchor.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its valid range.</exception>
    public FrameClock(double nominalSampleRate, double resyncThresholdSeconds = 0.5, double driftCorrection = 0.01)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nominalSampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resyncThresholdSeconds);
        ArgumentOutOfRangeException.ThrowIfNegative(driftCorrection);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(driftCorrection, 1.0);

        _sampleInterval = 1.0 / nominalSampleRate;
        _resyncThreshold = resyncThresholdSeconds;
        _driftCorrection = driftCorrection;
    }

    /// <summary>Number of frames stamped so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Number of samples stamped so far.</summary>
    public long SampleCount { get; private set; }

    /// <summary>
    /// Number of times the grid had to resynchronise, i.e. the stream visibly skipped.
    /// </summary>
    public long DiscontinuityCount { get; private set; }

    /// <summary>
    /// Number of frames whose device timestamp implies samples were lost in transit.
    /// </summary>
    public long DroppedFrameCount { get; private set; }

    /// <summary>
    /// Stamps one frame.
    /// </summary>
    /// <param name="sampleCount">Number of samples in the frame; must be positive.</param>
    /// <param name="deviceTimestampNanoseconds">
    /// The frame's device timestamp, used only to detect gaps. Pass 0 when unavailable.
    /// </param>
    /// <param name="arrivalTime">The LSL local clock reading when the frame arrived.</param>
    /// <param name="timestamps">Receives one timestamp per sample.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleCount"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="timestamps"/> is too small.</exception>
    public void Stamp(
        int sampleCount,
        ulong deviceTimestampNanoseconds,
        double arrivalTime,
        Span<double> timestamps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        if (timestamps.Length < sampleCount)
        {
            throw new ArgumentException("Timestamp buffer is too small.", nameof(timestamps));
        }

        // The device timestamp refers to the last sample in the frame, so it tells us whether
        // frames went missing between this one and the previous one.
        if (deviceTimestampNanoseconds != 0)
        {
            if (_lastDeviceTimestamp != 0)
            {
                var expected = sampleCount * _sampleInterval * 1e9;
                var actual = (double)(deviceTimestampNanoseconds - _lastDeviceTimestamp);
                if (Math.Abs(actual - expected) > 1.5 * _sampleInterval * 1e9)
                {
                    DroppedFrameCount++;
                }
            }

            _lastDeviceTimestamp = deviceTimestampNanoseconds;
        }

        var frameStart = arrivalTime - ((sampleCount - 1) * _sampleInterval);

        if (!_started)
        {
            _nextSampleTime = frameStart;
            _started = true;
        }
        else
        {
            var offset = frameStart - _nextSampleTime;
            if (Math.Abs(offset) > _resyncThreshold)
            {
                DiscontinuityCount++;
                _nextSampleTime = frameStart;
            }
            else
            {
                _nextSampleTime += _driftCorrection * offset;
            }
        }

        // Never emit a timestamp that goes backwards: LSL consumers assume monotonicity, and a
        // stalled host clock must not reorder samples.
        if (_nextSampleTime <= _lastEmitted)
        {
            _nextSampleTime = _lastEmitted + _sampleInterval;
        }

        for (var i = 0; i < sampleCount; i++)
        {
            timestamps[i] = _nextSampleTime + (i * _sampleInterval);
        }

        _lastEmitted = timestamps[sampleCount - 1];
        _nextSampleTime += sampleCount * _sampleInterval;

        FrameCount++;
        SampleCount += sampleCount;
    }
}
