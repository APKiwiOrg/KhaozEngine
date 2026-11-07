using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The caustic pass and atlases as a frame sees them, on the device-free fake.</summary>
[Collection("AllocSensitive")]   // Steady_participating_frames_allocate_nothing measures its neighbours too
public sealed class WaterCausticLifecycleTests
{
    const GpuPixelFormat AtlasFormat = GpuPixelFormat.R16G16B16A16Float;
    static readonly WaterLook Caustic = new() { UnderwaterCaustics = true };

    sealed class PassLog : IRenderPassObserver
    {
        internal readonly List<string> Begun = [];
        internal readonly List<string> Skipped = [];
        public void BeginPass(IGpuCommandList commands, RenderPassKey key) => Begun.Add(key.Id);
        public void EndPass(IGpuCommandList commands, RenderPassKey key) { }
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) => Skipped.Add(key.Id);
        public void BeginCpuPhase(string id) { }
        public void EndCpuPhase(string id) { }
    }

    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly Scene3D Scene;
        internal readonly RecordingGpuCommandList Commands = new(new NullGpuCommandList())
        {
            CapturePayloads = true,
            CaptureBindings = true,
            CommandTrace = [],
        };
        internal readonly PassLog Passes = new();
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal IGpuBuffer VolumeBuffer => Assert.Single(Factory.Buffers, b => b.SizeInBytes == 652816);

        internal Host()
        {
            Scene = new Scene3D(Device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
            Scene.Post.Starfield = false;
            Scene.Post.RenderScale = RenderScale.MatchViewport;
            Scene.Post.Water.SwellAmplitude = 0f;
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Target = Vector3.Zero;
            Scene.PassObserver = Passes;
        }

        internal void Queue(params WaterPlane[] planes)
        {
            Scene.Begin();
            foreach (WaterPlane plane in planes) Scene.DrawWater(plane);
        }

        internal void Render()
        {
            Commands.Clear();
            Commands.CommandTrace!.Clear();
            Passes.Begun.Clear();
            Passes.Skipped.Clear();
            using IGpuFramebuffer target = Factory.CreateFramebuffer(null, Scene.ResolvedDepthTarget);
            Scene.RenderInternal(Commands, 96, 64, target);
        }

        internal void Frame(params WaterPlane[] planes)
        {
            Queue(planes);
            Scene.PrepareFrame(96, 64);
            Render();
        }

        internal IGpuResourceSet[] LiveLitSets() => Scene.LiveMaterialSetIdentitiesForTests
            .Select(identity => (IGpuResourceSet)Factory.ResourceSets.Single(
                set => Scene3D.MaterialSetMatchesIdentityForTests(set, identity)))
            .ToArray();

        // A textured mesh owns its lit set. An untextured one borrows the model's default set.
        internal MeshHandle LoadTriangle() => Scene.LoadMesh(new GltfMesh(new[]
        {
            new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One, Vector2.Zero),
            new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One, Vector2.Zero),
            new ModelVertex(Vector3.UnitZ, Vector3.UnitY, Vector4.One, Vector2.Zero),
        }, new ushort[] { 0, 1, 2 }), Scene.LoadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1));

        internal RecordingGpuCommandList.Command[] Draws(string vertexGlsl) => Commands.CommandTrace!
            .Where(c => c.Kind == "draw" && c.Pipeline is FakePipeline { Request: { } request }
                && request.VertexGlsl == vertexGlsl)
            .ToArray();

        internal RecordingGpuCommandList.DrawBindings SheetBinding(int index = 0) => Commands.Bindings
            .Where(b => b.Pipeline is FakePipeline { Request: { } request }
                && request.VertexGlsl == ShaderSources.WaterCausticSheetVert)
            .ElementAt(index);

        internal IGpuBuffer UniformBuffer => ((GpuBufferRange)((FakeResourceSet)SheetBinding().Sets[0].Set).Resources[0]).Buffer;

        internal WaterCausticUniforms Slot(int body)
        {
            byte[] image = Assert.Single(Commands.Uploads, u => ReferenceEquals(u.Buffer, UniformBuffer)).Data!;
            return MemoryMarshal.Read<WaterCausticUniforms>(image.AsSpan(body * (int)WaterCausticRenderer.SlotBytes));
        }

        internal bool UploadedWindowBlock => Commands.Uploads.Any(u => ReferenceEquals(u.Buffer, VolumeBuffer)
            && u.Offset == WaterVolumeClusterBuilder.WindowUIntOffset * sizeof(uint));

        internal Vector4 WindowBlock => MemoryMarshal.Read<Vector4>(Assert.Single(Commands.Uploads,
            u => ReferenceEquals(u.Buffer, VolumeBuffer)
                && u.Offset == WaterVolumeClusterBuilder.WindowUIntOffset * sizeof(uint)).Data!);

        public void Dispose() { Commands.Dispose(); Scene.Dispose(); Device.Dispose(); }
    }

    static WaterPlane Plane(float x, float halfExtent = 2f, WaterLook? look = null, float z = 0f) =>
        new(x, 0f, z, halfExtent, look: look ?? Caustic);

    public static TheoryData<string, WaterCausticDiagnostics> NonParticipants() => new()
    {
        { "no water", default },
        { "not opted in", default },
        { "zero strength", new WaterCausticDiagnostics(1, 0, 0, 0, 0) },
        { "fft only", new WaterCausticDiagnostics(1, 0, 1, 0, 0) },
    };

    [Theory]
    [MemberData(nameof(NonParticipants))]
    public void No_participating_body_records_no_pass_and_allocates_no_atlas(string name, WaterCausticDiagnostics expected)
    {
        using var h = new Host();
        WaterPlane[] planes = name switch
        {
            "no water" => [],
            "not opted in" => [new WaterPlane(0f, 0f, 0f, 2f, look: new WaterLook { UnderwaterSunlight = true })],
            "zero strength" => [Plane(0f, look: new WaterLook { UnderwaterCaustics = true, CausticStrength = 0f })],
            _ => [Plane(0f, look: new WaterLook { UnderwaterCaustics = true, WaveSource = WaterWaveSource.FftOcean })],
        };

        h.Frame(planes);

        Assert.Null(h.Scene.WaterCausticMapForTests);
        Assert.Null(h.Scene.WaterSurfaceFieldForTests);
        Assert.Contains("water.caustics", h.Passes.Skipped);
        Assert.DoesNotContain("water.caustics", h.Passes.Begun);
        Assert.DoesNotContain(h.Factory.ShaderRequests, r => r.VertexGlsl == ShaderSources.WaterSurfaceFieldVert
            || r.VertexGlsl == ShaderSources.WaterCausticSubtractVert || r.VertexGlsl == ShaderSources.WaterCausticSheetVert);
        Assert.False(h.UploadedWindowBlock);
        Assert.Equal(expected, h.Scene.WaterCausticDiagnostics);
    }

    [Fact]
    public void First_participating_frame_allocates_and_replaces_lit_sets_once()
    {
        using var h = new Host();
        h.LoadTriangle();
        IGpuResourceSet[] before = h.LiveLitSets();
        Assert.NotEmpty(before);

        h.Frame(Plane(0f));

        var map = Assert.IsType<FakeTexture>(h.Scene.WaterCausticMapForTests);
        var field = Assert.IsType<FakeTexture>(h.Scene.WaterSurfaceFieldForTests);
        Assert.Equal((1024u, 1024u, AtlasFormat), (map.Width, map.Height, map.Format));
        Assert.Equal((512u, 512u, AtlasFormat), (field.Width, field.Height, field.Format));
        Assert.Equal(GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, map.Usage);
        Assert.Equal(GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled, field.Usage);
        IGpuResourceSet[] replaced = h.LiveLitSets();
        Assert.All(replaced, set => Assert.DoesNotContain(set, before));
        Assert.All(replaced, set => Assert.Contains(map, ((FakeResourceSet)set).Resources));
        Assert.Contains("water.caustics", h.Passes.Begun);

        h.Frame(Plane(0f));

        Assert.Same(map, h.Scene.WaterCausticMapForTests);
        Assert.Equal(replaced, h.LiveLitSets());
    }

    /// <summary>Two overlapping bodies take two layers, three or more size for four, and a smaller frame keeps it.</summary>
    [Fact]
    public void Layer_growth_retires_the_old_atlas_and_never_shrinks()
    {
        using var h = new Host();
        h.Frame(Plane(0f));
        var first = (FakeTexture)h.Scene.WaterCausticMapForTests!;
        var firstField = (FakeTexture)h.Scene.WaterSurfaceFieldForTests!;
        int retired = h.Scene.RetiredResourceCount;

        h.Frame(Plane(0f), Plane(1f));

        var second = (FakeTexture)h.Scene.WaterCausticMapForTests!;
        Assert.Equal((2048u, 1024u), (second.Width, second.Height));
        Assert.Equal((1024u, 512u), (h.Scene.WaterSurfaceFieldForTests!.Width, h.Scene.WaterSurfaceFieldForTests!.Height));
        Assert.False(first.Disposed);
        Assert.False(firstField.Disposed);
        Assert.True(h.Scene.RetiredResourceCount > retired);

        h.Frame(Plane(0f), Plane(1f), Plane(2f));
        var third = (FakeTexture)h.Scene.WaterCausticMapForTests!;
        Assert.Equal(4096u, third.Width);

        for (int frame = 0; frame < 8; frame++) h.Frame(Plane(0f));

        Assert.Same(third, h.Scene.WaterCausticMapForTests);
        Assert.True(first.Disposed);
        Assert.True(firstField.Disposed);
        Assert.True(second.Disposed);
        Assert.Equal(0, h.Factory.LiveSetsNamingAFreedTexture);
    }

    [Fact]
    public void Mesh_loaded_after_the_atlas_binds_the_live_map()
    {
        using var h = new Host();
        h.Frame(Plane(0f));
        IGpuTexture map = h.Scene.WaterCausticMapForTests!;

        h.LoadTriangle();

        IGpuResourceSet set = Assert.Single(h.LiveLitSets());
        Assert.Contains(map, ((FakeResourceSet)set).Resources);
    }

    [Fact]
    public void Shadow_layout_replacement_keeps_the_live_map()
    {
        using var h = new Host();
        h.LoadTriangle();
        h.Frame(Plane(0f));
        IGpuTexture map = h.Scene.WaterCausticMapForTests!;
        IGpuResourceSet[] before = h.LiveLitSets();

        Assert.True(h.Scene.ReplaceShadowLayout(32, 2));

        IGpuResourceSet set = Assert.Single(h.LiveLitSets());
        Assert.DoesNotContain(set, before);
        Assert.Contains(map, ((FakeResourceSet)set).Resources);
    }

    [Fact]
    public void Opting_out_keeps_the_atlas_and_stops_the_pass()
    {
        using var h = new Host();
        h.Frame(Plane(0f));
        IGpuTexture map = h.Scene.WaterCausticMapForTests!;
        Assert.Equal(1f, RecordLayerWord(h));

        h.Frame(new WaterPlane(0f, 0f, 0f, 2f, look: new WaterLook { UnderwaterSunlight = true }));

        Assert.Same(map, h.Scene.WaterCausticMapForTests);
        Assert.Empty(h.Draws(ShaderSources.WaterSurfaceFieldVert));
        Assert.Empty(h.Draws(ShaderSources.WaterCausticSheetVert));
        Assert.Contains("water.caustics", h.Passes.Skipped);
        Assert.Equal(0f, RecordLayerWord(h));
        Assert.False(h.UploadedWindowBlock);
        Assert.Equal(default, h.Scene.WaterCausticDiagnostics);

        static float RecordLayerWord(Host h)
        {
            byte[] image = h.Commands.Uploads.First(u => ReferenceEquals(u.Buffer, h.VolumeBuffer)).Data!;
            return MemoryMarshal.Read<float>(image.AsSpan((WaterVolumeClusterBuilder.RecordRegionUInts + 10) * sizeof(uint)));
        }
    }

    [Fact]
    public void Disposal_releases_atlases_in_the_water_stage()
    {
        using (var released = new Host())
        {
            released.Frame(Plane(0f));
            var map = (FakeTexture)released.Scene.WaterCausticMapForTests!;
            var field = (FakeTexture)released.Scene.WaterSurfaceFieldForTests!;
            released.Scene.Dispose();
            Assert.True(map.Disposed);
            Assert.True(field.Disposed);
        }

        using var h = new Host();
        h.Frame(Plane(0f));
        ((FakeTexture)h.Scene.WaterCausticMapForTests!).OnDispose =
            () => throw new InvalidOperationException("planned atlas release failure");

        var failure = Assert.Throws<GpuDiagnosticFailureException>(h.Scene.Dispose);

        Assert.StartsWith("renderer.session.dispose.scene.water.", Assert.Single(failure.Failures).Stage);
    }

    [Fact]
    public void Steady_participating_frames_allocate_nothing()
    {
        using var h = new Host();
        using var commands = new NullGpuCommandList();
        using IGpuFramebuffer target = h.Factory.CreateFramebuffer(null, h.Scene.ResolvedDepthTarget);
        h.Scene.PassObserver = null;
        WaterPlane near = Plane(-8f), far = Plane(8f), overlapping = Plane(9f);

        void Frame()
        {
            h.Scene.Begin();
            h.Scene.DrawWater(near);
            h.Scene.DrawWater(far);
            h.Scene.DrawWater(overlapping);
            h.Scene.PrepareFrame(96, 64);
            h.Scene.RenderInternal(commands, 96, 64, target);
        }

        for (int frame = 0; frame < 8; frame++) Frame();
        Assert.Equal(3, h.Scene.WaterCausticDiagnostics.DrawnPlanes);

        AllocAssert.NoPerCallAllocation("20 steady caustic frames", () =>
        {
            for (int frame = 0; frame < 20; frame++) Frame();
        });
    }

    /// <summary>A lit fragment reads the map, so the field then the map must precede the model target, and a staging
    /// backend must not have to reopen a target for an upload.</summary>
    [Fact]
    public void Pass_records_before_the_model_pass_and_after_its_uploads()
    {
        using var h = new Host();
        h.Frame(Plane(0f));

        IReadOnlyList<IGpuFramebuffer> binds = h.Commands.FramebufferBindings;
        int field = IndexOf(binds, h.Scene.WaterSurfaceFieldForTests!);
        int map = IndexOf(binds, h.Scene.WaterCausticMapForTests!);
        int model = IndexOf(binds, h.Scene.ColorTargetsForTests.ColorTex);
        Assert.True(field >= 0 && field + 1 == map && map < model, $"field {field}, map {map}, model {model}");

        Assert.All(new[] { h.UniformBuffer, h.SheetBinding().VertexBuffers[1] }, buffer =>
            Assert.Equal(field, Assert.Single(h.Commands.Uploads, u => ReferenceEquals(u.Buffer, buffer)).FramebufferBindsBefore));

        // The whole-atlas clears name their targets' documented starting values.
        Color ClearOf(int bind) => Assert.Single(h.Commands.ColourClears.Where(
            (_, i) => ReferenceEquals(h.Commands.ColourClearTargets[i], binds[bind]))).Rgba;
        Assert.Equal(new Color(0f, 0f, 0f, 0f), ClearOf(field));
        Assert.Equal(Color.White, ClearOf(map));

        // The next pass finds a full scissor.
        List<RecordingGpuCommandList.Command> trace = h.Commands.CommandTrace!;
        int lastSheet = trace.FindLastIndex(c => c.Kind == "draw" && Vertex(c) == ShaderSources.WaterCausticSheetVert);
        Assert.Equal("full-scissor", trace[lastSheet + 1].Kind);

        static int IndexOf(IReadOnlyList<IGpuFramebuffer> binds, IGpuTexture colour)
        {
            for (int i = 0; i < binds.Count; i++)
                if (binds[i] is FakeFramebuffer framebuffer && framebuffer.Colour.Contains(colour)) return i;
            return -1;
        }

        static string? Vertex(RecordingGpuCommandList.Command c) => (c.Pipeline as FakePipeline)?.Request?.VertexGlsl;
    }

    /// <summary>DrawWater refuses a plane once the frame is prepared, so a body joins the plan late through its settings:
    /// the scene default opts in a queued plane after sizing saw it as a non-participant.</summary>
    [Fact]
    public void Body_queued_after_preparation_never_exceeds_the_atlas()
    {
        using var h = new Host();
        h.Queue(Plane(0f), new WaterPlane(1f, 0f, 0f, 2f));
        h.Scene.PrepareFrame(96, 64);
        Assert.Equal(1024u, h.Scene.WaterCausticMapForTests!.Width);

        h.Scene.Post.Water.UnderwaterCaustics = true;
        h.Render();

        Assert.Equal(2048u, h.Scene.WaterCausticMapForTests!.Width);
        Assert.Equal(2, h.Scene.WaterCausticDiagnostics.Layers);
        WaterCausticUniforms late = h.Slot(1);
        Assert.Equal(new Vector2(1f, 2f), new Vector2(late.CausticAtlas.X, late.CausticAtlas.Y));
        Assert.Equal(2f, h.WindowBlock.W);
    }

    /// <summary>Sizing ignores the window, so a frame can use fewer layers than the atlas holds. The receivers and the
    /// sheets address the atlas, so both need its capacity and never the frame's used count.</summary>
    [Fact]
    public void Window_block_and_uniforms_carry_the_atlas_capacity()
    {
        using var h = new Host();
        h.Frame(Plane(-16f), Plane(0f), Plane(16f));

        Assert.Equal(1, h.Scene.WaterCausticDiagnostics.Layers);
        Assert.Equal(4096u, h.Scene.WaterCausticMapForTests!.Width);
        Assert.Equal(4f, h.Slot(0).CausticAtlas.Y);
        WaterSurfaceWindow window = h.Scene.WaterSurfaceWindowForTests;
        Assert.Equal(new Vector4(window.MinX, window.MinZ, 1f / 64f, 4f), h.WindowBlock);

        h.Frame(new WaterPlane(0f, 0f, 0f, 2f, look: new WaterLook { UnderwaterSunlight = true }));

        Assert.False(h.UploadedWindowBlock);
    }

    /// <summary>The slot is the shader's contract: its own column, the shared wave vectors, the refracted still ray.</summary>
    [Fact]
    public void Each_rectangle_draws_once_from_its_own_slot_and_instance_offset()
    {
        using var h = new Host();
        h.Scene.Post.LightDirection = Vector3.Normalize(new Vector3(0.6f, -1f, 0.2f));
        h.Scene.EffectTimeSeconds = 2.5f;
        h.Scene.Post.Water.SwellAmplitude = 0.2f;
        var strong = new WaterLook { UnderwaterCaustics = true, CausticStrength = 3f, RippleSeed = 9 };
        h.Frame(Plane(0f, 2f, strong), Plane(1f));

        Assert.Equal(2, h.Draws(ShaderSources.WaterSurfaceFieldVert).Length);
        Assert.Equal(2, h.Draws(ShaderSources.WaterCausticSubtractVert).Length);
        RecordingGpuCommandList.Command[] sheets = h.Draws(ShaderSources.WaterCausticSheetVert);
        Assert.Equal(2, sheets.Length);
        Assert.Equal(0u, sheets[0].Slot);
        Assert.Equal(sheets[0].Value, sheets[1].Slot);
        uint instances = sheets[0].Value + sheets[1].Value;
        Assert.Equal(instances * 12u, Assert.Single(h.Commands.Uploads,
            u => ReferenceEquals(u.Buffer, h.SheetBinding().VertexBuffers[1])).Bytes);
        Assert.Equal(WaterCausticRenderer.SlotBytes, h.SheetBinding(1).Sets[0].DynamicOffset);

        WaterCausticUniforms slot = h.Slot(0);
        var sun = new Vector3(slot.SunTravel.X, slot.SunTravel.Y, slot.SunTravel.Z);
        var still = new Vector3(slot.StillRay.X, slot.StillRay.Y, slot.StillRay.Z);
        Assert.Equal(3f, slot.SunTravel.W);
        Assert.Equal(1f, sun.Length(), 5);
        Assert.Equal(1f, still.Length(), 5);
        Assert.True(still.Y < sun.Y, "refraction bends the sun toward the downward normal");
        Assert.Equal(1.333f, new Vector2(sun.X, sun.Z).Length() / new Vector2(still.X, still.Z).Length(), 4);
        Assert.Equal(GpuClip.Correct(Matrix4x4.Identity, h.Device.Capabilities).M22, slot.CausticAtlas.Z);

        var settings = strong.ResolveInto(new WaterSettings(), h.Scene.Post.Water);
        WaterRenderer.WaterUbo water = WaterRenderer.PackUbo(Matrix4x4.Identity, Matrix4x4.Identity,
            h.Scene.Post.LightDirection, Color.White, Vector3.Zero, settings, h.Scene.Post.Sky, 2.5f);
        Assert.Equal(water.WaveParams, slot.WaveParams);
        Assert.Equal(water.DetailParams, slot.DetailParams);
        Assert.Equal(water.SwellParams, slot.SwellParams);
        Assert.Equal(water.SwellShape, slot.SwellShape);
        Assert.Equal(water.RippleSpectrum, slot.RippleSpectrum);
        Assert.Equal(settings.FootprintSamples, slot.FootprintParams.X);
    }

    /// <summary>The design's atomic path: a failed replacement keeps the sets it had, which still name the old map, so
    /// that map has to outlive the failure and the next frame retries.</summary>
    [Fact]
    public void Failed_map_replacement_keeps_the_old_map_and_retries()
    {
        using var h = new Host();
        h.LoadTriangle();
        h.Frame(Plane(0f));
        var first = (FakeTexture)h.Scene.WaterCausticMapForTests!;
        IGpuResourceSet[] before = h.LiveLitSets();

        h.Queue(Plane(0f), Plane(1f));
        h.Factory.ThrowOnResourceSetCreate = h.Factory.ResourceSets.Count + 2;
        Assert.Throws<InvalidOperationException>(() => h.Scene.PrepareFrame(96, 64));
        for (int frame = 0; frame < 8; frame++) h.Scene.Begin();
        h.Factory.ThrowOnResourceSetCreate = 0;

        Assert.False(first.Disposed);
        Assert.Equal(before, h.LiveLitSets());

        h.Frame(Plane(0f), Plane(1f));

        IGpuTexture second = h.Scene.WaterCausticMapForTests!;
        Assert.NotSame(first, second);
        Assert.Contains(second, ((FakeResourceSet)Assert.Single(h.LiveLitSets())).Resources);
        for (int frame = 0; frame < 8; frame++) h.Frame(Plane(0f));
        Assert.True(first.Disposed);
    }

    [Fact]
    public void Caustic_pipelines_add_exact_values_on_their_atlas_with_no_depth()
    {
        using var h = new Host();
        h.Frame(Plane(0f));

        FakeGraphicsPipelineRequest Request(string vertex) =>
            Assert.Single(h.Factory.GraphicsPipelines, r => r.VertexGlsl == vertex);
        FakeGraphicsPipelineRequest field = Request(ShaderSources.WaterSurfaceFieldVert);
        FakeGraphicsPipelineRequest subtract = Request(ShaderSources.WaterCausticSubtractVert);
        FakeGraphicsPipelineRequest sheet = Request(ShaderSources.WaterCausticSheetVert);

        Assert.False(field.Description.BlendAttachments[0].BlendEnabled);
        Assert.All(new[] { subtract, sheet }, request =>
        {
            GpuBlendAttachment blend = request.Description.BlendAttachments[0];
            Assert.True(blend.BlendEnabled);
            Assert.Equal((GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.Add),
                (blend.SourceColorFactor, blend.DestinationColorFactor, blend.ColorFunction));
            Assert.Equal((GpuBlendFactor.One, GpuBlendFactor.One, GpuBlendFunction.Add),
                (blend.SourceAlphaFactor, blend.DestinationAlphaFactor, blend.AlphaFunction));
        });
        Assert.All(new[] { field, subtract, sheet }, request =>
        {
            GpuPipelineDescription d = request.Description;
            Assert.Null(d.Outputs.Depth);
            Assert.Equal(AtlasFormat, Assert.Single(d.Outputs.Colour));
            Assert.False(d.DepthStencil.DepthTestEnabled);
            Assert.Equal(GpuFaceCull.None, d.Rasterizer.CullMode);
            Assert.True(d.Rasterizer.ScissorTestEnabled);
        });
    }
}
