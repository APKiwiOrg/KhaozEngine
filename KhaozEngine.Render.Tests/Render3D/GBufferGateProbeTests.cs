using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class GBufferGateProbeTests
{
    [Theory]
    [InlineData(GBufferGate.Rigid, true)]
    [InlineData(GBufferGate.Splat, true)]
    [InlineData(GBufferGate.TileGround, true)]
    [InlineData(GBufferGate.Foliage, true)]
    [InlineData(GBufferGate.Skinned, true)]
    [InlineData(GBufferGate.Skinned, false)]
    public void Only_the_selected_main_draw_disappears_and_the_next_baseline_preserves_uploads_and_shadows(
        GBufferGate gate, bool gpuSkinning)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.FrustumCulling = false;
        scene.UseGpuSkinning = gpuSkinning;
        scene.ForceTemporalForTests = true;
        scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
        MeshHandle rigid = scene.LoadMesh(MeshPrimitives.Box(1));
        var splat = scene.LoadSplatMaterial(1, 1, Enumerable.Range(0, 5)
            .Select(_ => new SplatLayerImage { AlbedoRgba = [255, 255, 255, 255], NormalRgba = [128, 128, 255, 255] }).ToArray());
        MeshHandle terrain = scene.LoadMesh(MeshPrimitives.Box(1), splat);
        var ground = scene.LoadTileGroundMaterial(1, 1, [new TileGroundLayerImage { AlbedoRgba = [255, 255, 255, 255] }]);
        MeshHandle tile = scene.LoadMesh(MeshPrimitives.Box(1), ground);
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh skin);
        using FoliageBatch foliage = scene.CreateFoliageBatch([new FoliageInstance(rigid, Matrix4x4.Identity, .1f,
            CastsShadows: true)]);
        IFrameDiagnosticsGBufferGates probe = scene;
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CommandTrace = [] };

        void Frame()
        {
            commands.Clear();
            commands.CommandTrace!.Clear();
            scene.Begin();
            scene.Draw(rigid, Matrix4x4.Identity);
            scene.Draw(terrain, Matrix4x4.Identity);
            scene.Draw(tile, Matrix4x4.Identity);
            scene.DrawFoliage(foliage, Vector3.Zero, new FoliageRenderSettings { DrawRadius = 100, DistantDensity = 1 });
            scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity), skin.RestPose);
            scene.DrawOverlayMesh(rigid, Matrix4x4.Identity);
            scene.DrawBeam(Vector3.Zero, Vector3.UnitY, .1f, Color.White);
            scene.AddLight(new Vector3(0, 2, 0), Color.White, 6, 1, LightShadow.Dynamic);
            scene.PrepareFrame();
            scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        }

        Frame();
        Frame();
        Span<long> skipped = stackalloc long[5];
        Assert.Equal(-1, probe.ConsumeApplied(skipped)); // Default scene, with no recorder registration.
        Assert.Equal(new long[5], skipped.ToArray());
        Assert.Null(scene.PassObserver);
        probe.Apply(GBufferGate.None);
        Frame();
        Assert.Equal(0, probe.ConsumeApplied(skipped));
        var baseline = Draws(commands);
        long uploadBytes = commands.Uploads.Sum(u => (long)u.Bytes);
        ShadowPassDiagnostics shadows = scene.LastShadowPassDiagnostics;
        Assert.True(shadows.TotalDrawCalls > 0);
        Assert.True(shadows.PointFaceDrawCalls > 0);
        string vertex = gate switch
        {
            GBufferGate.Rigid => ShaderSources.ModelMotionVert,
            GBufferGate.Splat => ShaderSources.SplatMotionVert,
            GBufferGate.TileGround => ShaderSources.TileGroundMotionVert,
            GBufferGate.Foliage => ShaderSources.FoliageMotionVert,
            _ => gpuSkinning ? ShaderSources.SkinnedModelMotionVert : ShaderSources.ModelCpuSkinnedMotionVert,
        };
        string selected = Assert.Single(commands.IndexedDraws.Where(d => d.Pipeline is FakePipeline { Request: { } r }
            && r.VertexGlsl == vertex).Select(d => MotionTargetPrograms.Name(((FakePipeline)d.Pipeline!).Request!.Value)).Distinct());
        Assert.Equal(1, baseline[selected]);
        probe.Apply(gate);
        Frame();
        Assert.Equal((int)gate, probe.ConsumeApplied(skipped));
        Assert.Equal(1, skipped[(int)gate - 1]);
        Assert.Equal(1, skipped.ToArray().Sum());
        Assert.Equal(0, Draws(commands).GetValueOrDefault(selected));
        Assert.Equal(baseline.Where(p => p.Key != selected).OrderBy(p => p.Key), Draws(commands).OrderBy(p => p.Key));
        Assert.Equal(uploadBytes, commands.Uploads.Sum(u => (long)u.Bytes));
        Assert.Equal(shadows.TotalDrawCalls, scene.LastShadowPassDiagnostics.TotalDrawCalls);
        Assert.Equal(shadows.PointFaceDrawCalls, scene.LastShadowPassDiagnostics.PointFaceDrawCalls);
        probe.Apply(GBufferGate.Disabled);
        Frame();
        Assert.Equal(-1, probe.ConsumeApplied(skipped));
        Assert.Equal(new long[5], skipped.ToArray());
        Assert.Equal(baseline.OrderBy(p => p.Key), Draws(commands).OrderBy(p => p.Key));
        Assert.Equal(uploadBytes, commands.Uploads.Sum(u => (long)u.Bytes));
        Assert.Null(scene.PassObserver);
    }

    [Fact]
    public void Applied_state_accumulates_draws_across_renders_and_reports_disagreement_without_repeating_evidence()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        MeshHandle mesh = scene.LoadMesh(MeshPrimitives.Box(1));
        IFrameDiagnosticsGBufferGates probe = scene;
        void Frame()
        {
            scene.Begin();
            scene.Draw(mesh, Matrix4x4.Identity);
            harness.Render();
        }
        probe.Apply(GBufferGate.Rigid);
        Frame();
        Frame();
        Span<long> skipped = stackalloc long[5];
        Assert.Equal(1, probe.ConsumeApplied(skipped));
        Assert.Equal(new long[] { 2, 0, 0, 0, 0 }, skipped.ToArray());
        probe.Apply(GBufferGate.None);
        Frame();
        probe.Apply(GBufferGate.Rigid);
        Frame();
        Assert.Equal(-2, probe.ConsumeApplied(skipped));
        Assert.Equal(new long[] { 1, 0, 0, 0, 0 }, skipped.ToArray());
        Assert.Equal(-1, probe.ConsumeApplied(skipped));
        Assert.Equal(new long[5], skipped.ToArray());
    }

    static Dictionary<string, int> Draws(RecordingGpuCommandList commands) =>
        commands.CommandTrace!.Where(c => c.Kind == "draw")
            .Select(d => MotionTargetPrograms.Name(((FakePipeline)d.Pipeline!).Request!.Value))
            .GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
}
