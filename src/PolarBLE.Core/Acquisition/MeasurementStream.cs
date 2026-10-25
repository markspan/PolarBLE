using PolarBLE.Core.Polar;
using PolarBLE.Core.Streaming;

namespace PolarBLE.Core.Acquisition;

/// <summary>
/// The complete path for one measurement: decode each frame, check it for gaps, push it to
/// the sink and keep count.
/// </summary>
/// <remarks>
/// Frames are processed one at a time under a lock, so the gap detector sees them in order
/// and the decode buffer can be reused, even if the Bluetooth stack ever delivers two
/// notifications concurrently.
/// </remarks>
public sealed class MeasurementStream : IDisposable
{
    private readonly object _gate = new();
    private readonly IMeasurementDecoder _decoder;
    private readonly ISampleSink _sink;
    private readonly FrameGapDetector _gapDetector;

    private float[] _buffer = [];
    private bool _disposed;

    /// <summary>Creates a stream that takes ownership of <paramref name="sink"/>.</summary>
    /// <param name="label">Short name shown to users, e.g. "ECG".</param>
    /// <param name="decoder">Decodes this measurement's frames.</param>
    /// <param name="nominalSampleRateHz">The rate the strap was configured for.</param>
    /// <param name="sink">Receives the decoded samples; disposed with this stream.</param>
    /// <param name="timeProvider">Clock for the statistics; the system clock by default.</param>
    /// <exception cref="ArgumentException">The sink and decoder disagree on the channel count.</exception>
    public MeasurementStream(
        string label,
        IMeasurementDecoder decoder,
        double nominalSampleRateHz,
        ISampleSink sink,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(sink);

        if (sink.ChannelCount != decoder.ChannelCount)
        {
            throw new ArgumentException(
                $"The sink has {sink.ChannelCount} channels but the decoder produces {decoder.ChannelCount}.",
                nameof(sink));
        }

        Label = label;
        _decoder = decoder;
        _sink = sink;
        _gapDetector = new FrameGapDetector(nominalSampleRateHz);
        Statistics = new StreamStatistics(timeProvider);
    }

    /// <summary>Short name shown to users.</summary>
    public string Label { get; }

    /// <summary>The measurement this stream carries.</summary>
    public PmdMeasurementType MeasurementType => _decoder.MeasurementType;

    /// <summary>Live counters for this stream.</summary>
    public StreamStatistics Statistics { get; }

    /// <summary>Decodes one frame and pushes its samples.</summary>
    /// <param name="header">The frame header.</param>
    /// <param name="payload">The frame payload, header already removed.</param>
    /// <exception cref="ArgumentException">The frame is for a different measurement.</exception>
    public void Process(in PmdFrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.MeasurementType != MeasurementType)
        {
            throw new ArgumentException(
                $"A {header.MeasurementType} frame was sent to the {Label} stream.",
                nameof(header));
        }

        if (!_decoder.IsSupported(header.FrameType))
        {
            // Unknown encodings are counted, never guessed at.
            Statistics.RecordUnsupportedFrame();
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var count = _decoder.SampleCount(header.FrameType, payload);
            if (count == 0)
            {
                return;
            }

            var values = EnsureBuffer(count * _decoder.ChannelCount);
            _decoder.Decode(header.FrameType, payload, values);

            var lost = _gapDetector.Observe(header.TimestampNanoseconds, count);
            _sink.PushChunk(values, count);
            Statistics.RecordFrame(count, lost);
        }
    }

    /// <summary>Disposes the sink. Frames arriving afterwards are ignored.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sink.Dispose();
        }
    }

    /// <summary>Returns a view of the reusable buffer, growing it if a frame needs more room.</summary>
    private Span<float> EnsureBuffer(int length)
    {
        if (_buffer.Length < length)
        {
            _buffer = new float[length];
        }

        return _buffer.AsSpan(0, length);
    }
}
