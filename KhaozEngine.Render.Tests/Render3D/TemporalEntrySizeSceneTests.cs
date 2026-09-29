using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The scene records the split on every backend at every internal size (<see cref="TemporalResolvePolicy"/>), whether
/// the internal targets are below the display or at its size: under each preset, an explicit upscale ratio, and the
/// render cap. Headless on <see cref="HeadlessSceneRig"/>, reading the entry point the resolve recorded. A process that
/// forces an entry point records that one.
/// </summary>
public sealed class TemporalEntrySizeSceneTests
{
    const int W = 96, H = 64, NoCap = 3840;

    [Theory]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, 0f, NoCap)]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Quality, 0f, NoCap)]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, 0.5f, NoCap)]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Quality, 1f, NoCap)]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, 0f, W / 2)]
    [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.Performance, 1f, NoCap)]
    [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.Native, 0.67f, NoCap)]
    [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Native, 0f, NoCap)]
    [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Quality, 1f, NoCap)]
    public void The_scene_records_the_split_at_every_size(GpuBackendKind backend, TemporalUpscale preset, float ratio,
        int maxRenderWidth)
    {
        using var rig = new HeadlessSceneRig(backend);
        Scene3D scene = rig.Scene;
        scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        scene.Post.Temporal.Upscale = preset;
        scene.Post.Temporal.UpscaleRatio = ratio > 0f ? ratio : null;
        scene.Post.MaxRenderWidth = maxRenderWidth;
        rig.Frame(W, H);

        Assert.True(scene.ResolvedLastRenderForTests);
        TemporalResolveEntry? recorded = scene.TemporalResolveRendererForTests?.LastEntry;
        Assert.Equal(TemporalResolvePolicy.Forced?.ToString() ?? "Split", recorded?.ToString());
    }
}
