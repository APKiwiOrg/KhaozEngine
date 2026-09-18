using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The atlas-binding transaction: its three answers and the failure LATCH behind them.
/// <para>
/// A failed rebuild leaves the previously bound atlas in place, which is the safe half. The dangerous half is
/// that it also leaves the renderer wanting the atlas it just failed on, so a host calling this every frame
/// re-enters the whole transaction, <c>WaitForIdle</c> included, on every frame from then on. A full GPU stall
/// once a frame forever, with nothing on screen saying so, is the failure these tests exist for: the latch turns
/// it into one attempt, and the separate <c>Failed</c> answer is what lets the caller see it at all.
/// </para>
/// </summary>
public sealed class PointShadowAtlasBindTests
{
    [Fact]
    public void BindingTheAtlasAlreadyBoundIsUnchangedAndStallsNothing()
    {
        using var rig = new BindRig();
        int setsBefore = rig.SetCount;
        int waits = rig.Device.WaitForIdleCalls;

        Assert.Equal(PointShadowBindResult.Unchanged, rig.Bind(null));

        Assert.Equal(waits, rig.Device.WaitForIdleCalls);
        Assert.Equal(setsBefore, rig.SetCount);
    }

    [Fact]
    public void ANewAtlasRebuildsEveryTrackedSetAndFreesTheOldOnes()
    {
        using var rig = new BindRig();
        int setsBefore = rig.SetCount;
        int disposedBefore = rig.DisposedSetCount;
        int waits = rig.Device.WaitForIdleCalls;

        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(rig.Atlas));

        Assert.Equal(waits + 1, rig.Device.WaitForIdleCalls);
        // The two default sets plus the one live material set, rebuilt and their originals freed.
        Assert.Equal(3, rig.SetCount - setsBefore);
        Assert.Equal(3, rig.DisposedSetCount - disposedBefore);
        Assert.Equal(PointShadowBindResult.Unchanged, rig.Bind(rig.Atlas));
    }

    [Fact]
    public void AFailedRebuildAnswersFailedAndLeavesThePreviousAtlasBound()
    {
        using var rig = new BindRig();
        rig.FailTheSecondSetCreate();
        int disposedBefore = rig.DisposedSetCount;
        int waits = rig.Device.WaitForIdleCalls;

        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));

        // It got as far as the stall and one allocation, and freed the one replacement it had built.
        Assert.Equal(waits + 1, rig.Device.WaitForIdleCalls);
        Assert.Equal(1, rig.DisposedSetCount - disposedBefore);
        // The 1x1 default is still what every receiver carries, so asking for it again moves nothing.
        Assert.Equal(PointShadowBindResult.Unchanged, rig.Bind(null));
    }

    [Fact]
    public void TheSameFailedAtlasIsNotRetried()
    {
        using var rig = new BindRig();
        rig.FailTheSecondSetCreate();
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));
        rig.StopFailing();
        int setsBefore = rig.SetCount;
        int waits = rig.Device.WaitForIdleCalls;

        // Twice more, and the injected failure is gone: a renderer that re-entered the transaction would succeed
        // here. The latch is what keeps the answer Failed, and it is the reason nothing stalled.
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));

        Assert.Equal(waits, rig.Device.WaitForIdleCalls);
        Assert.Equal(setsBefore, rig.SetCount);
    }

    [Fact]
    public void AskingForAnyOtherTextureClearsTheLatch()
    {
        using var rig = new BindRig();
        rig.FailTheSecondSetCreate();
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));
        rig.StopFailing();

        // A second atlas is a different texture, so it is attempted for real.
        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(rig.SecondAtlas));
        // And with the latch cleared by that success, the one that failed gets its own second chance.
        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(rig.Atlas));
    }

    [Fact]
    public void NullReleasesTheLatchedAtlasBackToTheDefault()
    {
        using var rig = new BindRig();
        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(rig.Atlas));
        rig.FailTheSecondSetCreate();
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.SecondAtlas));
        rig.StopFailing();

        // null is the 1x1 default, which is neither the bound atlas nor the latched one.
        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(null));
        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(rig.SecondAtlas));
    }

    /// <summary>
    /// THE LATCH MUST NOT OUTLIVE THE TEXTURE IT NAMES. A caller whose rebind was refused frees the atlas it was
    /// refused for, and the latch is a reference: left standing it holds a disposed handle reachable for the whole
    /// session, for a request that can never be made again because the object behind it is gone. Forgetting it is
    /// the caller saying so, and it is keyed, so it cannot clear a latch standing for a different texture.
    /// </summary>
    [Fact]
    public void ForgettingADiscardedTextureDropsItsLatchAndLeavesAnyOtherStanding()
    {
        using var rig = new BindRig();
        rig.FailTheSecondSetCreate();
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));

        // A texture the latch does not name changes nothing about it.
        rig.Forget(rig.SecondAtlas);
        int waits = rig.Device.WaitForIdleCalls;
        Assert.Equal(PointShadowBindResult.Failed, rig.Bind(rig.Atlas));
        Assert.Equal(waits, rig.Device.WaitForIdleCalls);

        rig.StopFailing();
        rig.Forget(rig.Atlas);

        // Nothing is being held against this handle any more, so the transaction is entered for real.
        Assert.Equal(PointShadowBindResult.Rebound, rig.Bind(rig.Atlas));
        Assert.Equal(waits + 1, rig.Device.WaitForIdleCalls);
    }

    /// <summary>A renderer over the fake device with one live material set beside its two default ones, plus the
    /// two spare atlas handles the latch tests need. Device-free on purpose: none of this reads a texel.</summary>
    sealed class BindRig : IDisposable
    {
        readonly ModelRenderer _model;
        readonly List<IGpuResourceSet> _liveSets;

        internal BindRig()
        {
            Device = new FakeGpuDevice();
            Factory = (FakeGpuResourceFactory)Device.Factory;
            IGpuTexture target = Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer framebuffer = Factory.CreateFramebuffer(null, target);
            _model = new ModelRenderer(Device, framebuffer.Outputs, 64, 1);
            _liveSets = new List<IGpuResourceSet> { _model.CreateMaterialSet() };
            Atlas = NewAtlas();
            SecondAtlas = NewAtlas();
        }

        internal FakeGpuDevice Device { get; }
        internal FakeGpuResourceFactory Factory { get; }
        internal IGpuTexture Atlas { get; }
        internal IGpuTexture SecondAtlas { get; }
        internal int SetCount => Factory.ResourceSets.Count;
        internal int DisposedSetCount => Factory.DisposedResourceSetCount;

        internal PointShadowBindResult Bind(IGpuTexture? atlas) =>
            _model.BindPointShadowAtlas(atlas, _liveSets, Commit);

        /// <summary>What a caller says as it frees an atlas whose bind was refused.</summary>
        internal void Forget(IGpuTexture atlas) => _model.ForgetPointShadowBindFailure(atlas);

        // The second allocation of the next transaction throws, so one replacement is built and then abandoned.
        internal void FailTheSecondSetCreate() => Factory.ThrowOnResourceSetCreate = Factory.ResourceSets.Count + 2;

        internal void StopFailing() => Factory.ThrowOnResourceSetCreate = 0;

        public void Dispose()
        {
            _model.Dispose();
            Device.Dispose();
        }

        IGpuTexture NewAtlas() => Factory.CreateTexture(GpuTextureDescription.Texture2D(
            384, 64, GpuPixelFormat.R32Float, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

        void Commit(Func<IGpuResourceSet, IGpuResourceSet> replacementFor)
        {
            for (int i = 0; i < _liveSets.Count; i++) _liveSets[i] = replacementFor(_liveSets[i]);
        }
    }
}
