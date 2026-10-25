using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

public sealed partial class GpuPerfTimestampRingTests
{
    [Fact]
    public void DisposalBeforeTeardownCannotFreeARetainedPool()
    {
        using var rig = new Rig();
        rig.Record(0);

        var retained = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Ring.Dispose).Failures);
        Assert.Equal("renderer.session.dispose.timestamps.retained", retained.Stage);
        Assert.IsType<InvalidOperationException>(retained.Error);

        Assert.Equal(new[] { 0, 1, 1 }, rig.Device.Pools.Select(pool => pool.Disposals));
        Assert.True(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.Throws<ObjectDisposedException>(() =>
            rig.Ring.TryBegin(rig.Commands, rig.Submission, 1, 2u, out _));
        rig.Ring.Dispose();
        Assert.Equal(new[] { 0, 1, 1 }, rig.Device.Pools.Select(pool => pool.Disposals));
        Assert.Equal(new[] { 1, 0, 0 }, rig.Device.Pools.Select(pool => pool.Begins));
        Assert.All(rig.Device.Pools, pool => Assert.Equal(0, pool.Resolves));
        Assert.Equal(0, rig.Device.Fences[0].ResetCalls);

        rig.Submission.Dispose();

        Assert.False(rig.Ring.HasPending(rig.Device.Fences[0]));
        Assert.All(rig.Device.Pools, pool => Assert.Equal(1, pool.Disposals));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateRetentionReleaseDisposesTheQuarantinedPoolExactlyOnce(bool discard)
    {
        using var rig = new Rig();
        if (discard) rig.Begin(0);
        else rig.Record(0);
        var fence = rig.Device.Fences[0];
        var retained = Assert.Single(Assert.Throws<GpuDiagnosticFailureException>(rig.Ring.Dispose).Failures);
        Assert.Equal("renderer.session.dispose.timestamps.retained", retained.Stage);
        Assert.IsType<InvalidOperationException>(retained.Error);

        if (discard)
        {
            rig.Submission.Abandon();
            rig.Ring.DiscardUnsubmitted(fence);
        }
        else
        {
            rig.Device.WaitForIdle();
            rig.Ring.ReleaseCompleted(fence);
            rig.Ring.ReleaseCompleted(fence);
        }

        Assert.All(rig.Device.Pools, pool => Assert.Equal(1, pool.Disposals));
        Assert.False(rig.Ring.HasPending(fence));
        Assert.Null(rig.Ring.ActivePool);
        Assert.Equal(0, fence.ResetCalls);
        Assert.Equal(new[] { 1, 0, 0 }, rig.Device.Pools.Select(pool => pool.Begins));
        Assert.All(rig.Device.Pools, pool => Assert.Equal(0, pool.Resolves));
        rig.Ring.Dispose();
        Assert.All(rig.Device.Pools, pool => Assert.Equal(1, pool.Disposals));
    }

    [Fact]
    public void PoolDisposalFailureDoesNotSkipOtherPoolsAndPreservesTheFirstFailure()
    {
        using var rig = new Rig();
        var expected = new InvalidOperationException("first pool disposal failure");
        rig.Device.Pools[0].OnDispose = () => throw expected;
        var later = new InvalidOperationException("later pool disposal failure");
        rig.Device.Pools[1].OnDispose = () => throw later;

        var actual = Record.Exception(rig.Ring.Dispose);

        Assert.All(rig.Device.Pools, pool => Assert.Equal(1, pool.Disposals));
        var transport = Assert.IsType<GpuDiagnosticFailureException>(actual);
        Assert.Same(expected, transport.Failures[0].Error);
        Assert.Same(later, transport.Failures[1].Error);
        rig.Ring.Dispose();
        Assert.All(rig.Device.Pools, pool => Assert.Equal(1, pool.Disposals));
    }

    [Fact]
    public void RepeatedDisposalDoesNotDisposeAnyPoolTwice()
    {
        using var rig = new Rig();

        rig.Ring.Dispose();
        rig.Ring.Dispose();

        Assert.Equal(3, rig.Device.Pools.Count);
        Assert.All(rig.Device.Pools, pool => Assert.Equal(1, pool.Disposals));
    }
}
