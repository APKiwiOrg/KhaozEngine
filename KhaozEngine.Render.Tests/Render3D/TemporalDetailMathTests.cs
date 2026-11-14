using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class TemporalDetailMathTests
{
    [Theory]
    [InlineData(128u, true, TemporalHistoryDetail.Fine)]
    [InlineData(127u, true, TemporalHistoryDetail.Display)]
    [InlineData(0u, true, TemporalHistoryDetail.Display)]
    [InlineData(128u, false, TemporalHistoryDetail.Display)]
    public void The_scene_reports_fallback_before_creating_any_fine_target(uint dimensionLimit, bool resolves,
        TemporalHistoryDetail expected)
    {
        using var rig = new HeadlessSceneRig(maxRenderTexture2DDimension: dimensionLimit);
        rig.Scene.Post.Quality.AntiAliasing = resolves ? AntiAliasing.Temporal : AntiAliasing.Off;
        rig.Scene.ForceTemporalForTests = true;
        rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        rig.Frame();
        Assert.Equal(expected, rig.Scene.TemporalHistoryDetailInEffect);
        Assert.Equal(expected == TemporalHistoryDetail.Fine, rig.Scene.TemporalHistory.DetailStore is not null);
    }

    [Fact]
    public void Fine_extents_are_exactly_twice_the_display_and_never_exceed_the_device()
    {
        foreach (int width in new[] { 1, 2, 17, 2560, 8192, 8193, int.MaxValue })
            foreach (int height in new[] { 1, 3, 1440, 8192, 8193, int.MaxValue })
            {
                var extent = TemporalDetailMath.Extent(width, height, 16384);
                bool fits = 2L * width <= 16384 && 2L * height <= 16384;
                Assert.Equal(fits, extent.HasValue);
                if (extent is not { } size) continue;
                Assert.Equal((2L * width, 2L * height), ((long)size.Width, (long)size.Height));
                Assert.InRange(size.Width, 1, 16384);
                Assert.InRange(size.Height, 1, 16384);
            }
        Assert.Null(TemporalDetailMath.Extent(0, 1, 16384));
        Assert.Null(TemporalDetailMath.Extent(1, -1, 16384));
        Assert.Null(TemporalDetailMath.Extent(1, 1, 0));
    }

    [Theory]
    [InlineData(true, 64, 48, 128, 3072, TemporalHistoryDetail.Fine)]
    [InlineData(true, 64, 48, 128, 3071, TemporalHistoryDetail.Display)]
    [InlineData(true, 64, 48, 127, 3072, TemporalHistoryDetail.Display)]
    [InlineData(true, 48, 64, 127, 3072, TemporalHistoryDetail.Display)]
    [InlineData(false, 64, 48, 128, 3072, TemporalHistoryDetail.Display)]
    [InlineData(true, 64, 48, 128, 0, TemporalHistoryDetail.Display)]
    [InlineData(true, 64, 48, 128, -1, TemporalHistoryDetail.Display)]
    public void Fine_requires_the_resolve_and_both_inclusive_limits(bool resolves, int width, int height,
        uint deviceLimit, long pixelLimit, TemporalHistoryDetail expected)
    {
        Assert.Equal(expected, TemporalDetailMath.Mode(TemporalHistoryDetail.Fine, resolves,
            width, height, deviceLimit, pixelLimit));
        Assert.Equal(TemporalHistoryDetail.Display, TemporalDetailMath.Mode(TemporalHistoryDetail.Display,
            resolves, width, height, deviceLimit, pixelLimit));
    }

    [Fact]
    public void The_default_budget_admits_1440p_and_refuses_2160p_and_unknown_modes_are_display()
    {
        var settings = new PixelPostProcessSettings().Temporal;
        Assert.Equal(TemporalHistoryDetail.Display, settings.HistoryDetail);
        Assert.Equal(TemporalHistoryDetail.Fine, TemporalDetailMath.Mode(TemporalHistoryDetail.Fine,
            true, 2560, 1440, 16384, settings.HistoryDetailPixelLimit));
        Assert.Equal(TemporalHistoryDetail.Display, TemporalDetailMath.Mode(TemporalHistoryDetail.Fine,
            true, 3840, 2160, 16384, settings.HistoryDetailPixelLimit));
        Assert.Equal(TemporalHistoryDetail.Display, TemporalDetailMath.Mode((TemporalHistoryDetail)99,
            true, 1, 1, 16384, long.MaxValue));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 3)]
    [InlineData(2559, 1439)]
    public void Each_display_pixel_owns_its_four_fine_texels(int x, int y)
    {
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
                Assert.Equal((x, y), TemporalDetailMath.DisplayPixel(2 * x + dx, 2 * y + dy));
    }

    [Fact]
    public void A_live_mode_change_resets_once_and_later_renders_keep_the_latched_mode()
    {
        using var rig = new HeadlessSceneRig();
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Frame();
        rig.Frame();
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        rig.Frame();
        Assert.Equal(TemporalHistoryDetail.Fine, rig.Scene.TemporalHistoryDetailInEffect);
        Assert.False(rig.Scene.TemporalHistory.IsValid);
        Assert.Equal(TemporalResetReason.HistoryDetail, rig.Scene.TemporalHistory.LastReset);
        int generation = rig.Scene.TemporalHistory.TargetGeneration;
        var detail = rig.Scene.TemporalHistory.DetailStore;
        Assert.NotNull(detail);
        rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Display;
        rig.Render(32, 24);
        Assert.Equal(TemporalHistoryDetail.Fine, rig.Scene.TemporalHistoryDetailInEffect);
        Assert.Same(detail, rig.Scene.TemporalHistory.DetailStore);
        Assert.Equal(generation, rig.Scene.TemporalHistory.TargetGeneration);
        rig.Scene.Post.Temporal.HistoryDetail = TemporalHistoryDetail.Fine;
        rig.Frame();
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        rig.Scene.Post.Temporal.HistoryDetailPixelLimit++;
        rig.Frame();
        Assert.True(rig.Scene.TemporalHistory.IsValid);
        Assert.Equal(generation, rig.Scene.TemporalHistory.TargetGeneration);
        rig.Scene.Post.Temporal.HistoryDetailPixelLimit = 0;
        rig.Frame();
        Assert.Equal(TemporalHistoryDetail.Display, rig.Scene.TemporalHistoryDetailInEffect);
        Assert.Equal(TemporalResetReason.HistoryDetail, rig.Scene.TemporalHistory.LastReset);
        Assert.False(rig.Scene.TemporalHistory.IsValid);
        Assert.Null(rig.Scene.TemporalHistory.DetailStore);
        rig.Frame();
        Assert.True(rig.Scene.TemporalHistory.IsValid);
    }
}
