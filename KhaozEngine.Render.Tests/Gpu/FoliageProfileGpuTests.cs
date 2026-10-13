using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class FoliageProfileGpuTests(FoliageGpuScene fixture) : IClassFixture<FoliageGpuScene>
{
    static FoliageRenderSettings Still => new() { DrawRadius = 100f, DistantDensity = 1f };
    static FoliageMotionProfile ShiverOnly => new(0f, 0f, 0f, .035f);

    [GpuFact]
    public void ZeroAgitationCapturesAreEqualAcrossTimes() => WithRestoredSettings(() =>
    {
        FoliageInteractor[] actors = [new(Vector3.Zero, 2f)];
        byte[] first = CaptureProfile(fixture, ShiverOnly, actors, 0f);
        byte[] second = CaptureProfile(fixture, ShiverOnly, actors, .1f);
        Assert.Equal(first, second);
    });

    [GpuFact]
    public void ShiverChangesUpperPixelsAndLeavesRootPixelsFixed() => WithRestoredSettings(() =>
    {
        byte[] calm = CaptureProfile(fixture, ShiverOnly, [new(Vector3.Zero, 2f)], 0f);
        byte[] first = CaptureProfile(fixture, ShiverOnly, [new(Vector3.Zero, 2f, 1f, 1f)], 0f);
        byte[] second = CaptureProfile(fixture, ShiverOnly, [new(Vector3.Zero, 2f, 1f, 1f)], .1f);
        int rootRow = Enumerable.Range(0, FoliageGpuScene.Height).Last(y => GreenPixels(Row(calm, y)) > 0);
        int rootStart = (rootRow - 5) * FoliageGpuScene.Width * 4;
        Assert.True(GreenPixels(calm[rootStart..]) > 5, "the planted root must cover real pixels");
        Assert.Equal(calm[rootStart..], first[rootStart..]);
        Assert.Equal(calm[rootStart..], second[rootStart..]);
        int upperEnd = rootRow - 10;
        Assert.True(upperEnd > 0);
        Assert.True(GreenPixels(calm[..(upperEnd * FoliageGpuScene.Width * 4)]) > 100);
        Assert.NotEqual(calm[..(upperEnd * FoliageGpuScene.Width * 4)], first[..(upperEnd * FoliageGpuScene.Width * 4)]);
        Assert.NotEqual(first[..(upperEnd * FoliageGpuScene.Width * 4)], second[..(upperEnd * FoliageGpuScene.Width * 4)]);
    });

    [GpuFact]
    public void CappedShrubMatchesItsDisplacedRigidReference() => WithRestoredSettings(() =>
    {
        CaptureProfile(fixture, new FoliageMotionProfile(.3f, .15f, .4f, .035f),
            [new(new Vector3(-.1f, 0f, 0f), 2f, 1f, 1f)], .1f);
    });

    [GpuFact]
    public void AgedProfileFixtureMatchesFresh() => WithRestoredSettings(() =>
    {
        CaptureProfile(fixture, ShiverOnly, [new(Vector3.Zero, 2f, 1f, 1f)], .1f);
        CaptureProfile(fixture, FoliageMotionProfile.Blade, [new(new Vector3(-.1f, 0f, 0f), 2f)], 1f);
        CaptureProfile(fixture, new FoliageMotionProfile(0f, 0f, 0f, .25f),
            [new(Vector3.Zero, 2f, 1f, 1f), new(Vector3.Zero, 2f, 1f, 1f)], .4f);
        var profile = new FoliageMotionProfile(.3f, .15f, .4f, .035f);
        FoliageInteractor[] actors = [new(new Vector3(-.1f, 0f, 0f), 2f, 1f, .6f)];
        byte[] aged = CaptureProfile(fixture, profile, actors, .25f);
        using var fresh = new FoliageGpuScene();
        Assert.Equal(CaptureProfile(fresh, profile, actors, .25f), aged);
    });

    // Each comparison rasterises the same authored attributes, with only the rigid reference's positions baked
    // by the independent CPU oracle. Successful draw calls alone cannot detect a missing profile or shiver.
    static byte[] CaptureProfile(FoliageGpuScene target, FoliageMotionProfile motion, FoliageInteractor[] actors, float time)
    {
        Scene3D scene = target.Scene;
        GltfMesh source = RootedShrub();
        foreach (ref ModelVertex vertex in source.Vertices.AsSpan()) vertex.Color = new Vector4(.3f, .7f, .15f, 1f);
        MeshHandle mesh = scene.LoadMesh(source);
        MeshHandle reference = default;
        bool referenceLoaded = false;
        try
        {
            using FoliageBatch batch = scene.CreateFoliageBatch([new FoliageInstance(mesh, Matrix4x4.Identity, .1f, motion)]);
            var camera = new IsoCamera3D { OrthoSize = 2f, Target = new Vector3(0f, .5f, 0f) };
            byte[] actual = target.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Still, actors), time, origin: Vector3.Zero, camera: camera);
            var uniforms = scene.FoliageUniformsForTests[0];
            var instance = new ModelRenderer.FoliageInstanceData
            {
                Model = Matrix4x4.Identity,
                Parameters = new Vector4(.1f, 0f, 1f, 0f),
                Motion = motion.ToVector4(),
            };
            var vertices = source.Vertices.ToArray();
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i].Position = FoliageWindMirror.VertexWorld(uniforms, instance, vertices[i].Position,
                    scene.CurrentFrameView.ViewProjection, Vector3.Zero, false, out bool rejected);
                Assert.False(rejected);
            }
            reference = scene.LoadMesh(new GltfMesh(vertices, source.Indices32));
            referenceLoaded = true;
            byte[] expected = target.Capture(s => s.Draw(reference, Matrix4x4.Identity, Color.White, Material.None, false),
                time, origin: Vector3.Zero, camera: camera);
            Assert.True(GreenPixels(actual) > 100, "the shrub must reach the colour image");
            Assert.Equal(expected, actual);
            return actual;
        }
        finally
        {
            if (referenceLoaded) scene.UnloadMesh(reference);
            scene.UnloadMesh(mesh);
        }
    }

    static GltfMesh RootedShrub()
    {
        GltfMesh crown = MeshPrimitives.Tile(.5f, 1f);
        // A visible base with every vertex at root height makes a planted region, rather than a one-pixel box corner.
        GltfMesh roots = MeshPrimitives.Plane(1.3f, 1.3f);
        ModelVertex[] vertices = crown.Vertices.Concat(roots.Vertices).ToArray();
        uint[] indices = crown.Indices32.Concat(roots.Indices32.Select(i => i + (uint)crown.Vertices.Length)).ToArray();
        return new GltfMesh(vertices, indices);
    }

    static byte[] Row(byte[] pixels, int y) => pixels.AsSpan(y * FoliageGpuScene.Width * 4, FoliageGpuScene.Width * 4).ToArray();

    static int GreenPixels(byte[] pixels)
    {
        int count = 0;
        for (int p = 0; p < pixels.Length; p += 4)
            if (pixels[p + 1] > pixels[p] * 1.3f && pixels[p + 1] > pixels[p + 2] * 1.3f) count++;
        return count;
    }

    void WithRestoredSettings(Action body)
    {
        Scene3D scene = fixture.Scene;
        IsoCamera3D camera = MotionExpectation.Snapshot(scene.Camera);
        var cameraOverride = scene.CameraOverride;
        var origin = scene.RenderOrigin;
        var time = scene.EffectTimeSeconds;
        var transparent = scene.Post.TransparentBackground;
        var starfield = scene.Post.Starfield;
        var background = scene.Post.BackgroundColor;
        var aa = scene.Post.Quality.AntiAliasing;
        var shadows = scene.Post.Quality.Shadows.Mode;
        try { body(); }
        finally
        {
            scene.Camera.Target = camera.Target;
            scene.Camera.OrthoSize = camera.OrthoSize;
            scene.Camera.Zoom = camera.Zoom;
            scene.Camera.AspectRatio = camera.AspectRatio;
            scene.Camera.Distance = camera.Distance;
            scene.Camera.NearPlane = camera.NearPlane;
            scene.Camera.FarPlane = camera.FarPlane;
            scene.Camera.Azimuth = camera.Azimuth;
            scene.Camera.Elevation = camera.Elevation;
            scene.CameraOverride = cameraOverride;
            scene.RenderOrigin = origin;
            scene.EffectTimeSeconds = time;
            scene.Post.TransparentBackground = transparent;
            scene.Post.Starfield = starfield;
            scene.Post.BackgroundColor = background;
            scene.Post.Quality.AntiAliasing = aa;
            scene.Post.Quality.Shadows.Mode = shadows;
        }
    }
}
