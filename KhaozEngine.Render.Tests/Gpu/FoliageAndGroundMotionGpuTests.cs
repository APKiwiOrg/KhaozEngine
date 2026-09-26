using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Foliage wind and interactor motion, and the ground passes' camera-only motion, on a real device
/// (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24, acceptance 2 and section 3).</summary>
public sealed class FoliageAndGroundMotionGpuTests
{
    const int W = 320, H = 180;

    static FoliageRenderSettings Still => new() { DrawRadius = 100f, DistantDensity = 1f };

    // A 1.2 m plate at the top of a 1 m blade whose root is the one vertex no triangle draws.
    internal static GltfMesh Plate()
    {
        GltfMesh plane = MeshPrimitives.Plane(1.2f, 1.2f);
        var vertices = new ModelVertex[plane.Vertices.Length + 1];
        for (int i = 0; i < plane.Vertices.Length; i++)
        {
            vertices[i] = plane.Vertices[i];
            vertices[i].Position.Y = 1f;
        }
        vertices[^1] = new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One);
        return new GltfMesh(vertices, plane.Indices32);
    }

    static void AssertPlateMotion(FoliageRenderSettings settings, Func<int, FoliageInteractor[]> interactors, float clockStep,
        Func<int, float>? zoom = null)
    {
        using var fx = new TemporalFixture(W, H, s =>
        {
            s.ForceTemporalForTests = true;
            s.Camera.OrthoSize = 4f;
        });
        MeshHandle plate = fx.Scene.LoadMesh(Plate());
        using FoliageBatch batch = fx.Scene.CreateFoliageBatch(new[] { new FoliageInstance(plate, Matrix4x4.Identity, .1f) });
        void Draw(Scene3D s, int n)
        {
            s.EffectTimeSeconds = clockStep * n;
            if (zoom is not null) s.Camera.Zoom = zoom(n);
            Assert.Equal(1, s.DrawFoliage(batch, Vector3.Zero, settings, interactors(n)));
        }

        fx.Frame(Draw);
        IsoCamera3D then = MotionExpectation.Snapshot(fx.Scene.Camera);
        FoliageUniforms last = fx.Scene.FoliageUniformsForTests[0];
        fx.Frame(Draw);
        IsoCamera3D now = MotionExpectation.Snapshot(fx.Scene.Camera);
        FoliageUniforms current = fx.Scene.FoliageUniformsForTests[0];
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        Assert.NotNull(fx.Scene.PreviousFrameView);

        // The plate's own motion: this frame's offset under this frame's camera against last frame's offset under last
        // frame's, each from its own frame's upload. Seen through last frame's camera it is the same at every pixel.
        Vector2 own = MotionExpectation.Moved(then, then,
            FoliageWindMirror.TopOffset(current, Vector3.Zero, now.ViewProjection),
            FoliageWindMirror.TopOffset(last, Vector3.Zero, then.ViewProjection), W, H);
        Assert.True(own.Length() > 2f, $"the plate moves {own} px");
        // The camera's motion of the pixel's ray, zero under a still camera, plus the plate's own.
        Vector2 Expected(int x, int y) => MotionExpectation.StaticSurface(now, then, x, y, W, H, jitter) + own;
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), Expected, .05f);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void WindReportsTheBladesOwnMotion()
        => AssertPlateMotion(Still with { WindStrength = .5f, WindDirection = Vector2.UnitX }, _ => [], clockStep: .3f);

    // The strength changes with the position, so last frame's evaluation reading this frame's strengths fails too.
    [GpuFact]
    public void AMovingInteractorReportsTheBendItCauses()
        => AssertPlateMotion(Still,
            n => [new FoliageInteractor(new Vector3(-.9f + .25f * n, 0f, .2f), 1.6f, .4f + .2f * n)], clockStep: 0f);

    // The wind clock stands still, so the plate's own motion is the wind fade alone: a 1 m blade is 1.5 fade heights
    // tall at zoom 1 and 1.8 at zoom 1.2. Last frame's evaluation that took this frame's pixel scale would report none.
    [GpuFact]
    public void AZoomReportsTheWindFadeAtEachFramesOwnPixelScale()
        => AssertPlateMotion(Still with { WindStrength = 1f, WindDirection = Vector2.UnitX, WindFadeBladePixels = 30f },
            _ => [], clockStep: 0f, zoom: n => n == 0 ? 1f : 1.2f);

    [GpuFact]
    public void SplatTerrainAndTileGroundReportTheCamerasMotion()
    {
        using var fx = new TemporalFixture(W, H, s =>
        {
            s.ForceTemporalForTests = true;
            s.Camera.OrthoSize = 12f;
        });
        Scene3D.SplatMaterialHandle splat = fx.Scene.LoadSplatMaterial(4, 4, GroundLayerImages.FlatSplatLayers(4));
        Scene3D.TileGroundMaterialHandle tile = fx.Scene.LoadTileGroundMaterial(4, 4, GroundLayerImages.FlatGroundLayers(4));
        MeshHandle terrain = fx.Scene.LoadMesh(Quad(tileGround: false), splat);
        MeshHandle ground = fx.Scene.LoadMesh(Quad(tileGround: true), tile);
        // Terrain left of the centre and tile ground right of it, placed so neither quad crosses the screen's centre
        // line and each half of the frame is one ground pass. Each is placed by its instance transform, so a variant
        // that projected the mesh's own positions instead of the world position misses by the placement.
        Matrix4x4 terrainAt = Matrix4x4.CreateTranslation(-6.5f, 0f, 6.5f);
        Matrix4x4 groundAt = Matrix4x4.CreateTranslation(6.5f, 0f, -6.5f);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.3f * n, 0f, .25f * n);
            s.Draw(terrain, terrainAt);
            s.Draw(ground, groundAt);
        }

        fx.Frame(Draw);
        IsoCamera3D then = MotionExpectation.Snapshot(fx.Scene.Camera);
        fx.Frame(Draw);
        IsoCamera3D now = MotionExpectation.Snapshot(fx.Scene.Camera);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();

        Vector2 Expected(int x, int y) => MotionExpectation.StaticSurface(now, then, x, y, W, H, jitter);
        Assert.True(Expected(W / 2, H / 2).Length() > 2f, $"the camera moves the ground {Expected(W / 2, H / 2)} px");
        int left = MotionExpectation.AssertDrawnPixels(motion, Expected, .05f, (x, _) => x < W / 2);
        int right = MotionExpectation.AssertDrawnPixels(motion, Expected, .05f, (x, _) => x >= W / 2);
        Assert.True(left > 1000 && right > 1000, $"{left} terrain and {right} tile-ground pixels drew");
    }

    // One quad per ground pipeline, 5 m half-width around the mesh origin, on the same vertex contract as the quads
    // FrameUniformUploadShapeGpuTests draws.
    static GltfMesh Quad(bool tileGround)
    {
        const float E = 5f;
        var w = new Vector4(1f, 0f, 0f, 0f);
        var tangent = tileGround ? new Vector4(0f, 0f, 1f, 0f) : Vector4.Zero;
        ModelVertex Corner(float x, float z, Vector2 uv) =>
            new(new Vector3(x, 0f, z), Vector3.UnitY, w, tileGround ? Vector2.Zero : uv, tangent);
        var vertices = new[]
        {
            Corner(-E, -E, new Vector2(0f, 0f)), Corner(E, -E, new Vector2(1f, 0f)),
            Corner(E, E, new Vector2(1f, 1f)), Corner(-E, E, new Vector2(0f, 1f)),
        };
        return new GltfMesh(vertices, new ushort[] { 0, 1, 2, 0, 2, 3 });
    }
}
