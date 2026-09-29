using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The split's targets released with no retire queue drain the device and free them at once, which is right only
/// where no open recording references them. While the device is recording that path refuses and leaves the targets
/// as they were, as the history's own release does (<see cref="TemporalHistoryReleaseTests"/>). Headless on
/// <see cref="HeadlessSceneRig"/>.
/// </summary>
public sealed class TemporalSplitReleaseTests
{
    [Fact]
    public void Releasing_the_split_targets_with_no_queue_refuses_while_the_device_records()
    {
        using var rig = new HeadlessSceneRig();
        Scene3D scene = rig.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = TemporalUpscale.Quality;
        scene.TemporalResolveEntryForTests = TemporalResolveEntry.Split;
        rig.Frame(96, 64);
        TemporalSplitResolve split = scene.TemporalResolveRendererForTests?.SplitResolveForTests
            ?? throw new InvalidOperationException("the split entry point was never built");
        Assert.True(split.TargetsAllocated);

        using (GpuRecording.Open(rig.Device, rig.Device.Factory.CreateCommandList(), "the window's frame list"))
        {
            var refused = Assert.Throws<InvalidOperationException>(() => split.ReleaseTargets(null));
            Assert.Contains("the window's frame list", refused.Message);
            Assert.True(split.TargetsAllocated);
        }

        split.ReleaseTargets(null);
        Assert.False(split.TargetsAllocated);
    }
}
