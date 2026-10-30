using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ReleaseProbeLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SplitOutputStillSurfacesTheReleasedPassObserverFailure(bool tail)
    {
        using var rig = new PreparedSceneOutputTests.Rig();
        var error = new InvalidOperationException("observer failed");
        rig.Scene.PassObserver = new ThrowingPass(tail ? "post.blit" : "post.uniforms", error);
        rig.Scene.Begin();
        rig.Scene.PrepareFrame();
        if (tail)
        {
            long token = rig.Scene.PrepareOutput(rig.Prefix, 32, 24);
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
                rig.Scene.RenderPreparedOutput(rig.Tail, 32, 24, rig.Output, token)).InnerException);
            Assert.False(rig.Scene.IsPreparedOutputCurrent(token));
        }
        else
        {
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
                rig.Scene.PrepareOutput(rig.Prefix, 32, 24)).InnerException);
        }
        Assert.Same(error, rig.Scene.PassObserverFailure);
    }

    [Fact]
    public void FailedDrainRetainsThePrefixOwnerUntilSceneDisposalCanSafelyComplete()
    {
        using var rig = new PreparedSceneOutputTests.Rig();
        var prefix = new PrefixOwner();
        var error = new InvalidOperationException("drain failed");
        rig.Device.OnWaitForIdle = () => throw error;
        try
        {
            Assert.ThrowsAny<Exception>(() => Render3DSurface.DisposeSceneAndPrefix(rig.Scene, prefix));
            Assert.False(rig.Scene.DisposalSafeForDependants);
            Assert.Equal(0, prefix.Disposals);
        }
        finally { rig.Device.OnWaitForIdle = null; }
        Render3DSurface.DisposeSceneAndPrefix(rig.Scene, prefix);
        Assert.True(rig.Scene.DisposalSafeForDependants);
        Assert.Equal(1, prefix.Disposals);
    }

    sealed class PrefixOwner : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    sealed class ThrowingPass(string id, Exception error) : IRenderPassObserver
    {
        public void BeginPass(IGpuCommandList commands, RenderPassKey key) { if (key.Id == id) throw error; }
        public void EndPass(IGpuCommandList commands, RenderPassKey key) { }
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) { }
        public void BeginCpuPhase(string phase) { }
        public void EndCpuPhase(string phase) { }
    }
}
