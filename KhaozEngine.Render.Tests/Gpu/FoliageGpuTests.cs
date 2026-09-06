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

    public byte[] Capture(Action<Scene3D> draw, float time = 0f, Vector3 center = default, Vector3? origin = null)
    {
        Scene3D scene = Scene;
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
