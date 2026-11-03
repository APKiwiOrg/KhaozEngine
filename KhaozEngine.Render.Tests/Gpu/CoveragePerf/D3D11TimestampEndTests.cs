using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

[Collection("AllocSensitive")]
public sealed partial class D3D11TimestampEndTests
{
    [Fact]
    public void ImmediateAndDeferredTailsHaveTheSameProgramAfterEveryDraw()
    {
        using var immediate = new D3D11TimestampEndFixture(false);
        using var deferred = new D3D11TimestampEndFixture(true);
        foreach (var rig in new[] { immediate, deferred })
        {
            rig.Start();
            rig.Arm();
            rig.List.Draw(3);
            rig.List.DrawIndexed(6, 2, 0, 0, 0);
            rig.List.Dispatch(1, 1, 1);
            rig.Api.OnEmitterEnd = () =>
            {
                Assert.Equal(new[] { "End(1)", "End(disjoint)" }, rig.Api.Calls.TakeLast(2));
                Assert.False(rig.IsSealed);
            };
            rig.List.End();
            if (rig.Deferred is { } recording)
            {
                Assert.Empty(rig.Api.Calls);
                Assert.True(recording.Emitter.Stream.Sealed);
                Assert.Equal(new[] { D3D11OpCode.Draw, D3D11OpCode.DrawIndexed, D3D11OpCode.Dispatch,
                    D3D11OpCode.WriteTimestamp, D3D11OpCode.EndTimestamps },
                    recording.Emitter.Stream.Ops.ToArray().TakeLast(5).Select(op => op.Code));
            }
            // The immediate callback ran before sealing. Replay sees the already sealed deferred stream.
            rig.Api.OnEmitterEnd = null;
            rig.Submit();
            Assert.Equal(new[] { "Begin(disjoint)", "End(0)", "Dispatch(1,1,1)", "End(1)", "End(disjoint)" }, rig.Api.Calls);
        }
        Assert.Equal(immediate.Log.Trace, deferred.Log.Trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArmValidatesBeforeStoringAndRefusesManualFinalization(bool deferred)
    {
        using var rig = new D3D11TimestampEndFixture(deferred);
        using var foreign = new D3D11TimestampEndFixture(false);
        var tail = Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List);
        Assert.Equal(GpuRecordingStateRefusal.CommandOutsideRecording, Assert.Throws<GpuRecordingStateException>(() => tail.FinishTimestampsOnEnd(rig.Pool, 1)).Reason);
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.WrongRecording, Assert.Throws<GpuTimestampStateException>(() => tail.FinishTimestampsOnEnd(rig.Pool, 1)).Reason);
        Assert.Throws<ArgumentNullException>(() => tail.FinishTimestampsOnEnd(null!, 1));
        Assert.Equal(GpuTimestampRefusal.ForeignPool, Assert.Throws<GpuTimestampArgumentException>(() => tail.FinishTimestampsOnEnd(foreign.Pool, 1)).Reason);
        rig.List.BeginTimestamps(rig.Pool, 2);
        var indexError = Assert.Throws<GpuTimestampRangeException>(() => rig.Arm(0));
        Assert.Equal(GpuTimestampRefusal.FinalIndexRequired, indexError.Reason);
        Assert.Equal("finalIndex", indexError.ParamName);
        Assert.Null(indexError.ActualValue);
        Assert.Equal(1u, indexError.FinalIndex);
        Assert.Equal(2u, indexError.Count);
        Assert.Equal(GpuTimestampRefusal.TailPrefixIncomplete, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        rig.List.WriteTimestamp(rig.Pool, 0);
        rig.Arm();
        Assert.Equal(GpuTimestampRefusal.TailAlreadyArmed, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        Assert.Equal(GpuTimestampRefusal.ManualFinalizationWhileArmed, Assert.Throws<GpuTimestampStateException>(() => rig.List.WriteTimestamp(rig.Pool, 1)).Reason);
        var manual = Assert.Throws<GpuTimestampStateException>(() => rig.List.EndTimestamps(rig.Pool));
        Assert.Equal(GpuTimestampRefusal.ManualFinalizationWhileArmed, manual.Reason);
        Assert.Equal(GpuBackendKind.Direct3D11Native, manual.Backend);
        Assert.Equal("EndTimestamps", manual.Command);
        rig.List.End();
        rig.Submit();
        Assert.Equal(new[] { "Begin(disjoint)", "End(0)", "End(1)", "End(disjoint)" }, rig.Api.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlreadyWrittenFinalAndOtherListOrRecordingAreRefused(bool deferred)
    {
        using var rig = new D3D11TimestampEndFixture(deferred);
        rig.Start();
        rig.List.WriteTimestamp(rig.Pool, 1);
        Assert.Equal(GpuTimestampRefusal.TailPrefixIncomplete, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        rig.List.End();
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.WrongRecording, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        using var pool = rig.NewPool();
        using var other = new D3D11CommandRecorder<D3D11TimestampTestEmitter>(rig.Emitter, rig.Fences);
        other.Begin();
        other.BeginTimestamps(pool, 2);
        other.WriteTimestamp(pool, 0);
        Assert.Equal(GpuTimestampRefusal.WrongRecording, Assert.Throws<GpuTimestampStateException>(() => Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List).FinishTimestampsOnEnd(pool, 1)).Reason);
        other.End();
        rig.List.End();
    }

    [Fact]
    public void MissingCapabilityRefusesArming()
    {
        using var rig = new D3D11TimestampEndFixture(false);
        using var list = new D3D11CommandRecorder<D3D11TimestampTestEmitter>(rig.Emitter);
        list.Begin();
        Assert.Equal(GpuTimestampRefusal.TimestampUnsupported, Assert.Throws<GpuTimestampUnsupportedException>(() => Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(list).FinishTimestampsOnEnd(rig.Pool, 1)).Reason);
        list.End();
    }

    [Theory]
    [InlineData("tail-write")]
    [InlineData("batch-end")]
    public void FailedTailAbandonsImmediateRecordingAndQuarantinesPool(string failure)
    {
        using var rig = new D3D11TimestampEndFixture(false);
        rig.Start();
        rig.Arm();
        var expected = new InvalidOperationException(failure);
        rig.Api.OnEnd = query =>
        {
            if ((failure == "tail-write" && query is int index && index == 1)
                || (failure == "batch-end" && query is string)) throw expected;
        };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(rig.List.End));
        Assert.False(rig.IsRecording);
        Assert.False(rig.IsSealed);
        int calls = rig.Api.Calls.Count;
        Assert.Equal(GpuRecordingStateRefusal.SubmissionWithoutSeal, Assert.Throws<GpuRecordingStateException>(rig.Submit).Reason);
        Assert.Equal(GpuRecordingStateRefusal.EndWithoutRecording, Assert.Throws<GpuRecordingStateException>(rig.List.End).Reason);
        Assert.Equal(calls, rig.Api.Calls.Count);
        rig.Api.OnEnd = null;
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.UnresolvedBatch, Assert.Throws<GpuTimestampStateException>(() => rig.List.BeginTimestamps(rig.Pool, 2)).Reason);
        rig.List.End();
        Assert.Equal(calls, rig.Api.Calls.Count);
        using var replacement = rig.NewPool();
        rig.List.Begin();
        rig.List.BeginTimestamps(replacement, 1);
        Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List).FinishTimestampsOnEnd(replacement, 0);
        rig.List.End();
    }

    [Fact]
    public void DeferredTailFailureDropsStreamReferencesAndNeverReplaysAbandonedDraws()
    {
        using var rig = new D3D11TimestampEndFixture(true);
        rig.Start();
        rig.Arm();
        rig.List.Draw(3);
        rig.Pool.Dispose();
        Assert.Throws<ObjectDisposedException>(rig.List.End);
        Assert.False(rig.IsRecording);
        Assert.False(rig.IsSealed);
        Assert.Equal(0, rig.Deferred!.Emitter.Stream.Count);
        Assert.Equal(0, rig.Deferred.Emitter.Stream.ReferenceCount);
        Assert.False(rig.Deferred.Emitter.Stream.Sealed);
        Assert.Equal(GpuRecordingStateRefusal.SubmissionWithoutSeal, Assert.Throws<GpuRecordingStateException>(rig.Submit).Reason);
        Assert.Empty(rig.Api.Calls);
        Assert.Equal(GpuRecordingStateRefusal.EndWithoutRecording, Assert.Throws<GpuRecordingStateException>(rig.List.End).Reason);
        rig.List.Begin();
        rig.List.End();
        rig.Submit();
        Assert.Equal(new[] { "ClearState()" }, rig.Log.Trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryEndLeavesTheProgramUnchanged(bool deferred)
    {
        using var rig = new D3D11TimestampEndFixture(deferred);
        rig.List.Begin();
        rig.List.Dispatch(1, 1, 1);
        rig.List.End();
        rig.Submit();
        Assert.Equal(new[] { "Dispatch(1,1,1)" }, rig.Api.Calls);
        Assert.Equal(new[] { "ClearState()", "Dispatch(1,1,1)" }, rig.Log.Trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArmingAndTailAllocateNothing(bool deferred)
    {
        using var rig = new D3D11TimestampEndFixture(deferred);
        rig.Api.Record = false;
        using var fence = (D3D11GpuFence)rig.Fences.CreateFence();
        fence.Arm(1);
        rig.Timeline.Completed = 1;
        ulong[] ticks = new ulong[2];
        rig.Start();
        rig.Arm();
        rig.List.End();
        Assert.Equal(GpuTimestampStatus.Ready, rig.Pool.TryResolve(fence, ticks, out _));
        rig.Start();
        var tail = Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List);
        Assert.Equal(0, AllocationMeasurement.Measure("D3D11 arm and timestamp tail", () =>
        {
            tail.FinishTimestampsOnEnd(rig.Pool, 1);
            rig.List.End();
        }));
        Assert.Equal(0, rig.Timeline.Flushes);
        Assert.Equal(0, rig.Timeline.Waits);
    }
}
