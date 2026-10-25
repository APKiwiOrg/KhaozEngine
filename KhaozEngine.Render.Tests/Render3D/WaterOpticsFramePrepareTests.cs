using System;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterOpticsFramePrepareTests
{
    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device;
        internal readonly Scene3D Scene;
        internal readonly WaterPlane Plane;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal Host(int maxMsaaSampleCount = 1)
        {
            Device = new FakeGpuDevice(maxMsaaSampleCount: maxMsaaSampleCount);
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

    static void ChangeAfterPreparation(Scene3D scene, string change)
    {
        if (change == "hdr") scene.Post.Hdr.Enabled = !scene.Post.Hdr.Enabled;
        if (change == "msaa") scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
        if (change == "grid") scene.Post.Water.GridMode = WaterGridMode.Clipmap;
    }

    // Only bound water freezes its inputs at preparation. A frame with no bound water keeps the legacy late
    // binding on the viewport form too: targets resize and the clipmap program is created while recording.
    [Theory]
    [InlineData("hdr")]
    [InlineData("msaa")]
    [InlineData("grid")]
    public void UnboundViewportPreparedFrameRendersAfterTargetOrGridChanges(string change)
    {
        using var device = new FakeGpuDevice(maxMsaaSampleCount: 4);
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Starfield = false;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Begin();
        scene.DrawWater(new WaterPlane(0f, 0f, 0f, 5f, look: new WaterLook { SwellAmplitude = .01f, SwellSteepness = 0f }));
        scene.PrepareFrame(128, 96);
        int clipRequests = factory.ShaderRequests.FindAll(r => r.VertexGlsl == ShaderSources.WaterClipmapVert).Count;
        ChangeAfterPreparation(scene, change);
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        int textures = factory.Textures.Count;
        scene.RenderInternal(commands, 128, 96, target);
        // The late binding really ran: targets were rebuilt while recording, or the clipmap program was created.
        if (change != "grid") Assert.True(factory.Textures.Count > textures);
        if (change == "msaa") Assert.Equal(4, scene.ModelSampleCountForTests);
        if (change == "grid")
            Assert.Equal(clipRequests + 1, factory.ShaderRequests.FindAll(r => r.VertexGlsl == ShaderSources.WaterClipmapVert).Count);
    }

    [Theory]
    [InlineData("hdr")]
    [InlineData("msaa")]
    [InlineData("grid")]
    public void BoundViewportPreparedFrameRefusesTargetOrGridChanges(string change)
    {
        using var h = new Host(maxMsaaSampleCount: 4);
        h.Queue();
        h.Scene.PrepareFrame(128, 96);
        ChangeAfterPreparation(h.Scene, change);
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = h.Factory.CreateFramebuffer(null, h.Scene.ResolvedDepthTarget);
        Assert.Throws<InvalidOperationException>(() => h.Scene.RenderInternal(commands, 128, 96, target));
    }

    // DrawWater admits the absolute plane. Preparation must revalidate the same double-precision rectangle, not
    // the separately reduced plane and binding origins, or a non-grid override refuses an admitted plane.
    [Fact]
    public void NonGridRenderOriginRevalidatesTheAbsoluteAdmissionAtPreparation()
    {
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Starfield = false;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Water.SwellAmplitude = 0f;
        var origin = new Vector3(.1f, 0f, .1f);
        scene.RenderOrigin = origin;
        // Zero slack in X: the certificate is exactly the absolute rectangle [90, 110].
        var field = new WaterOpticsTriangleField(new(200f), 1, 1,
            [new(new(0, -1, 0), new(200, -1, 0), new(200, -1, 200)), new(new(0, -1, 0), new(200, -1, 200), new(0, -1, 200))],
            [new(new(90f, 0f), new(110f, 200f))]);
        WaterOpticsHandle handle = scene.LoadWaterOpticsTriangles(field);
        var plane = new WaterPlane(100f, 0f, 100f, 10f, optics: new WaterOpticsBinding(handle, Vector3.Zero, new Vector2(200f)));
        var reduced = new WaterPlane(100f - origin.X, 0f, 100f - origin.Z, 10f,
            optics: new WaterOpticsBinding(handle, -origin, new Vector2(200f)));
        // Non-vacuity: the reduced floats round past the certificate.
        Assert.Throws<ArgumentException>(() => WaterOpticsCoverage.Validate(reduced, scene.Post.Water,
            new KhaozEngine.Render3D.Rendering.WaterOpticsPayload(field)));
        scene.Begin();
        Assert.True(scene.RenderOriginActive);
        scene.DrawWater(plane);
        scene.PrepareFrame(64, 64);
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
