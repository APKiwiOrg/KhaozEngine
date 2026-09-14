using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The target rim under a PERSPECTIVE camera around a tiered conifer, the shape a game selects most. The visible
/// mask re-rasterizes the target through its own vertex program and depth-tests it against the scene depth the
/// model program wrote. The two programs do not produce bit-identical depth, and the difference grows with the
/// depth slope of the grazing faces that form a cone's silhouette. Without a slope-scaled bias whole silhouette
/// edges fail that test, and sub-centimetre camera moves change which ones, which reads as a broken, flickering rim.
/// </summary>
public sealed class TargetOutlinePerspectiveGoldenTests
{
    const int W = 480;
    const int H = 480;
    const int CameraSteps = 10;
    static readonly Color Background = new(0.05f, 0.05f, 0.35f, 1f);
    static readonly Color Body = new(0.1f, 0.85f, 0.1f, 1f);
    static readonly Color Rim = new(1f, 1f, 0f, 1f);
    static readonly Vector3 TreeAt = new(43.5f, 0.21f, 71.5f);

    [GpuFact]
    public void Golden3D_TargetOutline_ConiferRimTouchesItsWholeSilhouetteAsTheCameraCreeps()
    {
        AssertWholeSilhouette(AntiAliasing.Off);
        AssertWholeSilhouette(AntiAliasing.Msaa(4));
    }

    static void AssertWholeSilhouette(AntiAliasing antiAliasing)
    {
        for (int step = 0; step < CameraSteps; step++)
        {
            Vector3 camera = TreeAt + new Vector3(0.0131f * step, 6.65f, 7f + 0.0073f * step);
            byte[] baseline = Capture(camera, antiAliasing, outlined: false);
            byte[] outlined = Capture(camera, antiAliasing, outlined: true);
            (int boundary, int touched) = Touch(baseline, outlined);
            Assert.True(boundary > 400, $"conifer silhouette boundary had only {boundary} pixels");
            double fraction = touched / (double)boundary;
            Assert.True(fraction >= 0.99,
                $"{antiAliasing.Mode} step {step}: rim touched {touched} of {boundary} boundary pixels ({fraction:F3})");
        }
    }

    static byte[] Capture(Vector3 cameraPosition, AntiAliasing antiAliasing, bool outlined)
    {
        MeshHandle trunk = default;
        MeshHandle tier = default;
        var camera = new FlyCamera3D
        {
            Position = cameraPosition,
            Yaw = MathF.PI,
            Pitch = -0.76f,
            FieldOfView = MathF.PI / 3f,
            AspectRatio = (float)W / H,
            NearPlane = 0.1f,
            FarPlane = 600f,
        };
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                scene.Post.Starfield = false;
                scene.Post.Outline = false;
                scene.Post.RenderScale = RenderScale.MatchViewport;
                scene.Post.Quality.AntiAliasing = antiAliasing;
                scene.Post.BackgroundColor = Background;
                scene.CameraOverride = camera;
                trunk = scene.LoadMesh(MeshPrimitives.Cylinder(0.12f, 1f, 7));
                tier = scene.LoadMesh(MeshPrimitives.Cone(1f, 1f, 9));
            },
            drawFrame: scene =>
            {
                Matrix4x4 placement = Matrix4x4.CreateScale(1.07f) * Matrix4x4.CreateRotationY(1.13f)
                    * Matrix4x4.CreateTranslation(TreeAt);
                MeshOutlineGroup group = default;
                if (outlined) group = scene.BeginMeshOutline(Rim, 2f);
                Draw(scene, group, outlined, trunk, Matrix4x4.CreateScale(1f, 1.4f, 1f) * placement);
                for (int i = 0; i < 4; i++)
                {
                    float radius = 1.25f - 0.24f * i;
                    Matrix4x4 local = Matrix4x4.CreateScale(radius, 1.5f, radius)
                        * Matrix4x4.CreateRotationY(0.37f * i)
                        * Matrix4x4.CreateTranslation(0f, 0.9f + 0.95f * i, 0f);
                    Draw(scene, group, outlined, tier, local * placement);
                }
            },
            frames: 2);
    }

    static void Draw(Scene3D scene, MeshOutlineGroup group, bool outlined, MeshHandle mesh, Matrix4x4 world)
    {
        scene.Draw(mesh, world, Body, Material.Glowing(Body));
        if (outlined) scene.DrawMeshOutline(group, mesh, world);
    }

    // The boundary is every background pixel 4-adjacent to the body. The rim must reach each of them.
    static (int Boundary, int Touched) Touch(byte[] baseline, byte[] outlined)
    {
        int boundary = 0;
        int touched = 0;
        for (int y = 1; y < H - 1; y++)
            for (int x = 1; x < W - 1; x++)
            {
                if (IsBody(baseline, x, y)) continue;
                if (!IsBody(baseline, x - 1, y) && !IsBody(baseline, x + 1, y)
                    && !IsBody(baseline, x, y - 1) && !IsBody(baseline, x, y + 1)) continue;
                boundary++;
                int i = (y * W + x) * 4;
                if (outlined[i] > 120 && outlined[i + 1] > 120 && outlined[i + 2] < 90) touched++;
            }
        return (boundary, touched);
    }

    static bool IsBody(byte[] pixels, int x, int y)
    {
        int i = (y * W + x) * 4;
        return pixels[i + 1] > 60 && pixels[i + 1] > pixels[i + 2];
    }
}
