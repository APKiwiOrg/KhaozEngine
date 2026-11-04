using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

[Collection("AllocSensitive")]
public sealed partial class MetalTimestampEndTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TailFollowsClearFlushAndEncoderClosureBeforeSeal(bool stage)
    {
        using var rig = new MetalTimestampEndFixture(stage);
        rig.Start();
        rig.PendingClear();
        rig.Arm();
        rig.Native.Trace.Clear();
        rig.Render.BeforeCreate = () => rig.Native.Trace.Add("clear-flush");
        rig.Calls.BeforeBegin = kind => rig.Native.Trace.Add($"begin {kind}");
        rig.Calls.AfterEnd = kind => rig.Native.Trace.Add($"end {kind}");
        rig.Native.BeforeBoundary = () =>
        {
            Assert.False(rig.List.IsSealed);
            Assert.False(rig.List.Passes.HasPendingClears);
            Assert.Equal(MetalEncoderKind.None, rig.List.Encoders.Open);
        };
        rig.Native.OnCall = call => Assert.False(rig.List.IsSealed);
        rig.List.End();
        Assert.Equal(new[] { "clear-flush", "begin Render", "end Render", "signal 3", "wait 3" }, rig.Native.Trace.Take(5));
        Assert.Equal(stage
            ? new[] { "stage blit", "copy 4", "end blit", "signal 4", "wait 4", "blit", "resolve 2", "end blit" }
            : new[] { "blit", "copy 4", "sample 1 barrier=True", "end blit", "signal 4", "wait 4", "blit", "resolve 2", "end blit" }, rig.Native.Trace.Skip(5));
        Assert.True(rig.List.IsSealed);
        Assert.False(rig.List.IsRecording);
        Assert.Equal(MetalEncoderKind.None, rig.List.Encoders.Open);
        Assert.Equal(0, rig.Native.OpenBlits);
        Assert.Equal(0, rig.Calls.OutstandingEncoders);
        Assert.Equal(0ul, rig.Harness.Timeline.LastAllocated);
        Assert.Equal(0, rig.Harness.Event.WaitCount);
    }

    [Fact]
    public void ArmValidatesBeforeStoringAndRefusesManualFinalization()
    {
        using var rig = new MetalTimestampEndFixture();
        using var foreign = new MetalTimestampEndFixture();
        var tail = Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List);
        Assert.Equal(GpuRecordingStateRefusal.CommandOutsideRecording, Assert.Throws<GpuRecordingStateException>(() => rig.Arm()).Reason);
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.WrongRecording, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        Assert.Throws<ArgumentNullException>(() => tail.FinishTimestampsOnEnd(null!, 1));
        Assert.Equal(GpuResourceOwnershipRefusal.DifferentDevice, Assert.Throws<GpuResourceOwnershipArgumentException>(() => tail.FinishTimestampsOnEnd(foreign.Pool, 1)).Reason);
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
        ulong epoch = rig.List.Encoders.Epoch;
        Assert.Equal(GpuTimestampRefusal.ManualFinalizationWhileArmed, Assert.Throws<GpuTimestampStateException>(() => rig.List.WriteTimestamp(rig.Pool, 1)).Reason);
        var manual = Assert.Throws<GpuTimestampStateException>(() => rig.List.EndTimestamps(rig.Pool));
        Assert.Equal(GpuTimestampRefusal.ManualFinalizationWhileArmed, manual.Reason);
        Assert.Equal(GpuBackendKind.MetalNative, manual.Backend);
        Assert.Equal("EndTimestamps", manual.Command);
        Assert.Equal(epoch, rig.List.Encoders.Epoch);
        rig.List.End();
    }

    [Fact]
    public void AlreadyWrittenFinalAndOtherListOrRecordingAreRefused()
    {
        using var rig = new MetalTimestampEndFixture();
        rig.Start();
        rig.List.WriteTimestamp(rig.Pool, 1);
        Assert.Equal(GpuTimestampRefusal.TailPrefixIncomplete, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        rig.List.End();
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.WrongRecording, Assert.Throws<GpuTimestampStateException>(() => rig.Arm()).Reason);
        using var pool = rig.NewPool();
        using var other = rig.Harness.NewList(new object(), timestampTimeline: rig.Harness.Timeline);
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
        using var rig = new MetalTimestampEndFixture(capability: false);
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.TimestampUnsupported, Assert.Throws<GpuTimestampUnsupportedException>(() => rig.Arm()).Reason);
        rig.List.End();
    }

    [Theory]
    [InlineData("clear-flush")]
    [InlineData("render-begin")]
    [InlineData("render-end")]
    [InlineData("encoder-close")]
    [InlineData("tail-write")]
    [InlineData("batch-end")]
    [InlineData("tail-close")]
    public void FailedEndAbandonsRecordingReleasesBufferAndQuarantinesPool(string failure)
    {
        using var rig = new MetalTimestampEndFixture();
        rig.Start();
        rig.Arm();
        rig.PendingClear();
        if (failure == "render-end") rig.List.Passes.PrepareDraw();
        if (failure == "encoder-close")
        {
            rig.List.Passes.PrepareDraw();
            rig.List.Encoders.EnsureComputeEncoder();
        }
        var expected = new InvalidOperationException(failure);
        rig.Render.BeforeCreate = () => { if (failure == "clear-flush") throw expected; };
        rig.Calls.BeforeBegin = kind => { if (failure == "render-begin") throw expected; };
        rig.Calls.AfterEnd = kind =>
        {
            if ((failure == "render-end" && kind == MetalEncoderKind.Render)
                || (failure == "encoder-close" && kind == MetalEncoderKind.Compute)) throw expected;
        };
        rig.Native.OnCall = call =>
        {
            if ((failure == "tail-write" && call == "copy") || (failure == "batch-end" && call == "resolve")
                || (failure == "tail-close" && call == "end blit")) throw expected;
        };
        IntPtr abandoned = rig.Buffers.Acquired[0];
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(rig.List.End));
        Assert.False(rig.List.IsRecording);
        Assert.False(rig.List.IsSealed);
        Assert.Equal(GpuRecordingStateRefusal.SubmissionWithoutSeal, Assert.Throws<GpuRecordingStateException>(rig.Submit).Reason);
        Assert.Equal(GpuRecordingStateRefusal.EndWithoutRecording, Assert.Throws<GpuRecordingStateException>(rig.List.End).Reason);
        Assert.Equal(new[] { abandoned }, rig.Buffers.Released);
        Assert.Equal(0, rig.Uncommitted.Outstanding);
        Assert.Equal(0, rig.Native.OpenBlits);
        Assert.Equal(0, rig.Calls.OutstandingEncoders);
        Assert.Equal(0, rig.Render.OutstandingDescriptors);
        Assert.Equal(0ul, rig.Harness.Timeline.LastAllocated);
        Assert.Equal(KhaozEngine.Gpu.Metal.MetalEncoderRefusal.RecordingMissing,
            Assert.Throws<KhaozEngine.Gpu.Metal.MetalEncoderStateException>(() => rig.List.Encoders.EnsureBlitEncoder()).Reason);
        rig.Render.BeforeCreate = null;
        rig.Calls.BeforeBegin = null;
        rig.Calls.AfterEnd = null;
        rig.Native.OnCall = null;
        int markerCount = rig.Native.Signals.Count;
        rig.List.Begin();
        Assert.False(rig.List.Passes.HasPendingClears);
        Assert.Equal(GpuTimestampRefusal.UnresolvedBatch, Assert.Throws<GpuTimestampStateException>(() => rig.List.BeginTimestamps(rig.Pool, 2)).Reason);
        rig.List.End();
        Assert.Equal(markerCount, rig.Native.Signals.Count);
        Assert.NotEqual(abandoned, rig.List.SealedCommandBuffer);
        using var replacement = rig.NewPool();
        rig.List.Begin();
        rig.List.BeginTimestamps(replacement, 1);
        Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List).FinishTimestampsOnEnd(replacement, 0);
        rig.List.End();
    }

    [Fact]
    public void OrdinaryEndLeavesTheProgramUnchanged()
    {
        using var rig = new MetalTimestampEndFixture();
        rig.List.Begin();
        rig.List.Encoders.EnsureBlitEncoder();
        rig.Native.Trace.Clear();
        rig.Calls.AfterEnd = kind => rig.Native.Trace.Add($"end {kind}");
        rig.List.End();
        Assert.Equal(new[] { "end Blit" }, rig.Native.Trace);
        Assert.Empty(rig.Native.Signals);
        rig.List.Begin();
        rig.Native.Trace.Clear();
        rig.PendingClear();
        rig.Render.BeforeCreate = () => rig.Native.Trace.Add("clear-flush");
        rig.Calls.BeforeBegin = kind => rig.Native.Trace.Add($"begin {kind}");
        rig.List.End();
        Assert.Equal(new[] { "clear-flush", "begin Render", "end Render" }, rig.Native.Trace);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArmingAndTailAllocateNothing(bool stage)
    {
        using var rig = new MetalTimestampEndFixture(stage);
        rig.Native.Record = false;
        using var fence = rig.Harness.Timeline.CreateFence();
        fence.Arm(1);
        rig.Harness.Event.Completed = 1;
        ulong[] ticks = new ulong[2];
        rig.Start();
        rig.Arm();
        rig.List.End();
        Assert.Equal(GpuTimestampStatus.Ready, rig.Pool.TryResolve(fence, ticks, out _));
        rig.Start();
        var tail = Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(rig.List);
        int allocations = rig.Native.Allocations;
        Assert.Equal(0, AllocationMeasurement.Measure("Metal arm and timestamp tail", () =>
        {
            tail.FinishTimestampsOnEnd(rig.Pool, 1);
            rig.List.End();
        }));
        Assert.Equal(allocations, rig.Native.Allocations);
        Assert.Equal(0, rig.Native.OpenBlits);
        Assert.Equal(0ul, rig.Harness.Timeline.LastAllocated);
        Assert.Equal(0, rig.Harness.Event.WaitCount);
    }
}
