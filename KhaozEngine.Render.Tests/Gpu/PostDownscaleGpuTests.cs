using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class PostDownscaleGpuTests(PostDownscaleScene scene) : IClassFixture<PostDownscaleScene>
{
    [GpuFact]
    public void Fxaa_with_mip_filtered_downscale_preserves_color_during_motion()
    {
        for (int frame = 0; frame < 32; frame++)
        {
            byte[] reference = scene.Capture(false, true, frame * .0075f);
            byte[] filtered = scene.Capture(true, true, frame * .0075f);
            AssertColor(reference, filtered, frame);
        }
    }

    [GpuFact]
    public void Reused_post_scene_matches_a_fresh_scene_after_filter_changes()
    {
        scene.Capture(true, false, .04f);
        scene.Capture(false, true, .02f);
        byte[] reused = scene.Capture(true, true, 0);
        using var fresh = new PostDownscaleScene();
        Assert.Equal(fresh.Capture(true, true, 0), reused);
    }

    static void AssertColor(byte[] reference, byte[] filtered, int frame)
    {
        double absoluteError = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            double before = 0, after = 0;
            for (int index = channel; index < reference.Length; index += 4)
            {
                before += reference[index];
                after += filtered[index];
                absoluteError += Math.Abs(reference[index] - filtered[index]);
            }
            double count = reference.Length / 4;
            Assert.True(before / count > 10, "The reference must contain visible colored geometry.");
            Assert.True(Math.Abs(after - before) / count < 3,
                $"Frame {frame}, channel {channel}: unfiltered mean {before / count:F3}, filtered mean {after / count:F3}.");
        }
        double meanError = absoluteError / (reference.Length / 4 * 3);
        Assert.True(meanError < 5, $"Frame {frame}: mean absolute RGB error {meanError:F3}.");
    }
}
