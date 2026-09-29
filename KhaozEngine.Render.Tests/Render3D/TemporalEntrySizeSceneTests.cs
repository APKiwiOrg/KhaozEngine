using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The scene chooses the resolve's entry point on the internal size it renders at against the display, not on the
/// requested preset (<see cref="TemporalResolvePolicy.Upscales"/>): an explicit upscale ratio overrides the preset, and
/// the render cap shrinks the internal targets under any preset. Headless on <see cref="HeadlessSceneRig"/>, reading
/// the entry point the resolve recorded. A process that forces an entry point records that one.
/// </summary>
public sealed class TemporalEntrySizeSceneTests
{
    const int W = 96, H = 64, NoCap = 3840;

    [Theory]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, 0f, NoCap, "Fused")]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Quality, 0f, NoCap, "Split")]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, 0.5f, NoCap, "Split")]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Quality, 1f, NoCap, "Fused")]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.Native, 0f, W / 2, "Split")]
    [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.Performance, 1f, NoCap, "Split")]
    [InlineData(GpuBackendKind.Direct3D11Native, TemporalUpscale.UltraPerformance, 1f, NoCap, "Fused")]
    [InlineData(GpuBackendKind.MetalNative, TemporalUpscale.Native, 0.67f, NoCap, "Split")]
    [InlineData(GpuBackendKind.VulkanNative, TemporalUpscale.Quality, 1f, NoCap, "Split")]
    public void The_scene_chooses_on_the_internal_size_against_the_display(GpuBackendKind backend,
        TemporalUpscale preset, float ratio, int maxRenderWidth, string expected)
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
        Assert.Equal(TemporalResolvePolicy.Forced?.ToString() ?? expected, recorded?.ToString());
    }
}
