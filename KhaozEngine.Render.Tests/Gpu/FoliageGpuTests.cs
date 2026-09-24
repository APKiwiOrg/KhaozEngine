using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class FoliageGpuTests(FoliageGpuScene fixture) : IClassFixture<FoliageGpuScene>
{
    static FoliageRenderSettings Solid => new() { DrawRadius = 100f, DistantDensity = 1f };

    [GpuFact]
    public void RepeatedFramesKeepInstanceDataOnTheGpu()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(new[]
        {
            new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .2f),
            new FoliageInstance(fixture.Blade, Matrix4x4.CreateTranslation(1f, 0f, 0f), .8f),
        });
        byte[] first = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        Assert.Equal(160, scene.LastFoliageStats.InstanceUploadBytes);
        byte[] second = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        Assert.Equal(first, second);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        Assert.Equal(0, scene.LastFrameStats.InstanceUploadBytes);
        Assert.Equal(2, scene.LastFoliageStats.CandidateInstances);
        Assert.Equal(1, scene.LastFoliageStats.SubmittedPatches);
        Assert.InRange(scene.LastFoliageStats.UniformUploadBytes, 144, 1024);
    }

    [GpuFact]
    public void StillFoliageMatchesTheExistingLitMesh()
    {
        Scene3D scene = fixture.Scene;
        var transform = Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(-.5f, 0f, 0f);
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(fixture.Blade, transform, .1f) });
        byte[] rigid = fixture.Capture(s => s.Draw(fixture.Blade, transform, Color.White, Material.None, false));
        byte[] foliage = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        Assert.Equal(rigid, foliage);
    }

    [GpuFact]
    public void WindChangesTipsWithoutMovingTheRootsOrUploadingTransforms()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .1f) });
        var wind = Solid with { WindStrength = .5f, WindDirection = Vector2.UnitX };
        byte[] calm = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        byte[] moving = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, wind), time: 1f);
        Assert.NotEqual(calm, moving);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        AssertRootPixelsMatch(calm, moving);
    }

    [GpuFact]
    public void NearbyInteractorBendsGrassAndAnotherFloorDoesNot()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .1f) });
        byte[] calm = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        byte[] bent = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid,
            new[] { new FoliageInteractor(new Vector3(-.25f, 0f, 0f), 1f) }));
        byte[] upstairs = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid,
            new[] { new FoliageInteractor(new Vector3(-.25f, 5f, 0f), 1f) }));
        Assert.NotEqual(calm, bent);
        Assert.Equal(calm, upstairs);
        AssertRootPixelsMatch(calm, bent);
    }

    [GpuFact]
    public void SeparateSubmissionsKeepTheirOwnWindAndFadeParameters()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch left = scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fixture.Blade, Matrix4x4.CreateTranslation(-1f, 0f, 0f), .1f) });
        using FoliageBatch right = scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fixture.Blade, Matrix4x4.CreateTranslation(1f, 0f, 0f), .1f) });
        var wind = Solid with { WindStrength = .5f };
        byte[] one = fixture.Capture(s => s.DrawFoliage(left, Vector3.Zero, wind), time: 1f);
        byte[] two = fixture.Capture(s =>
        {
            s.DrawFoliage(left, Vector3.Zero, wind);
            s.DrawFoliage(right, Vector3.Zero, Solid);
        }, time: 1f);
        Assert.Equal(LeftHalf(one), LeftHalf(two));
        Assert.Equal(2, scene.LastFoliageStats.SubmittedPatches);
    }

    [GpuFact]
    public void OriginChangeDoesNotRebuildPersistentInstances()
    {
        Scene3D scene = fixture.Scene;
        var shift = new Vector3(1024f, 0f, 0f);
        using FoliageBatch batch = scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fixture.Blade, Matrix4x4.CreateTranslation(shift), .1f) });
        byte[] absolute = fixture.Capture(s => s.DrawFoliage(batch, shift, Solid), center: shift, origin: Vector3.Zero);
        byte[] relative = fixture.Capture(s => s.DrawFoliage(batch, shift, Solid), center: shift, origin: shift);
        Assert.Equal(absolute, relative);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
    }

    [GpuFact]
    public void ExactShaderHeightFadeMatchesAHalfHeightRigidBlade()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .1f) });
        var fading = Solid with { DrawRadius = 10f, FadeBandWidth = 4f };
        byte[] reference = fixture.Capture(s => s.Draw(fixture.Blade, Matrix4x4.CreateScale(1f, .5f, 1f),
            Color.White, Material.None, false));
        byte[] faded = fixture.Capture(s => s.DrawFoliage(batch, new Vector3(8f, 0f, 0f), fading));
        Assert.Equal(reference, faded);
    }

    [GpuFact]
    public void HeightFadeKeepsAnOffsetMeshRootPlanted()
    {
        Scene3D scene = fixture.Scene;
        GltfMesh mesh = MeshPrimitives.Tile(.2f, 2f);
        foreach (ref ModelVertex vertex in mesh.Vertices.AsSpan()) vertex.Position.Y -= 1f;
        MeshHandle handle = scene.LoadMesh(mesh);
        try
        {
            using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(handle, Matrix4x4.Identity, .1f) });
            byte[] reference = fixture.Capture(s => s.Draw(handle,
                Matrix4x4.CreateScale(1f, .5f, 1f) * Matrix4x4.CreateTranslation(0f, -.5f, 0f),
                Color.White, Material.None, false));
            byte[] faded = fixture.Capture(s => s.DrawFoliage(batch, new Vector3(8f, 0f, 0f),
                Solid with { DrawRadius = 10f, FadeBandWidth = 4f }));
            Assert.Equal(reference, faded);
        }
        finally { scene.UnloadMesh(handle); }
    }

    [GpuFact]
    public void CameraChangesAfterSubmissionUseTheRenderedFrustum()
    {
        using FoliageBatch batch = fixture.Scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .1f) });
        byte[] expected = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        byte[] changed = fixture.Capture(s =>
        {
            s.Camera.Frame(new Vector3(1000f, 0f, 0f), new Vector3(4f, 3f, 4f));
            s.DrawFoliage(batch, Vector3.Zero, Solid);
            s.Camera.Frame(Vector3.Zero, new Vector3(4f, 3f, 4f));
        });
        Assert.Equal(expected, changed);
    }

    [GpuFact]
    public void ZoomChangesAfterSubmissionUseTheRenderedPixelScale()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .1f) });
        var faded = Solid with { WindStrength = .5f, WindDirection = Vector2.UnitX, WindFadeBladePixels = 8f };
        const float ZoomedOut = .01f;
        byte[] calm = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid), time: 1f);
        byte[] expected = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, faded), time: 1f);
        // Orthographic clip w is 1, so the two metre blade covers M22 times the render height in pixels.
        float framed = MathF.Abs(scene.Camera.Projection.M22) * scene.Post.RenderHeight;
        Assert.InRange(framed, 2f * faded.WindFadeBladePixels * 1.5f, float.MaxValue);
        Assert.InRange(framed * ZoomedOut, 0f, faded.WindFadeBladePixels / 1.5f);

        byte[] changed = fixture.Capture(s =>
        {
            s.Camera.Zoom = ZoomedOut;
            try { s.DrawFoliage(batch, Vector3.Zero, faded); }
            finally { s.Camera.Zoom = 1f; }
        }, time: 1f);

        Assert.NotEqual(calm, expected);
        Assert.Equal(expected, changed);
    }

    [GpuFact]
    public void ReusedSceneMatchesAFreshSceneAfterWindAndOriginChanges()
    {
        using FoliageBatch batch = fixture.Scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fixture.Blade, Matrix4x4.Identity, .1f) });
        fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid with { WindStrength = .8f }), time: 8f);
        fixture.Capture(_ => { }, center: new Vector3(1024f, 0f, 0f));
        byte[] reused = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, Solid));
        using var fresh = new FoliageGpuScene();
        using FoliageBatch freshBatch = fresh.Scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fresh.Blade, Matrix4x4.Identity, .1f) });
        byte[] expected = fresh.Capture(s => s.DrawFoliage(freshBatch, Vector3.Zero, Solid));
        Assert.Equal(expected, reused);
    }

    [GpuFact]
    public void WindStopsOnBladesThatAreOnlyAFewPixelsTall()
    {
        Scene3D scene = fixture.Scene;
        // Perspective looking down -Z, so world -X is the left half of the image and +X the right half.
        var camera = new FlyCamera3D { Position = new Vector3(0f, 1f, 0f), Yaw = MathF.PI, AspectRatio = 16f / 9f };
        var placements = new List<FoliageInstance>();
        foreach (float x in new[] { -4.2f, -3.2f, -2.2f }) placements.Add(Blade(x, -5f));
        foreach (float x in new[] { 14f, 18f, 22f, 26f, 30f }) placements.Add(Blade(x, -60f));
        using FoliageBatch batch = scene.CreateFoliageBatch(placements.ToArray());
        var wind = new FoliageRenderSettings { DrawRadius = 200f, DistantDensity = 1f, WindStrength = .8f };
        var faded = wind with { WindFadeBladePixels = 60f };
        float metresPerPixel = 2f / (camera.Projection.M22 * scene.Post.RenderHeight);
        // Two metre blades cover about 312 internal pixels at 5 m and about 26 at 60 m.
        Assert.InRange(2f / (5f * metresPerPixel), 2f * faded.WindFadeBladePixels * 1.5f, float.MaxValue);
        Assert.InRange(2f / (60f * metresPerPixel), 0f, faded.WindFadeBladePixels / 1.5f);

        byte[] fadedFirst = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, faded), time: 1f, camera: camera);
        byte[] fadedSecond = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, faded), time: 2.5f, camera: camera);
        byte[] windFirst = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, wind), time: 1f, camera: camera);
        byte[] windSecond = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, wind), time: 2.5f, camera: camera);

        Assert.True(GreenPixels(RightHalf(fadedFirst)) > 0, "The far blades must reach the image.");
        Assert.Equal(RightHalf(fadedFirst), RightHalf(fadedSecond));
        Assert.NotEqual(LeftHalf(fadedFirst), LeftHalf(fadedSecond));
        Assert.NotEqual(RightHalf(windFirst), RightHalf(windSecond));
        Assert.Equal(LeftHalf(windFirst), LeftHalf(fadedFirst));
    }

    [GpuFact]
    public void WindFadeLeavesInteractorBendingOnAFarBlade()
    {
        Scene3D scene = fixture.Scene;
        var camera = new FlyCamera3D { Position = new Vector3(0f, 1f, 0f), Yaw = MathF.PI, AspectRatio = 16f / 9f };
        using FoliageBatch batch = scene.CreateFoliageBatch(new[] { Blade(18f, -60f) });
        var still = new FoliageRenderSettings { DrawRadius = 200f, DistantDensity = 1f };
        var faded = still with { WindFadeBladePixels = 60f };
        FoliageInteractor[] actor = [new FoliageInteractor(new Vector3(17.75f, 0f, -60f), 1f)];
        float metresPerPixel = 2f / (camera.Projection.M22 * scene.Post.RenderHeight);
        Assert.InRange(2f / (60f * metresPerPixel), 0f, faded.WindFadeBladePixels / 1.5f);

        byte[] calm = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, still), time: 1f, camera: camera);
        byte[] bent = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, still, actor), time: 1f, camera: camera);
        byte[] fadedBent = fixture.Capture(s => s.DrawFoliage(batch, Vector3.Zero, faded, actor), time: 1f, camera: camera);

        Assert.True(GreenPixels(calm) > 0, "The far blade must reach the image.");
        Assert.NotEqual(calm, bent);
        Assert.NotEqual(calm, fadedBent);
        Assert.Equal(bent, fadedBent);
    }

    FoliageInstance Blade(float x, float z) =>
        new(fixture.Blade, Matrix4x4.CreateScale(4f, 1f, 4f) * Matrix4x4.CreateTranslation(x, 0f, z), .1f);

    [Fact]
    public void FoliageShaderCompilesThroughThePortableShaderValidator() =>
        ShaderValidation.ValidatePair(KhaozEngine.Render3D.Internal.ShaderSources.FoliageVert,
            KhaozEngine.Render3D.Internal.ShaderSources.ModelFrag, "Foliage");

    [GpuFact]
    public void EmptyCulledAndDisposedBatchesDoNotDraw()
    {
        Scene3D scene = fixture.Scene;
        using FoliageBatch empty = scene.CreateFoliageBatch(Array.Empty<FoliageInstance>());
        using FoliageBatch distant = scene.CreateFoliageBatch(new[]
            { new FoliageInstance(fixture.Blade, Matrix4x4.CreateTranslation(1000f, 0f, 0f), .1f) });
        fixture.Capture(s =>
        {
            Assert.Equal(0, s.DrawFoliage(empty, Vector3.Zero, Solid));
            Assert.Equal(0, s.DrawFoliage(distant, Vector3.Zero, Solid));
        });
        Assert.Equal(0, scene.LastFoliageStats.CandidateInstances);
        Assert.Equal(0, scene.LastFoliageStats.InstanceUploadBytes);
        distant.Dispose();
        Assert.Throws<ObjectDisposedException>(() => scene.DrawFoliage(distant, Vector3.Zero, Solid));
    }

    static byte[] LeftHalf(byte[] pixels)
    {
        var half = new byte[pixels.Length / 2];
        for (int y = 0; y < FoliageGpuScene.Height; y++)
            pixels.AsSpan(y * FoliageGpuScene.Width * 4, FoliageGpuScene.Width * 2)
                .CopyTo(half.AsSpan(y * FoliageGpuScene.Width * 2));
        return half;
    }

    static byte[] RightHalf(byte[] pixels)
    {
        var half = new byte[pixels.Length / 2];
        for (int y = 0; y < FoliageGpuScene.Height; y++)
            pixels.AsSpan(y * FoliageGpuScene.Width * 4 + FoliageGpuScene.Width * 2, FoliageGpuScene.Width * 2)
                .CopyTo(half.AsSpan(y * FoliageGpuScene.Width * 2));
        return half;
    }

    static int GreenPixels(byte[] pixels)
    {
        int count = 0;
        for (int p = 0; p + 3 < pixels.Length; p += 4)
            if (pixels[p + 1] > pixels[p] * 1.3f && pixels[p + 1] > pixels[p + 2] * 1.3f) count++;
        return count;
    }

    static void AssertRootPixelsMatch(byte[] still, byte[] bent)
    {
        // The lowest green silhouette is the planted base. Lighting can change which face covers a pixel.
        Assert.Equal(RootSilhouette(still), RootSilhouette(bent));
    }

    static int[] RootSilhouette(byte[] pixels)
    {
        for (int y = FoliageGpuScene.Height - 1; y >= 0; y--)
        {
            var root = new List<int> { y };
            for (int x = 0; x < FoliageGpuScene.Width; x++)
            {
                int p = (y * FoliageGpuScene.Width + x) * 4;
                if (pixels[p + 1] > pixels[p] * 1.3f && pixels[p + 1] > pixels[p + 2] * 1.3f) root.Add(x);
            }
            if (root.Count > 1) return root.ToArray();
        }
        Assert.Fail("The root sample must contain grass, not background.");
        return Array.Empty<int>();
    }
}

public sealed class FoliageGpuScene : IDisposable
{
    public const int Width = 240, Height = 160;
    GpuDeviceContext? _gpu;
    Render3DPreview? _preview;
    public MeshHandle Blade { get; private set; }
    public Scene3D Scene
    {
        get
        {
            if (_preview is not null) return _preview.Scene;
            _gpu = GpuDeviceContext.CreateHeadless();
            _preview = new Render3DPreview(_gpu.GpuDevice, Width, Height);
            GltfMesh mesh = MeshPrimitives.Tile(.2f, 2f);
            foreach (ref ModelVertex vertex in mesh.Vertices.AsSpan()) vertex.Color = new Vector4(.3f, .7f, .15f, 1f);
            Blade = _preview.Scene.LoadMesh(mesh);
            return _preview.Scene;
        }
    }

    public byte[] Capture(Action<Scene3D> draw, float time = 0f, Vector3 center = default, Vector3? origin = null,
        IIsoCamera3D? camera = null)
    {
        Scene3D scene = Scene;
        scene.CameraOverride = camera;
        scene.Post.TransparentBackground = false;
        scene.Post.Starfield = false;
        scene.Post.BackgroundColor = new Color(.08f, .1f, .14f, 1f);
        scene.Post.Quality.AntiAliasing = default;
        scene.Post.Quality.Shadows.Mode = ShadowMode.Off;
        scene.Camera.Frame(center, new Vector3(4f, 3f, 4f));
        scene.RenderOrigin = origin;
        scene.EffectTimeSeconds = time;
        _preview!.Capture(draw);
        return _preview.ReadbackRgba();
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _gpu?.Dispose();
    }
}
