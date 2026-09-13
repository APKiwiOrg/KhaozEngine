using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class TargetOutlineGoldenTests
{
    const int W = 480;
    const int H = 320;
    static readonly Color Body = new(0.12f, 0.72f, 0.25f, 1f);
    static readonly Color Rim = new(0.95f, 0.08f, 0.04f, 1f);

    [GpuFact]
    public void Golden3D_TargetOutline_GroupedPartsDrawOnlyOutsideTargetCoverage()
    {
        byte[] baseline = Capture(outlined: false);
        byte[] outlined = Capture(outlined: true);
        int rimPixels = 0;

        for (int i = 0; i < outlined.Length; i += 4)
        {
            if (!IsRim(outlined, i)) continue;
            rimPixels++;
            Assert.False(IsBody(baseline, i), $"outline painted target interior at pixel {i / 4}");
        }

        Assert.InRange(rimPixels, 100, 900);
    }

    [GpuFact]
    public void Golden3D_TargetOutline_Msaa4AndSsaa2KeepTheRimOutsideBaselineCoverage()
    {
        AssertOutsideBaseline(AntiAliasing.Msaa(4));
        AssertOutsideBaseline(AntiAliasing.Ssaa(2f));
    }

    [GpuFact]
    public void Golden3D_TargetOutline_NearWallSuppressesTheOcclusionCutAndDestinationLeak()
    {
        MeshHandle target = default;
        MeshHandle wall = default;
        byte[] pixels = Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Off);
                target = scene.LoadMesh(MeshPrimitives.Box(1f));
                wall = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4 targetWorld = Matrix4x4.CreateScale(1.5f, 2f, 0.8f)
                    * Matrix4x4.CreateTranslation(0f, 1f, 0f);
                scene.Draw(target, targetWorld, Body, Material.Glowing(Body));
                scene.DrawMeshOutline(target, targetWorld, Rim, 1.25f);
                scene.Draw(wall, Matrix4x4.CreateScale(0.7f, 2.6f, 0.5f)
                    * Matrix4x4.CreateTranslation(0.6f, 1.3f, 1.1f), new Color(0f, 0.2f, 1f, 1f),
                    Material.Glowing(new Color(0f, 0.2f, 1f, 1f)));
            }, frames: 2);

        int rim = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (!IsRim(pixels, i)) continue;
            rim++;
            Assert.False(IsBlue(pixels, i), $"outline leaked onto the near wall at pixel {i / 4}");
        }
        Assert.True(rim > 60, $"visible external silhouette drew only {rim} pixels");
    }

    [GpuFact]
    public void Golden3D_TargetOutline_MultipleGroupsKeepTheirOwnGeometryAndColor()
    {
        MeshHandle box = default;
        byte[] pixels = Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Off);
                box = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4 left = Matrix4x4.CreateTranslation(-1.35f, 0.5f, 0f);
                scene.Draw(box, left, Body, Material.Glowing(Body));
                scene.DrawMeshOutline(box, left, Rim, 1.25f);

                MeshOutlineGroup right = scene.BeginMeshOutline(new Color(1f, 0.85f, 0.05f, 1f), 1.25f);
                Matrix4x4 rightLow = Matrix4x4.CreateScale(0.7f)
                    * Matrix4x4.CreateTranslation(1.25f, 0.35f, 0f);
                Matrix4x4 rightHigh = Matrix4x4.CreateScale(0.45f)
                    * Matrix4x4.CreateTranslation(1.55f, 1.0f, 0f);
                scene.Draw(box, rightLow, Body, Material.Glowing(Body));
                scene.Draw(box, rightHigh, Body, Material.Glowing(Body));
                scene.DrawMeshOutline(right, box, rightLow);
                scene.DrawMeshOutline(right, box, rightHigh);
            }, frames: 2);

        int redLeft = 0;
        int yellowRight = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                if (x < W / 2 && IsRim(pixels, i)) redLeft++;
                if (x > W / 2 && IsYellow(pixels, i)) yellowRight++;
            }
        Assert.True(redLeft > 40, $"first group lost its geometry or color, red pixels {redLeft}");
        Assert.True(yellowRight > 40, $"second group lost its geometry or color, yellow pixels {yellowRight}");
    }

    static void AssertOutsideBaseline(AntiAliasing antiAliasing)
    {
        byte[] baseline = Capture(outlined: false, antiAliasing);
        byte[] outlined = Capture(outlined: true, antiAliasing);
        int rim = 0;
        for (int i = 0; i < outlined.Length; i += 4)
        {
            if (!IsRim(outlined, i)) continue;
            rim++;
            Assert.False(IsBody(baseline, i),
                $"{antiAliasing} outline painted baseline target coverage at pixel {i / 4}");
        }
        Assert.InRange(rim, 80, 1100);
    }

    static byte[] Capture(bool outlined, AntiAliasing? antiAliasing = null)
    {
        MeshHandle box = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, antiAliasing ?? AntiAliasing.Off);
                box = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4[] parts =
                {
                    Matrix4x4.CreateScale(0.8f, 1.2f, 0.65f) * Matrix4x4.CreateTranslation(0f, 0.7f, 0f),
                    Matrix4x4.CreateScale(0.48f, 0.7f, 0.5f) * Matrix4x4.CreateTranslation(-0.48f, 1.15f, 0f),
                    Matrix4x4.CreateScale(0.48f, 0.7f, 0.5f) * Matrix4x4.CreateTranslation(0.48f, 1.15f, 0f),
                    Matrix4x4.CreateScale(0.42f, 0.85f, 0.48f) * Matrix4x4.CreateTranslation(-0.24f, 0.05f, 0f),
                    Matrix4x4.CreateScale(0.42f, 0.85f, 0.48f) * Matrix4x4.CreateTranslation(0.24f, 0.05f, 0f),
                };
                MeshOutlineGroup group = default;
                if (outlined) group = scene.BeginMeshOutline(Rim, 1.25f);
                foreach (Matrix4x4 world in parts)
                {
                    scene.Draw(box, world, Body, Material.Glowing(Body));
                    if (outlined) scene.DrawMeshOutline(group, box, world);
                }
            },
            frames: 2);
    }

    static void Configure(Scene3D scene, AntiAliasing antiAliasing)
    {
        scene.Post.Starfield = false;
        scene.Post.Outline = false;
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.Quality.AntiAliasing = antiAliasing;
        scene.Post.BackgroundColor = new Color(0.025f, 0.03f, 0.045f, 1f);
        scene.Camera.Frame(new Vector3(0f, 1.1f, 0f), new Vector3(3.8f, 2.7f, 5.5f));
    }

    static bool IsBody(byte[] pixels, int i) =>
        pixels[i + 1] > 110 && pixels[i + 1] > pixels[i] * 2 && pixels[i + 1] > pixels[i + 2] * 2;

    static bool IsRim(byte[] pixels, int i) =>
        pixels[i] > 140 && pixels[i] > pixels[i + 1] * 2 && pixels[i] > pixels[i + 2] * 2;

    static bool IsBlue(byte[] pixels, int i) => pixels[i + 2] > 120 && pixels[i + 2] > pixels[i] * 2;
    static bool IsYellow(byte[] pixels, int i) =>
        pixels[i] > 120 && pixels[i + 1] > 120 && pixels[i + 2] < 80;
}
