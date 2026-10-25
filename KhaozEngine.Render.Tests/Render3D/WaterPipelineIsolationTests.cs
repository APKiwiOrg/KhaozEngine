using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WaterPipelineIsolationTests
{
    // Observe factory requests and the handles actually bound, rather than a catalog's advertised names.
    static bool Triangle(string fragment) => fragment.Contains("bool tryOpticsTriangle(", StringComparison.Ordinal);

    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly RenderResources Targets;
        internal readonly GpuRetireQueue Retired;
        internal readonly WaterRenderer Renderer;
        internal readonly RecordingGpuCommandList Commands = new(new NullGpuCommandList()) { CaptureBindings = true };
        internal readonly WaterSettings Settings = new() { SwellAmplitude = 0f, ClipmapLevels = 1, ClipmapRingCells = 8 };
        internal WaterPlane[] Planes = [];
        internal readonly WaterOpticsHandle Triangles, Bilinear;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal Host()
        {
            Targets = new RenderResources(Device, 96, 64, false);
            Retired = GpuRetireQueue.CreateFrameCounted(Device, 2);
            Renderer = new WaterRenderer(Device, Targets.ColorDepthFB.Outputs, Retired);
            Triangles = Renderer.Optics.LoadTriangles(WaterOpticsTriangleResourceTests.Field());
            Bilinear = Renderer.Optics.Load(new WaterOpticsField(2, 2, [1, 1, 1, 1]));
        }
        internal WaterPlane Plane(WaterOpticsHandle handle = default, float amplitude = 0f) =>
            new(1, 0, 1, .25f, look: new WaterLook { SwellAmplitude = amplitude, SwellSteepness = 0f },
                optics: handle.IsValid ? new(handle, Vector3.Zero, new Vector2(2)) : default);
        internal void Prepare()
        {
            var frame = new FramePrepare(Settings, Planes, 0);
            Renderer.PrepareFrame(frame);
            Renderer.PrepareOpticsFrame(Targets, frame);
        }
        internal void Draw()
        {
            Commands.Clear();
            Renderer.Draw(Commands, Targets, Planes, WaterTestFrames.TopDown, WaterTestFrames.TopDown,
                -Vector3.UnitY, Color.White, Vector3.UnitY * 100, Settings, new SkySettings(), 0);
        }
        internal FakeShaderRequest[] TriangleRequests() => Factory.ShaderRequests.Where(r => Triangle(r.FragmentGlsl)).ToArray();
        public void Dispose()
        {
            Renderer.Dispose(); Retired.Dispose(); Targets.Dispose(); Commands.Dispose(); Device.Dispose();
        }
    }

    [Fact]
    public void SceneConstructionLegacyAndBilinearNeverRequestTrianglePrograms()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        Assert.DoesNotContain(factory.ShaderRequests, r => Triangle(r.FragmentGlsl));
        scene.Post.Starfield = false;
        scene.Post.Water.SwellAmplitude = 0;
        WaterOpticsHandle field = scene.LoadWaterOpticsField(new(2, 2, [1, 1, 1, 1]));
        scene.Begin();
        scene.DrawWater(new(1, 0, 1, .25f));
        scene.DrawWater(new(1, 0, 1, .25f, optics: new(field, Vector3.Zero, new Vector2(2))));
        scene.PrepareFrame(96, 64);
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        scene.RenderInternal(commands, 96, 64, target);
        Assert.DoesNotContain(factory.ShaderRequests, r => Triangle(r.FragmentGlsl));
    }

    [Fact]
    public void LegacyNoArgumentPreparationSurvivesOutputReplacementWithoutLateShaderRequests()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Hdr.Enabled = false;
        scene.Post.Water.GridMode = WaterGridMode.Clipmap;
        scene.Begin();
        scene.DrawWater(new(0, 0, 0, 2));
        scene.PrepareFrame();
        int requests = factory.ShaderRequests.Count;
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = factory.CreateFramebuffer(null, scene.ResolvedDepthTarget);
        scene.RenderInternal(commands, 96, 64, target);
        Assert.Equal(requests, factory.ShaderRequests.Count);
        Assert.DoesNotContain(factory.ShaderRequests, r => Triangle(r.FragmentGlsl));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TrianglePreparationRequestsOnlyTheRequiredGeometryAndWarmUseRequestsNothing(bool clipmap, bool flat)
    {
        using var h = new Host();
        Assert.Empty(h.TriangleRequests());
        int coldRequests = h.Factory.ShaderRequests.Count;
        h.Settings.GridMode = clipmap ? WaterGridMode.Clipmap : WaterGridMode.CameraFocused;
        h.Planes = [h.Plane(h.Triangles, flat ? 0 : .01f)];
        h.Prepare();
        FakeShaderRequest request = Assert.Single(h.TriangleRequests());
        Assert.Equal(coldRequests + 1, h.Factory.ShaderRequests.Count);
        Assert.Equal(clipmap && !flat ? ShaderSources.WaterClipmapVert : ShaderSources.WaterVert, request.VertexGlsl);
        int requests = h.Factory.ShaderRequests.Count;
        h.Draw();
        h.Prepare(); h.Draw();
        Assert.Equal(requests, h.Factory.ShaderRequests.Count);
        Assert.Single(h.Commands.IndexedDraws);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameGeometryAlternatingKindsAndReversalKeepPipelineSetAndSlotOrder(bool clipmap)
    {
        using var h = new Host();
        h.Settings.GridMode = clipmap ? WaterGridMode.Clipmap : WaterGridMode.CameraFocused;
        h.Planes = [h.Plane(default, .01f), h.Plane(h.Triangles, .01f), h.Plane(h.Bilinear, .01f), h.Plane(h.Triangles, .01f)];
        Check();
        Array.Reverse(h.Planes);
        Check();
        void Check()
        {
            h.Prepare(); h.Draw();
            Assert.Equal(4, h.Commands.Bindings.Count);
            for (int i = 0; i < 4; i++)
            {
                var draw = h.Commands.Bindings[i];
                var pipeline = Assert.IsType<FakePipeline>(draw.Pipeline);
                bool triangle = h.Planes[i].Optics.Field == h.Triangles;
                Assert.Equal(triangle, Triangle(pipeline.Request!.Value.FragmentGlsl));
                Assert.Equal(clipmap ? ShaderSources.WaterClipmapVert : ShaderSources.WaterVert, pipeline.Request.Value.VertexGlsl);
                Assert.Equal((uint)i * 1280, draw.Sets[0].DynamicOffset);
                var set = Assert.IsType<FakeResourceSet>(draw.Sets[0].Set);
                if (h.Planes[i].Optics.IsBound)
                    Assert.Same(h.Renderer.Optics.Resolve(h.Planes[i].Optics.Field).Texture, set.Resources[10]);
                Assert.Equal(1280u, ((GpuBufferRange)set.Resources[6]).Size);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedOptionalCreationLeavesFrameUnpreparedDisposesPartialAndRetries(bool shaderFailure)
    {
        using var h = new Host();
        h.Planes = [h.Plane(h.Triangles)];
        int shaders = h.Factory.ShaderSets.Count;
        if (shaderFailure) h.Factory.ThrowOnShaderCreate = h.Factory.ShaderRequests.Count + 1;
        else h.Factory.ThrowOnPipelineCreate = h.Factory.GraphicsPipelines.Count + 1;
        Assert.Throws<InvalidOperationException>(h.Prepare);
        Assert.Throws<InvalidOperationException>(h.Draw);
        if (!shaderFailure) Assert.Equal(1, h.Factory.ShaderSets[shaders].DisposeCount);
        h.Factory.ThrowOnShaderCreate = h.Factory.ThrowOnPipelineCreate = 0;
        h.Prepare(); h.Draw();
        Assert.True(Triangle(Assert.IsType<FakePipeline>(h.Commands.IndexedDraws[0].Pipeline).Request!.Value.FragmentGlsl));
    }

    [Fact]
    public void ResizeTouchesOnlyCreatedVariantsRollsBackFailureAndDefersOldPipelineDisposal()
    {
        using var h = new Host();
        h.Planes = [h.Plane(h.Triangles)];
        h.Prepare(); h.Draw();
        FakePipeline previous = Assert.IsType<FakePipeline>(h.Commands.IndexedDraws[0].Pipeline);
        int requests = h.Factory.ShaderRequests.Count, count = h.Factory.GraphicsPipelines.Count;
        h.Factory.ThrowOnPipelineCreate = count + 2;
        Assert.Throws<InvalidOperationException>(() => h.Renderer.SetOutputs(h.Targets.ColorDepthFB.Outputs));
        Assert.False(previous.Disposed);
        Assert.True(h.Factory.Pipelines[^1].Disposed);
        Assert.Throws<InvalidOperationException>(h.Draw);
        h.Factory.ThrowOnPipelineCreate = 0;
        h.Prepare(); h.Draw();
        Assert.NotSame(previous, h.Commands.IndexedDraws[0].Pipeline);
        count = h.Factory.GraphicsPipelines.Count;
        h.Renderer.SetOutputs(h.Targets.ColorDepthFB.Outputs);
        Assert.Equal(count + 2, h.Factory.GraphicsPipelines.Count);
        Assert.Equal(requests, h.Factory.ShaderRequests.Count);
        Assert.False(previous.Disposed);
        h.Retired.BeginFrame(); Assert.False(previous.Disposed);
        h.Retired.BeginFrame(); Assert.True(previous.Disposed);
        h.Prepare(); h.Draw();
        Assert.NotSame(previous, h.Commands.IndexedDraws[0].Pipeline);
    }

    [Fact]
    public void FailedOutputReplacementRetriesDuringPreparationBeforeNewTargetsCanDraw()
    {
        using var h = new Host();
        h.Planes = [h.Plane(h.Triangles)]; h.Prepare(); h.Draw();
        FakePipeline previous = Assert.IsType<FakePipeline>(h.Commands.IndexedDraws[0].Pipeline);
        h.Targets.Resize(96, 64, false, 1, false, true);
        h.Factory.ThrowOnPipelineCreate = h.Factory.GraphicsPipelines.Count + 2;
        Assert.Throws<InvalidOperationException>(() => h.Renderer.SetOutputs(h.Targets.ColorDepthFB.Outputs));
        Assert.False(previous.Disposed);
        Assert.Throws<InvalidOperationException>(h.Draw);
        h.Factory.ThrowOnPipelineCreate = 0;
        h.Prepare(); h.Draw();
        FakePipeline retried = Assert.IsType<FakePipeline>(h.Commands.IndexedDraws[0].Pipeline);
        Assert.NotSame(previous, retried);
        Assert.Equal(h.Targets.ColorDepthFB.Outputs.Colour, retried.Request!.Value.Description.Outputs.Colour);
        Assert.False(previous.Disposed);
        h.Retired.BeginFrame(); h.Retired.BeginFrame();
        Assert.Equal(1, previous.DisposeCount);
    }

    [Fact]
    public void ReleasedPayloadCannotUseCachedTrianglePipelineAndAllCreatedResourcesDisposeOnce()
    {
        var h = new Host();
        h.Planes = [h.Plane(h.Triangles)]; h.Prepare(); h.Draw();
        h.Settings.GridMode = WaterGridMode.Clipmap;
        h.Planes = [h.Plane(h.Triangles, .01f), h.Plane(h.Bilinear, .01f)]; h.Prepare(); h.Draw();
        h.Renderer.UnloadOpticsField(h.Triangles);
        Assert.Throws<ArgumentException>(h.Draw);
        Assert.Throws<ArgumentException>(h.Prepare);
        h.Dispose();
        Assert.All(h.Factory.ShaderSets, s => Assert.Equal(1, s.DisposeCount));
        Assert.All(h.Factory.Pipelines, p => Assert.Equal(1, p.DisposeCount));
    }

    [Fact]
    public void InvalidOrFftPayloadNeverCompilesAndCulledValidSubmissionPreparesHonestly()
    {
        using var h = new Host();
        h.Planes = [new(1, 0, 1, 3, optics: new(h.Triangles, Vector3.Zero, new Vector2(2)))];
        Assert.Throws<ArgumentException>(h.Prepare);
        Assert.Empty(h.TriangleRequests());
        h.Planes = [new(1, 0, 1, .25f, look: new WaterLook { WaveSource = WaterWaveSource.FftOcean },
            optics: new(h.Triangles, Vector3.Zero, new Vector2(2)))];
        Assert.Throws<NotSupportedException>(h.Prepare);
        Assert.Empty(h.TriangleRequests());
        h.Planes = [new(1001, 0, 1001, .25f, look: new WaterLook { SwellAmplitude = 0 },
            optics: new(h.Triangles, new(1000, 0, 1000), new Vector2(2)))];
        h.Prepare();
        Assert.Single(h.TriangleRequests());
        h.Draw();
        Assert.Empty(h.Commands.IndexedDraws);
        Assert.Equal(1, h.Renderer.LastCulledPlanes);
    }

    [Fact]
    public void AllFourVariantsResizeWithoutCompilingAndSceneDisposalOwnsOptionalPrograms()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Starfield = false;
        WaterOpticsHandle triangle = scene.LoadWaterOpticsTriangles(WaterOpticsTriangleResourceTests.Field());
        void Prepare(WaterGridMode mode)
        {
            scene.Post.Water.GridMode = mode;
            scene.Begin();
            var look = new WaterLook { SwellAmplitude = .01f, SwellSteepness = 0f };
            scene.DrawWater(new(1, 0, 1, .25f, look: look));
            scene.DrawWater(new(1, 0, 1, .25f, look: look, optics: new(triangle, Vector3.Zero, new Vector2(2))));
            scene.PrepareFrame(96, 64);
        }
        Prepare(WaterGridMode.CameraFocused);
        Prepare(WaterGridMode.Clipmap);
        FakeShaderSet[] programs = factory.ShaderSets.Where(s => s.FragmentGlsl == ShaderSources.WaterFrag
            || Triangle(s.FragmentGlsl)).ToArray();
        Assert.Equal(4, programs.Length);
        int requests = factory.ShaderRequests.Count;
        scene.Post.Hdr.Enabled = !scene.Post.Hdr.Enabled;
        Prepare(WaterGridMode.Clipmap);
        Assert.Equal(requests, factory.ShaderRequests.Count);
        scene.UnloadWaterOpticsField(triangle);
        scene.Dispose();
        Assert.All(programs, p => Assert.Equal(1, p.DisposeCount));
        Assert.All(factory.Pipelines.Where(p => p.Request is { } r
            && (r.FragmentGlsl == ShaderSources.WaterFrag || Triangle(r.FragmentGlsl))), p => Assert.Equal(1, p.DisposeCount));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmMixedFrameAddsNoAllocationsProgramsSetsTexturesOrOpticalUploads(bool clipmap)
    {
        using var h = new Host();
        h.Settings.GridMode = clipmap ? WaterGridMode.Clipmap : WaterGridMode.CameraFocused;
        h.Planes = [h.Plane(default, .01f), h.Plane(h.Triangles, .01f), h.Plane(h.Bilinear, .01f)];
        using var commands = new NullGpuCommandList();
        var sky = new SkySettings();
        void Frame()
        {
            h.Prepare();
            h.Renderer.Draw(commands, h.Targets, h.Planes, WaterTestFrames.TopDown, WaterTestFrames.TopDown,
                -Vector3.UnitY, Color.White, Vector3.UnitY * 100, h.Settings, sky, 0);
        }
        Frame();
        int shaders = h.Factory.ShaderRequests.Count, sets = h.Factory.ResourceSets.Count, textures = h.Factory.Textures.Count;
        long uploads = h.Renderer.Optics.Stats.UploadCount;
        Assert.Equal(0, AllocationMeasurement.Measure("one warm mixed optical capability frame", Frame));
        Assert.Equal(shaders, h.Factory.ShaderRequests.Count);
        Assert.Equal(sets, h.Factory.ResourceSets.Count);
        Assert.Equal(textures, h.Factory.Textures.Count);
        Assert.Equal(uploads, h.Renderer.Optics.Stats.UploadCount);
    }
}
