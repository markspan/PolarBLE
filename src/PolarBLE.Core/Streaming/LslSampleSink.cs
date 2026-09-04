using LSL;

namespace PolarBLE.Core.Streaming;

/// <summary>
/// An <see cref="ISampleSink"/> backed by a LabStreamingLayer outlet.
/// </summary>
/// <remarks>
/// Samples are pushed a whole frame at a time rather than one interop call per sample, which
/// is the only change from letting liblsl time each push by default: no timestamp is supplied
/// here either, so liblsl stamps the chunk at push time and derives every earlier sample in it
/// from the stream's own declared sample rate, exactly as it would for a single-sample push.
/// </remarks>
public sealed class LslSampleSink : ISampleSink
{
    // liblsl interprets max_buffered as seconds when the stream has a nominal rate.
    private const int MaxBufferedSeconds = 360;

    // 0 lets the sender decide the chunk granularity, which is what we want: we already push
    // one BLE frame at a time.
    private const int OutletChunkSize = 0;

    private readonly object _pushLock = new();
    private readonly StreamInfo _info;
    private readonly StreamOutlet _outlet;

    private float[,]? _buffer;
    private bool _disposed;

    /// <summary>
    /// Creates the outlet and advertises it on the network.
    /// </summary>
    /// <param name="definition">The stream to advertise.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is null.</exception>
    public LslSampleSink(StreamDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        Definition = definition;
        ChannelCount = definition.Channels.Count;

        _info = new StreamInfo(
            definition.Name,
            definition.Type,
            ChannelCount,
            definition.SampleRate,
            channel_format_t.cf_float32,
            definition.SourceId);

        DescribeChannels(_info, definition);
        _outlet = new StreamOutlet(_info, OutletChunkSize, MaxBufferedSeconds);
    }

    /// <summary>The stream this sink advertises.</summary>
    public StreamDefinition Definition { get; }

    /// <inheritdoc />
    public int ChannelCount { get; }

    /// <inheritdoc />
    public long SamplesPushed { get; private set; }

    /// <summary>True when at least one consumer is currently subscribed.</summary>
    public bool HasConsumers => !_disposed && _outlet.have_consumers();

    /// <inheritdoc />
    public void PushChunk(ReadOnlySpan<float> interleaved, int sampleCount)
    {
        if (sampleCount <= 0 || _disposed)
        {
            return;
        }

        var values = sampleCount * ChannelCount;
        if (interleaved.Length < values)
        {
            throw new ArgumentException("Sample buffer is shorter than the declared frame.", nameof(interleaved));
        }

        lock (_pushLock)
        {
            // liblsl derives the element count from the array length, so the staging buffer
            // must be sized exactly. Frame sizes are near-constant, so this reallocates once.
            if (_buffer is null || _buffer.GetLength(0) != sampleCount)
            {
                _buffer = new float[sampleCount, ChannelCount];
            }

            for (var sample = 0; sample < sampleCount; sample++)
            {
                for (var channel = 0; channel < ChannelCount; channel++)
                {
                    _buffer[sample, channel] = interleaved[(sample * ChannelCount) + channel];
                }
            }

            // No timestamp argument: liblsl stamps the chunk with the current time and derives
            // every earlier sample in it from the stream's declared sample rate.
            _outlet.push_chunk(_buffer);
            SamplesPushed += sampleCount;
        }
    }

    /// <summary>
    /// Closes the outlet. Failing to do this leaves a zombie stream on the network that a
    /// recorder can still discover and bind to.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_pushLock)
        {
            _outlet.Dispose();
            _info.Dispose();
        }
    }

    private static void DescribeChannels(StreamInfo info, StreamDefinition definition)
    {
        var description = info.desc();

        var channels = description.append_child("channels");
        foreach (var channel in definition.Channels)
        {
            channels.append_child("channel")
                .append_child_value("label", channel.Label)
                .append_child_value("unit", channel.Unit)
                .append_child_value("type", channel.Type);
        }

        var acquisition = description.append_child("acquisition");
        acquisition.append_child_value("manufacturer", definition.Manufacturer);
        acquisition.append_child_value("model", definition.Model);
        acquisition.append_child_value("device_address", definition.DeviceAddress);
        foreach (var (key, value) in definition.Acquisition)
        {
            acquisition.append_child_value(key, value);
        }
    }
}
