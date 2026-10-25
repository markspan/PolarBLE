using System.Buffers.Binary;
using PolarBLE.Core.Polar;
using PolarBLE.Core.Streaming;

namespace PolarBLE.Core.Tests;

/// <summary>Records every chunk pushed to it, in place of a network outlet.</summary>
internal sealed class RecordingSink(int channelCount) : ISampleSink
{
    public int ChannelCount { get; } = channelCount;

    public List<float[]> Chunks { get; } = [];

    public bool IsDisposed { get; private set; }

    public void PushChunk(ReadOnlySpan<float> interleaved, int sampleCount) =>
        Chunks.Add(interleaved[..(sampleCount * ChannelCount)].ToArray());

    public void Dispose() => IsDisposed = true;
}

/// <summary>A clock that only moves when told to.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by) => _timestamp += by.Ticks;
}

/// <summary>Builds raw PMD data notifications.</summary>
internal static class PmdFrames
{
    public static byte[] Build(
        PmdMeasurementType measurementType,
        ulong timestampNanoseconds,
        byte frameType,
        params byte[] payload)
    {
        var frame = new byte[PmdFrameHeader.Length + payload.Length];
        frame[0] = (byte)measurementType;
        BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(1), timestampNanoseconds);
        frame[9] = frameType;
        payload.CopyTo(frame.AsSpan(PmdFrameHeader.Length));
        return frame;
    }

    /// <summary>An ECG frame of <paramref name="samples"/> samples, each equal to its index.</summary>
    public static byte[] Ecg(ulong timestampNanoseconds, int samples)
    {
        var payload = new byte[samples * EcgFrameDecoder.BytesPerSample];
        for (var i = 0; i < samples; i++)
        {
            payload[i * EcgFrameDecoder.BytesPerSample] = (byte)i;
        }

        return Build(PmdMeasurementType.Ecg, timestampNanoseconds, EcgFrameDecoder.UncompressedFrameType, payload);
    }
}
