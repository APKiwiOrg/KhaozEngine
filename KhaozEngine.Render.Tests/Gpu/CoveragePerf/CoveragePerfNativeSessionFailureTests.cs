using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfNativeSessionFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadyEvidenceSurvivesALaterResolverFailure(bool phases)
    {
        var layout = phases ? CoveragePerfTimerLayout.Phases : CoveragePerfTimerLayout.Total;
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true, layout: layout);
        run.Session.Typical.Scene.CoverageDiagnosticCompletionHeldForTests = true;
        CoveragePerfFrameSample[] samples = [run.Frame(0), run.Frame(1), run.Frame(2)];
        run.Session.Typical.Scene.CoverageDiagnosticCompletionHeldForTests = false;
        var expected = new InvalidOperationException("resolver failure");
        run.TimestampPools[1].ResolveFailure = expected;

        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => run.Session.CompleteBatch(samples)));

        Assert.Equal(3, run.Session.CompletedOutcomes);
        Assert.All(samples, sample => Assert.True(sample.DeviceComplete));
        Assert.All(samples, sample => Assert.False(sample.Fallback));
        var ready = Assert.IsType<CoveragePerfGpuResult>(samples[0].Gpu);
        Assert.True(ready.Valid);
        Assert.Equal(layout, ready.Layout);
        Assert.Equal<ulong>(layout == CoveragePerfTimerLayout.Total ? [0, 10] : [0, 10, 20, 30, 40], ready.Ticks);
        Assert.Equal(2.5, ready.PeriodNanoseconds);
        Assert.Null(samples[1].Gpu);
        Assert.True(Assert.IsType<CoveragePerfGpuResult>(samples[2].Gpu).Valid);
        Assert.All(samples, sample => Assert.Equal("GPU timestamp resolution or publication failed.", sample.Error));
        Assert.All(run.TimestampPools, pool => Assert.Equal(1, pool.Resolves));
        Assert.Equal(1, run.Session.HarnessDrains);
    }

    [Fact]
    public void MatchedFallbackSurvivesALaterOutcomeMatchFailure()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true);
        run.RefuseOutcomes = true;
        run.Session.Typical.Scene.CoverageDiagnosticCompletionHeldForTests = true;
        CoveragePerfFrameSample[] samples = [run.Frame(0), run.Frame(1)];
        run.Session.Typical.Scene.CoverageDiagnosticCompletionHeldForTests = false;
        var expected = new InvalidOperationException("outcome match failure");
        int matches = 0;
        run.OnTrace = name => { if (name == "outcome-match" && ++matches == 2) throw expected; };

        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => run.Session.CompleteBatch(samples)));

        Assert.Equal(1, run.Session.CompletedOutcomes);
        Assert.True(samples[0].DeviceComplete);
        Assert.True(samples[0].Fallback);
        Assert.False(samples[1].Fallback);
        Assert.Equal(1, run.Session.HarnessDrains);
    }

    [Fact]
    public void PreviouslyMatchedFallbackSurvivesOutcomeBufferFailure()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true);
        run.RefuseOutcomes = true;
        var first = run.Frame(0);
        run.CompletePendingOutcomes();
        CoveragePerfFrameSample[] samples = [first, run.Frame(1)];
        Assert.Equal(1, run.Session.CompletedOutcomes);
        var expected = new InvalidOperationException("outcome buffer failure");
        run.OnTrace = name => { if (name == "outcome-read") throw expected; };

        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => run.Session.CompleteBatch(samples)));

        Assert.True(samples[0].DeviceComplete);
        Assert.True(samples[0].Fallback);
        Assert.Equal(1, run.Session.HarnessDrains);
    }

    [Fact]
    public void ConsumerCallbackFailureStillDisposesTheRingAndEveryLaterResource()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true,
            layout: CoveragePerfTimerLayout.Total);
        var expected = new InvalidOperationException("consumer release failure");
        var consumer = new FailingConsumer(expected, () => run.Trace("consumer-release"));
        IGpuFence? completion = null;
        run.OnReadbacks = frame => Assert.True(frame.Submission.TryRetain(consumer, out completion));
        run.Frame(0);
        var fence = Assert.IsType<SubmissionTestFence>(completion);
        Assert.Equal(3, run.TimestampPools.Count);
        Assert.Equal(new[] { 1, 0, 0 }, run.TimestampPools.Select(pool => pool.Begins));
        run.Events.Clear();
        run.OnTrace = name =>
        {
            if (name is "commands-dispose" or "host-release") throw new InvalidOperationException("later cleanup failure");
        };

        var actual = Record.Exception(run.Dispose);

        Assert.False(consumer.HasPending(fence));
        Assert.Equal(1, fence.DisposeCalls);
        Assert.All(run.TimestampPools, pool => Assert.Equal(1, pool.Disposals));
        AssertLaterResourcesDisposed(run);
        Assert.Equal(new[] { "consumer-release", "timestamp-dispose-0", "timestamp-dispose-1", "timestamp-dispose-2",
            "commands-dispose", "target-dispose", "colour-dispose", "host-release" },
            run.Events.Where(name => name.Contains("dispose", StringComparison.Ordinal)
                || name is "consumer-release" or "host-release"));
        Assert.Same(expected, actual);
    }

    [Fact]
    public void CommandListFailureStillDisposesEveryLaterResource()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true,
            layout: CoveragePerfTimerLayout.Total);
        run.Frame(0);
        run.Events.Clear();
        var expected = new InvalidOperationException("command list disposal failure");
        run.OnTrace = name =>
        {
            if (name == "commands-dispose") throw expected;
            if (name == "host-release") throw new InvalidOperationException("host release failure");
        };

        var actual = Record.Exception(run.Dispose);

        Assert.Equal(3, run.TimestampPools.Count);
        Assert.All(run.TimestampPools, pool => Assert.Equal(1, pool.Disposals));
        AssertLaterResourcesDisposed(run);
        Assert.Same(expected, actual);
    }

    [Fact]
    public void FailedTeardownDrainKeepsRetainedPoolsAndAttemptsLaterResources()
    {
        using var run = CoveragePerfSceneProbe.Open(1, covered: true, completeOutcomes: true,
            layout: CoveragePerfTimerLayout.Total);
        var expected = new InvalidOperationException("teardown drain failure");
        var consumer = new FailingConsumer(expected, () => run.Trace("consumer-release"));
        IGpuFence? completion = null;
        run.OnReadbacks = frame => Assert.True(frame.Submission.TryRetain(consumer, out completion));
        run.Frame(0);
        var fence = Assert.IsType<SubmissionTestFence>(completion);
        Assert.Equal(3, run.TimestampPools.Count);
        Assert.Equal(new[] { 1, 0, 0 }, run.TimestampPools.Select(pool => pool.Begins));
        run.Events.Clear();
        bool failDrain = true;
        run.OnTrace = name =>
        {
            if (name == "drain" && failDrain) { failDrain = false; throw expected; }
            if (name == "commands-dispose") throw new InvalidOperationException("command list disposal failure");
        };

        var actual = Record.Exception(run.Dispose);

        Assert.True(consumer.HasPending(fence));
        Assert.Equal(0, fence.DisposeCalls);
        Assert.All(run.TimestampPools, pool => Assert.Equal(0, pool.Disposals));
        Assert.DoesNotContain(run.Events, name => name.StartsWith("timestamp-dispose-", StringComparison.Ordinal));
        AssertLaterResourcesDisposed(run);
        Assert.Equal(0, fence.DisposeCalls);
        Assert.All(run.TimestampPools, pool => Assert.Equal(0, pool.Disposals));
        Assert.Same(expected, actual);
    }

    static void AssertLaterResourcesDisposed(CoveragePerfSceneProbe run)
    {
        Assert.Equal(1, run.Events.Count(name => name == "commands-dispose"));
        Assert.NotEmpty(run.Session.Typical.Actors);
        Assert.All(run.Session.Typical.Actors, actor => Assert.True(actor.Conformer.Disposed));
        Assert.NotEmpty(run.Resources.Framebuffers);
        Assert.All(run.Resources.Framebuffers, target => Assert.True(target.Disposed));
        Assert.NotEmpty(run.Resources.Textures);
        Assert.All(run.Resources.Textures, texture => Assert.True(texture.Disposed));
        Assert.Contains("host-release", run.Events);
        run.Dispose();
        Assert.Equal(1, run.Events.Count(name => name == "host-release"));
    }

    sealed class FailingConsumer(Exception failure, Action release) : IGpuFrameCompletionConsumer
    {
        bool _pending = true;
        public bool HasPending(IGpuFence completion) => _pending;
        public void DiscardUnsubmitted(IGpuFence completion) => _pending = false;
        public void ReleaseCompleted(IGpuFence completion) { release(); _pending = false; throw failure; }
    }
}
