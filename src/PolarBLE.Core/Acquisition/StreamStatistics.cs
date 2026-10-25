namespace PolarBLE.Core.Acquisition;

/// <summary>
/// Live counters for one measurement stream, written from Bluetooth callback threads and read
/// by the user interface. They are what tells a stalled or lossy stream apart from a healthy
/// one while a recording is in progress.
/// </summary>
public sealed class StreamStatistics
{
    private readonly TimeProvider _timeProvider;

    private long _frames;
    private long _samples;
    private long _unsupportedFrames;
    private long _gaps;
    private long _samplesLost;
    private long _lastActivityTimestamp;

    /// <summary>Creates a set of zeroed counters.</summary>
    /// <param name="timeProvider">The clock used to measure idle time; the system clock by default.</param>
    public StreamStatistics(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivityTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>Records a decoded frame that was pushed to the sink.</summary>
    /// <param name="sampleCount">Number of samples in the frame.</param>
    /// <param name="samplesLostBefore">Estimated samples lost before this frame, or zero.</param>
    public void RecordFrame(int sampleCount, int samplesLostBefore)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _samples, sampleCount);
        if (samplesLostBefore > 0)
        {
            Interlocked.Increment(ref _gaps);
            Interlocked.Add(ref _samplesLost, samplesLostBefore);
        }

        Interlocked.Exchange(ref _lastActivityTimestamp, _timeProvider.GetTimestamp());
    }

    /// <summary>Records a frame whose frame type cannot be decoded.</summary>
    public void RecordUnsupportedFrame() => Interlocked.Increment(ref _unsupportedFrames);

    /// <summary>Reads every counter at once.</summary>
    /// <returns>The current values.</returns>
    public StreamStatisticsSnapshot Snapshot() => new(
        Frames: Interlocked.Read(ref _frames),
        Samples: Interlocked.Read(ref _samples),
        UnsupportedFrames: Interlocked.Read(ref _unsupportedFrames),
        Gaps: Interlocked.Read(ref _gaps),
        SamplesLost: Interlocked.Read(ref _samplesLost),
        IdleTime: _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp)));
}

/// <summary>A point-in-time copy of a <see cref="StreamStatistics"/>.</summary>
/// <param name="Frames">Frames decoded and pushed.</param>
/// <param name="Samples">Samples pushed.</param>
/// <param name="UnsupportedFrames">Frames dropped because their frame type is unknown.</param>
/// <param name="Gaps">Intervals in which notifications were lost on the radio link.</param>
/// <param name="SamplesLost">Estimated total samples lost in those gaps.</param>
/// <param name="IdleTime">Time since the last frame arrived, or since the stream was created.</param>
public readonly record struct StreamStatisticsSnapshot(
    long Frames,
    long Samples,
    long UnsupportedFrames,
    long Gaps,
    long SamplesLost,
    TimeSpan IdleTime)
{
    /// <summary>True if any data has been lost or discarded.</summary>
    public bool HasDataLoss => Gaps > 0 || UnsupportedFrames > 0;

    /// <summary>Whether no frame has arrived for at least <paramref name="threshold"/>.</summary>
    /// <param name="threshold">How long silence may last before it counts as a stall.</param>
    /// <returns>True if the stream is stalled.</returns>
    public bool IsStalled(TimeSpan threshold) => IdleTime >= threshold;
}
