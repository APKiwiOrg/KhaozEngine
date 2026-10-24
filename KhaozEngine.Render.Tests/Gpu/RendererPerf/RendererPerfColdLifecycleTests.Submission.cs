using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfColdLifecycleTests
{
    [Fact]
    public void Actual_abandon_preserves_participant_discard_and_pending_events_at_unit_outcome()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var operational = new InvalidOperationException("prepare");
        var participant = new InvalidOperationException("participant");
        var repeated = new InvalidOperationException("independent repeated discard");
        var pending = new InvalidOperationException("pending");
        Frame? frame = null;
        rig.SessionCreated = session => frame = SessionFrame(session);
        var first = new EventConsumer { DiscardError = repeated, QueryError = new GpuDiagnosticFailureException([new("native.pending.origin", pending)]) };
        var second = new EventConsumer { DiscardError = repeated };
        rig.Boundary = stage =>
        {
            if (stage != "frame") return;
            Assert.True(frame!.Submission.TryRegister(new EventParticipant(participant)));
            Assert.True(frame.Submission.TryRetain(first, out _));
            Assert.True(frame.Submission.TryRetain(second, out _));
            throw operational;
        };
        var outcome = rig.Run();
        Assert.Equal(new[] { "renderer.frame", "renderer.frame.submission.participant.0", "renderer.frame.submission.discard.0.0",
            "renderer.frame.submission.discard.0.1", "native.pending.origin" }, outcome.Failures.Select(f => f.Stage));
        Exception[] expected = [operational, participant, repeated, repeated, pending];
        Assert.Equal(expected.Length, outcome.Failures.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Same(expected[i], outcome.Failures[i].Error);
        Assert.Equal(1, first.Discards); Assert.Equal(1, second.Discards);
        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
        Assert.Equal(0, rig.Production.Submissions); Assert.Equal(0, rig.Diagnostic.CompilerReads);
        _output.WriteLine("abandon actual stages={0}; repeatedReference={1}", string.Join(",", outcome.Failures.Select(f => f.Stage)),
            ReferenceEquals(outcome.Failures[2].Error, outcome.Failures[3].Error));
    }

    [Fact]
    public void Actual_begin_queries_from_multiple_retained_frames_preserve_every_event_at_unit_outcome()
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var repeated = new InvalidOperationException("same query independently thrown");
        var tail = new InvalidOperationException("query tail");
        var first = new EventConsumer { QueryError = repeated };
        var second = new EventConsumer { QueryError = Owned("native.begin.query.origin", repeated, tail, true) };
        Frame? frame = null;
        IGpuFence? a = null, b = null;
        int prepared = 0;
        rig.SessionCreated = session => frame = SessionFrame(session);
        rig.Boundary = stage =>
        {
            if (stage != "frame") return;
            if (prepared == 0) Assert.True(frame!.Submission.TryRetain(first, out a));
            if (prepared == 1) Assert.True(frame!.Submission.TryRetain(second, out b));
            prepared++;
        };
        var outcome = rig.Run();
        Assert.Equal(3, outcome.Failures.Length);
        Assert.Equal(new[] { "renderer.frame.submission.pending.0.0", "native.begin.query.origin", "native.begin.query.origin.tail" },
            outcome.Failures.Select(f => f.Stage));
        Assert.Same(repeated, outcome.Failures[0].Error); Assert.Same(repeated, outcome.Failures[1].Error); Assert.Same(tail, outcome.Failures[2].Error);
        Assert.Equal(3, prepared); Assert.Equal(3, rig.Production.Submissions);
        Assert.Equal(0, ((SubmissionTestFence)a!).ResetCalls); Assert.Equal(0, ((SubmissionTestFence)b!).ResetCalls);
        Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
        Assert.Equal(0, rig.Diagnostic.CompilerReads);
        _output.WriteLine("Begin actual stages={0}; submissions=3 repeatedReference=true", string.Join(",", outcome.Failures.Select(f => f.Stage)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_notify_keeps_origin_with_one_or_two_owned_events(bool two)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var first = new InvalidOperationException("notify first"); var second = new InvalidOperationException("notify second");
        var transport = Owned("native.notify.origin", first, second, two);
        Frame? frame = null;
        rig.SessionCreated = session => frame = SessionFrame(session);
        rig.Boundary = stage => { if (stage == "frame") Assert.True(frame!.Submission.TryRegister(new EventParticipant(transport, submitted: true))); };
        var outcome = rig.Run();
        Assert.Equal(two ? 2 : 1, outcome.Failures.Length);
        Assert.Equal("native.notify.origin", outcome.Failures[0].Stage); Assert.Same(first, outcome.Failures[0].Error);
        if (two) { Assert.Equal("native.notify.origin.tail", outcome.Failures[1].Stage); Assert.Same(second, outcome.Failures[1].Error); }
        Assert.Equal(1, rig.Production.Submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_submission_teardown_keeps_single_or_multiple_drain_origins(bool two)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var frame = new InvalidOperationException("frame");
        var first = new InvalidOperationException("drain first"); var second = new InvalidOperationException("drain second");
        int drains = 0;
        rig.Boundary = stage =>
        {
            if (stage == "frame") throw frame;
            if (stage == "production.drain" && ++drains == 2) throw Owned("native.drain.origin", first, second, two);
        };
        var outcome = rig.Run();
        Assert.Equal(two ? 3 : 2, outcome.Failures.Length);
        Assert.Same(frame, outcome.Failures[0].Error);
        Assert.Equal("native.drain.origin", outcome.Failures[1].Stage); Assert.Same(first, outcome.Failures[1].Error);
        if (two) { Assert.Equal("native.drain.origin.tail", outcome.Failures[2].Stage); Assert.Same(second, outcome.Failures[2].Error); }
        Assert.DoesNotContain("production.dispose.shader", rig.Trace);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Actual_timestamp_poll_and_pool_ordinal_survive_one_or_two_events(bool dispose, bool two)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var first = new InvalidOperationException("timestamp first"); var second = new InvalidOperationException("timestamp second");
        bool armed = false;
        rig.Boundary = stage =>
        {
            if (stage != "frame" || armed) return;
            armed = true;
            if (dispose)
            {
                rig.Production.Pools[1].OnDispose = () => throw first;
                if (two) rig.Production.Pools[2].OnDispose = () => throw second;
                throw new InvalidOperationException("stop before workload record");
            }
            rig.Production.Pools[0].ResolveFailure = Owned("native.timestamp.poll.origin", first, second, two);
        };
        var outcome = rig.Run();
        int offset = dispose ? 1 : 0;
        Assert.Equal((two ? 2 : 1) + offset, outcome.Failures.Length);
        Assert.Equal(dispose ? "renderer.session.dispose.timestamps.1" : "native.timestamp.poll.origin", outcome.Failures[offset].Stage);
        Assert.Same(first, outcome.Failures[offset].Error);
        if (two)
        {
            Assert.Equal(dispose ? "renderer.session.dispose.timestamps.2" : "native.timestamp.poll.origin.tail", outcome.Failures[offset + 1].Stage);
            Assert.Same(second, outcome.Failures[offset + 1].Error);
        }
        Assert.All(rig.Production.Pools, pool => Assert.Equal(1, pool.Disposals));
        _output.WriteLine("timestamp dispose={0} two={1} actual stages={2}", dispose, two, string.Join(",", outcome.Failures.Select(f => f.Stage)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_timestamp_teardown_preserves_already_owned_origin_for_one_or_two_events(bool two)
    {
        using var rig = new RendererPerfColdLifecycleFixture();
        var operational = new InvalidOperationException("frame");
        var first = new InvalidOperationException("owned pool origin"); var second = new InvalidOperationException("owned pool tail");
        rig.Boundary = stage =>
        {
            if (stage != "frame") return;
            rig.Production.Pools[1].OnDispose = () => throw Owned("native.pool.one.origin", first, second, two);
            throw operational;
        };
        var outcome = rig.Run();
        Assert.Equal(two ? 3 : 2, outcome.Failures.Length);
        Assert.Same(operational, outcome.Failures[0].Error);
        Assert.Equal("native.pool.one.origin", outcome.Failures[1].Stage); Assert.Same(first, outcome.Failures[1].Error);
        if (two) { Assert.Equal("native.pool.one.origin.tail", outcome.Failures[2].Stage); Assert.Same(second, outcome.Failures[2].Error); }
        Assert.All(rig.Production.Pools, pool => Assert.Equal(1, pool.Disposals));
    }

    static Frame SessionFrame(RendererPerfNativeSession session) =>
        (Frame)typeof(RendererPerfNativeSession).GetField("_frame", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
    static GpuDiagnosticFailureException Owned(string stage, Exception first, Exception second, bool two) =>
        new(two ? [new(stage, first), new(stage + ".tail", second)] : [new(stage, first)]);
    sealed class EventParticipant(Exception error, bool submitted = false) : IGpuFrameSubmissionParticipant
    {
        public void Submitted() { if (submitted) throw error; }
        public void Abandoned() { if (!submitted) throw error; }
    }
    sealed class EventConsumer : IGpuFrameCompletionConsumer
    {
        internal Exception? DiscardError, QueryError;
        internal int Discards, Releases;
        public void DiscardUnsubmitted(IGpuFence completion) { Discards++; if (DiscardError is { } error) throw error; }
        public bool HasPending(IGpuFence completion)
        { if (QueryError is { } error) { QueryError = null; throw error; } return Releases == 0; }
        public void ReleaseCompleted(IGpuFence completion) => Releases++;
    }
}
