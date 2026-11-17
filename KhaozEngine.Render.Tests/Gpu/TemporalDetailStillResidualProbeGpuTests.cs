using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// REPORT-ONLY. How much motion a still scene stores, the residual the detail history's still gate
/// (<see cref="TemporalDetailMath.StillEpsilon"/>) has to clear: a still camera over boxes, a ground plane and
/// optionally foliage, at a consumer's display size, under orthographic and perspective cameras, near the world
/// origin and far from it, with camera-relative rendering in force or opted out (<c>RenderOrigin = Vector3.Zero</c>,
/// which this fixture starts with). Each frame after the first prints the texels that store any motion, the most
/// least RG16F steps (2^-24 UV) on an axis, the texels whose motion reaches the gate in display pixels, and the
/// render origin the frame latched.
/// </summary>
public sealed class TemporalDetailStillResidualProbeGpuTests(ITestOutputHelper output)
{
    [GpuTheory, Trait("Category", "Diagnostic")]
    [InlineData("front-ortho", 0f, 1591, 1027)]
    [InlineData("iso", 0f, 1591, 1027)]
    [InlineData("iso-keyed", 0f, 1591, 1027)]
    [InlineData("fly", 0f, 1591, 1027)]
    [InlineData("foliage", 0f, 1591, 1027)]
    [InlineData("iso-relative", 4096f, 1591, 1027)]
    [InlineData("iso-default", 4156f, 1591, 1027)]
    [InlineData("iso-default", 4001f, 1591, 1027)]
    [InlineData("iso-default", 4001f, 2560, 1440)]
    [InlineData("fly-default", 4027f, 1591, 1027)]
    [InlineData("iso-optout", 256f, 1591, 1027)]
    [InlineData("iso-optout", 1024f, 1591, 1027)]
    [InlineData("iso-optout", 2048f, 1591, 1027)]
    [InlineData("iso-optout", 4096f, 1591, 1027)]
    [InlineData("iso-optout", 1024f, 2560, 1440)]
    [InlineData("iso-optout", 4096f, 2560, 1440)]
    public void Still_motion_residual(string kind, float distance, int width, int height)
    {
        // iso-relative anchors on the frame grid point nearest the stage, the best case of camera-relative rendering.
        // The default rows clear the explicit origin, so the engine anchors on the grid point nearest the eye, and
        // their distances put the stage well off that point: 68 m on both axes at 4156, 95 m at 4001 (the farthest
        // the iso eye offset allows), and about 60 m and 70 m under the fly camera at 4027. Opt-out rows keep the
        // fixture's zero origin.
        Vector3 centre = new(distance, 0f, distance);
        using var fx = new TemporalFixture(width, height, s =>
        {
            s.ForceTemporalForTests = true;
            s.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            s.Post.Quality.Shadows.Mode = ShadowMode.Off;
            s.Camera.AspectRatio = (float)width / height;
            s.Camera.OrthoSize = 12f;
            s.Camera.Target = centre;
            if (kind == "front-ortho") { s.Camera.Azimuth = 0f; s.Camera.Elevation = 0f; }
            if (kind == "iso-relative") s.RenderOrigin = WorldFrame.Nearest(centre).Anchor;
            if (kind is "iso-default" or "fly-default") s.RenderOrigin = null;
            if (kind is "fly" or "fly-default")
                s.CameraOverride = new FlyCamera3D
                {
                    Position = centre + new Vector3(4f, 5f, 9f), Yaw = 0.4f, Pitch = -0.45f,
                    AspectRatio = (float)width / height,
                };
        });
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(1f));
        MeshHandle ground = fx.Scene.LoadMesh(MeshPrimitives.Plane(40f, 40f));
        MeshHandle plate = fx.Scene.LoadMesh(FoliageAndGroundMotionGpuTests.Plate());
        FoliageBatch? batch = null;
        if (kind == "foliage")
        {
            var instances = new FoliageInstance[64];
            for (int i = 0; i < instances.Length; i++)
                instances[i] = new FoliageInstance(plate, Matrix4x4.CreateRotationY(i * 0.7f)
                    * Matrix4x4.CreateTranslation(centre + new Vector3(-6f + 1.5f * (i % 8), 0f, -6f + 1.5f * (i / 8))), .1f);
            batch = fx.Scene.CreateFoliageBatch(instances);
        }
        var settings = new FoliageRenderSettings
        {
            DrawRadius = 100f, DistantDensity = 1f, WindStrength = .5f, WindDirection = Vector2.UnitX,
        };
        void Draw(Scene3D s, int n)
        {
            s.EffectTimeSeconds = 100f;
            s.Draw(ground, Matrix4x4.CreateTranslation(centre), new Color(0.5f, 0.6f, 0.45f, 1f));
            for (int i = 0; i < 25; i++)
            {
                Matrix4x4 world = Matrix4x4.CreateScale(0.6f, 1.5f + 0.1f * i, 0.6f) * Matrix4x4.CreateRotationY(0.3f * i)
                    * Matrix4x4.CreateTranslation(centre + new Vector3(-8f + 4f * (i % 5), 0.75f, -8f + 4f * (i / 5)));
                s.Draw(new RigidInstanceDraw(box, world)
                {
                    Tint = new Color(0.9f, 0.8f, 0.7f, 1f),
                    Motion = kind == "iso-keyed" ? MotionKey.From((ulong)(100 + i)) : MotionKey.None,
                });
            }
            if (batch is not null) s.DrawFoliage(batch, centre, settings, []);
        }
        try
        {
            for (int n = 0; n < 4; n++)
            {
                fx.Frame(Draw);
                if (n == 0) continue;
                MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
                int drawn = 0, nonzero = 0, atGate = 0;
                float steps = 0f, pixels = 0f;
                foreach (Vector2 m in motion.Motion)
                {
                    if (MotionMath.IsBackground(m)) continue;
                    drawn++;
                    if (m == Vector2.Zero) continue;
                    nonzero++;
                    steps = MathF.Max(steps, MathF.Max(MathF.Abs(m.X), MathF.Abs(m.Y)) / (float)Half.Epsilon);
                    float displayPixels = (m * new Vector2(width, height)).Length();
                    pixels = MathF.Max(pixels, displayPixels);
                    if (displayPixels >= TemporalDetailMath.StillEpsilon) atGate++;
                }
                output.WriteLine($"RESIDUAL|{kind}|{distance}|{width}x{height}|frame {n}|drawn {drawn}|nonzero {nonzero}"
                    + $"|max steps {steps}|max display pixels {pixels:R}|at or above the gate {atGate}"
                    + $"|origin {fx.Scene.RenderOrigin}");
            }
        }
        finally { batch?.Dispose(); }
    }
}
