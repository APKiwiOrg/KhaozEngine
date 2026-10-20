using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfFrameBracketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateIncludesBeginPrepareUploadsEndAndSubmitExactlyOnce(bool covered)
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered);
        var warmup = run.Warm();
        Assert.Equal(3, warmup[0].ProductionWaits);
        var sample = run.Frame(CoveragePerfManifest.WarmupFrameCount);
        Assert.True(sample.CpuFramePreparationTicks > 0);
        var events = run.Events;
        Assert.Equal("aggregate-start", events[0]);
        Assert.Equal("aggregate-stop", events[^2]);
        Assert.Equal("present-forward", events[^1]);
        foreach (string name in new[] { "scene-begin", "pose-and-stage", "prepare", "commands-begin", "render",
            "readbacks", "commands-end", "submit-span", "history-submitted" })
        {
            Assert.Equal(1, events.Count(e => e == name));
            Assert.InRange(events.IndexOf(name), 1, events.Count - 3);
        }
        Assert.True(events.IndexOf("prepare") < events.IndexOf("commands-begin"));
        Assert.True(events.IndexOf("readbacks") < events.IndexOf("commands-end"));
        Assert.True(events.IndexOf("submit-span") < events.IndexOf("history-submitted"));
        Assert.True(run.UploadCalls > 0);
        Assert.Equal(1, sample.Submissions);
        Assert.Equal(0, sample.ProductionWaits);
        Assert.Equal(run.TickAtPresentEntry - run.TickAtStart, sample.CpuFramePreparationTicks);
        Assert.True(run.Ticks > run.TickAtPresentEntry);
        Assert.True(run.HistoryCommittedAtPresent);
        if (!covered) Assert.Equal(1, run.ParticipantsAtReadback);
    }

    [Fact]
    public void NestedResolutionAndSurfaceNeverIncreaseAggregate()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true);
        var sample = run.Frame(0);
        Assert.False(sample.CpuChildren.IsDefaultOrEmpty);
        Assert.Equal(new[] { "coverage.resolution", "coverage.surface" }, sample.CpuChildren.Select(s => s.Name));
        Assert.All(sample.CpuChildren, span => Assert.True(span.Ticks > 0));
        Assert.Equal(run.TickAtPresentEntry - run.TickAtStart, sample.CpuFramePreparationTicks);
        Assert.True(sample.CpuChildren.Sum(c => c.Ticks) < sample.CpuFramePreparationTicks);
    }
}
