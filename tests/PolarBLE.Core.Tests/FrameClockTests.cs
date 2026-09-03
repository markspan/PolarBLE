using PolarBLE.Core.Timing;
using Xunit;

namespace PolarBLE.Core.Tests;

public class FrameClockTests
{
    private const double Rate = 100.0;
    private const double Interval = 1.0 / Rate;

    /// <summary>
    /// The core defect this class exists to fix: liblsl's default stamping collapses a whole
    /// frame onto one instant. Samples must instead be spread at the nominal interval.
    /// </summary>
    [Fact]
    public void Stamp_SpreadsSamplesAcrossTheFrameInsteadOfCollapsingThem()
    {
        var clock = new FrameClock(Rate);
        Span<double> stamps = new double[10];

        clock.Stamp(10, 0, arrivalTime: 1.0, stamps);

        for (var i = 1; i < stamps.Length; i++)
        {
            Assert.Equal(Interval, stamps[i] - stamps[i - 1], precision: 9);
        }

        // The last sample of the frame is the one that just arrived.
        Assert.Equal(1.0, stamps[9], precision: 9);
    }

    [Fact]
    public void Stamp_KeepsTheGridContinuousAcrossFrames()
    {
        var clock = new FrameClock(Rate);
        Span<double> first = new double[10];
        Span<double> second = new double[10];

        clock.Stamp(10, 0, arrivalTime: 1.0, first);
        clock.Stamp(10, 0, arrivalTime: 1.1, second);

        // No seam: the gap between frames equals the gap within a frame.
        Assert.Equal(Interval, second[0] - first[9], precision: 9);
        Assert.Equal(0, clock.DiscontinuityCount);
        Assert.Equal(20, clock.SampleCount);
        Assert.Equal(2, clock.FrameCount);
    }

    [Fact]
    public void Stamp_IsAlwaysMonotonic()
    {
        var clock = new FrameClock(Rate);
        var previous = double.NegativeInfinity;
        Span<double> stamps = new double[8];

        // Arrival times that jitter, stall and even step backwards.
        foreach (var arrival in new[] { 1.0, 1.05, 1.04, 1.30, 1.31, 1.31 })
        {
            clock.Stamp(8, 0, arrival, stamps);
            foreach (var stamp in stamps)
            {
                Assert.True(stamp > previous, "timestamps must never go backwards");
                previous = stamp;
            }
        }
    }

    [Fact]
    public void Stamp_ResynchronisesAndCountsARealDiscontinuity()
    {
        var clock = new FrameClock(Rate, resyncThresholdSeconds: 0.5);
        Span<double> stamps = new double[10];

        clock.Stamp(10, 0, arrivalTime: 1.0, stamps);
        Assert.Equal(0, clock.DiscontinuityCount);

        // A four-second hole: the grid must jump rather than smear the gap away.
        clock.Stamp(10, 0, arrivalTime: 5.0, stamps);

        Assert.Equal(1, clock.DiscontinuityCount);
        Assert.Equal(5.0, stamps[9], precision: 9);
    }

    [Fact]
    public void Stamp_UsesDeviceTimestampsToNoticeLostFrames()
    {
        var clock = new FrameClock(Rate);
        Span<double> stamps = new double[10];

        // 10 samples at 100 Hz is 100 ms, i.e. 100_000_000 ns between consecutive frames.
        clock.Stamp(10, 1_000_000_000, 1.0, stamps);
        clock.Stamp(10, 1_100_000_000, 1.1, stamps);
        Assert.Equal(0, clock.DroppedFrameCount);

        // The device clock jumped 400 ms: three frames never arrived.
        clock.Stamp(10, 1_500_000_000, 1.2, stamps);
        Assert.Equal(1, clock.DroppedFrameCount);
    }

    [Fact]
    public void Stamp_ToleratesAMissingDeviceTimestamp()
    {
        var clock = new FrameClock(Rate);
        Span<double> stamps = new double[4];

        clock.Stamp(4, 0, 1.0, stamps);
        clock.Stamp(4, 0, 1.04, stamps);

        Assert.Equal(0, clock.DroppedFrameCount);
        Assert.Equal(8, clock.SampleCount);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Constructor_RejectsANonPositiveRate(double rate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrameClock(rate));
    }

    [Fact]
    public void Stamp_RejectsAnUndersizedBuffer()
    {
        var clock = new FrameClock(Rate);

        Assert.Throws<ArgumentException>(() =>
        {
            Span<double> tooSmall = new double[2];
            clock.Stamp(4, 0, 1.0, tooSmall);
        });
    }
}
