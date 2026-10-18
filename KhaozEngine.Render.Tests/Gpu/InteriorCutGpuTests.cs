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
    public void OptedInMissIsByteIdentical()
    {
        byte[] baseline = _stage.Frame(null, false);
        Assert.Equal(baseline, _stage.Frame(null, true));
        Assert.Equal(baseline, _stage.Frame(Stage.BoundaryField, true));
    }

    [GpuFact]
    public void ReusedSceneMatchesFreshSceneAfterFieldAndModeChanges()
    {
        _stage.BoundaryFrame(Stage.BoundaryField, .25f);
        _stage.Frame(Stage.Field, true, temporal: true);
        _stage.Frame(null, true);
        byte[] reused = _stage.Frame(Stage.Field, false);
        using var fresh = new Stage();
        Assert.Equal(fresh.Frame(Stage.Field, false), reused);
    }

    [GpuFact]
    public void RebasedBoundaryMatchesCpu()
    {
        _stage.Frame(null, false);
        InteriorHeightField field = Stage.BoundaryField;
        Assert.Equal(new byte[] { 0, 15, 0, 40 }, field.Texels.Span[..4].ToArray());
        Assert.True(field.TryGetSpan(8, 8, out float floor, out float top));
        byte[] background = _stage.BoundaryFrame(null, .25f, draw: false);
        byte[] solid = _stage.BoundaryFrame(null, .25f);
        int cutProbes = 0, visibleProbes = 0;
        foreach (float height in new[] { MathF.BitDecrement(floor), floor, MathF.BitIncrement(floor),
            MathF.BitDecrement(top), top, MathF.BitIncrement(top) })
        {
            byte[] pixels = _stage.BoundaryFrame(field, height);
            Assert.Equal(new Vector3(10000f, 0f, 10000f), _stage.Scene.CurrentFrameView.RenderOrigin);
            bool cpuInside = field.Contains(new Vector3(10000f, height, 10000f));
            float q = (height - field.BaseY) * (1f / field.StepY);
            bool shaderInside = 40f > 15f && q >= 15f && q < 40f;
            for (int y = 70; y < 90; y++)
                for (int x = 70; x < 90; x++)
                {
                    int at = (y * Stage.W + x) * 4;
                    Assert.NotEqual(background.AsSpan(at, 4).ToArray(), solid.AsSpan(at, 4).ToArray());
                    bool gpuBackground = pixels.AsSpan(at, 4).SequenceEqual(background.AsSpan(at, 4));
                    Assert.Equal(shaderInside, gpuBackground);
                    Assert.Equal(cpuInside, gpuBackground);
                    if (!cpuInside) Assert.Equal(solid.AsSpan(at, 4).ToArray(), pixels.AsSpan(at, 4).ToArray());
                }
            if (cpuInside) cutProbes++; else visibleProbes++;
        }
        Assert.True(cutProbes > 0 && visibleProbes > 0);
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
        internal static readonly InteriorHeightField BoundaryField = InteriorHeightFieldBuilder.Build([new InteriorVolume
        {
            Outline = [new(9998f), new(10002f, 9998f), new(10002f), new(9998f, 10002f)],
            FloorY = .1525f,
            CeilingY = .4025f,
        }], new InteriorWindow(new Vector2(9998f), .25f, 16), InteriorEncoding.For(0f, 1f));
        TemporalFixture? _fixture;
        MeshHandle _box;
        MeshHandle _boundaryPlane;
        bool _boundaryPlaneLoaded;
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
            Scene.RenderOrigin = Vector3.Zero;
            Scene.Camera.Target = Vector3.Zero;
            Scene.Camera.Elevation = 0f;
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

        internal byte[] BoundaryFrame(InteriorHeightField? field, float height, bool draw = true)
        {
            if (!_boundaryPlaneLoaded)
            {
                _boundaryPlane = Scene.LoadMesh(MeshPrimitives.Plane(2f, 2f));
                _boundaryPlaneLoaded = true;
            }
            Scene.RenderOrigin = new Vector3(10000f, 0f, 10000f);
            Scene.Camera.Target = new Vector3(10000f, .25f, 10000f);
            Scene.Camera.Elevation = MathF.PI * .45f;
            Scene.InteriorCut = field;
            Scene.ForceTemporalForTests = false;
            return Fixture.Frame((s, _) =>
            {
                if (draw) s.Draw(new RigidInstanceDraw(_boundaryPlane,
                    Matrix4x4.CreateTranslation(10000f, height, 10000f))
                {
                    CutInsideInteriors = true,
                    Tint = new Color(.8f, .5f, .2f, 1f),
                });
            });
        }

        public void Dispose() => _fixture?.Dispose();
    }
}
