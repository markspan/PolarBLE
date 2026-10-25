using PolarBLE.Core.Acquisition;
using Xunit;

namespace PolarBLE.Core.Tests;

public class FrameGapDetectorTests
{
    // At 100 Hz one sample lasts exactly 10 ms, which keeps the arithmetic readable.
    private const double RateHz = 100;
    private const ulong SampleNs = 10_000_000;

    [Fact]
    public void FirstFrame_OnlyEstablishesTheBaseline()
    {
        var detector = new FrameGapDetector(RateHz);

        Assert.Equal(0, detector.Observe(123_456_789, 50));
    }

    [Fact]
    public void ContiguousFrames_ReportNoGap()
    {
        var detector = new FrameGapDetector(RateHz);
        detector.Observe(50 * SampleNs, 50);

        Assert.Equal(0, detector.Observe(100 * SampleNs, 50));
        Assert.Equal(0, detector.Observe(150 * SampleNs, 50));
    }

    [Fact]
    public void ALostNotification_IsReportedWithItsSampleCount()
    {
        var detector = new FrameGapDetector(RateHz);
        detector.Observe(50 * SampleNs, 50);

        // The frame ending at 100 never arrived; the one ending at 150 did.
        Assert.Equal(50, detector.Observe(150 * SampleNs, 50));
    }

    [Fact]
    public void JitterWithinTolerance_IsNotAGap()
    {
        var detector = new FrameGapDetector(RateHz);
        detector.Observe(50 * SampleNs, 50);

        // Ten samples late is well under half of a fifty-sample frame.
        Assert.Equal(0, detector.Observe(110 * SampleNs, 50));
    }

    [Fact]
    public void ATimestampThatDoesNotAdvance_ResetsTheBaseline()
    {
        var detector = new FrameGapDetector(RateHz);
        detector.Observe(500 * SampleNs, 50);

        Assert.Equal(0, detector.Observe(10 * SampleNs, 50));
        Assert.Equal(0, detector.Observe(60 * SampleNs, 50));
    }

    [Fact]
    public void Reset_ForgetsTheBaseline()
    {
        var detector = new FrameGapDetector(RateHz);
        detector.Observe(50 * SampleNs, 50);
        detector.Reset();

        Assert.Equal(0, detector.Observe(5_000 * SampleNs, 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsANonPositiveRate(double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameGapDetector(rate));
    }

    [Fact]
    public void AHugeClockJump_SaturatesInsteadOfOverflowing()
    {
        var detector = new FrameGapDetector(RateHz);
        detector.Observe(1, 50);

        Assert.Equal(int.MaxValue, detector.Observe(ulong.MaxValue, 50));
    }
}
