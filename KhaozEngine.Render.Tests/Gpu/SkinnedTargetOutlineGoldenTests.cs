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
        byte[] outlined = scene.CaptureMixed(gpuSkinning, outlined: true);
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
            }

        Assert.True(changedBody >= 100, $"partial dissolve changed only {changedBody} body pixels");
        Assert.True(changedRim >= 20, $"partial dissolve changed only {changedRim} external rim pixels");
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
        int width = maxX - minX + 1;
        int height = maxY - minY + 1;
        int holeBody = 0;
        int holeRim = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                if (SkinnedTargetOutlineScene.IsRim(outlined, pixel))
                    Assert.False(SkinnedTargetOutlineScene.IsBody(baseline, pixel),
                        $"cutout outline covered opaque baseline texel {x},{y}");

                bool inHoleCentre = x >= minX + width * 35 / 100 && x <= maxX - width * 35 / 100
                    && y >= minY + height * 35 / 100 && y <= maxY - height * 35 / 100;
                if (inHoleCentre && SkinnedTargetOutlineScene.IsBody(baseline, pixel)) holeBody++;
                bool insideOuterEdge = x >= minX + 8 && x <= maxX - 8
                    && y >= minY + 8 && y <= maxY - 8;
                if (insideOuterEdge && SkinnedTargetOutlineScene.IsRim(outlined, pixel)) holeRim++;
            }

        Assert.Equal(0, holeBody);
        Assert.True(holeRim >= 20, $"alpha-cutout hole drew only {holeRim} rim pixels");
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
}
