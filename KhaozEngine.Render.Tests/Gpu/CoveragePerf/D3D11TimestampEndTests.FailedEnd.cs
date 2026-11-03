using KhaozEngine.Gpu;
using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

public sealed partial class D3D11TimestampEndTests
{
    [Fact]
    public void FailedEmitterEndAbandonsImmediateRecording()
    {
        using var rig = new D3D11TimestampEndFixture(false);
        rig.Start();
        var expected = new InvalidOperationException("emitter-end");
        rig.Api.OnEmitterEnd = () => throw expected;
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(rig.List.End));
        Assert.False(rig.IsRecording);
        Assert.False(rig.IsSealed);
        int calls = rig.Api.Calls.Count;
        Assert.Equal(GpuRecordingStateRefusal.SubmissionWithoutSeal, Assert.Throws<GpuRecordingStateException>(rig.Submit).Reason);
        Assert.Equal(GpuRecordingStateRefusal.EndWithoutRecording, Assert.Throws<GpuRecordingStateException>(rig.List.End).Reason);
        Assert.Equal(calls, rig.Api.Calls.Count);
        rig.Api.OnEnd = null;
        rig.Api.OnEmitterEnd = null;
        rig.List.Begin();
        Assert.Equal(GpuTimestampRefusal.UnresolvedBatch, Assert.Throws<GpuTimestampStateException>(() => rig.List.BeginTimestamps(rig.Pool, 2)).Reason);
        rig.List.End();
        Assert.Equal(calls, rig.Api.Calls.Count);
    }
}
