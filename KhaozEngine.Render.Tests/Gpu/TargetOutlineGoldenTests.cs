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
        AssertOcclusion(AntiAliasing.Off);
        AssertOcclusion(AntiAliasing.Msaa(4));
    }

    static void AssertOcclusion(AntiAliasing antiAliasing)
    {
        byte[] baseline = CaptureOcclusion(outlined: false, antiAliasing);
        byte[] pixels = CaptureOcclusion(outlined: true, antiAliasing);
        int rim = 0;
        int changedWall = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (IsBlue(baseline, i) && !SamePixel(baseline, pixels, i)) changedWall++;
            if (!IsRim(pixels, i)) continue;
            rim++;
            Assert.False(IsBody(baseline, i), $"outline drew on the target interior at pixel {i / 4}");
            Assert.False(IsBlue(baseline, i), $"outline leaked onto the near wall at pixel {i / 4}");
        }
        Assert.Equal(0, changedWall);
        Assert.True(rim > 60, $"visible external silhouette drew only {rim} pixels");
    }

    [GpuFact]
    public void Golden3D_TargetOutline_MinimumWidthIsVisibleAndFractionalWidthAddsCoverage()
    {
        byte[] half = CaptureSingleWidth(0.5f);
        byte[] oneAndQuarter = CaptureSingleWidth(1.25f);
        long halfEnergy = RimEnergy(half);
        long widerEnergy = RimEnergy(oneAndQuarter);

        Assert.True(halfEnergy > 500, $"0.5 pixel outline energy was {halfEnergy}");
        Assert.True(widerEnergy > halfEnergy * 3 / 2,
            $"1.25 pixel outline energy {widerEnergy} did not exceed 0.5 pixel energy {halfEnergy}");
    }

    [GpuFact]
    public void Golden3D_TargetOutline_AlphaCutoutHoleIsARealSilhouetteHole()
    {
        byte[] baseline = CaptureCutout(outlined: false);
        byte[] outlined = CaptureCutout(outlined: true);
        (int minX, int minY, int maxX, int maxY) = BodyBounds(baseline);
        int internalRim = 0;
        for (int y = minY + 8; y <= maxY - 8; y++)
            for (int x = minX + 8; x <= maxX - 8; x++)
            {
                int i = (y * W + x) * 4;
                if (!IsRim(outlined, i)) continue;
                internalRim++;
                Assert.False(IsBody(baseline, i), $"cutout outline covered an opaque texel at {x},{y}");
            }
        Assert.True(internalRim > 20, $"alpha-cutout hole drew only {internalRim} internal rim pixels");
    }

    [GpuFact]
    public void Golden3D_TargetOutline_BackfacingSheetMatchesTheModelsTwoSidedCoverage()
    {
        byte[] baseline = CaptureBackfacingSheet(outlined: false);
        byte[] outlined = CaptureBackfacingSheet(outlined: true);
        int body = 0;
        int rim = 0;
        for (int i = 0; i < outlined.Length; i += 4)
        {
            if (IsBody(baseline, i)) body++;
            if (!IsRim(outlined, i)) continue;
            rim++;
            Assert.False(IsBody(baseline, i), $"backface outline covered model coverage at pixel {i / 4}");
        }
        Assert.True(body > 500, $"ordinary model path culled the test sheet, body pixels {body}");
        Assert.True(rim > 40, $"mask culling disagreed with the ordinary model path, rim pixels {rim}");
    }

    [GpuFact]
    public void Golden3D_TargetOutline_TouchesTheSilhouetteAtTwoSizesAndLowResolutionUpscale()
    {
        OutlineMetric small = MeasureOutline(scale: 0.65f, fixedWidth: 0, fixedHeight: 0);
        OutlineMetric large = MeasureOutline(scale: 1.5f, fixedWidth: 0, fixedHeight: 0);
        OutlineMetric upscale = MeasureOutline(scale: 1f, fixedWidth: W / 2, fixedHeight: H / 2);

        float min = Math.Min(small.Thickness, Math.Min(large.Thickness, upscale.Thickness));
        float max = Math.Max(small.Thickness, Math.Max(large.Thickness, upscale.Thickness));
        Assert.True(max < min * 1.75f,
            $"physical thickness drifted: small {small.Thickness:0.00}, large {large.Thickness:0.00}, upscale {upscale.Thickness:0.00}");
        Assert.True(small.TouchFraction > 0.9f && large.TouchFraction > 0.9f && upscale.TouchFraction > 0.85f,
            $"rim detached: small {small.TouchFraction:0.00}, large {large.TouchFraction:0.00}, upscale {upscale.TouchFraction:0.00}");
    }

    [GpuFact]
    public void Golden3D_TargetOutline_FullyDissolvedPartDoesNotEnlargeTheUnionEnvelope()
    {
        byte[] visibleOnly = CaptureDissolvedEnvelope(includeInvisiblePart: false);
        byte[] withInvisible = CaptureDissolvedEnvelope(includeInvisiblePart: true);

        Assert.Equal(visibleOnly, withInvisible);
    }

    static byte[] CaptureOcclusion(bool outlined, AntiAliasing antiAliasing)
    {
        MeshHandle target = default;
        MeshHandle wall = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, antiAliasing);
                target = scene.LoadMesh(MeshPrimitives.Box(1f));
                wall = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4 targetWorld = Matrix4x4.CreateScale(1.5f, 2f, 0.8f)
                    * Matrix4x4.CreateTranslation(0f, 1f, 0f);
                scene.Draw(target, targetWorld, Body, Material.Glowing(Body));
                if (outlined) scene.DrawMeshOutline(target, targetWorld, Rim, 1.25f);
                scene.Draw(wall, Matrix4x4.CreateScale(0.7f, 2.6f, 0.5f)
                    * Matrix4x4.CreateTranslation(0.6f, 1.3f, 1.1f), new Color(0f, 0.2f, 1f, 1f),
                    Material.Glowing(new Color(0f, 0.2f, 1f, 1f)));
            }, frames: 2);
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

    static byte[] CaptureSingleWidth(float widthPixels)
    {
        MeshHandle box = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Off);
                box = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4 world = Matrix4x4.CreateScale(1.2f, 1.8f, 0.8f)
                    * Matrix4x4.CreateTranslation(0f, 0.9f, 0f);
                scene.Draw(box, world, Body, Material.Glowing(Body));
                scene.DrawMeshOutline(box, world, Rim, widthPixels);
            }, frames: 2);
    }

    static OutlineMetric MeasureOutline(float scale, int fixedWidth, int fixedHeight)
    {
        byte[] baseline = CaptureScale(outlined: false, scale, fixedWidth, fixedHeight);
        byte[] outlined = CaptureScale(outlined: true, scale, fixedWidth, fixedHeight);
        int rimPixels = 0;
        int boundaryPixels = 0;
        int touchedBoundary = 0;
        for (int y = 1; y < H - 1; y++)
            for (int x = 1; x < W - 1; x++)
            {
                int i = (y * W + x) * 4;
                if (IsRimDelta(baseline, outlined, i)) rimPixels++;
                if (!IsBody(baseline, i) || AllBodyNeighbours(baseline, x, y)) continue;
                boundaryPixels++;
                if (HasRimNear(baseline, outlined, x, y, 3)) touchedBoundary++;
            }
        Assert.True(rimPixels > 20 && boundaryPixels > 20,
            $"metric scene was empty: rim {rimPixels}, boundary {boundaryPixels}");
        return new OutlineMetric((float)rimPixels / boundaryPixels, (float)touchedBoundary / boundaryPixels);
    }

    static byte[] CaptureScale(bool outlined, float scale, int fixedWidth, int fixedHeight)
    {
        MeshHandle box = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Off);
                if (fixedWidth > 0)
                {
                    scene.Post.RenderScale = RenderScale.FixedInternal;
                    scene.Post.RenderWidth = fixedWidth;
                    scene.Post.RenderHeight = fixedHeight;
                }
                box = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4 world = Matrix4x4.CreateScale(scale, 1.4f * scale, 0.8f * scale)
                    * Matrix4x4.CreateTranslation(0f, 0.7f * scale, 0f);
                scene.Draw(box, world, Body, Material.Glowing(Body));
                if (outlined) scene.DrawMeshOutline(box, world, Rim, 1.25f);
            }, frames: 2);
    }

    static byte[] CaptureDissolvedEnvelope(bool includeInvisiblePart)
    {
        MeshHandle box = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Msaa(4));
                box = scene.LoadMesh(MeshPrimitives.Box(1f));
            },
            drawFrame: scene =>
            {
                Matrix4x4 visible = Matrix4x4.CreateScale(0.8f, 1.4f, 0.7f)
                    * Matrix4x4.CreateTranslation(0f, 0.7f, 0f);
                scene.Draw(box, visible, Body, Material.Glowing(Body));
                MeshOutlineGroup group = scene.BeginMeshOutline(Rim, 1.25f);
                scene.DrawMeshOutline(group, box, visible);
                if (includeInvisiblePart)
                {
                    Matrix4x4 envelope = Matrix4x4.CreateScale(1.8f, 2.3f, 1.4f)
                        * Matrix4x4.CreateTranslation(0f, 1.15f, 0f);
                    scene.DrawMeshOutlineDissolved(group, box, envelope, 1f, dissolveComplement: false);
                }
            }, frames: 2);
    }

    static bool AllBodyNeighbours(byte[] pixels, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (!IsBody(pixels, ((y + dy) * W + x + dx) * 4)) return false;
        return true;
    }

    static bool HasRimNear(byte[] baseline, byte[] outlined, int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
                if (IsRimDelta(baseline, outlined, ((y + dy) * W + x + dx) * 4)) return true;
        return false;
    }

    static bool IsRimDelta(byte[] baseline, byte[] outlined, int i) =>
        outlined[i] > baseline[i] + 8
        && outlined[i] - outlined[i + 1] > baseline[i] - baseline[i + 1] + 8;

    static byte[] CaptureCutout(bool outlined)
    {
        MeshHandle sheet = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Off);
                byte[] pixels = new byte[8 * 8 * 4];
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 8; x++)
                    {
                        int i = (y * 8 + x) * 4;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                        pixels[i + 3] = x >= 2 && x <= 5 && y >= 2 && y <= 5 ? (byte)0 : (byte)255;
                    }
                Scene3D.TextureHandle texture = scene.LoadTexture(pixels, 8, 8, TextureMipPolicy.None);
                sheet = scene.LoadMesh(BackfacingSheet(), new Scene3D.SurfaceMaps(texture, alphaCutoff: 0.5f));
            },
            drawFrame: scene =>
            {
                scene.Draw(sheet, Matrix4x4.Identity, Body, Material.Glowing(Body));
                if (outlined) scene.DrawMeshOutline(sheet, Matrix4x4.Identity, Rim, 1.25f);
            }, frames: 2);
    }

    static byte[] CaptureBackfacingSheet(bool outlined)
    {
        MeshHandle sheet = default;
        return Render3DSnapshot.Capture(W, H,
            setup: scene =>
            {
                Configure(scene, AntiAliasing.Off);
                sheet = scene.LoadMesh(BackfacingSheet());
            },
            drawFrame: scene =>
            {
                scene.Draw(sheet, Matrix4x4.Identity, Body, Material.Glowing(Body));
                if (outlined) scene.DrawMeshOutline(sheet, Matrix4x4.Identity, Rim, 1.25f);
            }, frames: 2);
    }

    static GltfMesh BackfacingSheet()
    {
        Vector3 n = -Vector3.UnitZ;
        Vector4 c = Vector4.One;
        ModelVertex[] vertices =
        {
            new(new Vector3(-1f, 0f, 0f), n, c, new Vector2(0f, 1f)),
            new(new Vector3(1f, 0f, 0f), n, c, new Vector2(1f, 1f)),
            new(new Vector3(1f, 2f, 0f), n, c, new Vector2(1f, 0f)),
            new(new Vector3(-1f, 2f, 0f), n, c, new Vector2(0f, 0f)),
        };
        return new GltfMesh(vertices, new ushort[] { 0, 2, 1, 0, 3, 2 });
    }

    static long RimEnergy(byte[] pixels)
    {
        long energy = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            energy += Math.Max(0, pixels[i] - Math.Max(pixels[i + 1], pixels[i + 2]));
        return energy;
    }

    static (int minX, int minY, int maxX, int maxY) BodyBounds(byte[] pixels)
    {
        int minX = W;
        int minY = H;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                if (!IsBody(pixels, (y * W + x) * 4)) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        Assert.True(maxX >= minX && maxY >= minY, "baseline body coverage was empty");
        return (minX, minY, maxX, maxY);
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

    static bool SamePixel(byte[] left, byte[] right, int i) =>
        left[i] == right[i] && left[i + 1] == right[i + 1]
        && left[i + 2] == right[i + 2] && left[i + 3] == right[i + 3];

    readonly record struct OutlineMetric(float Thickness, float TouchFraction);
}
