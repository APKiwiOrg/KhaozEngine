using System;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRecipeTests
{
    [Fact]
    public void Changing_actor_shadow_submission_changes_the_construction_digest()
    {
        using var baseline = RendererPerfRecipe.Create("crowd");
        using var changed = new RendererPerfCrowdScene(RendererPerfCrowdScene.DefaultActorPolicy with { CastsShadows = false });
        changed.Freeze();
        Assert.Equal(baseline.Facts.SettingsSha256, changed.Facts.SettingsSha256);
        Assert.Equal(baseline.Facts.SequenceSha256, changed.Facts.SequenceSha256);
        Assert.NotEqual(baseline.Facts.RecipeSha256, changed.Facts.RecipeSha256);
        Assert.NotEqual(baseline.Facts.GeometrySha256, changed.Facts.GeometrySha256);

        using var rig = new HeadlessSceneRig();
        changed.ResetBlock(rig.Scene);
        rig.Frame(1920, 1080, scene => changed.Queue(scene, 64));
        Assert.Equal(32, rig.Scene.DrawnSkinnedInstances);
        Assert.False(rig.Scene.LastShadowPassDiagnostics.AnySkinnedCaster);
    }

    [Fact]
    public void Fixed_topology_counts_and_seed_have_stable_digest()
    {
        foreach (var (id, terrain, buildings, props, actors) in new[]
        {
            ("empty", 0, 0, 0, 0), ("village", 64, 144, 1024, 0), ("crowd", 16, 0, 128, 32),
        })
        {
            using var recipe = RendererPerfRecipe.Create(id);
            using var repeated = RendererPerfRecipe.Create(id);
            Assert.Equal((terrain, buildings, props, actors),
                (recipe.Facts.TerrainChunks, recipe.Facts.BuildingComponents,
                    recipe.Facts.PropInstances, recipe.Facts.ActorInstances));
            Assert.Equal(recipe.Facts, repeated.Facts);
            Assert.Equal(recipe.Describe(), repeated.Describe());
            using JsonDocument frozen = JsonDocument.Parse(recipe.Describe());
            Assert.Equal(0x51ABC0DEu, frozen.RootElement.GetProperty("Seed").GetUInt32());
            Assert.All(recipe.Terrain, mesh =>
            {
                Assert.Equal(1089, mesh.Vertices.Length);
                Assert.Equal(6144, mesh.Indices32.Length);
                Assert.All(mesh.Indices32, index => Assert.InRange(index, 0u, 1088u));
            });
            Assert.Equal(terrain, recipe.Terrain.Distinct().Count());
            using var rig = new HeadlessSceneRig();
            recipe.ResetBlock(rig.Scene);
            using var commands = new RecordingGpuCommandList(rig.Device.Factory.CreateCommandList());
            rig.Frame(1920, 1080, scene => recipe.Queue(scene, 64), commands);
            Assert.Equal(terrain, commands.IndexedDraws.Count(draw =>
                draw.Pipeline is FakePipeline { Request: { } request }
                && request.VertexGlsl == ShaderSources.SplatMotionVert));
            Assert.Equal(terrain + buildings + props, rig.Scene.DrawnInstances);
            Assert.Equal(0, rig.Scene.CulledInstances);
            Assert.Equal(terrain == 0 ? 0 : 1, rig.Scene.LiveSplatMaterialCount);
            Assert.Equal(actors > 0, rig.Scene.LastShadowPassDiagnostics.AnySkinnedCaster);
            if (id == "empty")
            {
                Assert.Equal(AntiAliasing.Off, rig.Scene.Post.Quality.AntiAliasing);
                Assert.False(rig.Scene.Post.Hdr.Enabled || rig.Scene.Post.Bloom.Enabled || rig.Scene.Post.Atmosphere.Enabled);
                Assert.Equal(0, rig.Scene.PointShadowedLights);
            }
            if (id == "village")
            {
                rig.Frame(1920, 1080, scene => recipe.Queue(scene, 64));
                Assert.True(rig.Scene.LastShadowPassDiagnostics.Skipped);
            }
        }
    }

    [Fact]
    public void Periodic_camera_and_pose_use_a_continuous_resettable_block_clock()
    {
        using var recipe = RendererPerfRecipe.Create("crowd");
        Assert.Equal(Enumerable.Range(64, 192), RendererPerfRecipe.WarmupIndices);
        Assert.Equal(Enumerable.Range(0, 256), RendererPerfRecipe.MeasuredIndices);
        Assert.Equal(recipe.Frame(0), recipe.Frame(256));
        Assert.Equal(recipe.Frame(255), recipe.Frame(-1));
        Assert.NotEqual(recipe.Frame(0), recipe.Frame(1));
        Assert.Equal(1f / 60f, RendererPerfRecipe.Dt);
        using var first = new HeadlessSceneRig();
        recipe.ResetBlock(first.Scene);
        Assert.False(first.Scene.TemporalHistory.IsValid);
        using var second = new HeadlessSceneRig();
        foreach (var rig in new[] { first, second })
        {
            if (ReferenceEquals(rig, second)) recipe.ResetBlock(rig.Scene);
            int ordinal = 64;
            foreach (int index in RendererPerfRecipe.WarmupIndices.Concat(RendererPerfRecipe.MeasuredIndices))
            {
                rig.Scene.Begin();
                recipe.Queue(rig.Scene, index);
                Assert.Equal(ordinal++ * (1f / 60f), rig.Scene.EffectTimeSeconds);
                Assert.Equal(recipe.Frame(index).ViewProjection, rig.Scene.Camera.ViewProjection);
                Assert.Equal(recipe.Frame(index).LightDirection, rig.Scene.Post.LightDirection);
            }
            Assert.Equal(512, ordinal);
            Assert.Equal(511f * (1f / 60f), rig.Scene.EffectTimeSeconds);
        }
        Assert.Throws<InvalidOperationException>(() => recipe.ResetBlock(first.Scene));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("village")]
    [InlineData("crowd")]
    [InlineData("outdoors")]
    [InlineData("effects-ui")]
    public void Frozen_sequence_includes_absolute_effect_times(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id);
        using var json = JsonDocument.Parse(recipe.Describe());
        JsonElement frames = json.RootElement.GetProperty("BlockFrames");
        Assert.Equal(448, frames.GetArrayLength());
        Assert.Collection(new[] { frames[0], frames[191], frames[192], frames[447] },
            frame => Check(frame, 64, 64, 64f / 60f),
            frame => Check(frame, 255, 255, 255f / 60f),
            frame => Check(frame, 256, 0, 256f / 60f),
            frame => Check(frame, 511, 255, 511f * (1f / 60f)));

        static void Check(JsonElement frame, int ordinal, int index, float seconds)
        {
            Assert.Equal(ordinal, frame.GetProperty("Ordinal").GetInt32());
            Assert.Equal(index, frame.GetProperty("SequenceIndex").GetInt32());
            Assert.Equal(seconds, frame.GetProperty("EffectTimeSeconds").GetSingle(), precision: 6);
        }
    }

    [Fact]
    public void Crowd_uses_32_visible_gpu_actors_and_declared_shadow_rows()
    {
        using var recipe = RendererPerfRecipe.Create("crowd");
        using var rig = new HeadlessSceneRig();
        recipe.ResetBlock(rig.Scene);
        for (int frame = 64; frame < 68; frame++)
            rig.Frame(1920, 1080, scene => recipe.Queue(scene, frame));
        using JsonDocument frozen = JsonDocument.Parse(recipe.Describe());
        JsonElement actor = frozen.RootElement.GetProperty("Geometry").GetProperty("Actor");
        Assert.Equal(13640, actor.GetProperty("Vertices").GetInt32());
        Assert.Equal(81600, actor.GetProperty("Indices").GetInt32());
        Assert.Equal(48, actor.GetProperty("Bones").GetInt32());
        Assert.Equal(96, actor.GetProperty("VertexStride").GetInt32());
        using var commands = new RecordingGpuCommandList(rig.Device.Factory.CreateCommandList());
        foreach (int index in RendererPerfRecipe.MeasuredIndices)
        {
            commands.Clear();
            rig.Frame(1920, 1080, s => recipe.Queue(s, index), commands);
            Assert.Equal(32, rig.Scene.DrawnSkinnedInstances);
            Assert.Equal(0, rig.Scene.CulledSkinnedInstances);
            Assert.Equal(32, commands.IndexedDraws.Count(draw =>
                draw.Pipeline is FakePipeline { Request: { } request }
                && request.VertexGlsl == ShaderSources.SkinnedModelMotionVert));
            Assert.Equal(0, rig.Scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointDynamicRenders);
            Assert.Equal(1, rig.Scene.LastShadowPassDiagnostics.PointTransientRowsRendered);
        }
        Scene3D scene = rig.Scene;
        Assert.True(scene.UseGpuSkinning);
        Assert.True(scene.FrustumCulling);
        Assert.Equal(32, scene.DrawnSkinnedInstances);
        Assert.Equal(0, scene.CulledSkinnedInstances);
        Assert.Equal(32, scene.QueuedSkinnedInstancesForTests.Select(i => i.Mesh).Distinct().Count());
        Assert.Equal(32, Enumerable.Range(0, 32).Select(scene.GpuSkinnedMotionForTests).Distinct().Count());
        Assert.All(Enumerable.Range(0, 32), i => Assert.False(scene.GpuSkinnedMotionForTests(i).IsNone));
        Assert.Equal(4, scene.LastShadowPassDiagnostics.CascadeCount);
        Assert.Equal(2048, scene.Post.Quality.Shadows.ShadowMapResolution);
        Assert.Equal(256, scene.ResolvedPointShadows.FaceResolution);
        Assert.Equal(2, scene.PointShadowedLights);
        Assert.Equal(0, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
        Assert.Equal(1, scene.LastShadowPassDiagnostics.PointDynamicRenders);
        Assert.Equal(1, scene.LastShadowPassDiagnostics.PointTransientRowsRendered);
        Assert.True(scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls > 0);
        Assert.True(scene.LastShadowPassDiagnostics.PointStaticTransientSkinnedDrawCalls > 0);
        Assert.True(scene.TemporalHistory.IsValid);
        Assert.Equal(AntiAliasing.Temporal, scene.Post.Quality.AntiAliasing);
        Assert.Equal(TemporalUpscale.Native, scene.Post.Temporal.Upscale);
        Assert.True(scene.Post.Hdr.Enabled && scene.Post.Bloom.Enabled && scene.Post.Atmosphere.Enabled);
    }

    [Fact]
    public void Recipe_contains_no_consumer_asset()
    {
        foreach (string id in new[] { "empty", "village", "crowd" })
        {
            using var recipe = RendererPerfRecipe.Create(id);
            using JsonDocument json = JsonDocument.Parse(recipe.Describe());
            Assert.Equal("public-engine-primitives/1", json.RootElement.GetProperty("Provenance").GetString());
            Assert.Empty(json.RootElement.GetProperty("ExternalAssets").EnumerateArray());
            Assert.Equal(64, recipe.Facts.AssetSha256.Length);
            string text = System.Text.Encoding.UTF8.GetString(recipe.Describe());
            Assert.DoesNotContain("Grimhollow", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".glb", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/Users/", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
