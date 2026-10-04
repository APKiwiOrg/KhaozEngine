using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The client's server tick timeline: ticks stamped on the presentation clock and bracketed at a render time by the
/// same rule the remote samples use, so the answer is the tick the remotes are drawn at.
/// </summary>
[Collection("AllocSensitive")]
public class ServerTickTimelineTests
{
    [Fact]
    public void EmptyTimelineIsUnknown()
    {
        var timeline = new ServerTickTimeline(600);

        Assert.Equal(-1.0, timeline.At(5));
        Assert.Equal(0, timeline.Count);
    }

    [Fact]
    public void BracketsLerpByTrueStamps()
    {
        var timeline = new ServerTickTimeline(600);
        timeline.Record(1.0, 10);
        timeline.Record(1.1, 13);

        Assert.Equal(11.5, timeline.At(1.05), 1e-9);
    }

    [Fact]
    public void BeforeTheOldestClampsAndPastTheNewestHolds()
    {
        var timeline = new ServerTickTimeline(600);
        timeline.Record(1.0, 10);
        timeline.Record(1.1, 13);

        Assert.Equal(10.0, timeline.At(0.5));
        Assert.Equal(13.0, timeline.At(9));
    }

    [Fact]
    public void ASharedStampKeepsTheNewestTick()
    {
        var timeline = new ServerTickTimeline(600);
        timeline.Record(1.0, 10);
        timeline.Record(1.0, 11);

        Assert.Equal(11.0, timeline.At(1.0));
        Assert.Equal(1, timeline.Count);
    }

    [Fact]
    public void PruningKeepsTheAnswer()
    {
        var pruned = new ServerTickTimeline(600);
        for (int i = 0; i < 20; i++) pruned.Record(i * 0.1, 100 + 3 * i);

        for (double renderTime = -0.05; renderTime < 2.2; renderTime += 0.037)
        {
            var fresh = new ServerTickTimeline(600);
            for (int i = 0; i < 20; i++) fresh.Record(i * 0.1, 100 + 3 * i);

            Assert.Equal(fresh.At(renderTime), pruned.At(renderTime), 1e-9);
        }
        Assert.Equal(1, pruned.Count);
    }

    [Fact]
    public void AFullRingKeepsTheNewest()
    {
        var timeline = new ServerTickTimeline(600);
        for (int i = 0; i < 700; i++) timeline.Record(i * 0.01, i);

        Assert.Equal(600, timeline.Count);
        Assert.Equal(100.0, timeline.At(100 * 0.01));
        Assert.Equal(699.0, timeline.At(10));
    }

    [Fact]
    public void RecordAndAtAllocateNothing()
    {
        var timeline = new ServerTickTimeline(600);
        double stamp = 0;
        long tick = 0;
        timeline.Record(stamp, tick);

        AllocAssert.NoPerCallAllocation("ServerTickTimeline.Record and At", () =>
        {
            for (int i = 0; i < 1000; i++)
            {
                stamp += 1.0 / 30.0;
                tick++;
                timeline.Record(stamp, tick);
                _ = timeline.At(stamp - 2.0 / 30.0);
            }
        });
    }
}
