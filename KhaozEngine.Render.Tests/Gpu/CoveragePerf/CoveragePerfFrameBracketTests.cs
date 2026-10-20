using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfFrameBracketTests
{
    [Fact]
    public void CompletedOutcomesAreReadInsideAndMatchedOutsideTheAggregate()
    {
        using var run = CoveragePerfSceneProbe.Open(32, covered: true, completeOutcomes: true);
        var first = run.Frame(0);
        var identities = run.Session.Typical.Actors.Select(a =>
        {
            var p = a.Frame.Preparation!;
            return (p.Generation, p.View, p.Actor);
        }).ToArray();
        Assert.Equal(0, run.Session.CompletedOutcomes);
        run.CompletePendingOutcomes();
        var second = run.Frame(1);
        var events = run.Events;
        Assert.InRange(events.IndexOf("outcome-read"), events.IndexOf("aggregate-start") + 1,
            events.IndexOf("aggregate-stop") - 1);
        Assert.All(events.Select((name, index) => (name, index)).Where(e => e.name == "outcome-match"),
            e => Assert.True(e.index > events.IndexOf("aggregate-stop"), string.Join(",", events)));
        Assert.Equal(32, events.Count(e => e == "outcome-match"));
        Assert.Equal(32, run.Session.CompletedOutcomes);
        var outcomes = run.Session.OutcomesForFrame(0).ToArray();
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(identities[i], (outcomes[i].FrameId, outcomes[i].ViewId, outcomes[i].Actor));
            Assert.Equal(CoveragePresentation.Covered, outcomes[i].Outcome.Presentation);
        }
        Assert.Equal(run.TickAtPresentEntry - run.TickAtStart, second.CpuFramePreparationTicks);
        CoveragePerfFrameSample[] samples = [first, second];
        run.Session.CompleteBatch(samples);
        Assert.Equal(64, run.Session.CompletedOutcomes);
        Assert.All(samples, sample => { Assert.True(sample.DeviceComplete); Assert.False(sample.Fallback); Assert.Null(sample.Error); });
    }

    [Fact]
    public void BatchCompletionMatchesTheFullThreeFrameActorBound()
    {
        using var run = CoveragePerfSceneProbe.Open(32, covered: true, completeOutcomes: true);
        run.Session.Typical.Scene.CoverageDiagnosticCompletionHeldForTests = true;
        CoveragePerfFrameSample[] samples = [run.Frame(0), run.Frame(1), run.Frame(2)];
        Assert.Equal(0, run.Session.CompletedOutcomes);
        run.Session.Typical.Scene.CoverageDiagnosticCompletionHeldForTests = false;
        run.Session.CompleteBatch(samples);
        Assert.Equal(96, run.Session.CompletedOutcomes);
        Assert.Equal(96, run.Events.Count(e => e == "outcome-match"));
        Assert.All(samples, sample => { Assert.True(sample.DeviceComplete); Assert.False(sample.Fallback); Assert.Null(sample.Error); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateOutcomesFailOutsideTheAggregate(bool atBatchCompletion)
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true);
        var first = run.Frame(0);
        using var duplicate = run.RepeatCurrentOutcomes();
        run.CompletePendingOutcomes();
        var error = atBatchCompletion
            ? Assert.Throws<InvalidOperationException>(() => run.Session.CompleteBatch([first]))
            : Assert.Throws<InvalidOperationException>(() => run.Frame(1));
        Assert.Equal("Duplicate coverage outcome.", error.Message);
        Assert.True(run.Events.IndexOf("outcome-match") > run.Events.IndexOf("aggregate-stop"));
        Assert.Equal(1, run.Session.CompletedOutcomes);
    }

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
        Assert.Equal(new[] { "host.beginSubmission", "host.endAndPreSubmit", "device.submit", "coverage.resolution", "coverage.surface" }, sample.CpuChildren.Select(s => s.Name));
        Assert.All(sample.CpuChildren, span => Assert.True(span.Ticks > 0));
        Assert.Equal(run.TickAtPresentEntry - run.TickAtStart, sample.CpuFramePreparationTicks);
        Assert.True(sample.CpuChildren.Sum(c => c.Ticks) < sample.CpuFramePreparationTicks);
    }
}
