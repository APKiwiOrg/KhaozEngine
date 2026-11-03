using System;
using System.IO;
using System.Collections.Generic;
using KhaozEngine.Primitives;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfEffectsRecipeTests
{
    [Fact]
    public void Foliage_water_and_fft_routes_match_fixed_counts()
    {
        using var recipe = RendererPerfRecipe.Create("outdoors");
        using var rig = new EffectsRig();
        recipe.ResetBlock(rig.Scene);
        rig.Frame(recipe, 64);
        Assert.False(rig.Scene.TemporalHistory.IsValid);
        Assert.Equal(16, recipe.Facts.TerrainChunks);
        Assert.Equal(0, rig.Scene.CulledInstances);
        Assert.Equal(64, rig.Scene.LastFoliageStats.SubmittedPatches);
        Assert.Equal(4096, rig.Scene.LastFoliageStats.CandidateInstances);
        Assert.True(rig.Scene.FoliageShadowDrawCalls > 0);
        Assert.Contains(rig.Record.IndexedDraws, d => Shader(d)?.FragmentGlsl == ShaderSources.FoliageDepthCutoutFrag);
        Assert.Equal(2, rig.Scene.WaterPlaneCount);
        Assert.Equal(WaterRenderer.PlaneRoute.Clipmap, rig.Scene.WaterRouteForTests(0));
        Assert.Equal(WaterRenderer.PlaneRoute.Clipmap, rig.Scene.WaterRouteForTests(1));
        Assert.Equal(2, rig.Scene.Post.Water.SeaState.CascadeCount);
        Assert.Equal(256, rig.Scene.Post.Water.SeaState.CascadeResolution);
        Assert.Equal(1, rig.Scene.LastWaterStats.OceanStalls);
        rig.Frame(recipe, 65);
        Assert.Equal(0, rig.Scene.LastWaterStats.OceanStalls);
        Assert.Equal(4, rig.Scene.LastShadowPassDiagnostics.CascadeCount);
        Assert.Equal(0, rig.Scene.PointShadowedLights);
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        Assert.Equal(2, rig.Commands.Tally[GpuCommandKind.Dispatch]);
        Assert.Equal(64, rig.Record.CommandTrace!.Count(c => c.Kind == "draw"
            && (c.Pipeline as FakePipeline)?.Request?.VertexGlsl == ShaderSources.FoliageMotionVert));
        Assert.Equal(4096u, (uint)rig.Record.CommandTrace!.Where(c => c.Kind == "draw"
            && (c.Pipeline as FakePipeline)?.Request?.VertexGlsl == ShaderSources.FoliageMotionVert).Sum(c => c.Value));
        Assert.Equal(2, recipe.Facts.Schedule.Features!.OceanDispatches);
        Assert.Equal(4, rig.Record.TextureCopies.Count(c => c.Width == 256 && c.Height == 256));
        Assert.Contains(rig.Record.IndexedDraws, d => Shader(d)?.VertexGlsl == ShaderSources.FoliageMotionVert);
        using var frozen = JsonDocument.Parse(recipe.Describe());
        Assert.Equal(4096, frozen.RootElement.GetProperty("Expected").GetProperty("Foliage").GetInt32());
        Assert.Equal(2, frozen.RootElement.GetProperty("Expected").GetProperty("Water").GetInt32());
    }

    [Fact]
    public void Ui_quads_textures_and_clips_are_fixed()
    {
        using var recipe = RendererPerfRecipe.Create("effects-ui");
        using var rig = new EffectsRig();
        recipe.ResetBlock(rig.Scene);
        RendererPerfUiScene ui = recipe.UiScene!;
        ui.Load(rig.Device);
        using var batch = new SpriteBatch(rig.Device, rig.Target.Outputs);
        rig.Frame(recipe, 64);
        rig.Frame(recipe, 65);
        Assert.Equal(16, recipe.Facts.TerrainChunks);
        Assert.Equal(64, recipe.Facts.PropInstances);
        Assert.Equal(4, rig.Scene.DrawnSkinnedInstances);
        Assert.Equal(0, rig.Scene.CulledSkinnedInstances);
        Assert.Equal(32, rig.Scene.DecalCount);
        Assert.Equal(256, rig.Scene.ParticleSpriteCount);
        Assert.Equal(64, rig.Scene.DistortionSpriteCount);
        Assert.Equal(64, rig.Scene.TexturedBillboardCount);
        Assert.Equal(8, rig.Scene.BeamCount);
        Assert.Equal(8, rig.Scene.TrailCount);
        Assert.Equal(8, rig.Scene.OverlayMeshDrawCount);
        Assert.Equal(4, rig.Scene.SilhouetteDrawCount);
        Assert.Equal(4, rig.Scene.MeshOutlineGroupCount);
        Assert.Equal(4, rig.Scene.MeshOutlinePartCount);
        Assert.Equal(1, recipe.Facts.Schedule.Features!.TemporalOutlineDraws);
        Assert.Equal(0, rig.Scene.PointShadowedLights);
        foreach (string fragment in new[] { ShaderSources.DecalFrag, ShaderSources.ParticleFrag,
            ShaderSources.TexturedBillboardMotionFrag, ShaderSources.BeamMotionFrag, ShaderSources.TrailMotionFrag,
            ShaderSources.OverlayUnlitMotionFrag, ShaderSources.SilhouetteMotionFrag, ShaderSources.DistortionFrag,
            ShaderSources.TargetOutlineFullMaskFrag, ShaderSources.TargetOutlineVisibleMaskFrag,
            ShaderSources.TargetOutlineCompositeFrag, ShaderSources.TemporalEdgeFrag,
            ShaderSources.DistortionApplyFrag, ShaderSources.BloomBrightFrag, ShaderSources.BloomBlurFrag,
            ShaderSources.BloomCompositeFrag, ShaderSources.TonemapFrag, ShaderSources.BlitFrag,
            ShaderSources.TemporalDepthStoreFrag, ShaderSources.TemporalSharpenFrag })
            Assert.Contains(rig.Record.CommandTrace!, c => c.Kind == "draw" && (c.Pipeline as FakePipeline)?.Request?.FragmentGlsl == fragment);
        Assert.Equal(1, rig.Record.CommandTrace!.Count(c => c.Kind == "draw"
            && (c.Pipeline as FakePipeline)?.Request?.FragmentGlsl == ShaderSources.TemporalEdgeFrag));
        Assert.Equal(4, rig.Record.CommandTrace!.Count(c => c.Kind == "draw"
            && (c.Pipeline as FakePipeline)?.Request?.FragmentGlsl == ShaderSources.TargetOutlineCompositeFrag));
        Assert.True(rig.Scene.Post.Outline && rig.Scene.Post.Bloom.Enabled && rig.Scene.Post.Hdr.Enabled);
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        rig.Record.Clear();
        rig.Record.Begin();
        rig.Record.SetFramebuffer(rig.Target);
        var trace = new UiTrace(rig.Record);
        batch.NewFrame(trace, 1920, 1080);
        batch.Begin();
        ui.Queue(batch, 65);
        batch.End();
        rig.Record.End();
        Assert.Equal(3072, batch.FrameStats.Quads);
        Assert.Equal(64, batch.FrameStats.DrawCalls);
        Assert.Equal(16, batch.FrameStats.Flushes);
        Assert.Equal(0, batch.ScissorDepth);
        Assert.Equal(4, trace.Textures.Count);
        Assert.Equal(16, trace.Clips.Where(c => c.W == 480 && c.H == 270).Distinct().Count());
        Assert.Equal(16, trace.Clips.Count(c => c.W == 1920 && c.H == 1080));
        Assert.Equal("319cff6e7a31f0f2a41c475dca42890aa5d19fe16017e2290f8c1d4e14f76481", ui.FontSha256);
        using var second = RendererPerfRecipe.Create("effects-ui");
        Assert.Equal(recipe.Facts, second.Facts);
        Assert.Equal(recipe.Describe(), second.Describe());
    }

    [Fact]
    public void Every_measured_ocean_frame_has_no_prime_submission_or_wait()
    {
        using var recipe = RendererPerfRecipe.Create("outdoors");
        using var rig = new EffectsRig();
        recipe.ResetBlock(rig.Scene);
        int warmupStalls = 0;
        foreach (int index in RendererPerfRecipe.WarmupIndices)
        {
            rig.Frame(recipe, index);
            warmupStalls += rig.Scene.LastWaterStats.OceanStalls;
        }
        Assert.Equal(1, warmupStalls);
        int submits = rig.Device.Submits, waits = rig.Spy.WaitForIdleCalls;
        var stalls = new List<int>();
        var extraSubmits = new List<int>();
        var extraWaits = new List<int>();
        foreach (int index in RendererPerfRecipe.MeasuredIndices)
        {
            rig.Frame(recipe, index);
            stalls.Add(rig.Scene.LastWaterStats.OceanStalls);
            extraSubmits.Add(rig.Device.Submits - submits);
            extraWaits.Add(rig.Spy.WaitForIdleCalls - waits);
            submits = rig.Device.Submits;
            waits = rig.Spy.WaitForIdleCalls;
        }
        Assert.Multiple(
            () => Assert.All(stalls, value => Assert.Equal(0, value)),
            () => Assert.All(extraSubmits, value => Assert.Equal(0, value)),
            () => Assert.All(extraWaits, value => Assert.Equal(0, value)));
    }

    [Fact]
    public void Foliage_motion_history_keeps_the_continuous_effect_clock()
    {
        using var recipe = RendererPerfRecipe.Create("outdoors");
        using var rig = new EffectsRig();
        recipe.ResetBlock(rig.Scene);
        foreach (int index in RendererPerfRecipe.WarmupIndices) rig.Frame(recipe, index);
        rig.Frame(recipe, 0);
        var uniforms = rig.Scene.FoliageUniformsForTests.ToArray();
        Assert.NotEmpty(uniforms);
        Assert.All(uniforms, u => Assert.Multiple(
            () => Assert.Equal(256f * (1f / 60f), u.WindTime.Z),
            () => Assert.Equal(255f * (1f / 60f), u.PrevFocus.W)));
    }

    [Fact]
    public void Effect_uniforms_keep_advancing_across_the_periodic_boundary()
    {
        using var recipe = RendererPerfRecipe.Create("effects-ui");
        using var rig = new EffectsRig();
        recipe.ResetBlock(rig.Scene);
        foreach (int index in RendererPerfRecipe.WarmupIndices) rig.Frame(recipe, index);
        rig.Record.CapturePayloads = true;
        rig.Frame(recipe, 0);
        float time = 256f * (1f / 60f);
        Assert.Multiple(
            () => Assert.Equal(time, Time(ShaderSources.DecalFrag, 512, 320)),
            () => Assert.Equal(time, Time(ShaderSources.ParticleFrag, 192, 172)),
            () => Assert.Equal(time, Time(ShaderSources.BeamMotionFrag, 80, 64)),
            () => Assert.Equal(time, Time(ShaderSources.DistortionFrag, 192, 172)));

        float Time(string fragment, uint bytes, int offset)
        {
            var draws = rig.Record.CommandTrace!.Where(c => c.Kind is "draw" or "dispatch").ToArray();
            int draw = Array.FindIndex(draws, c => (c.Pipeline as FakePipeline)?.Request?.FragmentGlsl == fragment);
            Assert.InRange(draw, 0, draws.Length - 1);
            var upload = rig.Record.Uploads.Last(u => u.Bytes == bytes && u.DrawsBefore <= draw);
            return BitConverter.ToSingle(upload.Data!, offset);
        }
    }

    [Fact]
    public void Clipmap_event_sequence_is_repeatable()
    {
        using var recipe = RendererPerfRecipe.Create("outdoors");
        using var first = new EffectsRig();
        using var second = new EffectsRig();
        recipe.ResetBlock(first.Scene);
        int[] indices = RendererPerfRecipe.WarmupIndices.Concat(RendererPerfRecipe.MeasuredIndices).ToArray();
        var rebuiltPlanes = new List<uint[]>();
        uint vertexBytes = (uint)WaterClipmap.VertexCount(4, 32) * WaterRenderer.ClipVertexBytes;
        int[] a = indices.Select(index =>
        {
            first.Frame(recipe, index);
            Assert.Equal(0, first.Scene.CulledInstances);
            Assert.Equal(64, first.Scene.LastFoliageStats.SubmittedPatches);
            Assert.Equal(4096, first.Scene.LastFoliageStats.CandidateInstances);
            rebuiltPlanes.Add(first.Record.Uploads.Where(u => u.Bytes == vertexBytes)
                .Select(u => u.Offset / vertexBytes).ToArray());
            return first.Scene.LastWaterStats.ClipmapRebuilds;
        }).ToArray();
        recipe.ResetBlock(second.Scene);
        int[] b = indices.Select(i => { second.Frame(recipe, i); return second.Scene.LastWaterStats.ClipmapRebuilds; }).ToArray();
        Assert.Equal(a, b);
        using var json = JsonDocument.Parse(File.ReadAllBytes(RendererPerfRecipe.RecipePath("outdoors")));
        JsonElement states = json.RootElement.GetProperty("Authored").GetProperty("Geometry").GetProperty("ClipmapStates");
        string[]? previous = null;
        for (int i = 0; i < indices.Length; i++)
        {
            string[] current = states[indices[i]].EnumerateArray().Select(p => p.GetRawText()).ToArray();
            int expected = previous is null ? 2 : current.Where((state, p) => state != previous[p]).Count();
            Assert.Equal(expected, a[i]);
            previous = current;
        }
        Assert.Multiple(
            () => Assert.Equal(new[] { 224 }, a.Take(192).Select((count, step) => (count, step))
                .Where(pair => pair.step > 0 && pair.count > 0).Select(pair => indices[pair.step])),
            () => Assert.Equal(new[] { 33, 224 }, a.Skip(192).Select((count, index) => (count, index))
                .Where(pair => pair.count > 0).Select(pair => pair.index)),
            () => Assert.Equal(2, a[0]),
            () => Assert.Equal(2, a.Skip(1).Take(191).Sum()),
            () => Assert.Equal(4, a.Skip(192).Sum()));
        foreach (int step in Enumerable.Range(0, indices.Length))
        {
            bool rebuild = step == 0 || indices[step] == 224 || step >= 192 && indices[step] == 33;
            Assert.Equal(rebuild ? 2 : 0, a[step]);
            Assert.Equal(rebuild ? new uint[] { 0, 1 } : Array.Empty<uint>(), rebuiltPlanes[step]);
        }
        Assert.False(first.Scene.ShadowPassSkippedLastFrame);
        Assert.Equal(64, first.Scene.LastFoliageStats.SubmittedPatches);
        Assert.Equal(recipe.Frame(0), recipe.Frame(256));
        recipe.VerifyFrozen();
    }

    [Fact]
    public void Frozen_clipmap_schedule_names_both_events_and_the_affected_planes()
    {
        using var recipe = RendererPerfRecipe.Create("outdoors");
        using var json = JsonDocument.Parse(recipe.Describe());
        JsonElement schedule = json.RootElement.GetProperty("Schedule").GetProperty("Features").GetProperty("Clipmap");
        Check(schedule.GetProperty("Outward"), 33);
        Check(schedule.GetProperty("Return"), 224);
        Assert.Equal(2, schedule.GetProperty("ColdRebuilds").GetInt32());
        Assert.Equal(2, schedule.GetProperty("WarmupRebuilds").GetInt32());
        Assert.Equal(4, schedule.GetProperty("MeasuredRebuilds").GetInt32());
        static void Check(JsonElement e, int index)
        {
            Assert.Equal(index, e.GetProperty("Index").GetInt32());
            Assert.Equal(1, e.GetProperty("Plane0Rebuilds").GetInt32());
            Assert.Equal(1, e.GetProperty("Plane1Rebuilds").GetInt32());
        }
    }

    [Fact]
    public void No_water_frame_cannot_reuse_stale_water_stats()
    {
        using var recipe = RendererPerfRecipe.Create("outdoors");
        using var rig = new EffectsRig();
        recipe.ResetBlock(rig.Scene);
        rig.Frame(recipe, 64);
        Assert.NotNull(RendererPerfOutdoorsScene.CurrentWaterStats(rig.Scene));
        Assert.Equal(1, rig.Scene.LastWaterStats.OceanStalls);
        rig.Frame(null, 65);
        Assert.Equal(1, rig.Scene.LastWaterStats.OceanStalls);
        Assert.Null(RendererPerfOutdoorsScene.CurrentWaterStats(rig.Scene));
        Assert.Equal(0, rig.Commands.Tally[GpuCommandKind.Dispatch]);
    }

    static FakeGraphicsPipelineRequest? Shader(RecordingGpuCommandList.IndexedDraw d) =>
        (d.Pipeline as FakePipeline)?.Request;

    sealed class UiTrace(IGpuCommandList inner) : IGpuCommandList
    {
        internal readonly HashSet<IGpuTexture> Textures = [];
        internal readonly List<(uint X, uint Y, uint W, uint H)> Clips = [];
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            foreach (IGpuTexture texture in ((FakeResourceSet)set).Resources.OfType<IGpuTexture>()) Textures.Add(texture);
            inner.SetGraphicsResourceSet(slot, set);
        }
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
        { Clips.Add((x, y, w, h)); inner.SetScissorRect(index, x, y, w, h); }
        public void Begin() => inner.Begin();
        public void End() => inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) => inner.SetFramebuffer(fb);
        public void ClearColorTarget(uint index, Color color) => inner.ClearColorTarget(index, color);
        public void ClearDepthStencil(float depth) => inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline pipeline) => inner.SetPipeline(pipeline);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint offset) => inner.SetGraphicsResourceSet(slot, set, offset);
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer) => inner.SetVertexBuffer(slot, buffer);
        public void SetVertexBuffer(uint slot, IGpuBuffer buffer, uint offset) => inner.SetVertexBuffer(slot, buffer, offset);
        public void SetIndexBuffer(IGpuBuffer buffer, GpuIndexFormat format) => inner.SetIndexBuffer(buffer, format);
        public void SetFullScissorRects() => inner.SetFullScissorRects();
        public void Draw(uint count) => inner.Draw(count);
        public void Draw(uint count, uint instances, uint start, uint first) => inner.Draw(count, instances, start, first);
        public void DrawIndexed(uint count, uint instances, uint start, int offset, uint first) => inner.DrawIndexed(count, instances, start, offset, first);
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, in T data) where T : unmanaged => inner.UpdateBuffer(buffer, offset, in data);
        public void UpdateBuffer<T>(IGpuBuffer buffer, uint offset, ReadOnlySpan<T> data) where T : unmanaged => inner.UpdateBuffer(buffer, offset, data);
        public void CopyBuffer(IGpuBuffer a, uint ao, IGpuBuffer b, uint bo, uint count) => inner.CopyBuffer(a, ao, b, bo, count);
        public void CopyTexture(IGpuTexture a, IGpuTexture b) => inner.CopyTexture(a, b);
        public void CopyTextureSubresource(IGpuTexture a, uint mip, uint layer, IGpuTexture b, uint w, uint h) => inner.CopyTextureSubresource(a, mip, layer, b, w, h);
        public void CopyTextureSubresource(IGpuTexture a, uint am, uint al, IGpuTexture b, uint bm, uint bl, uint w, uint h) => inner.CopyTextureSubresource(a, am, al, b, bm, bl, w, h);
        public void GenerateMipmaps(IGpuTexture texture) => inner.GenerateMipmaps(texture);
        public void ResolveTexture(IGpuTexture a, IGpuTexture b) => inner.ResolveTexture(a, b);
        public void ResolveTexture(IGpuTexture a, IGpuTexture b, bool preserveSource) => inner.ResolveTexture(a, b, preserveSource);
        public void SetComputePipeline(IGpuComputePipeline pipeline) => inner.SetComputePipeline(pipeline);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint offset) => inner.SetComputeResourceSet(slot, set, offset);
        public void Dispatch(uint x, uint y, uint z) => inner.Dispatch(x, y, z);
        public void Dispose() { }
    }

    sealed class EffectsRig : IDisposable
    {
        internal readonly OpenListTrackingGpuDevice Device = new();
        internal readonly SpyGpuDevice Spy;
        readonly IGpuTexture _target;
        internal readonly IGpuFramebuffer Target;
        internal readonly Scene3D Scene;
        internal readonly CommandTallyGpuCommandList Commands;
        internal readonly RecordingGpuCommandList Record;
        internal EffectsRig()
        {
            _target = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(1920, 1080,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            Target = Device.Factory.CreateFramebuffer(null, _target);
            Spy = new SpyGpuDevice(Device);
            Scene = new Scene3D(Spy, Target.Outputs);
            Commands = new CommandTallyGpuCommandList(Device.Factory.CreateCommandList());
            Record = new RecordingGpuCommandList(Commands) { CommandTrace = [] };
        }
        internal void Frame(RendererPerfRecipe? recipe, int index)
        {
            Commands.Clear();
            Record.Clear();
            Record.CommandTrace!.Clear();
            Scene.Begin();
            recipe?.Queue(Scene, index);
            Scene.PrepareFrame();
            Record.Begin();
            Scene.RenderInternal(Record, 1920, 1080, Target);
            Record.End();
            Assert.Equal(1, Device.PeakOpenLists);
        }
        public void Dispose() { Scene.Dispose(); Record.Dispose(); Target.Dispose(); _target.Dispose(); Device.Dispose(); }
    }
}
