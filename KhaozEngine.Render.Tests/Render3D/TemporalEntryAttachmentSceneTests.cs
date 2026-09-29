using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The split's first pass writes <see cref="TemporalSplitFormats.FirstPassAttachments"/> colour attachments, and a
/// device that allows fewer (<see cref="GpuCapabilities.MaxColorAttachments"/>, Vulkan guarantees 4) records the fused
/// entry point instead, even when the split is forced. Headless on <see cref="HeadlessSceneRig"/>.
/// </summary>
public sealed class TemporalEntryAttachmentSceneTests
{
    const int W = 96, H = 64;

    [Fact]
    public void A_device_with_fewer_colour_attachments_than_the_first_pass_writes_records_the_fused_entry()
    {
        using var rig = new HeadlessSceneRig(GpuBackendKind.VulkanNative, maxColorAttachments: 4);
        Scene3D scene = rig.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
        scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        rig.Frame(W, H);

        Assert.True(scene.ResolvedLastRenderForTests);
        Assert.Equal(TemporalResolveEntry.Fused, scene.TemporalResolveRendererForTests?.LastEntry);
        Assert.False(scene.TemporalResolveRendererForTests!.SplitTargetsAllocated);
    }
}
