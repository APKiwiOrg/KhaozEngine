using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("AllocSensitive")]
public sealed class RendererPerfGeometryTraceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Nests_every_cascade_face_and_geometry_route_once(bool gpu)
    {
        using var rig = new GeometryRig(gpu);
        rig.Frame();
        rig.Frame();
        var observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(rigidOffset: .2f);
        observer.AssertClosed();
        Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.directional");
        Assert.Equal(4, observer.Passes.Count(p => p.Key.Id == "shadow.cascade.rigid"));
        Assert.Equal(4, observer.Passes.Count(p => p.Key.Id == "shadow.cascade.foliage"));
        if (gpu) Assert.Equal(4, observer.Passes.Count(p => p.Key.Id == "shadow.cascade.gpu-skinned"));
        Assert.Equal(18, observer.Passes.Count(p => p.Key.Id.EndsWith(".face", StringComparison.Ordinal)));
        Assert.Equal(6, observer.Passes.Count(p => p.Key.Id == "shadow.point.static.face"));
        Assert.Equal(6, observer.Passes.Count(p => p.Key.Id == "shadow.point.dynamic.face"));
        Assert.Equal(6, observer.Passes.Count(p => p.Key.Id == "shadow.point.transient.face"));
        foreach (string route in new[] { "opaque.rigid", "opaque.splat", "opaque.tile-ground", "opaque.foliage", gpu ? "opaque.gpu-skinned" : "opaque.cpu-skinned" })
        {
            Assert.Single(observer.Passes, p => p.Key.Id == route);
            Assert.Contains(observer.Fused, k => k.Id == route);
        }
        Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.rigid.opaque");
        Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.rigid.cutout");
        Assert.Contains(observer.Passes, p => p.Key.Id == "shadow.rigid.dissolve");
        Assert.Contains(observer.Cpu, p => p == "cpu.geometry.grouping");
        Assert.Contains(observer.Cpu, p => p == "cpu.geometry.visibility");
        Assert.Contains(observer.Cpu, p => p == "cpu.geometry.upload-packing");
        Assert.All(rig.Commands.Events.Where(e => e.Op.StartsWith("draw", StringComparison.Ordinal) && IsGeometry(e.Pipeline)), e => Assert.NotNull(e.Owner));
        Assert.All(observer.Passes, p => Assert.True(p.Parent is null || observer.Passes.Any(q => q.Key == p.Parent)));
        Assert.All(rig.Commands.Events.Where(e => e.Owner is not null), e =>
            Assert.DoesNotContain(observer.Passes, p => p.Parent == e.Owner));
        Assert.Equal(rig.Commands.Events.Count(e => e.Owner is not null),
            observer.Passes.Sum(p => rig.Commands.Events.Count(e => e.Owner == p.Key)));
        var definitions = observer.Freeze();
        Assert.InRange(definitions.Length, 1, 255);
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        var adapter = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        rig.Scene.PassObserver = adapter;
        var traced = RendererPerfCommandList.Wrap(rig.Commands, recorder);
        recorder.BeginFrame(0, 1, 0);
        adapter.BeginFrame();
        rig.Frame(rigidOffset: .4f, observedCommands: traced);
        recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.Empty(sample.Errors);
        Assert.Contains(true, adapter.FusedMotionPasses.ToArray());
        Assert.Equal(rig.Commands.Events.Count(e => e.Op == "draw-indexed"), recorder.ReadCounter(0, RendererPerfCounter.IndexedDraws));
    }

    [Fact]
    public void Frozen_point_layout_survives_static_cache_reuse()
    {
        using var rig = new GeometryRig(true);
        rig.Frame(); rig.Frame();
        var observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(rigidOffset: .2f);
        var definitions = observer.Freeze();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        rig.Scene.PassObserver = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorder.BeginFrame(0, 0, 0);
        rig.Frame(rigidOffset: .2f, observedCommands: RendererPerfCommandList.Wrap(rig.Commands, recorder));
        recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.Empty(sample.Errors);
        Assert.Equal(RendererPerfMetricStatus.NotExecuted,
            Assert.Single(sample.Passes, p => p.Id == "shadow.point.static").Duration.Status);
    }

    [Fact]
    public void Cached_shadow_or_lut_emits_NotExecuted()
    {
        using var rig = new GeometryRig(true);
        rig.Frame(skin: false);
        rig.Frame(skin: false);
        var observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(skin: false);
        Assert.Contains(new RenderPassKey("shadow.directional"), observer.Skipped);
        Assert.Contains(new RenderPassKey("shadow.point.static", 0), observer.Skipped);
        Assert.Contains(new RenderPassKey("atmosphere.transmittance"), observer.Skipped);
        Assert.Contains(new RenderPassKey("atmosphere.multiple-scattering"), observer.Skipped);
        Assert.DoesNotContain(observer.Passes, p => p.Key.Id == "atmosphere.transmittance");
        Assert.Contains(observer.Passes, p => p.Key.Id == "atmosphere.sky-view");
        var definitions = observer.Freeze();
        var recorder = new RendererPerfRecorder(1, RendererPerfLayout.PerPass, definitions, () => 0, 1000);
        rig.Scene.PassObserver = new RendererPerfPassObserver(recorder, RendererPerfLayout.PerPass, definitions);
        recorder.BeginFrame(0, 0, 0);
        rig.Frame(skin: false, observedCommands: RendererPerfCommandList.Wrap(rig.Commands, recorder));
        recorder.EndFrame();
        var sample = recorder.Harvest(0);
        Assert.Empty(sample.Errors);
        foreach (string id in new[] { "shadow.directional", "shadow.point.static", "atmosphere.transmittance", "atmosphere.multiple-scattering" })
            Assert.Equal(RendererPerfMetricStatus.NotExecuted, Assert.Single(sample.Passes, p => p.Id == id).Duration.Status);
        rig.Scene.Post.Background = BackgroundMode.Starfield;
        observer = new GeometryObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(skin: false);
        Assert.Contains(observer.Passes, p => p.Key.Id == "background.starfield");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Null_observer_warm_frame_allocates_nothing(bool gpu)
    {
        using var rig = new GeometryRig(gpu);
        rig.Frame(); rig.Frame(); rig.Frame();
        rig.Frame();
        // Run the real warmed frame over a nonrecording list inside the checked allocation region.
        rig.Commands.Capture = false;
        AllocationMeasurement.NoPerCallAllocation("null scene pass observer", () => rig.Frame());
        var total = new RendererPerfRecorder(1, RendererPerfLayout.TotalOnly, [], () => 0, 1000);
        rig.Scene.PassObserver = new RendererPerfPassObserver(total, RendererPerfLayout.TotalOnly, []);
        total.BeginFrame(0, 0, 0);
        rig.Frame(observedCommands: RendererPerfCommandList.Wrap(rig.Commands, total));
        total.EndFrame();
        Assert.Empty(total.Harvest(0).Errors);
        Assert.Empty(total.Harvest(0).Passes);
        Assert.Equal(2u, total.MarkerCount);
    }

    [Theory]
    [InlineData("begin-pass")]
    [InlineData("end-pass")]
    [InlineData("skip-pass")]
    [InlineData("begin-cpu")]
    [InlineData("end-cpu")]
    [InlineData("fused")]
    public void Observer_failure_is_reported_after_recording_and_cannot_replace_frame_failure(string callback)
    {
        using var rig = new GeometryRig(true);
        rig.Frame();
        var failure = new InvalidOperationException("observer failed");
        rig.Scene.PassObserver = new ThrowingObserver(failure, callback);
        var error = Assert.Throws<InvalidOperationException>(() => rig.Frame());
        Assert.Same(failure, error.InnerException);
        Assert.Contains(rig.Commands.Events, e => e.Op == "draw-indexed");
        var renderFailure = new InvalidOperationException("render failed");
        Assert.Contains(rig.Commands.Events, e => e.Op == "draw-indexed"
            && e.Pipeline is FakePipeline { Request: { } request } && request.VertexGlsl == ShaderSources.ModelMotionVert);
        rig.Commands.FailureVertex = ShaderSources.ModelMotionVert;
        rig.Commands.Failure = renderFailure;
        Assert.Same(renderFailure, Assert.Throws<InvalidOperationException>(() => rig.Frame()));
        Assert.Same(failure, rig.Scene.PassObserverFailure);
    }

    static bool IsGeometry(IGpuPipeline? pipeline)
    {
        if ((pipeline as FakePipeline)?.Request is not { } request) return false;
        return request.VertexGlsl == ShaderSources.ModelVert || request.VertexGlsl == ShaderSources.ModelMotionVert
            || request.VertexGlsl == ShaderSources.SkinnedModelVert || request.VertexGlsl == ShaderSources.SkinnedModelMotionVert
            || request.VertexGlsl == ShaderSources.ShadowDepthVert || request.VertexGlsl == ShaderSources.ShadowDepthCutoutVert
            || request.VertexGlsl == ShaderSources.FoliageVert || request.VertexGlsl == ShaderSources.FoliageMotionVert
            || request.VertexGlsl == ShaderSources.FoliageDepthVert || request.VertexGlsl == ShaderSources.ShadowDepthDissolveVert
            || request.VertexGlsl == ShaderSources.SkinnedShadowDepthVert || request.VertexGlsl == ShaderSources.SkinnedShadowDepthDissolveVert
            || request.VertexGlsl == ShaderSources.PointShadowRigidVert || request.VertexGlsl == ShaderSources.PointShadowRigidDissolveVert
            || request.VertexGlsl == ShaderSources.PointShadowSkinnedVert || request.VertexGlsl == ShaderSources.PointShadowSkinnedDissolveVert
            || request.VertexGlsl == ShaderSources.PointShadowClearVert || request.VertexGlsl == ShaderSources.SplatMotionVert
            || request.VertexGlsl == ShaderSources.TileGroundMotionVert || request.VertexGlsl == ShaderSources.ModelCpuSkinnedMotionVert
            || request.FragmentGlsl == ShaderSources.AtmosphereTransmittanceFrag
            || request.FragmentGlsl == ShaderSources.AtmosphereMultiScatteringFrag
            || request.FragmentGlsl == ShaderSources.AtmosphereSkyViewFrag || request.FragmentGlsl == ShaderSources.SkyFrag
            || request.FragmentGlsl == ShaderSources.StarfieldFrag;
    }

    sealed class ThrowingObserver(Exception failure, string callback) : IRenderPassObserver
    {
        void Check(string kind) { if (kind == callback) throw failure; }
        public void BeginPass(IGpuCommandList commands, RenderPassKey key) => Check("begin-pass");
        public void EndPass(IGpuCommandList commands, RenderPassKey key) => Check("end-pass");
        public void SkipPass(IGpuCommandList commands, RenderPassKey key) => Check("skip-pass");
        public void BeginCpuPhase(string id) => Check("begin-cpu");
        public void EndCpuPhase(string id) => Check("end-cpu");
        public void FusedMotion(IGpuCommandList commands, RenderPassKey key) => Check("fused");
    }

    internal sealed class GeometryObserver(GeometryTraceCommands commands) : IRenderPassObserver
    {
        internal readonly List<(RenderPassKey Key, RenderPassKey? Parent)> Passes = [];
        internal readonly List<RenderPassKey> Skipped = [], Fused = [];
        internal readonly List<string> Cpu = [];
        readonly List<(RenderPassKey Key, int Kind)> _events = [];
        readonly Stack<RenderPassKey> _stack = new();
        readonly Stack<string> _cpu = new();
        public void BeginPass(IGpuCommandList cl, RenderPassKey key)
        {
            Assert.DoesNotContain(Passes, p => p.Key == key);
            _events.Add((key, 0));
            Passes.Add((key, _stack.TryPeek(out var parent) ? parent : null));
            _stack.Push(key); commands.Owner = key;
        }
        public void EndPass(IGpuCommandList cl, RenderPassKey key)
        {
            _events.Add((key, 1));
            Assert.Equal(key, _stack.Pop());
            commands.Owner = _stack.TryPeek(out var parent) ? parent : null;
        }
        public void SkipPass(IGpuCommandList cl, RenderPassKey key) { Skipped.Add(key); _events.Add((key, 2)); }
        public void BeginCpuPhase(string id)
        {
            Cpu.Add(id); _cpu.Push(id);
            if (!id.StartsWith("device.", StringComparison.Ordinal)) return;
            var key = new RenderPassKey(id);
            Assert.DoesNotContain(Passes, p => p.Key == key);
            _events.Add((key, 3));
            Passes.Add((key, _stack.TryPeek(out var parent) ? parent : null));
            _stack.Push(key); commands.Owner = key;
        }
        public void EndCpuPhase(string id)
        {
            Assert.Equal(id, _cpu.Pop());
            if (!id.StartsWith("device.", StringComparison.Ordinal)) return;
            var key = new RenderPassKey(id);
            _events.Add((key, 4));
            Assert.Equal(key, _stack.Pop());
            commands.Owner = _stack.TryPeek(out var parent) ? parent : null;
        }
        public void FusedMotion(IGpuCommandList cl, RenderPassKey key) => Fused.Add(key);
        internal void AssertClosed() { Assert.Empty(_stack); Assert.Empty(_cpu); }
        internal RendererPerfPassDefinition[] Freeze()
        {
            var definitions = new List<RendererPerfPassDefinition>();
            var stack = new Stack<int>();
            uint marker = 1;
            foreach (var entry in _events)
            {
                if (entry.Kind is 1 or 4)
                {
                    int index = stack.Pop();
                    if (entry.Kind == 1) definitions[index] = definitions[index] with { EndMarker = marker++ };
                }
                else
                {
                    int index = definitions.Count;
                    definitions.Add(new(entry.Key.Id, entry.Key.Occurrence, stack.TryPeek(out int parent) ? parent : -1,
                        entry.Kind == 3 ? 0 : marker++, entry.Kind == 2 ? marker++ : 0, DeviceOnly: entry.Kind == 3));
                    if (entry.Kind is 0 or 3) stack.Push(index);
                }
            }
            return definitions.ToArray();
        }
    }

    internal sealed class GeometryRig : IDisposable
    {
        readonly FakeGpuDevice _device = new();
        readonly IGpuTexture _texture;
        readonly IGpuFramebuffer _target;
        readonly MeshHandle _box, _cutout, _splat, _tile;
        readonly SkinnedGltfMesh _tube;
        readonly SkinnedMeshHandle _skin;
        readonly FoliageBatch _foliage;
        readonly bool _secondStaticRow;
        internal readonly Scene3D Scene;
        internal readonly GeometryTraceCommands Commands;
        internal GeometryRig(bool gpu, bool secondStaticRow = false)
        {
            _secondStaticRow = secondStaticRow;
            _texture = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(32, 32, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            _target = _device.Factory.CreateFramebuffer(null, _texture);
            var shadows = new ShadowSettings { Mode = ShadowMode.ShadowMap, ShadowCascadeCount = 4, ShadowMapResolution = 64 };
            shadows.PointShadows.MaxStaticRebuildsPerFrame = 1;
            shadows.PointShadows.MaxDynamicLightsPerFrame = 1;
            Scene = new Scene3D(_device, _target.Outputs, shadows) { UseGpuSkinning = gpu, FrustumCulling = false };
            Scene.Post.Atmosphere = AtmosphereSettings.Earth(); Scene.Post.Atmosphere.Enabled = true;
            Scene.Post.Background = BackgroundMode.Sky;
            Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            _box = Scene.LoadMesh(MeshPrimitives.Box(.5f));
            var splat = Scene.LoadSplatMaterial(1, 1, Enumerable.Range(0, 5).Select(_ => new SplatLayerImage
            { AlbedoRgba = new byte[] { 255, 255, 255, 255 }, NormalRgba = new byte[] { 128, 128, 255, 255 } }).ToArray());
            _splat = Scene.LoadMesh(MeshPrimitives.Box(.5f), splat);
            var tile = Scene.LoadTileGroundMaterial(1, 1, new[] { new TileGroundLayerImage
                { AlbedoRgba = new byte[] { 255, 255, 255, 255 } } });
            _tile = Scene.LoadMesh(MeshPrimitives.Box(.5f), tile);
            var albedo = Scene.LoadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            _cutout = Scene.LoadMesh(MeshPrimitives.Box(.5f), new Scene3D.SurfaceMaps(albedo, alphaCutoff: .5f));
            _tube = SkinnedMeshBuilder.BuildTube(.35f, 2f, 4, 6, 3, Axis.Z);
            _skin = Scene.LoadSkinnedMesh(_tube);
            _foliage = Scene.CreateFoliageBatch([new(_cutout, Matrix4x4.Identity, .1f, CastsShadows: true)]);
            Commands = new GeometryTraceCommands(new NullGpuCommandList());
        }
        internal void Frame(bool skin = true, float rigidOffset = 0f, IGpuCommandList? observedCommands = null,
            float secondRigidOffset = 0f)
        {
            Commands.Events.Clear();
            Scene.Begin();
            Scene.Draw(_box, Matrix4x4.CreateTranslation(rigidOffset, 0f, 0f));
            if (_secondStaticRow) Scene.Draw(_box, Matrix4x4.CreateTranslation(20f + secondRigidOffset, 0f, 0f));
            Scene.Draw(_cutout, Matrix4x4.CreateTranslation(1f, 0f, 0f));
            Scene.Draw(_splat, Matrix4x4.CreateTranslation(0f, -1f, 0f));
            Scene.Draw(_tile, Matrix4x4.CreateTranslation(0f, -2f, 0f));
            Scene.Draw(new RigidInstanceDraw(_box, Matrix4x4.CreateTranslation(-1f, 0f, 0f)) { Dissolve = .3f });
            if (skin) Scene.DrawFoliage(_foliage, Vector3.Zero, new FoliageRenderSettings { DrawRadius = 100f });
            if (skin)
            {
                Scene.DrawSkinned(_skin, _tube.RestPose, Matrix4x4.Identity, Color.White);
                Scene.DrawSkinned(_skin, _tube.RestPose, Matrix4x4.CreateTranslation(.5f, 0f, 0f), Color.White,
                    Material.None, .3f, .1f, Color.White, castsShadows: true);
            }
            Scene.AddLight(new(0f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Static(7));
            if (_secondStaticRow) Scene.AddLight(new(20f, 1f, 1f), Color.White, 6f, 1f, LightShadow.Static(8));
            Scene.AddLight(new(1f, 1f, 1f), Color.White, 10f, 1f, LightShadow.Dynamic);
            Scene.PrepareFrame();
            var commands = observedCommands ?? Commands;
            commands.Begin();
            try { Scene.RenderInternal(commands, 32, 32, _target); }
            finally { commands.End(); }
        }
        public void Dispose() { _foliage.Dispose(); Scene.Dispose(); Commands.Dispose(); _target.Dispose(); _texture.Dispose(); _device.Dispose(); }
    }
}
