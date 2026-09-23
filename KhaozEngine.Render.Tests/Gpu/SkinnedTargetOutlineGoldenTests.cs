using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class SkinnedTargetOutlineGoldenTests(SkinnedTargetOutlineScene scene)
    : IClassFixture<SkinnedTargetOutlineScene>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_rigid_and_skinned_parts_have_no_internal_stroke(bool gpuSkinning)
    {
        byte[] skinned = scene.CaptureMixedPart(
            gpuSkinning, SkinnedTargetOutlineScene.MixedPartCase.SkinnedBody);
        byte[] rigid = scene.CaptureMixedPart(
            gpuSkinning, SkinnedTargetOutlineScene.MixedPartCase.RigidBody);
        byte[] unoutlined = scene.CaptureMixedGroup(gpuSkinning, outlined: false);
        byte[] outlined = scene.CaptureMixedGroup(gpuSkinning, outlined: true);
        int seamPixels = 0;
        int internalRim = 0;

        for (int y = 2; y < SkinnedTargetOutlineScene.H - 2; y++)
            for (int x = 2; x < SkinnedTargetOutlineScene.W - 2; x++)
            {
                if (!SkinnedTargetOutlineScene.IsErodedBody(skinned, x, y, 2)
                    || !SkinnedTargetOutlineScene.IsErodedBody(rigid, x, y, 2)) continue;
                seamPixels++;
                if (SkinnedTargetOutlineScene.IsRim(outlined, y * SkinnedTargetOutlineScene.W + x)) internalRim++;
            }

        Assert.True(seamPixels >= 20, $"mixed controls exposed only {seamPixels} interior overlap pixels");
        Assert.Equal(0, SkinnedTargetOutlineScene.CountRim(unoutlined));
        Assert.Equal(0, internalRim);
        Assert.True(SkinnedTargetOutlineScene.CountRim(outlined) >= 80,
            "mixed target did not draw a substantial external rim");
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outline_never_replaces_ordinary_target_coverage(bool gpuSkinning)
    {
        byte[] baseline = scene.CaptureMixed(gpuSkinning, outlined: false);
        byte[] outlined = scene.CaptureMixed(gpuSkinning, outlined: true);
        int rimPixels = 0;

        for (int pixel = 0; pixel < outlined.Length / 4; pixel++)
        {
            if (!SkinnedTargetOutlineScene.IsRim(outlined, pixel)) continue;
            rimPixels++;
            Assert.False(SkinnedTargetOutlineScene.IsBody(baseline, pixel),
                $"outline replaced ordinary target coverage at pixel {pixel}, baseline "
                + $"{Pixel(baseline, pixel)}, outlined {Pixel(outlined, pixel)}");
        }

        Assert.True(rimPixels >= 80, $"mixed target drew only {rimPixels} rim pixels");
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opaque_wall_suppresses_default_outline_without_an_occlusion_cut_line(bool gpuSkinning)
    {
        byte[] baseline = scene.CaptureOcclusion(gpuSkinning, outlined: false);
        byte[] outlined = scene.CaptureOcclusion(gpuSkinning, outlined: true);
        int wallPixels = 0;
        int changedWall = 0;
        int externalRim = 0;

        for (int pixel = 0; pixel < baseline.Length / 4; pixel++)
        {
            if (SkinnedTargetOutlineScene.IsWall(baseline, pixel))
            {
                wallPixels++;
                if (!SkinnedTargetOutlineScene.SamePixel(baseline, outlined, pixel)) changedWall++;
            }
            if (SkinnedTargetOutlineScene.IsRim(outlined, pixel)
                && !SkinnedTargetOutlineScene.IsWall(baseline, pixel)) externalRim++;
        }

        Assert.True(wallPixels >= 100, $"occlusion control drew only {wallPixels} wall pixels");
        Assert.Equal(0, changedWall);
        Assert.True(externalRim >= 40, $"unobscured side drew only {externalRim} rim pixels");
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_dissolve_changes_visible_external_border_without_interior_dither_strokes(bool gpuSkinning)
    {
        byte[] solidBody = scene.CaptureDissolve(
            gpuSkinning, SkinnedTargetOutlineScene.DissolveCase.SolidBaseline);
        byte[] partialBody = scene.CaptureDissolve(
            gpuSkinning, SkinnedTargetOutlineScene.DissolveCase.PartialBody);
        byte[] solidOutline = scene.CaptureSolidDissolveOutline(gpuSkinning);
        byte[] partialOutline = scene.CaptureDissolve(
            gpuSkinning, SkinnedTargetOutlineScene.DissolveCase.PartialSceneDepthOutline);
        int changedBody = 0;
        int changedRim = 0;
        int partialExternalRim = 0;
        int interiorRim = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                if (SkinnedTargetOutlineScene.IsBody(solidBody, pixel)
                    != SkinnedTargetOutlineScene.IsBody(partialBody, pixel)) changedBody++;
                if (SkinnedTargetOutlineScene.IsRim(solidOutline, pixel)
                    != SkinnedTargetOutlineScene.IsRim(partialOutline, pixel)) changedRim++;
                if (SkinnedTargetOutlineScene.IsRim(partialOutline, pixel)
                    && SkinnedTargetOutlineScene.IsErodedBody(solidBody, x, y, 3)) interiorRim++;
                if (SkinnedTargetOutlineScene.IsRim(partialOutline, pixel)
                    && !SkinnedTargetOutlineScene.IsErodedBody(solidBody, x, y, 3)) partialExternalRim++;
            }

        Assert.True(changedBody >= 100, $"partial dissolve changed only {changedBody} body pixels");
        Assert.Equal(0, SkinnedTargetOutlineScene.CountRim(partialBody));
        Assert.True(changedRim >= 20, $"partial dissolve changed only {changedRim} external rim pixels");
        Assert.True(partialExternalRim >= 40,
            $"partial scene-depth outline drew only {partialExternalRim} external rim pixels");
        Assert.Equal(0, interiorRim);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Through_geometry_partial_dissolve_uses_the_full_envelope(bool gpuSkinning)
    {
        byte[] solidBody = scene.CaptureDissolve(
            gpuSkinning, SkinnedTargetOutlineScene.DissolveCase.SolidBaseline);
        byte[] solidOutline = scene.CaptureSolidDissolveOutline(gpuSkinning);
        byte[] partialThrough = scene.CaptureDissolve(
            gpuSkinning, SkinnedTargetOutlineScene.DissolveCase.PartialThroughGeometryOutline);
        int missingFromPartial = 0;
        int missingFromSolid = 0;
        int interiorRim = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                if (SkinnedTargetOutlineScene.IsRim(solidOutline, pixel)
                    && !SkinnedTargetOutlineScene.HasRimNear(partialThrough, x, y, 2)) missingFromPartial++;
                if (SkinnedTargetOutlineScene.IsRim(partialThrough, pixel)
                    && !SkinnedTargetOutlineScene.HasRimNear(solidOutline, x, y, 2)) missingFromSolid++;
                if (SkinnedTargetOutlineScene.IsRim(partialThrough, pixel)
                    && SkinnedTargetOutlineScene.IsErodedBody(solidBody, x, y, 3)) interiorRim++;
            }

        Assert.True(SkinnedTargetOutlineScene.CountRim(solidOutline) >= 80,
            "solid envelope did not draw a substantial rim");
        Assert.True(SkinnedTargetOutlineScene.CountRim(partialThrough) >= 80,
            "through-geometry dissolve did not draw a substantial rim");
        Assert.Equal(0, missingFromPartial);
        Assert.Equal(0, missingFromSolid);
        Assert.Equal(0, interiorRim);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skinned_alpha_cutout_hole_is_absent_from_body_and_is_an_outline_hole(bool gpuSkinning)
    {
        byte[] baseline = scene.CaptureCutout(gpuSkinning, outlined: false);
        byte[] outlined = scene.CaptureCutout(gpuSkinning, outlined: true);
        (int minX, int minY, int maxX, int maxY) = BodyBounds(baseline);
        CutoutMetrics metrics = MeasureCutout(baseline, outlined, minX, minY, maxX, maxY);

        Assert.True(metrics.BoundaryPixels >= 20,
            $"baseline exposed only {metrics.BoundaryPixels} transparent hole-boundary pixels");
        Assert.True(metrics.CenterPixels >= 20,
            $"baseline exposed only {metrics.CenterPixels} well-inside transparent center pixels");
        Assert.Equal(0, metrics.RimOverOpaqueBody);
        Assert.True(metrics.BoundaryRim >= 20,
            $"alpha-cutout hole boundary drew only {metrics.BoundaryRim} rim pixels");
        Assert.Equal(0, metrics.CenterRim);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cutout_metric_rejects_unrelated_rim_inside_the_transparent_center(bool gpuSkinning)
    {
        byte[] baseline = scene.CaptureCutout(gpuSkinning, outlined: false);
        byte[] actual = scene.CaptureCutout(gpuSkinning, outlined: true);
        (int minX, int minY, int maxX, int maxY) = BodyBounds(baseline);
        byte[] falsePass = CutoutFalsePass(baseline, actual, minX, minY, maxX, maxY);
        CutoutMetrics metrics = MeasureCutout(baseline, falsePass, minX, minY, maxX, maxY);

        Assert.True(metrics.UnscopedInteriorRim >= 20,
            "negative control did not satisfy the retired broad interior-rim count");
        Assert.True(metrics.BoundaryRim < 20,
            "negative control unexpectedly retained enough real hole-boundary rim");
        Assert.True(metrics.CenterRim >= 20,
            "negative control did not place unrelated rim in the transparent center");
    }

    [GpuFact]
    public void Reused_scene_matches_a_fresh_scene_after_outline_configuration_changes()
    {
        scene.CaptureMixed(gpuSkinning: false, outlined: false);
        scene.CaptureOcclusion(gpuSkinning: true, outlined: true);
        scene.CaptureDissolve(
            gpuSkinning: false, SkinnedTargetOutlineScene.DissolveCase.PartialThroughGeometryOutline);
        scene.CaptureCutout(gpuSkinning: true, outlined: true);
        byte[] aged = scene.CaptureMixed(gpuSkinning: true, outlined: true);

        using var fresh = new SkinnedTargetOutlineScene();
        byte[] alone = fresh.CaptureMixed(gpuSkinning: true, outlined: true);
        Assert.Equal(alone, aged);
    }

    [GpuFact]
    public void Golden3D_SkinnedTargetOutline_Cpu()
    {
        byte[] frame = scene.CaptureMixed(gpuSkinning: false, outlined: true);
        GoldenCompare.AssertOrUpdate(
            "skinned_target_outline_cpu", frame, SkinnedTargetOutlineScene.W, SkinnedTargetOutlineScene.H);
    }

    [GpuFact]
    public void Golden3D_SkinnedTargetOutline_Gpu()
    {
        byte[] frame = scene.CaptureMixed(gpuSkinning: true, outlined: true);
        GoldenCompare.AssertOrUpdate(
            "skinned_target_outline_gpu", frame, SkinnedTargetOutlineScene.W, SkinnedTargetOutlineScene.H);
    }

    static (int MinX, int MinY, int MaxX, int MaxY) BodyBounds(byte[] pixels)
    {
        int minX = SkinnedTargetOutlineScene.W;
        int minY = SkinnedTargetOutlineScene.H;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                if (!SkinnedTargetOutlineScene.IsBody(pixels, y * SkinnedTargetOutlineScene.W + x)) continue;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        Assert.True(maxX >= minX && maxY >= minY, "cutout baseline body coverage was empty");
        return (minX, minY, maxX, maxY);
    }

    static string Pixel(byte[] pixels, int pixel)
    {
        int i = pixel * 4;
        return $"({pixels[i]},{pixels[i + 1]},{pixels[i + 2]},{pixels[i + 3]})";
    }

    static CutoutMetrics MeasureCutout(
        byte[] baseline,
        byte[] outlined,
        int minX,
        int minY,
        int maxX,
        int maxY)
    {
        int boundaryPixels = 0;
        int boundaryRim = 0;
        int centerPixels = 0;
        int centerRim = 0;
        int rimOverOpaqueBody = 0;
        int unscopedInteriorRim = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                bool rim = SkinnedTargetOutlineScene.IsRim(outlined, pixel);
                if (rim && SkinnedTargetOutlineScene.IsBody(baseline, pixel)) rimOverOpaqueBody++;
                if (rim && IsInsideCutout(minX, minY, maxX, maxY, x, y)) unscopedInteriorRim++;
                if (IsHoleBoundary(baseline, x, y, minX, minY, maxX, maxY))
                {
                    boundaryPixels++;
                    if (rim) boundaryRim++;
                }
                if (IsHoleCenter(baseline, x, y, minX, minY, maxX, maxY))
                {
                    centerPixels++;
                    if (rim) centerRim++;
                }
            }

        return new CutoutMetrics(
            boundaryPixels,
            boundaryRim,
            centerPixels,
            centerRim,
            rimOverOpaqueBody,
            unscopedInteriorRim);
    }

    static byte[] CutoutFalsePass(
        byte[] baseline,
        byte[] actual,
        int minX,
        int minY,
        int maxX,
        int maxY)
    {
        byte[] falsePass = (byte[])actual.Clone();
        int paintedCenter = 0;
        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                if (IsHoleBoundary(baseline, x, y, minX, minY, maxX, maxY))
                    CopyPixel(baseline, falsePass, pixel);
                if (paintedCenter >= 20
                    || !IsHoleCenter(baseline, x, y, minX, minY, maxX, maxY)) continue;
                SetRim(falsePass, pixel);
                paintedCenter++;
            }
        return falsePass;
    }

    static bool IsHoleBoundary(
        byte[] baseline,
        int x,
        int y,
        int minX,
        int minY,
        int maxX,
        int maxY) =>
        IsInsideCutout(minX, minY, maxX, maxY, x, y)
        && !SkinnedTargetOutlineScene.IsBody(baseline, y * SkinnedTargetOutlineScene.W + x)
        && SkinnedTargetOutlineScene.HasBodyNear(baseline, x, y, 3);

    static bool IsHoleCenter(
        byte[] baseline,
        int x,
        int y,
        int minX,
        int minY,
        int maxX,
        int maxY) =>
        IsInsideCutout(minX, minY, maxX, maxY, x, y)
        && !SkinnedTargetOutlineScene.IsBody(baseline, y * SkinnedTargetOutlineScene.W + x)
        && !SkinnedTargetOutlineScene.HasBodyNear(baseline, x, y, 8);

    static bool IsInsideCutout(int minX, int minY, int maxX, int maxY, int x, int y)
    {
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        return x >= minX + width / 5
            && x <= maxX - width / 5
            && y >= minY + height / 5
            && y <= maxY - height / 5;
    }

    static void CopyPixel(byte[] source, byte[] destination, int pixel)
    {
        int i = pixel * 4;
        destination[i] = source[i];
        destination[i + 1] = source[i + 1];
        destination[i + 2] = source[i + 2];
        destination[i + 3] = source[i + 3];
    }

    static void SetRim(byte[] pixels, int pixel)
    {
        int i = pixel * 4;
        pixels[i] = 255;
        pixels[i + 1] = 0;
        pixels[i + 2] = 0;
        pixels[i + 3] = 255;
    }

    readonly record struct CutoutMetrics(
        int BoundaryPixels,
        int BoundaryRim,
        int CenterPixels,
        int CenterRim,
        int RimOverOpaqueBody,
        int UnscopedInteriorRim);
}
