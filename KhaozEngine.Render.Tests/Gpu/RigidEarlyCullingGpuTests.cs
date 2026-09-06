using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class RigidEarlyCullingGpuTests(RigidEarlyCullingScene fixture)
    : IClassFixture<RigidEarlyCullingScene>
{
    static readonly Color Grass = new(0.25f, 0.7f, 0.2f, 1f);
    static int Stride => Unsafe.SizeOf<ModelRenderer.InstanceData>();

    [GpuFact]
    public void OffscreenOptOutsDoNotUploadOrSplitVisibleDraws()
    {
        var reference = fixture.Capture(s => DrawPopulation(s, fixture.Box, 0));
        var off = fixture.Capture(s => DrawPopulation(s, fixture.Box, 4096), culling: false);
        var on = fixture.Capture(s => DrawPopulation(s, fixture.Box, 4096));

        Assert.Equal(reference.Pixels, off.Pixels);
        Assert.Equal(off.Pixels, on.Pixels);
        Assert.Equal(4100, off.Drawn);
        Assert.Equal(0, off.Culled);
        Assert.Equal(4, on.Drawn);
        Assert.Equal(4096, on.Culled);
        Assert.Equal(4100L * Stride, off.Stats.InstanceUploadBytes);
        Assert.Equal(4L * Stride, on.Stats.InstanceUploadBytes);
        Assert.Equal(reference.Stats.DrawCalls, on.Stats.DrawCalls);
    }

    [GpuFact]
    public void HiddenFirstInstanceDoesNotReorderOverlappingMeshBuckets()
    {
        void Draw(Scene3D scene)
        {
            scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(1000f, 0f, 0f), Color.White, Material.None, false);
            scene.Draw(fixture.OtherBox, Matrix4x4.Identity, new Color(0.1f, 0.2f, 0.9f, 1f), Material.None, false);
            scene.Draw(fixture.Box, Matrix4x4.Identity, new Color(0.9f, 0.2f, 0.1f, 1f), Material.None, false);
        }

        var off = fixture.Capture(Draw, culling: false);
        var on = fixture.Capture(Draw);
        Assert.Equal(off.Pixels, on.Pixels);
        Assert.Equal(2L * Stride, on.Stats.InstanceUploadBytes);
        Assert.Equal(2, on.Drawn);
        Assert.Equal(1, on.Culled);
    }

    [GpuFact]
    public void NonzeroRenderOriginCullsAbsoluteBoundsAndUploadsOnlyVisibleInstances()
    {
        var center = new Vector3(1024f, 0f, -1024f);
        void Draw(Scene3D scene) => DrawPopulation(scene, fixture.Box, 128, center);
        var off = fixture.Capture(Draw, culling: false, center);
        var on = fixture.Capture(Draw, center: center);
        Assert.True(fixture.Scene.RenderOriginActive);
        Assert.Equal(off.Pixels, on.Pixels);
        Assert.Equal(4, on.Drawn);
        Assert.Equal(128, on.Culled);
        Assert.Equal(4L * Stride, on.Stats.InstanceUploadBytes);
    }

    [GpuFact]
    public void OffscreenCastersSurviveCompactionWithTheirShadowMapUnchanged()
    {
        void Draw(Scene3D scene, bool addOptOuts)
        {
            if (addOptOuts)
                for (int i = 0; i < 128; i++)
                    scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(1000f + i, 0f, 0f), Grass, Material.None, false);
            scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(-10f, 0.9f, 0f));
            scene.Draw(fixture.Floor, Matrix4x4.Identity);
        }

        var reference = fixture.Capture(s => Draw(s, false), shadows: true);
        float[] depthBefore = fixture.Scene.DebugReadShadowMap(out _, out _);
        var populated = fixture.Capture(s => Draw(s, true), shadows: true);
        float[] depthAfter = fixture.Scene.DebugReadShadowMap(out _, out _);

        Assert.Equal(reference.Pixels, populated.Pixels);
        Assert.Equal(depthBefore, depthAfter);
        Assert.Equal(2L * Stride, populated.Stats.InstanceUploadBytes);
        Assert.Equal(reference.Drawn, populated.Drawn);
        Assert.Equal(reference.Culled + 128, populated.Culled);
        Assert.True(reference.Culled >= 1);
    }

    [GpuFact]
    public void FullyRejectedFrameUploadsNothingAndNextFrameResetsCounters()
    {
        var rejected = fixture.Capture(s => s.Draw(fixture.Box,
            Matrix4x4.CreateTranslation(1000f, 0f, 0f), Grass, Material.None, false));
        Assert.Equal(0, rejected.Drawn);
        Assert.Equal(1, rejected.Culled);
        Assert.Equal(0, rejected.Stats.InstanceUploadBytes);

        var empty = fixture.Capture(_ => { });
        Assert.Equal(0, empty.Drawn);
        Assert.Equal(0, empty.Culled);
        Assert.Equal(0, empty.Stats.InstanceUploadBytes);
    }

    [GpuFact]
    public void StaleOptOutHandleIsKeptConservativelyWhenSlotIsReused()
    {
        Scene3D scene = fixture.Scene;
        MeshHandle stale = scene.LoadMesh(MeshPrimitives.Box(0.6f));
        scene.UnloadMesh(stale);
        MeshHandle replacement = scene.LoadMesh(MeshPrimitives.Box(0.6f));
        try
        {
            Assert.Equal(stale.Index, replacement.Index);
            var frame = fixture.Capture(s =>
            {
                s.Draw(stale, Matrix4x4.CreateTranslation(1000f, 0f, 0f), Grass, Material.None, false);
                s.Draw(replacement, Matrix4x4.Identity, Grass, Material.None, false);
            });
            Assert.Equal(2, frame.Drawn);
            Assert.Equal(0, frame.Culled);
            Assert.Equal(2L * Stride, frame.Stats.InstanceUploadBytes);
            Assert.Equal(1, frame.Stats.Instances);
        }
        finally { scene.UnloadMesh(replacement); }
    }

    [GpuFact]
    public void ReusedSceneMatchesFreshSceneAfterOtherCullingConfigurations()
    {
        fixture.Capture(s => DrawPopulation(s, fixture.Box, 128), culling: false, new Vector3(1024f, 0f, 0f));
        fixture.Capture(s => s.Draw(fixture.Floor, Matrix4x4.Identity), shadows: true);
        var reused = fixture.Capture(s => DrawPopulation(s, fixture.Box, 128));
        MeshHandle box = default;
        byte[] fresh = Render3DSnapshot.Capture(RigidEarlyCullingScene.Width, RigidEarlyCullingScene.Height,
            setup: s =>
            {
                RigidEarlyCullingScene.Configure(s, true, Vector3.Zero, false);
                box = s.LoadMesh(MeshPrimitives.Box(0.6f));
            }, drawFrame: s => DrawPopulation(s, box, 128));
        Assert.Equal(fresh, reused.Pixels);
    }

    [GpuFact]
    public void SeparateMeshOptOutEnteringAndLeavingViewKeepsTheShadowAtlas()
        => CheckShadowReuse(sameMeshGap: false);

    [GpuFact]
    public void OptOutGapBetweenSameMeshCastersKeepsTheShadowAtlas()
        => CheckShadowReuse(sameMeshGap: true);

    void CheckShadowReuse(bool sameMeshGap)
    {
        float optOutX = 0f, casterX = -1f;
        void Draw(Scene3D scene)
        {
            if (!sameMeshGap)
                scene.Draw(fixture.OtherBox, Matrix4x4.CreateTranslation(optOutX, 0.3f, 0f), Grass, Material.None, false);
            scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(casterX, 0.3f, 0f));
            if (sameMeshGap)
                scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(optOutX, 0.3f, 0f), Grass, Material.None, false);
            scene.Draw(fixture.Box, Matrix4x4.CreateTranslation(1f, 0.3f, 0f));
            scene.Draw(fixture.Floor, Matrix4x4.Identity);
        }

        fixture.Capture(Draw, shadows: true);
        fixture.Capture(Draw, shadows: true);
        Assert.True(fixture.Scene.ShadowPassSkippedLastFrame);
        float[] depth = fixture.Scene.DebugReadShadowMap(out _, out _);

        foreach (float position in new[] { 1000f, 0f, 1000f })
        {
            optOutX = position;
            var frame = fixture.Capture(Draw, shadows: true);
            Assert.Equal((position == 0f ? 4L : 3L) * Stride, frame.Stats.InstanceUploadBytes);
            var diagnostics = fixture.Scene.LastShadowPassDiagnostics;
            Assert.False(diagnostics.LightMatrixChanged);
            Assert.False(diagnostics.CasterDataChanged);
            Assert.True(diagnostics.Skipped);
            Assert.Equal(depth, fixture.Scene.DebugReadShadowMap(out _, out _));
        }

        casterX = -2f;
        fixture.Capture(Draw, shadows: true);
        Assert.True(fixture.Scene.LastShadowPassDiagnostics.CasterDataChanged);
        Assert.False(fixture.Scene.ShadowPassSkippedLastFrame);
        Assert.NotEqual(depth, fixture.Scene.DebugReadShadowMap(out _, out _));
        fixture.Capture(Draw, shadows: true);
        Assert.True(fixture.Scene.ShadowPassSkippedLastFrame);
    }

    static void DrawPopulation(Scene3D scene, MeshHandle mesh, int offscreen, Vector3 center = default)
    {
        for (int group = 0; group < 4; group++)
        {
            scene.Draw(mesh, Matrix4x4.CreateTranslation(center + new Vector3(group - 1.5f, 0f, 0f)),
                Grass, Material.None, false);
            for (int i = group; i < offscreen; i += 4)
                scene.Draw(mesh, Matrix4x4.CreateTranslation(center + new Vector3(1000f + i, 0f, 0f)),
                    Grass, Material.None, false);
        }
    }
}

public sealed class RigidEarlyCullingScene : IDisposable
{
    public const int Width = 240, Height = 160;
    GpuDeviceContext? _gpu;
    Render3DPreview? _preview;
    public MeshHandle Box { get; private set; }
    public MeshHandle OtherBox { get; private set; }
    public MeshHandle Floor { get; private set; }

    public Scene3D Scene
    {
        get
        {
            if (_preview != null) return _preview.Scene;
            _gpu = GpuDeviceContext.CreateHeadless();
            _preview = new Render3DPreview(_gpu.GpuDevice, Width, Height);
            Box = _preview.Scene.LoadMesh(MeshPrimitives.Box(0.6f));
            OtherBox = _preview.Scene.LoadMesh(MeshPrimitives.Box(0.6f));
            Floor = _preview.Scene.LoadMesh(MeshPrimitives.Tile(12f, 0.1f));
            return _preview.Scene;
        }
    }

    public Frame Capture(Action<Scene3D> draw, bool culling = true, Vector3 center = default, bool shadows = false)
    {
        Configure(Scene, culling, center, shadows);
        _preview!.Capture(draw);
        byte[] pixels = _preview.ReadbackRgba();
        return new Frame(pixels, Scene.LastFrameStats, Scene.DrawnInstances, Scene.CulledInstances);
    }

    public static void Configure(Scene3D scene, bool culling, Vector3 center, bool shadows)
    {
        scene.FrustumCulling = culling;
        scene.RenderOrigin = null;
        scene.Post.TransparentBackground = false;
        scene.Post.Starfield = false;
        scene.Post.BackgroundColor = new Color(0.08f, 0.10f, 0.14f, 1f);
        scene.Post.Quality.Shadows.Mode = shadows ? ShadowMode.ShadowMap : ShadowMode.Off;
        scene.Post.Quality.Shadows.ShadowNearDistance = 10f;
        scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
        scene.Camera.Frame(center, new Vector3(5f, 4f, 5f));
        scene.EffectTimeSeconds = 0f;
    }

    public readonly record struct Frame(byte[] Pixels, RenderFrameStats Stats, int Drawn, int Culled);

    public void Dispose()
    {
        _gpu?.GpuDevice.WaitForIdle();
        _preview?.Dispose();
        _gpu?.Dispose();
    }
}
