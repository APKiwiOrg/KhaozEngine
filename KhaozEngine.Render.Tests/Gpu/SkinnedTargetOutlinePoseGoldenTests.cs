using KhaozEngine.Imaging;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class SkinnedTargetOutlinePoseGoldenTests(SkinnedTargetOutlineScene scene)
    : IClassFixture<SkinnedTargetOutlineScene>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bent_outline_follows_bent_pose_and_not_rest_pose(bool gpuSkinning)
    {
        byte[] rest = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.RestBody);
        byte[] bent = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.BentBody);
        byte[] outlined = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.BentOutlineOnly);
        int bentRim = 0;
        int restRim = 0;
        int bentOnlyPixels = 0;
        int restOnlyPixels = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                bool bentBody = SkinnedTargetOutlineScene.IsBody(bent, pixel);
                bool restBody = SkinnedTargetOutlineScene.IsBody(rest, pixel);
                if (bentBody && !restBody) bentOnlyPixels++;
                if (restBody && !bentBody) restOnlyPixels++;
                if (!SkinnedTargetOutlineScene.IsRim(outlined, pixel)) continue;
                if (SkinnedTargetOutlineScene.HasExclusiveBodyNear(bent, rest, x, y, 4)
                    && !SkinnedTargetOutlineScene.HasBodyNear(rest, x, y, 4)) bentRim++;
                if (SkinnedTargetOutlineScene.HasExclusiveBodyNear(rest, bent, x, y, 4)
                    && !SkinnedTargetOutlineScene.HasBodyNear(bent, x, y, 4)) restRim++;
            }

        Assert.True(bentOnlyPixels >= 200, $"bent control had only {bentOnlyPixels} exclusive body pixels");
        Assert.True(restOnlyPixels >= 200, $"rest control had only {restOnlyPixels} exclusive body pixels");
        Assert.True(bentRim >= 80, $"bent-only coverage had only {bentRim} adjacent rim pixels");
        Assert.Equal(0, restRim);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outline_only_submission_draws_the_bent_silhouette(bool gpuSkinning)
    {
        byte[] background = scene.CaptureBackground(gpuSkinning);
        byte[] rest = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.RestBody);
        byte[] bent = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.BentBody);
        byte[] outlined = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.BentOutlineOnly);
        int backgroundTargetPixels = 0;
        int bentRim = 0;
        int restRim = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                if (SkinnedTargetOutlineScene.IsBody(background, pixel)
                    || SkinnedTargetOutlineScene.IsRim(background, pixel)) backgroundTargetPixels++;
                if (!SkinnedTargetOutlineScene.IsRim(outlined, pixel)) continue;
                if (SkinnedTargetOutlineScene.HasExclusiveBodyNear(bent, rest, x, y, 4)
                    && !SkinnedTargetOutlineScene.HasBodyNear(rest, x, y, 4)) bentRim++;
                if (SkinnedTargetOutlineScene.HasExclusiveBodyNear(rest, bent, x, y, 4)
                    && !SkinnedTargetOutlineScene.HasBodyNear(bent, x, y, 4)) restRim++;
            }

        Assert.Equal(0, backgroundTargetPixels);
        Assert.True(bentRim >= 80, $"outline-only bent silhouette drew only {bentRim} bent rim pixels");
        Assert.Equal(0, restRim);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ordinary_rest_pose_and_bent_outline_use_only_their_own_poses(bool gpuSkinning)
    {
        byte[] rest = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.RestBody);
        byte[] bent = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.BentBody);
        byte[] mixed = scene.CapturePose(gpuSkinning, SkinnedTargetOutlineScene.PoseCase.RestBodyBentOutline);
        int restExclusiveControl = 0;
        int restExclusiveRetained = 0;
        int missingRestInterior = 0;
        int bentExclusiveBody = 0;
        int bentRim = 0;
        int restRim = 0;

        for (int y = 0; y < SkinnedTargetOutlineScene.H; y++)
            for (int x = 0; x < SkinnedTargetOutlineScene.W; x++)
            {
                int pixel = y * SkinnedTargetOutlineScene.W + x;
                bool restBody = SkinnedTargetOutlineScene.IsBody(rest, pixel);
                bool bentBody = SkinnedTargetOutlineScene.IsBody(bent, pixel);
                bool mixedBody = SkinnedTargetOutlineScene.IsBody(mixed, pixel);
                if (restBody && !bentBody)
                {
                    restExclusiveControl++;
                    if (mixedBody) restExclusiveRetained++;
                }
                if (bentBody && !SkinnedTargetOutlineScene.HasBodyNear(rest, x, y, 4) && mixedBody)
                    bentExclusiveBody++;
                if (SkinnedTargetOutlineScene.IsErodedBody(rest, x, y, 2)
                    && !mixedBody
                    && !SkinnedTargetOutlineScene.HasRimNear(mixed, x, y, 2)) missingRestInterior++;
                if (!SkinnedTargetOutlineScene.IsRim(mixed, pixel)) continue;
                if (SkinnedTargetOutlineScene.HasExclusiveBodyNear(bent, rest, x, y, 4)
                    && !SkinnedTargetOutlineScene.HasBodyNear(rest, x, y, 4)) bentRim++;
                if (SkinnedTargetOutlineScene.HasExclusiveBodyNear(rest, bent, x, y, 4)
                    && !SkinnedTargetOutlineScene.HasBodyNear(bent, x, y, 4)) restRim++;
            }

        Assert.True(restExclusiveControl >= 200,
            $"rest control had only {restExclusiveControl} exclusive body pixels");
        Assert.True(restExclusiveRetained >= restExclusiveControl * 9 / 10,
            $"ordinary rest pose retained {restExclusiveRetained} of {restExclusiveControl} exclusive body pixels");
        Assert.Equal(0, missingRestInterior);
        Assert.Equal(0, bentExclusiveBody);
        Assert.True(bentRim >= 80, $"mismatched pose drew only {bentRim} bent rim pixels");
        Assert.Equal(0, restRim);
    }

    [GpuFact]
    public void Cpu_and_gpu_bent_outline_frames_match()
    {
        const float parityTolerance = 0.08f;
        byte[] cpu = scene.CaptureMixed(gpuSkinning: false, outlined: true);
        byte[] gpu = scene.CaptureMixed(gpuSkinning: true, outlined: true);
        Assert.True(SkinnedTargetOutlineScene.CountRim(cpu) >= 80, "CPU frame had no substantial rim");
        Assert.True(SkinnedTargetOutlineScene.CountRim(gpu) >= 80, "GPU frame had no substantial rim");

        GoldenGridComparison comparison = GoldenGrid.Compare(
            GoldenGrid.Downsample(cpu, SkinnedTargetOutlineScene.W, SkinnedTargetOutlineScene.H),
            GoldenGrid.Downsample(gpu, SkinnedTargetOutlineScene.W, SkinnedTargetOutlineScene.H),
            parityTolerance);
        Assert.True(comparison.Passed,
            $"CPU and GPU skinned outlines diverged beyond {parityTolerance}, "
            + $"worst {comparison.WorstDiff:0.###}");
    }

    [GpuFact]
    public void Reused_pose_scene_matches_a_fresh_scene_after_pose_and_path_changes()
    {
        scene.CapturePose(gpuSkinning: false, SkinnedTargetOutlineScene.PoseCase.RestBody);
        scene.CapturePose(gpuSkinning: true, SkinnedTargetOutlineScene.PoseCase.BentOutlineOnly);
        scene.CapturePose(gpuSkinning: false, SkinnedTargetOutlineScene.PoseCase.BentBody);
        byte[] aged = scene.CapturePose(
            gpuSkinning: true, SkinnedTargetOutlineScene.PoseCase.RestBodyBentOutline);

        using var fresh = new SkinnedTargetOutlineScene();
        byte[] alone = fresh.CapturePose(
            gpuSkinning: true, SkinnedTargetOutlineScene.PoseCase.RestBodyBentOutline);
        Assert.Equal(alone, aged);
    }
}
