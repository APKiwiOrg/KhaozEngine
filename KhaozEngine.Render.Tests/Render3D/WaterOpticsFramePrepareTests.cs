using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterOpticsFramePrepareTests
{
    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly Scene3D Scene;
        internal readonly WaterPlane Plane;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal Host()
        {
            Scene = new Scene3D(Device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
            Scene.Post.Starfield = false;
            Scene.Post.RenderScale = RenderScale.MatchViewport;
            Scene.Post.Water.SwellAmplitude = 0f;
            WaterOpticsHandle handle = Scene.LoadWaterOpticsField(new WaterOpticsField(2, 2, [2f, 2f, 2f, 2f]));
            Plane = new WaterPlane(0f, 0f, 0f, 5f,
                optics: new WaterOpticsBinding(handle, new Vector3(-5f, 0f, -5f), new Vector2(10f)));
        }
        internal void Queue() { Scene.Begin(); Scene.DrawWater(Plane); }
        public void Dispose() { Scene.Dispose(); Device.Dispose(); }
    }

    [Fact]
    public void FirstBoundFrameAndResizePrepareTargetsAndPreserveHandles()
    {
        using var h = new Host();
        h.Queue();
        h.Scene.PrepareFrame(128, 96);
        Assert.Equal(128u, h.Scene.ResolvedDepthTarget.Width);
        Assert.Equal(96u, h.Scene.ResolvedDepthTarget.Height);
        IGpuTexture first = h.Scene.ResolvedDepthTarget;
        int sets = h.Factory.ResourceSets.Count;
        h.Scene.PrepareFrame(128, 96);
        Assert.Equal(sets, h.Factory.ResourceSets.Count);
        h.Queue();
        h.Scene.PrepareFrame(160, 120);
        Assert.NotSame(first, h.Scene.ResolvedDepthTarget);
        Assert.Equal(160u, h.Scene.ResolvedDepthTarget.Width);
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;
        h.Queue();
        h.Scene.PrepareFrame(160, 120);
        Assert.Equal(1, h.Scene.WaterOpticsStats.LiveFields);
        Assert.Equal(1, h.Scene.WaterOpticsStats.UploadCount);
        Assert.Throws<InvalidOperationException>(() => h.Scene.DrawWater(h.Plane));
    }

    [Theory]
    [InlineData("viewport")]
    [InlineData("aa")]
    [InlineData("scale")]
    public void ChangedViewportOrTargetSettingsAreRefusedBeforeLateTargetMutation(string change)
    {
        using var h = new Host();
        h.Queue();
        h.Scene.PrepareFrame(128, 96);
        IGpuTexture before = h.Scene.ResolvedDepthTarget;
        int textures = h.Factory.Textures.Count;
        if (change == "aa") h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        if (change == "scale") h.Scene.Post.Supersample = 2f;
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = h.Factory.CreateFramebuffer(null, before);
        Assert.Throws<InvalidOperationException>(() => h.Scene.RenderInternal(commands,
            change == "viewport" ? 160 : 128, 96, target));
        Assert.Same(before, h.Scene.ResolvedDepthTarget);
        Assert.Equal(textures, h.Factory.Textures.Count);
    }

    [Fact]
    public void MissingViewportPreparationIsRefusedBeforeAllocatingTargets()
    {
        using var h = new Host();
        h.Queue();
        int textures = h.Factory.Textures.Count;
        Assert.Throws<InvalidOperationException>(h.Scene.PrepareFrame);
        Assert.Equal(textures, h.Factory.Textures.Count);
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = h.Factory.CreateFramebuffer(null, h.Scene.ResolvedDepthTarget);
        Assert.Throws<InvalidOperationException>(() => h.Scene.RenderInternal(commands, 128, 96, target));
        Assert.Equal(textures, h.Factory.Textures.Count);
        h.Scene.PrepareFrame(128, 96);
    }

    [Fact]
    public void FailedPreparationRetriesAllStagesAndLatchesOnlyAfterSuccess()
    {
        using var h = new Host();
        h.Queue();
        // Fail the bound water resource set, after sizing succeeds and the legacy set is available.
        h.Factory.ThrowOnResourceSetCreate = h.Factory.ResourceSets.Count + 2;
        Assert.Throws<InvalidOperationException>(() => h.Scene.PrepareFrame(128, 96));
        h.Scene.DrawWater(h.Plane);
        h.Factory.ThrowOnResourceSetCreate = 0;
        h.Scene.PrepareFrame(128, 96);
        Assert.Throws<InvalidOperationException>(() => h.Scene.DrawWater(h.Plane));
    }

    // Drive the production GameApp3D seam through the actual pre-record phase, carrying Frame dimensions.
    [Fact]
    public void RealHostPreparationForwardsFrameDimensionsBeforeRecording()
    {
        using var h = new Host();
        using var commands = new NullGpuCommandList();
        void Run(int width, int height)
        {
            using IGpuTexture texture = h.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)width, (uint)height, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            using IGpuFramebuffer target = h.Factory.CreateFramebuffer(null, texture);
            var frame = new Frame { Width = width, Height = height, Commands = commands };
            FramePhases.Run(frame, true, h.Device, commands, KhaozEngine.Primitives.Color.Black,
                onPrepare: f => GameApp3D.PrepareScene(h.Scene, s => s.DrawWater(h.Plane), f.Width, f.Height),
                onFrame: f =>
                {
                    Assert.Equal((uint)f.Width, h.Scene.ResolvedDepthTarget.Width);
                    Assert.Equal((uint)f.Height, h.Scene.ResolvedDepthTarget.Height);
                    int sets = h.Factory.ResourceSets.FindAll(s => s.Resources.Length == 12).Count;
                    h.Scene.PrepareFrame(f.Width, f.Height);
                    h.Scene.RenderInternal(f.Commands, f.Width, f.Height, target);
                    Assert.Equal(sets, h.Factory.ResourceSets.FindAll(s => s.Resources.Length == 12).Count);
                });
        }
        Run(144, 104);
        Run(180, 128);
        Assert.Equal(1, h.Scene.WaterOpticsStats.UploadCount);
    }
}
