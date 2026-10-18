using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class InteriorCutGpuTests : IClassFixture<InteriorCutGpuTests.Stage>
{
    readonly Stage _stage;
    public InteriorCutGpuTests(Stage stage) => _stage = stage;

    [GpuFact]
    public void ColourCutsOnlyInteriorPixels()
    {
        byte[] background = _stage.Frame(null, false, draw: false);
        byte[] solid = _stage.Frame(null, false);
        byte[] cut = _stage.Frame(Stage.Field, true);
        AssertPixels(_stage, background, solid, cut, temporal: false);
    }

    [GpuFact]
    public void ColourAndMotionCutTogether()
    {
        byte[] background = _stage.Frame(null, false, draw: false, temporal: true);
        byte[] solid = _stage.Frame(null, false, temporal: true);
        byte[] cut = _stage.Frame(Stage.Field, true, temporal: true);
        AssertPixels(_stage, background, solid, cut, temporal: true);
    }

    [GpuFact]
    public void NoOptInIsByteIdentical()
    {
        byte[] withoutFieldPixels = _stage.Frame(null, false);
        byte[] fieldButNoOptInPixels = _stage.Frame(Stage.Field, false);
        Assert.Equal(withoutFieldPixels, fieldButNoOptInPixels);
    }

    [GpuFact]
    public void ReusedSceneMatchesFreshSceneAfterFieldAndModeChanges()
    {
        _stage.Frame(Stage.Field, true, temporal: true);
        _stage.Frame(null, true);
        byte[] reused = _stage.Frame(Stage.Field, false);
        using var fresh = new Stage();
        Assert.Equal(fresh.Frame(Stage.Field, false), reused);
    }

    static void AssertPixels(Stage stage, byte[] background, byte[] solid, byte[] cut, bool temporal)
    {
        MotionTargetReadback motion = temporal ? stage.Scene.ReadMotionTargetForTests() : default;
        int inside = 0, outside = 0;
        // Probe pixel centres away from the box edges and cut boundaries. A side view leaves x and y
        // constant along every ray, and the interior spans the box's complete depth.
        for (int y = 20; y < Stage.H - 20; y++)
            for (int x = 20; x < Stage.W - 20; x++)
            {
                Vector2 jitter = temporal ? stage.Scene.CurrentFrameView.JitterPixels : Vector2.Zero;
                float wx = (x + .5f - jitter.X - Stage.W / 2f) * Stage.PixelWorld;
                float wy = (Stage.H / 2f - y - .5f + jitter.Y) * Stage.PixelWorld;
                if (MathF.Abs(wx) > .85f || MathF.Abs(wy) > 1.85f
                    || MathF.Abs(wx) < Stage.PixelWorld || MathF.Abs(wy + 1) < Stage.PixelWorld
                    || MathF.Abs(wy - 1) < Stage.PixelWorld) continue;
                bool cpuInside = Stage.Field.Contains(new Vector3(wx, wy, .5f));
                int at = (y * Stage.W + x) * 4;
                Assert.NotEqual(background.AsSpan(at, 4).ToArray(), solid.AsSpan(at, 4).ToArray());
                bool gpuBackgroundAtProbe = cut.AsSpan(at, 4).SequenceEqual(background.AsSpan(at, 4));
                Assert.Equal(cpuInside, gpuBackgroundAtProbe);
                if (!cpuInside) Assert.Equal(solid.AsSpan(at, 4).ToArray(), cut.AsSpan(at, 4).ToArray());
                if (temporal) Assert.Equal(cpuInside, motion.IsBackground(x, y));
                if (cpuInside) inside++; else outside++;
            }
        Assert.True(inside > 300 && outside > 300, $"inside {inside}, outside {outside}");
    }

    public sealed class Stage : IDisposable
    {
        internal const int W = 160, H = 160;
        internal const float PixelWorld = 6f / H;
        internal static readonly InteriorHeightField Field = InteriorHeightFieldBuilder.Build(new[]
        {
            new InteriorVolume
            {
                Outline = new[] { new Vector2(0, -2), new Vector2(2, -2), new Vector2(2, 2), new Vector2(0, 2) },
                FloorY = -1,
                CeilingY = 1,
            },
        }, new InteriorWindow(new Vector2(-2), .25f, 16), InteriorEncoding.For(-1, 1));
        TemporalFixture? _fixture;
        MeshHandle _box;
        internal Scene3D Scene => Fixture.Scene;
        TemporalFixture Fixture => _fixture ??= Create();

        TemporalFixture Create()
        {
            var fixture = new TemporalFixture(W, H, s =>
            {
                s.Post.Quality.AntiAliasing = AntiAliasing.Off;
                s.Post.Quality.Shadows.Mode = ShadowMode.Off;
                s.Post.BackgroundColor = new Color(.02f, .03f, .04f, 1);
                s.Camera.Azimuth = 0;
                s.Camera.Elevation = 0;
                s.Camera.OrthoSize = 6;
                s.Camera.Target = Vector3.Zero;
                _box = s.LoadMesh(MeshPrimitives.Box(1));
            });
            return fixture;
        }

        internal byte[] Frame(InteriorHeightField? field, bool optIn, bool draw = true, bool temporal = false)
        {
            Scene.InteriorCut = field;
            Scene.ForceTemporalForTests = temporal;
            return Fixture.Frame((s, _) =>
            {
                if (draw) s.Draw(new RigidInstanceDraw(_box, Matrix4x4.CreateScale(2, 4, 2))
                {
                    CutInsideInteriors = optIn,
                    Tint = new Color(.8f, .5f, .2f, 1),
                });
            });
        }

        public void Dispose() => _fixture?.Dispose();
    }
}
