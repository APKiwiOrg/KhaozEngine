using System.Numerics;
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

    [Fact]
    public void Default_theme_reproduces_the_legacy_grey_palette()
    {
        // Guards the ship-with look so existing callers (e.g. the showcase legend) stay byte-identical.
        var t = OverlayLegendTheme.Default;
        Assert.Equal(new Vector4(0.05f, 0.06f, 0.09f, 0.75f), t.PanelFill);
        Assert.Equal(new Vector4(0.25f, 0.28f, 0.34f, 0.9f), t.BorderColor);
        Assert.Equal(new Vector4(0.92f, 0.94f, 0.97f, 1f), t.LabelText);
        Assert.Equal(OverlayCorner.TopLeft, t.Corner);
        Assert.Equal(12f, t.Margin);
        Assert.Equal(8f, t.Padding);
        Assert.Equal(14f, t.SwatchSize);
        Assert.Equal(8f, t.SwatchGap);
        Assert.Equal(4f, t.RowSpacing);
        Assert.Equal(1f, t.TextScale);
    }

    [Fact]
    public void FromDiagnostics_copies_the_palette_margin_padding_and_text_scale()
    {
        var diag = new DiagnosticsOverlayTheme
        {
            Corner = OverlayCorner.TopRight,
            Margin = 20f,
            PanelFill = new Vector4(0.1f, 0.2f, 0.3f, 0.9f),
            BorderColor = new Vector4(0.4f, 0.5f, 0.6f, 0.8f),
            ValueText = new Vector4(0.9f, 0.9f, 0.9f, 1f),
            LabelText = new Vector4(0.5f, 0.5f, 0.5f, 1f), // must NOT be picked up
            BorderThickness = 2f,
            Padding = 10f,
            Scale = 0.5f,
        };

        var t = OverlayLegendTheme.FromDiagnostics(diag);

        Assert.Equal(OverlayCorner.TopRight, t.Corner);
        Assert.Equal(20f, t.Margin);
        Assert.Equal(diag.PanelFill, t.PanelFill);
        Assert.Equal(diag.BorderColor, t.BorderColor);
        Assert.Equal(diag.ValueText, t.LabelText); // near-white row text, not the dimmer label text
        Assert.Equal(2f, t.BorderThickness);
        Assert.Equal(10f, t.Padding);
        Assert.Equal(0.5f, t.TextScale);
    }

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 12f, 12f)]
    [InlineData(OverlayCorner.TopRight, 158f, 12f)]   // 200 - 12 - 30
    [InlineData(OverlayCorner.BottomLeft, 12f, 158f)] // 200 - 12 - 30
    [InlineData(OverlayCorner.BottomRight, 158f, 158f)]
    public void Anchor_places_the_panel_in_the_themed_corner(OverlayCorner corner, float expectX, float expectY)
    {
        var t = new OverlayLegendTheme { Corner = corner, Margin = 12f };
        var vp = new Rect(0, 0, 200, 200);

        Vector2 p = t.Anchor(vp, panelW: 30f, panelH: 30f);

        Assert.Equal(expectX, p.X, 3);
        Assert.Equal(expectY, p.Y, 3);
    }

    [Fact]
    public void Anchor_offsets_by_the_viewport_origin()
    {
        // A non-zero viewport origin (e.g. a windowed design-space top-left) shifts the top-left anchor.
        var t = new OverlayLegendTheme { Corner = OverlayCorner.TopLeft, Margin = 12f };
        Vector2 p = t.Anchor(new Rect(100, 50, 200, 200), 30f, 30f);
        Assert.Equal(112f, p.X, 3);
        Assert.Equal(62f, p.Y, 3);
    }
}
