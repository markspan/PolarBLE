using PolarBLE.Core.Acquisition;
using PolarBLE.Core.Polar;
using Xunit;

namespace PolarBLE.Core.Tests;

public class PmdDataRouterTests
{
    private readonly RecordingSink _ecgSink = new(channelCount: 1);
    private readonly RecordingSink _accSink = new(channelCount: 3);
    private readonly PmdDataRouter _router;

    public PmdDataRouterTests()
    {
        _router = new PmdDataRouter(
        [
            new MeasurementStream("ECG", MeasurementDecoders.Ecg, 130, _ecgSink),
            new MeasurementStream("ACC", MeasurementDecoders.Acc, 200, _accSink),
        ]);
    }

    [Fact]
    public void Route_SendsEachFrameOnlyToItsOwnStream()
    {
        Assert.True(_router.Route(PmdFrames.Ecg(1, samples: 2)));
        Assert.True(_router.Route(PmdFrames.Build(
            PmdMeasurementType.Acc, 1, AccFrameDecoder.UncompressedFrameType, new byte[6])));

        Assert.Single(_ecgSink.Chunks);
        Assert.Single(_accSink.Chunks);
    }

    [Fact]
    public void Route_IgnoresANotificationShorterThanAHeader()
    {
        Assert.False(_router.Route(new byte[PmdFrameHeader.Length - 1]));
        Assert.Equal(1, _router.IgnoredNotifications);
    }

    [Fact]
    public void Route_IgnoresAMeasurementWithNoStream()
    {
        Assert.False(_router.Route(PmdFrames.Build(PmdMeasurementType.Ppg, 1, 0x00, 0x01, 0x02, 0x03)));
        Assert.Empty(_ecgSink.Chunks);
        Assert.Empty(_accSink.Chunks);
    }

    [Fact]
    public void Constructor_RejectsTwoStreamsForOneMeasurement()
    {
        Assert.Throws<ArgumentException>(() => new PmdDataRouter(
        [
            new MeasurementStream("ECG", MeasurementDecoders.Ecg, 130, new RecordingSink(1)),
            new MeasurementStream("ECG2", MeasurementDecoders.Ecg, 130, new RecordingSink(1)),
        ]));
    }
}
