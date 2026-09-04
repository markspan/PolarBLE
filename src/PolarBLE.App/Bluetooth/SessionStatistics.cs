namespace PolarBLE.App.Bluetooth;

/// <summary>
/// Live counters for one measurement stream, written from BLE callback threads and read by the
/// user interface timer. The original implementation offered no evidence that data was flowing
/// beyond an animated ellipsis, so a stalled stream looked exactly like a healthy one.
/// </summary>
internal sealed class SessionStatistics(string label)
{
    private long _samples;
    private long _frames;
    private long _unsupportedFrames;

    /// <summary>Human-readable stream name.</summary>
    public string Label { get; } = label;

    /// <summary>Total samples pushed to LSL.</summary>
    public long Samples => Interlocked.Read(ref _samples);

    /// <summary>Total frames decoded.</summary>
    public long Frames => Interlocked.Read(ref _frames);

    /// <summary>Frames rejected because their frame type is not supported.</summary>
    public long UnsupportedFrames => Interlocked.Read(ref _unsupportedFrames);

    /// <summary>Records a successfully decoded and pushed frame.</summary>
    /// <param name="sampleCount">Number of samples in the frame.</param>
    public void RecordFrame(int sampleCount)
    {
        Interlocked.Increment(ref _frames);
        Interlocked.Add(ref _samples, sampleCount);
    }

    /// <summary>Records a frame that could not be decoded.</summary>
    public void RecordUnsupportedFrame() => Interlocked.Increment(ref _unsupportedFrames);
}
