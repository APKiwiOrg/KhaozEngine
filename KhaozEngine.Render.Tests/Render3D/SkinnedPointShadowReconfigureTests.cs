using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SkinnedPointShadowReconfigureTests
{
    [Fact]
    public void OneRendererClearsEachAtlasOnItsOwnFirstUse()
    {
        using var device = new FakeGpuDevice();
        using PointShadowAtlas baseAtlas = Assert.IsType<PointShadowAtlas>(
            PointShadowAtlas.TryCreate(device, 32, 4));
        using PointShadowAtlas transientAtlas = Assert.IsType<PointShadowAtlas>(
            PointShadowAtlas.TryCreate(device, 32, 1));
        using var palette = new SkinnedBonePalette(device);
        using var renderer = new PointShadowRenderer(device, palette.Layout);
        using IGpuCommandList commands = device.Factory.CreateCommandList();
        commands.Begin();

        renderer.BeginPass(commands, baseAtlas);
        Assert.True(baseAtlas.IsCleared);
        Assert.False(transientAtlas.IsCleared);
        renderer.BeginPass(commands, transientAtlas);
        Assert.True(transientAtlas.IsCleared);
        commands.End();
    }

    [Fact]
    public void NoDemandLeavesTheTransientAtlasUnallocated()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        long baseBytes = rig.Scene.ResolvedPointShadows.BaseAtlasBytes;

        rig.Scene.Begin();

        Assert.Equal(0, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
        Assert.Equal(0, rig.Scene.ResolvedPointShadows.TransientAtlasBytes);
        Assert.Equal(baseBytes, rig.Scene.ResolvedPointShadows.TotalAtlasBytes);
    }

    [Fact]
    public void FirstDemandIsPendingThenAllocatesExactRowsAtTheNextBoundary()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        int pipelines = rig.Factory.GraphicsPipelines.Count;

        rig.Scene.RecordPointShadowTransientDemand(1);
        Assert.Equal(0, rig.Scene.ResolvedPointShadows.TransientAtlasRows);

        rig.Scene.Begin();

        Assert.Equal(1, rig.Scene.PointShadowTransientRows);
        Assert.Equal(1, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
        Assert.Equal(3_538_944L, rig.Scene.ResolvedPointShadows.TransientAtlasBytes);
        Assert.Equal(pipelines, rig.Factory.GraphicsPipelines.Count);
    }

    [Fact]
    public void CompatibleLayoutKeepsItsHighWaterWhenDemandFallsToZero()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        rig.Scene.RecordPointShadowTransientDemand(3);
        rig.Scene.Begin();
        IGpuTexture transient = Assert.IsType<FakeTexture>(rig.Scene.PointShadowTransientTexture);

        rig.Scene.Begin();

        Assert.Same(transient, rig.Scene.PointShadowTransientTexture);
        Assert.Equal(3, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
    }

    [Fact]
    public void GrowthKeepsTheOldRowUntilTheNextBoundaryThenUsesExactDemand()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        rig.Scene.RecordPointShadowTransientDemand(1);
        rig.Scene.Begin();
        FakeTexture old = Assert.IsType<FakeTexture>(rig.Scene.PointShadowTransientTexture);
        rig.Scene.RecordPointShadowTransientDemand(3);
        Assert.Equal(1, rig.Scene.PointShadowTransientRows);

        rig.Scene.Begin();

        Assert.Equal(3, rig.Scene.PointShadowTransientRows);
        Assert.True(old.Disposed);
        Assert.Same(rig.Scene.PointShadowTransientTexture, rig.Scene.BoundPointShadowTransientTexture);
    }

    [Fact]
    public void DemandIsCappedByTheLiveBaseRows()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        rig.Scene.RecordPointShadowTransientDemand(20);

        rig.Scene.Begin();

        Assert.Equal(8, rig.Scene.PointShadowTransientRows);
        Assert.Equal(8, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
    }

    [Fact]
    public void TextureRefusalIsLatchedUntilRowDemandChanges()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        IGpuTexture defaultTransient = rig.Scene.BoundPointShadowTransientTexture;
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;
        rig.Scene.RecordPointShadowTransientDemand(2);
        rig.Scene.Begin();
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.Same(defaultTransient, rig.Scene.BoundPointShadowTransientTexture);
        rig.Factory.ThrowOnTextureCreate = 0;
        int textures = rig.Factory.Textures.Count;

        rig.Scene.RecordPointShadowTransientDemand(2);
        rig.Scene.Begin();
        Assert.Equal(textures, rig.Factory.Textures.Count);
        Assert.Equal(0, rig.Scene.PointShadowTransientRows);

        rig.Scene.RecordPointShadowTransientDemand(3);
        rig.Scene.Begin();
        Assert.Equal(3, rig.Scene.PointShadowTransientRows);
        Assert.False(rig.Scene.ResolvedPointShadows.Degraded);
    }

    [Fact]
    public void FramebufferRefusalDisposesBothCandidateTextures()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        int textureCount = rig.Factory.Textures.Count;
        rig.Factory.ThrowOnFramebufferCreate = rig.Factory.Framebuffers.Count + 1;
        rig.Scene.RecordPointShadowTransientDemand(1);

        rig.Scene.Begin();

        Assert.Equal(0, rig.Scene.PointShadowTransientRows);
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.Equal(textureCount + 2, rig.Factory.Textures.Count);
        Assert.All(rig.Factory.Textures.Skip(textureCount), texture => Assert.True(texture.Disposed));
    }

    [Fact]
    public void ReceiverSetRefusalKeepsTheOldPairAndFreesTheCandidate()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        IGpuTexture oldBase = rig.Scene.BoundPointShadowTexture;
        IGpuTexture oldTransient = rig.Scene.BoundPointShadowTransientTexture;
        int textureCount = rig.Factory.Textures.Count;
        rig.Factory.ThrowOnResourceSetCreate = rig.Factory.ResourceSets.Count + 1;
        rig.Scene.RecordPointShadowTransientDemand(1);

        rig.Scene.Begin();

        Assert.Same(oldBase, rig.Scene.BoundPointShadowTexture);
        Assert.Same(oldTransient, rig.Scene.BoundPointShadowTransientTexture);
        Assert.Equal(0, rig.Scene.PointShadowTransientRows);
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.All(rig.Factory.Textures.Skip(textureCount), texture => Assert.True(texture.Disposed));
        Assert.Equal(0, rig.Factory.LiveSetsNamingAFreedTexture);
    }

    [Fact]
    public void ChangedFaceResolutionRetriesARefusedTransientShape()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 1;
        rig.Scene.RecordPointShadowTransientDemand(1);
        rig.Scene.Begin();
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        rig.Factory.ThrowOnTextureCreate = 0;
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { FaceResolution = 128 });
        rig.Scene.RecordPointShadowTransientDemand(1);

        rig.Scene.Begin();

        Assert.Equal(128, rig.Scene.ResolvedPointShadows.FaceResolution);
        Assert.Equal(1, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
        Assert.False(rig.Scene.ResolvedPointShadows.Degraded);
    }

    [Fact]
    public void DisableReleasesBothAtlasesAndRestoresTheWhiteTransientDefault()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        IGpuTexture defaultTransient = rig.Scene.BoundPointShadowTransientTexture;
        rig.Scene.RecordPointShadowTransientDemand(1);
        rig.Scene.Begin();
        FakeTexture transient = Assert.IsType<FakeTexture>(rig.Scene.PointShadowTransientTexture);
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });

        rig.Scene.Begin();

        Assert.True(transient.Disposed);
        Assert.Equal(0, rig.Scene.ResolvedPointShadows.TotalAtlasBytes);
        Assert.Same(defaultTransient, rig.Scene.BoundPointShadowTransientTexture);
        Assert.False(rig.Scene.ResolvedPointShadows.Enabled);
    }

    [Fact]
    public void BaseReplacementBindRefusalKeepsBothOldAtlases()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        rig.Scene.RecordPointShadowTransientDemand(1);
        rig.Scene.Begin();
        IGpuTexture oldBase = rig.Scene.BoundPointShadowTexture;
        IGpuTexture oldTransient = rig.Scene.BoundPointShadowTransientTexture;
        int textureCount = rig.Factory.Textures.Count;
        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { MaxShadowedLights = 16 });
        rig.Scene.RecordPointShadowTransientDemand(2);
        rig.Factory.ThrowOnResourceSetCreate = rig.Factory.ResourceSets.Count + 2;

        rig.Scene.Begin();

        Assert.Same(oldBase, rig.Scene.BoundPointShadowTexture);
        Assert.Same(oldTransient, rig.Scene.BoundPointShadowTransientTexture);
        Assert.Equal(8, rig.Scene.ResolvedPointShadows.MaxShadowedLights);
        Assert.Equal(1, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.All(rig.Factory.Textures.Skip(textureCount), texture => Assert.True(texture.Disposed));
        Assert.Equal(0, rig.Factory.LiveSetsNamingAFreedTexture);
    }

    [Fact]
    public void SceneDisposalFreesBothLiveAtlases()
    {
        using var rig = new ReconfigureRig();
        rig.WarmBase();
        rig.Scene.RecordPointShadowTransientDemand(1);
        rig.Scene.Begin();
        FakeTexture baseAtlas = Assert.IsType<FakeTexture>(rig.Scene.PointShadowTexture);
        FakeTexture transientAtlas = Assert.IsType<FakeTexture>(rig.Scene.PointShadowTransientTexture);

        rig.DisposeScene();

        Assert.True(baseAtlas.Disposed);
        Assert.True(transientAtlas.Disposed);
    }

    [Fact]
    public void BaseShrinkToEightDropsOversizedTransientWhenReplacementAllocationFails()
    {
        using var rig = new ReconfigureRig();
        rig.Settings.PointShadows.MaxShadowedLights = 60;
        rig.WarmBase();
        IGpuTexture transientDefault = rig.Scene.BoundPointShadowTransientTexture;
        rig.Scene.RecordPointShadowTransientDemand(60);
        rig.Scene.Begin();
        FakeTexture oldTransient = Assert.IsType<FakeTexture>(rig.Scene.PointShadowTransientTexture);
        Assert.Equal(60, rig.Scene.ResolvedPointShadows.TransientAtlasRows);

        rig.Scene.RequestPointShadowSettings(new PointShadowSettings { MaxShadowedLights = 8 });
        rig.Scene.RecordPointShadowTransientDemand(8);
        rig.Factory.ThrowOnTextureCreate = rig.Factory.Textures.Count + 3;
        rig.Scene.Begin();

        Assert.Equal(8, rig.Scene.ResolvedPointShadows.MaxShadowedLights);
        Assert.Equal(0, rig.Scene.ResolvedPointShadows.TransientAtlasRows);
        Assert.Equal(rig.Scene.ResolvedPointShadows.BaseAtlasBytes,
            rig.Scene.ResolvedPointShadows.TotalAtlasBytes);
        Assert.True(rig.Scene.ResolvedPointShadows.Degraded);
        Assert.Same(transientDefault, rig.Scene.BoundPointShadowTransientTexture);
        Assert.True(oldTransient.Disposed);
        Assert.NotSame(transientDefault, oldTransient);
    }

    sealed class ReconfigureRig : IDisposable
    {
        readonly IGpuTexture _targetTexture;
        readonly IGpuFramebuffer _target;
        readonly MeshHandle _mesh;
        bool _sceneDisposed;

        internal ReconfigureRig()
        {
            Device = new FakeGpuDevice();
            Factory = (FakeGpuResourceFactory)Device.Factory;
            _targetTexture = Factory.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = Factory.CreateFramebuffer(null, _targetTexture);
            Settings = new ShadowSettings { Mode = ShadowMode.Off };
            Scene = new Scene3D(Device, _target.Outputs, Settings);
            _mesh = Scene.LoadMesh(MeshPrimitives.Box(1f));
        }

        internal FakeGpuDevice Device { get; }
        internal FakeGpuResourceFactory Factory { get; }
        internal ShadowSettings Settings { get; }
        internal Scene3D Scene { get; }

        internal void WarmBase()
        {
            RenderFrame();
            RenderFrame();
        }

        void RenderFrame()
        {
            Scene.Begin();
            Scene.Draw(_mesh, Matrix4x4.Identity);
            Scene.AddLight(new Vector3(0f, 2f, 0f), Color.White, 10f, 1f, LightShadow.Static(1));
            Scene.PrepareFrame();
            using IGpuCommandList commands = Factory.CreateCommandList();
            commands.Begin();
            Scene.RenderInternal(commands, 16, 16, _target);
            commands.End();
        }

        internal void DisposeScene()
        {
            if (_sceneDisposed) return;
            Scene.Dispose();
            _sceneDisposed = true;
        }

        public void Dispose()
        {
            DisposeScene();
            _target.Dispose();
            _targetTexture.Dispose();
            Device.Dispose();
        }
    }
}
