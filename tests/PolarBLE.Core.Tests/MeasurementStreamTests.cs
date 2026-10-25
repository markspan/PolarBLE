using PolarBLE.Core.Acquisition;
using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class MeasurementStreamTests
{
    private const ulong EcgSampleNs = 1_000_000_000UL / PolarH10Configuration.EcgSampleRateHz;

    private static MeasurementStream CreateEcgStream(RecordingSink sink) =>
        new("ECG", MeasurementDecoders.Ecg, PolarH10Configuration.EcgSampleRateHz, sink, new ManualTimeProvider());

    private static void Process(MeasurementStream stream, byte[] notification)
    {
        Assert.True(PmdFrameHeader.TryParse(notification, out var header, out var payload));
        stream.Process(header, payload);
    }

    [Fact]
    public void Process_DecodesAFrameAndPushesItAsOneChunk()
    {
        var sink = new RecordingSink(channelCount: 1);
        using var stream = CreateEcgStream(sink);

        Process(stream, PmdFrames.Ecg(timestampNanoseconds: 1, samples: 3));

        var chunk = Assert.Single(sink.Chunks);
        Assert.Equal([0f, 1f, 2f], chunk);
        Assert.Equal(3, stream.Statistics.Snapshot().Samples);
    }

    [Fact]
    public void Process_DecodesAccelerometerAxesInterleaved()
    {
        var sink = new RecordingSink(channelCount: 3);
        using var stream = new MeasurementStream("ACC", MeasurementDecoders.Acc, 200, sink);

        byte[] payload = [0x01, 0x00, 0xFF, 0xFF, 0x10, 0x00];
        Process(stream, PmdFrames.Build(PmdMeasurementType.Acc, 1, AccFrameDecoder.UncompressedFrameType, payload));

        Assert.Equal([1f, -1f, 16f], Assert.Single(sink.Chunks));
    }

    [Fact]
    public void Process_CountsAnUnsupportedFrameTypeWithoutPushingAnything()
    {
        var sink = new RecordingSink(channelCount: 1);
        using var stream = CreateEcgStream(sink);

        Process(stream, PmdFrames.Build(PmdMeasurementType.Ecg, 1, frameType: 0x7F, 0x01, 0x02, 0x03));

        Assert.Empty(sink.Chunks);
        Assert.Equal(1, stream.Statistics.Snapshot().UnsupportedFrames);
    }

    [Fact]
    public void Process_RecordsAGapWhenANotificationIsLost()
    {
        var sink = new RecordingSink(channelCount: 1);
        using var stream = CreateEcgStream(sink);

        Process(stream, PmdFrames.Ecg(10 * EcgSampleNs, samples: 10));
        Process(stream, PmdFrames.Ecg(30 * EcgSampleNs, samples: 10));

        var snapshot = stream.Statistics.Snapshot();
        Assert.Equal(1, snapshot.Gaps);
        Assert.Equal(10, snapshot.SamplesLost);

        // Diagnostics never alter the data: both frames are still delivered.
        Assert.Equal(2, sink.Chunks.Count);
    }

    [Fact]
    public void Process_RejectsAFrameForAnotherMeasurement()
    {
        using var stream = CreateEcgStream(new RecordingSink(channelCount: 1));
        var notification = PmdFrames.Build(PmdMeasurementType.Acc, 1, AccFrameDecoder.UncompressedFrameType);

        Assert.Throws<ArgumentException>(() => Process(stream, notification));
    }

    [Fact]
    public void Dispose_DisposesTheSinkAndIgnoresLaterFrames()
    {
        var sink = new RecordingSink(channelCount: 1);
        var stream = CreateEcgStream(sink);

        stream.Dispose();
        Process(stream, PmdFrames.Ecg(1, samples: 3));

        Assert.True(sink.IsDisposed);
        Assert.Empty(sink.Chunks);
    }

    [Fact]
    public void Constructor_RejectsASinkWithTheWrongChannelCount()
    {
        Assert.Throws<ArgumentException>(
            () => new MeasurementStream("ECG", MeasurementDecoders.Ecg, 130, new RecordingSink(channelCount: 3)));
    }
}
