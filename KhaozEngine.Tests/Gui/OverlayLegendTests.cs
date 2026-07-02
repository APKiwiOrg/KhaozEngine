using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui;

/// <summary>
/// Headless coverage for <see cref="OverlayLegend"/>. A real <see cref="KhaozEngine.Render2D.SpriteFont"/> needs
/// an <c>IGpuDevice</c> to bake its atlas (<c>SpriteFont.Build</c>), so it cannot be constructed in a default
/// headless test run. The font-dependent layout math (row/column growth) is covered by the GPU golden instead.
/// This file sticks to the font-free surface: entry storage and the empty-legend early return in
/// <see cref="OverlayLegend.Measure"/>, which returns before touching the font.
/// </summary>
public class OverlayLegendTests
{
    [Fact]
    public void SetEntries_updates_count()
    {
        var legend = new OverlayLegend();
        legend.SetEntries(new[]
        {
            new LegendEntry(new Color(1, 0, 0, 0.4f), "Box"),
            new LegendEntry(new Color(0, 0, 1, 0.4f), "Sphere"),
        });
        Assert.Equal(2, legend.EntryCount);
    }

    [Fact]
    public void Empty_legend_measures_zero_without_a_font()
    {
        var legend = new OverlayLegend();
        Rect r = legend.Measure(null!);
        Assert.Equal(0f, r.Width);
        Assert.Equal(0f, r.Height);
    }

    [Fact]
    public void SetEntries_with_null_resets_to_empty()
    {
        var legend = new OverlayLegend();
        legend.SetEntries(new[] { new LegendEntry(Color.White, "A") });
        Assert.Equal(1, legend.EntryCount);

        legend.SetEntries(null!);
        Assert.Equal(0, legend.EntryCount);
    }
}
