using PolarBLE.Core.Acquisition;
using Xunit;

namespace PolarBLE.Core.Tests;

public class StreamStatisticsTests
{
    [Fact]
    public void RecordFrame_AccumulatesSamplesAndGaps()
    {
        var statistics = new StreamStatistics(new ManualTimeProvider());

        statistics.RecordFrame(73, samplesLostBefore: 0);
        statistics.RecordFrame(73, samplesLostBefore: 146);
        statistics.RecordUnsupportedFrame();

        var snapshot = statistics.Snapshot();
        Assert.Equal(2, snapshot.Frames);
        Assert.Equal(146, snapshot.Samples);
        Assert.Equal(1, snapshot.Gaps);
        Assert.Equal(146, snapshot.SamplesLost);
        Assert.Equal(1, snapshot.UnsupportedFrames);
        Assert.True(snapshot.HasDataLoss);
    }

    [Fact]
    public void IdleTime_CountsFromCreationUntilTheFirstFrame()
    {
        var clock = new ManualTimeProvider();
        var statistics = new StreamStatistics(clock);

        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.True(statistics.Snapshot().IsStalled(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void IdleTime_RestartsWithEachFrame()
    {
        var clock = new ManualTimeProvider();
        var statistics = new StreamStatistics(clock);

        clock.Advance(TimeSpan.FromSeconds(10));
        statistics.RecordFrame(1, samplesLostBefore: 0);
        clock.Advance(TimeSpan.FromSeconds(1));

        var snapshot = statistics.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(1), snapshot.IdleTime);
        Assert.False(snapshot.IsStalled(TimeSpan.FromSeconds(3)));
        Assert.False(snapshot.HasDataLoss);
    }
}
