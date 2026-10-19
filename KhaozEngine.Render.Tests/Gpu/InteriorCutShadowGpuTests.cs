using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class InteriorCutShadowGpuTests(InteriorCutShadowGpuTests.Stage stage) : IClassFixture<InteriorCutShadowGpuTests.Stage>
{
    [GpuFact]
    public void CascadeCutsTheOutsideHalf() => AssertHalf(point: false);

    [GpuFact]
    public void PointCutsTheOutsideHalf() => AssertHalf(point: true);

    void AssertHalf(bool point)
    {
        byte[] open = stage.Frame(point, null, false, draw: false);
        byte[] solid = stage.Frame(point, null, true);
        byte[] cut = stage.Frame(point, Stage.Field, true);
        int kept = 0, removed = 0;
        float edge = stage.PcfFootprint(point);
        Assert.InRange(edge, 0f, .25f);
        // Sweep the footprint on both sides, excluding only the existing filter's support at the cut edge.
        foreach (float z in new[] { -.4f, 0f, .4f })
            for (int step = -12; step <= 12; step++)
            {
                float x = step * .1f;
                if (MathF.Abs(x) <= edge) continue;
                Vector3 world = new(x, 0, z);
                float lit = stage.Red(open, world), full = stage.Red(solid, world), actual = stage.Red(cut, world);
                Assert.True(full < lit - 20, $"shadow control at {world}: open {lit}, full {full}");
                if (x > 0)
                {
                    Assert.InRange(actual, lit - 2, lit + 2);
                    removed++;
                }
                else
                {
                    Assert.InRange(actual, full - 2, full + 2);
                    kept++;
                }
            }
        Assert.True(kept >= 30 && removed >= 30, $"kept {kept}, removed {removed}");
    }

    [GpuFact]
    public void CascadeOptOutIsByteIdentical()
    {
        byte[] noFieldShadowPixels = stage.Frame(false, null, false);
        byte[] optOutShadowPixels = stage.Frame(false, Stage.Field, false);
        Assert.Equal(noFieldShadowPixels, optOutShadowPixels);
    }

    [GpuFact]
    public void PointOptOutIsByteIdentical()
    {
        byte[] noFieldShadowPixels = stage.Frame(true, null, false);
        byte[] optOutShadowPixels = stage.Frame(true, Stage.Field, false);
        Assert.Equal(noFieldShadowPixels, optOutShadowPixels);
    }

    [GpuFact]
    public void ReusedShadowSceneMatchesFreshAfterChangingFieldsAndPasses()
    {
        stage.Frame(true, Stage.Field, true);
        stage.Frame(false, Stage.Field, false);
        stage.Frame(true, null, true);
        byte[] reused = stage.Frame(false, Stage.Field, true);
        using var fresh = new Stage();
        Assert.Equal(fresh.Frame(false, Stage.Field, true), reused);
    }

    public sealed class Stage : IDisposable
    {
        const int W = 240, H = 240;
        internal static readonly InteriorHeightField Field = InteriorHeightFieldBuilder.Build([new InteriorVolume
        {
            Outline = [new(0, -4), new(4, -4), new(4, 4), new(0, 4)], FloorY = .5f, CeilingY = 4,
        }], new InteriorWindow(new Vector2(-4), .25f, 32), InteriorEncoding.For(.5f, 4));
        TemporalFixture? _fixture;
        MeshHandle _floor, _box;
        TemporalFixture Fixture => _fixture ??= Create();

        TemporalFixture Create() => new(W, H, scene =>
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            scene.Post.Hdr.Enabled = false;
            scene.Post.CelBands = 0;
            scene.Post.FillLightColor = new Color(0, 0, 0, 1);
            scene.Post.AmbientColor = new Color(.1f, .1f, .1f, 1);
            scene.Post.LightDirection = -Vector3.UnitY;
            scene.Post.Quality.Shadows.PointShadows.FaceResolution = 256;
            scene.Post.Quality.Shadows.PointShadows.MaxShadowedLights = 1;
            scene.Post.Quality.Shadows.PointShadows.Filter = PointShadowFilter.Hard;
            scene.Camera.Azimuth = 0;
            scene.Camera.Elevation = 1.35f;
            scene.Camera.Frame(Vector3.Zero, new Vector3(10, 2, 10), margin: 1);
            _floor = scene.LoadMesh(MeshPrimitives.Tile(20f, .1f));
            _box = scene.LoadMesh(MeshPrimitives.Box(1));
        });

        internal byte[] Frame(bool point, InteriorHeightField? field, bool optIn, bool draw = true)
        {
            Scene3D scene = Fixture.Scene;
            scene.InteriorCut = field;
            scene.Post.Quality.Shadows.Mode = point ? ShadowMode.Off : ShadowMode.ShadowMap;
            scene.Post.LightColor = point ? new Color(0, 0, 0, 1) : Color.White;
            Action<Scene3D, int> describe = (s, _) =>
            {
                s.EffectTimeSeconds = 0;
                s.Draw(_floor, Matrix4x4.Identity, new Color(.6f, .6f, .6f, 1));
                if (draw) s.Draw(new RigidInstanceDraw(_box,
                    Matrix4x4.CreateScale(3, 2, 2) * Matrix4x4.CreateTranslation(0, 2, 0))
                {
                    CutInsideInteriors = optIn,
                    ShadowOnly = true,
                });
                if (point) s.AddLight(new Vector3(0, 6, 0), Color.White, 16, 3, LightShadow.Static(42));
            };
            // The first request schedules allocation for the next frame boundary.
            Fixture.Frame(describe);
            return Fixture.Frame(describe);
        }

        internal float PcfFootprint(bool point)
        {
            // 1.5 texels includes the outer PCF tap and the stored texel's half-width.
            if (point) return 18f / Fixture.Scene.Post.Quality.Shadows.PointShadows.ResolvedFaceResolution;
            float widest = 0;
            foreach (Matrix4x4 matrix in Fixture.Scene.CascadeFitRelativeForTests)
            {
                float clipPerMetre = MathF.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
                widest = MathF.Max(widest, 3f / (clipPerMetre * Fixture.Scene.Post.Quality.Shadows.ShadowMapResolution));
            }
            return widest;
        }

        internal float Red(byte[] pixels, Vector3 world)
        {
            Assert.True(Fixture.Scene.Camera.WorldToScreen(world, W, H, out Vector2 pixel));
            int x = (int)pixel.X, y = (int)pixel.Y;
            return pixels[(y * W + x) * 4];
        }

        public void Dispose() => _fixture?.Dispose();
    }
}
