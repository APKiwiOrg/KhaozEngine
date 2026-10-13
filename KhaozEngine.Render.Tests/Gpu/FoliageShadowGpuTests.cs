using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Imaging;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class FoliageShadowGpuTests(FoliageShadowGpuScene fixture) : IClassFixture<FoliageShadowGpuScene>
{
    static readonly FoliageMotionProfile Shrub = new(.3f, .15f, .4f, .035f);
    static FoliageRenderSettings Solid => new() { DrawRadius = 100f, DistantDensity = 1f };
    static FoliageRenderSettings Wind => Solid with { WindStrength = .5f };
    static FoliageInteractor[] Actors =>
    [
        new(new Vector3(-.25f, 0f, 0f), 2f, 1f, 1f),
        new(new Vector3(-.5f, 0f, .1f), 2f, 1f, 1f),
        new(new Vector3(-.75f, 0f, -.1f), 2f, 1f, 1f),
        new(new Vector3(-1f, 0f, 0f), 2f, 1f, 1f),
    ];

    [GpuFact]
    public void AnimatedCascadeShadowMatchesDisplacedRigidReference()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(
            [new(fixture.Shrub, Matrix4x4.Identity, .1f, Shrub, true)]);
        fixture.Capture(_ => { });
        byte[] animated = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Wind, Actors));
        float[] animatedDepth = scene.DebugReadShadowMap(out _, out _);
        Assert.True(scene.FoliageShadowDrawCalls > 0);
        Assert.False(scene.LastShadowPassDiagnostics.AnySkinnedCaster);
        Assert.Equal(0, scene.LastShadowPassDiagnostics.RigidDrawCalls);
        Assert.False(scene.LastShadowPassDiagnostics.CasterDataChanged);
        MeshHandle reference = OracleMesh(FoliageShadowGpuScene.ShrubMesh(), Matrix4x4.Identity, Wind, Actors);
        try
        {
            byte[] rigid = fixture.Capture(s => s.Draw(reference, Matrix4x4.Identity));
            AssertPixelsMatch(rigid, animated);
            AssertDepthMatches(scene.DebugReadShadowMap(out _, out _), animatedDepth);
            float[] stillDepth;
            fixture.Capture(s => s.Draw(fixture.Shrub, Matrix4x4.Identity));
            stillDepth = scene.DebugReadShadowMap(out _, out _);
            Assert.NotEqual(stillDepth, animatedDepth);
        }
        finally { scene.UnloadMesh(reference); }
        fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Wind, Actors));
        fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Wind, Actors));
        Assert.False(scene.ShadowPassSkippedLastFrame);
    }

    [GpuFact]
    public void CameraWindFadeAlsoControlsTheDepthPass()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(
            [new(fixture.Shrub, Matrix4x4.Identity, .1f, Shrub, true)]);
        var camera = new FlyCamera3D
        {
            Position = new Vector3(0f, 3f, 9f), Yaw = MathF.PI, Pitch = -.25f,
            AspectRatio = (float)FoliageShadowGpuScene.Width / FoliageShadowGpuScene.Height,
        };
        var faded = Wind with { WindFadeBladePixels = 200f };
        byte[] moving = fixture.Capture(s =>
        {
            s.CameraOverride = camera;
            s.DrawFoliage(batch, Vector3.Zero, Wind);
        });
        float[] movingDepth = scene.DebugReadShadowMap(out _, out _);
        byte[] windFaded = fixture.Capture(s =>
        {
            s.DrawFoliage(batch, Vector3.Zero, faded);
            s.CameraOverride = camera;
        });
        float[] fadedDepth = scene.DebugReadShadowMap(out _, out _);
        float pixelScale = ModelRenderer.FoliageUniforms.MetresPerPixel(
            scene.CurrentFrameView.Projection, scene.CurrentFrameView.Height);
        Assert.Equal(pixelScale, scene.FoliageUniformsForTests[0].WindFade.Y);
        float cameraDepth = Vector4.Transform(new Vector4(Vector3.Zero, 1f), scene.CurrentFrameView.ViewProjection).W;
        Assert.InRange(2f / (cameraDepth * pixelScale), 0f, faded.WindFadeBladePixels);
        Assert.True(2f / pixelScale > 2f * faded.WindFadeBladePixels,
            "Using orthographic light depth 1 would leave the wind fully active.");
        byte[] calm = fixture.Capture(s =>
        {
            s.CameraOverride = camera;
            s.Draw(fixture.Shrub, Matrix4x4.Identity);
        });
        Assert.Equal(calm, windFaded);
        AssertDepthMatches(scene.DebugReadShadowMap(out _, out _), fadedDepth);
        Assert.NotEqual(calm, moving);
        Assert.NotEqual(movingDepth, fadedDepth);
    }

    [GpuFact]
    public void NonCastingGrassAddsNoDepthDraws()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch([new(fixture.Shrub, Matrix4x4.Identity, .1f)]);
        fixture.Capture(_ => { });
        float[] clear = scene.DebugReadShadowMap(out _, out _);
        fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Wind));
        Assert.Equal(0, scene.FoliageShadowDrawCalls);
        Assert.Equal(0, scene.LastShadowPassDiagnostics.TotalDrawCalls);
        Assert.True(scene.ShadowPassSkippedLastFrame);
        Assert.Equal(clear, scene.DebugReadShadowMap(out _, out _));
        Assert.All(clear, depth => Assert.Equal(1f, depth));
    }

    [GpuFact]
    public void CutoutFoliageCastsItsAlphaSilhouette()
    {
        Scene3D scene = fixture.Scene;
        var pixels = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                int i = (y * 8 + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = (byte)(x < 4 ? 0 : 255);
            }
        var texture = scene.LoadTexture(pixels, 8, 8);
        var maps = new Scene3D.SurfaceMaps(texture, alphaCutoff: .5f);
        GltfMesh card = MeshPrimitives.Plane(1.4f, 2.2f);
        Matrix4x4 transform = Matrix4x4.CreateRotationX(2f * MathF.PI / 3f)
            * Matrix4x4.CreateTranslation(0f, 1.5f, 0f);
        MeshHandle masked = scene.LoadMesh(card, maps);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var solidTexture = scene.LoadTexture(pixels, 8, 8);
        MeshHandle opaque = scene.LoadMesh(card, new Scene3D.SurfaceMaps(solidTexture, alphaCutoff: .5f));
        try
        {
            using FoliageBatch batch = scene.CreateFoliageBatch([new(masked, transform, .1f, Shrub, true)]);
            byte[] cutout = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Wind));
            float[] depth = scene.DebugReadShadowMap(out _, out _);
            MeshHandle reference = OracleMesh(card, transform, Wind, [], maps);
            try
            {
                byte[] rigid = fixture.Capture(s => s.Draw(reference, Matrix4x4.Identity));
                AssertPixelsMatch(rigid, cutout);
                AssertDepthMatches(scene.DebugReadShadowMap(out _, out _), depth);
                fixture.Capture(s => s.Draw(reference, Matrix4x4.Identity, Color.White, Material.None, false));
                float[] clear = scene.DebugReadShadowMap(out _, out _);
                Assert.True(depth.Count(d => d < 1f) > 0);
                Assert.All(clear, d => Assert.Equal(1f, d));
            }
            finally { scene.UnloadMesh(reference); }
            byte[] solid = fixture.Capture(s => s.Draw(opaque, transform));
            Assert.True(GroundLum(cutout, new Vector3(-.5f, .1f, 2.4f))
                > GroundLum(solid, new Vector3(-.5f, .1f, 2.4f)) + .05f);
            Assert.True(GroundLum(cutout, new Vector3(.5f, .1f, 2.4f))
                < GroundLum(cutout, new Vector3(-3f, .1f, 2.4f)) * .85f);
        }
        finally { scene.UnloadMesh(masked); scene.UnloadMesh(opaque); }
    }

    [GpuFact]
    public void OffCameraCasterUploadsAndItsRemovalClearsTheAtlasOnce()
    {
        Scene3D scene = fixture.Scene;
        void Camera(Scene3D s)
        {
            s.Camera.Elevation = 1.55f;
            s.Camera.Azimuth = 0f;
            s.Camera.Frame(new Vector3(0f, 0f, 5f), new Vector3(4f, 1f, 4f));
        }
        byte[] reference = fixture.Capture(Camera);
        float[] clear = scene.DebugReadShadowMap(out _, out _);
        using FoliageBatch batch = scene.CreateFoliageBatch(
            [new(fixture.Shrub, Matrix4x4.CreateScale(2f, 3f, 2f)
                * Matrix4x4.CreateTranslation(0f, 0f, -4f), .1f, new(0f, 0f, 0f, 0f), true)]);
        byte[] shadow = fixture.Capture(s =>
        {
            s.DrawFoliage(batch, Vector3.Zero, Solid);
            Camera(s);
        });
        Assert.Equal(96, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Equal(0, scene.LastFoliageStats.SubmittedPatches);
        Assert.True(scene.LastFoliageStats.UniformUploadBytes > 0);
        Assert.True(scene.FoliageShadowDrawCalls > 0);
        Assert.True(GroundLum(shadow, new Vector3(0f, .1f, 5f))
            < GroundLum(reference, new Vector3(0f, .1f, 5f)) * .85f);
        Assert.NotEqual(clear, scene.DebugReadShadowMap(out _, out _));
        batch.Dispose();
        byte[] removed = fixture.Capture(Camera);
        Assert.Equal(reference, removed);
        Assert.Equal(clear, scene.DebugReadShadowMap(out _, out _));
        Assert.False(scene.ShadowPassSkippedLastFrame);
        fixture.Capture(Camera);
        Assert.True(scene.ShadowPassSkippedLastFrame);
        Assert.Equal(0, scene.FoliageShadowDrawCalls);
    }

    [GpuFact]
    public void GlobalCastingCoverStillCastsAfterRetention()
    {
        Scene3D scene = fixture.Scene;
        var cover = new GroundCoverBatch([new GroundCoverInstance("shrub", Vector3.Zero, Matrix4x4.Identity, .1f)]);
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>> { ["shrub"] = [fixture.Shrub] };
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale,
            CastsShadows = true, DistantDensity = 1f, DrawRadius = 100f,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile> { ["shrub"] = Shrub },
        };
        try
        {
            byte[] lit = fixture.Capture(_ => { });
            byte[] global = fixture.Capture(s => s.DrawGroundCover(cover, meshes, Vector3.Zero, options));
            float[] depth = scene.DebugReadShadowMap(out _, out _);
            Assert.Equal(96, scene.LastFoliageStats.InstanceUploadBytes);
            Assert.True(scene.FoliageShadowDrawCalls > 0);
            Assert.True(GroundLum(global, new Vector3(0f, .1f, 2.4f))
                < GroundLum(lit, new Vector3(0f, .1f, 2.4f)) * .85f);
            options.CastsShadows = false;
            options.ModelCastsShadows = new Dictionary<string, bool> { ["shrub"] = true };
            byte[] perModel = fixture.Capture(s => s.DrawGroundCover(cover, meshes, Vector3.Zero, options));
            Assert.Equal(global, perModel);
            Assert.Equal(depth, scene.DebugReadShadowMap(out _, out _));
        }
        finally { scene.ReleaseGroundCover(cover); }
    }

    [GpuFact]
    public void PointShadowStaysAtRestWhileCascadeMoves()
    {
        Scene3D scene = fixture.Scene;
        Assert.True(scene.Post.Quality.Shadows.PointShadows.Enabled);
        FoliageInteractor[] still = Actors.Select(actor => actor with { Agitation = 0f }).ToArray();
        static void Lamp(Scene3D s) =>
            s.AddLight(new Vector3(1.5f, 1f, 0f), Color.White, 6f, 1f, LightShadow.Static(77));
        FoliageBatch batch = scene.CreateFoliageBatch([new(fixture.Shrub, Matrix4x4.Identity, .1f, Shrub, true)]);
        try
        {
            // The first request brings the atlas up, the second draws the row.
            fixture.Capture(s => { s.DrawFoliage(batch, Vector3.Zero, Wind, still); Lamp(s); });
            fixture.Capture(s => { s.DrawFoliage(batch, Vector3.Zero, Wind, still); Lamp(s); });
            ShadowPassDiagnostics rest = scene.LastShadowPassDiagnostics;
            Assert.Equal(1, rest.PointStaticRebuilds);
            Assert.Equal(0, rest.RigidDrawCalls);
            Assert.True(rest.PointFaceDrawCalls > 0);
            Assert.Equal(rest.PointFaceDrawCalls, scene.FoliagePointShadowDrawCalls);
            float[] restAtlas = scene.DebugReadPointShadowAtlas(out _, out _);
            float[] restCascade = scene.DebugReadShadowMap(out _, out _);
            Assert.Contains(restAtlas, distance => distance < 1f);

            // A shiver-only change moves the cascade caster and leaves the cached point map alone.
            fixture.Capture(s => { s.DrawFoliage(batch, Vector3.Zero, Wind, Actors); Lamp(s); });
            Assert.Equal(0, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            Assert.Equal(0, scene.FoliagePointShadowDrawCalls);
            Assert.True(scene.FoliageShadowDrawCalls > 0);
            Assert.Equal(restAtlas, scene.DebugReadPointShadowAtlas(out _, out _));
            Assert.NotEqual(restCascade, scene.DebugReadShadowMap(out _, out _));

            // The proxy is exactly the rigid caster at the authored transform.
            fixture.Capture(s => { s.Draw(fixture.Shrub, Matrix4x4.Identity); Lamp(s); });
            Assert.Equal(1, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            Assert.Equal(0, scene.FoliagePointShadowDrawCalls);
            AssertDepthMatches(scene.DebugReadPointShadowAtlas(out _, out _), restAtlas);

            fixture.Capture(s => { s.DrawFoliage(batch, Vector3.Zero, Wind, Actors); Lamp(s); });
            Assert.Equal(1, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            Assert.Equal(restAtlas, scene.DebugReadPointShadowAtlas(out _, out _));

            // Removing the proxy invalidates the static row once and clears it.
            batch.Dispose();
            fixture.Capture(Lamp);
            Assert.Equal(1, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            Assert.All(scene.DebugReadPointShadowAtlas(out _, out _), distance => Assert.Equal(1f, distance));
            fixture.Capture(Lamp);
            Assert.Equal(0, scene.LastShadowPassDiagnostics.PointStaticRebuilds);
            Assert.Equal(0, scene.FoliagePointShadowStreamCount);
        }
        finally
        {
            batch.Dispose();
            fixture.Capture(_ => { });
        }
    }

    [GpuFact]
    public void AgedShadowFixtureMatchesFresh()
    {
        using FoliageBatch agedBatch = fixture.Scene.CreateFoliageBatch(
            [new(fixture.Shrub, Matrix4x4.Identity, .1f, Shrub, true)]);
        fixture.Capture(s => s.DrawFoliage(agedBatch, Vector3.Zero, Wind, Actors));
        fixture.Capture(s => s.DrawFoliage(agedBatch, new Vector3(8f, 0f, 0f),
            Solid with { DrawRadius = 10f, FadeBandWidth = 4f }));
        fixture.Capture(_ => { });
        byte[] aged = fixture.Capture(s => s.DrawFoliage(agedBatch, Vector3.Zero, Wind));
        float[] agedDepth = fixture.Scene.DebugReadShadowMap(out _, out _);
        using var fresh = new FoliageShadowGpuScene();
        using FoliageBatch freshBatch = fresh.Scene.CreateFoliageBatch(
            [new(fresh.Shrub, Matrix4x4.Identity, .1f, Shrub, true)]);
        Assert.Equal(fresh.Capture(s => s.DrawFoliage(freshBatch, Vector3.Zero, Wind)), aged);
        Assert.Equal(fresh.Scene.DebugReadShadowMap(out _, out _), agedDepth);
    }

    MeshHandle OracleMesh(GltfMesh mesh, Matrix4x4 transform, FoliageRenderSettings settings,
        FoliageInteractor[] actors, Scene3D.SurfaceMaps? maps = null)
    {
        Scene3D scene = fixture.Scene;
        MeshBounds bounds = MeshBounds.FromVertices(mesh.Vertices);
        var instance = new ModelRenderer.FoliageInstanceData
        {
            Model = transform, Motion = Shrub.ToVector4(),
            Parameters = new Vector4(.1f, bounds.Min.Y,
                bounds.Max.Y > bounds.Min.Y ? 1f / (bounds.Max.Y - bounds.Min.Y) : 0f, maps?.AlphaCutoff ?? 0f),
        };
        var uniforms = ModelRenderer.FoliageUniforms.Build(Vector3.Zero, settings, actors, 1f);
        uniforms.WindFade.Y = ModelRenderer.FoliageUniforms.MetresPerPixel(scene.CurrentFrameView.Projection,
            scene.CurrentFrameView.Height);
        foreach (ref ModelVertex vertex in mesh.Vertices.AsSpan())
        {
            vertex.Position = FoliageWindMirror.VertexWorld(uniforms, instance, vertex.Position,
                scene.CurrentFrameView.ViewProjection, Vector3.Zero, false, out bool rejected);
            Assert.False(rejected);
            vertex.Normal = Vector3.TransformNormal(vertex.Normal, transform);
            var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), transform);
            vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
        }
        return maps is { } surface ? scene.LoadMesh(mesh, surface) : scene.LoadMesh(mesh);
    }

    static void AssertPixelsMatch(byte[] expected, byte[] actual)
    {
        var comparison = GoldenGrid.Compare(
            GoldenGrid.Downsample(expected, FoliageShadowGpuScene.Width, FoliageShadowGpuScene.Height),
            GoldenGrid.Downsample(actual, FoliageShadowGpuScene.Width, FoliageShadowGpuScene.Height));
        Assert.True(comparison.Passed, $"Displaced rigid colour differs, worst cell {comparison.WorstDiff}.");
    }

    static void AssertDepthMatches(float[] expected, float[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        int different = 0, written = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            if (actual[i] < 1f || expected[i] < 1f) written++;
            if (MathF.Abs(actual[i] - expected[i]) > 1e-5f) different++;
        }
        Assert.True(written > 0, "The caster must actually write depth.");
        Assert.True(different <= Math.Max(2, written / 100),
            $"Depth differs at {different} of {written} written texels.");
    }

    float GroundLum(byte[] pixels, Vector3 position)
    {
        Assert.True(fixture.Scene.Camera.WorldToScreen(position, FoliageShadowGpuScene.Width,
            FoliageShadowGpuScene.Height, out Vector2 screen));
        int x = (int)MathF.Round(screen.X), y = (int)MathF.Round(screen.Y);
        Assert.InRange(x, 1, FoliageShadowGpuScene.Width - 2);
        Assert.InRange(y, 1, FoliageShadowGpuScene.Height - 2);
        float sum = 0f;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int i = ((y + dy) * FoliageShadowGpuScene.Width + x + dx) * 4;
                sum += (.299f * pixels[i] + .587f * pixels[i + 1] + .114f * pixels[i + 2]) / 255f;
            }
        return sum / 9f;
    }
}
