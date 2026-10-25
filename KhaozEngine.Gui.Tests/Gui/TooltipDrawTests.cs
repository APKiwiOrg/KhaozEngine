using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class TooltipDrawTests
{
    sealed class FixedFont(float width = 10f, float height = 20f) : ITextMeasurer
    {
        public float LineHeight => height;
        public Vector2 Measure(string text) => new(text.Length * width, height);
    }

    static readonly FixedFont Font = new();
    static readonly Vector2 Viewport = new(960, 540);
    static readonly Vector2 Anchor = new(300, 200);

    // Identity-only wrappers. No GPU resource is allocated or owned by these device-free fixtures.
    static Texture2D Texture() => Texture2D.Wrap(null!, 1, 1, ownsHandle: false);

    static Tooltip Shown(params TooltipLine[] lines)
    {
        var tooltip = new Tooltip(null!, null!) { Viewport = Viewport };
        tooltip.Show(LocalizedText.Raw("Hello"), lines, Anchor);
        return tooltip;
    }

    [Fact]
    public void Null_backdrop_preserves_default_draw_calls()
    {
        var tooltip = Shown(new TooltipLine("World", Vector4.One));
        var sink = new RecordingSink();

        tooltip.DrawTo(ref sink, Font, Font);

        Assert.Equal(new[] { "Fill", "Border", "Text", "Text" }, sink.Calls.Select(call => call.Kind));
        Assert.Equal(new Rect(265, 129, 70, 61), sink.Calls[0].Bounds);
        Assert.Equal(new Rect(265, 129, 70, 61), sink.Calls[1].Bounds);
        Assert.Equal(1f, sink.Calls[1].Thickness);
        Assert.True(sink.Calls[2].TitleFont);
        Assert.Equal("Hello", sink.Calls[2].Text);
        Assert.Equal(new Vector2(275, 137), sink.Calls[2].Position);
        Assert.False(sink.Calls[3].TitleFont);
        Assert.Equal("World", sink.Calls[3].Text);
        Assert.Equal(new Vector2(275, 162), sink.Calls[3].Position);
        Assert.Equal(new Vector4(0.055f, 0.055f, 0.094f, 0.94f), sink.Calls[0].Color);
        Assert.Equal(new Vector4(0.24f, 0.255f, 0.31f, 0.78f), sink.Calls[1].Color);
        Assert.Equal(1f, sink.Calls[2].Scale);
        Assert.Equal(1f, sink.Calls[3].Scale);
    }

    [Fact]
    public void Tiles_crop_edges_in_row_major_order()
    {
        using var texture = Texture();
        var backdrop = new TooltipBackdrop(texture, 16f, new(0.8f, 0.7f, 0.6f, 0.5f));
        var sink = new RecordingSink();

        TooltipBackdropPainter.Draw(ref sink, new Rect(10, 20, 35, 19), backdrop, 0.5f);

        Assert.Equal(6, sink.Calls.Count);
        Assert.Equal(new Rect(10, 20, 16, 16), sink.Calls[0].Bounds);
        Assert.Equal(new Rect(26, 20, 16, 16), sink.Calls[1].Bounds);
        Assert.Equal(new Rect(42, 20, 3, 16), sink.Calls[2].Bounds);
        Assert.Equal(new Rect(10, 36, 16, 3), sink.Calls[3].Bounds);
        Assert.Equal(new Rect(26, 36, 16, 3), sink.Calls[4].Bounds);
        Assert.Equal(new Rect(42, 36, 3, 3), sink.Calls[5].Bounds);
        Assert.Equal(new Vector4(0, 0, 1, 1), sink.Calls[0].SourceUV);
        Assert.Equal(new Vector4(0, 0, 3f / 16f, 1), sink.Calls[2].SourceUV);
        Assert.Equal(new Vector4(0, 0, 1, 3f / 16f), sink.Calls[3].SourceUV);
        Assert.Equal(new Vector4(0, 0, 3f / 16f, 3f / 16f), sink.Calls[5].SourceUV);
        Assert.All(sink.Calls, call =>
        {
            Assert.Equal("Sprite", call.Kind);
            Assert.Same(texture, call.Texture);
            Assert.Equal(new Vector4(0.8f, 0.7f, 0.6f, 0.25f), call.Color);
        });
    }

    [Fact]
    public void Fractional_edge_tiles_keep_design_geometry_and_proportional_uvs()
    {
        using var texture = Texture();
        var sink = new RecordingSink();

        TooltipBackdropPainter.Draw(ref sink, new Rect(10.5f, 20.25f, 18.5f, 17.25f),
            new TooltipBackdrop(texture, 16f, Vector4.One), 1f);

        Assert.Equal(4, sink.Calls.Count);
        Assert.Equal(new Rect(10.5f, 20.25f, 16f, 16f), sink.Calls[0].Bounds);
        Assert.Equal(new Rect(26.5f, 20.25f, 2.5f, 16f), sink.Calls[1].Bounds);
        Assert.Equal(new Rect(26.5f, 36.25f, 2.5f, 1.25f), sink.Calls[3].Bounds);
        Assert.Equal(new Vector4(0, 0, 2.5f / 16f, 1.25f / 16f), sink.Calls[3].SourceUV);
    }

    [Fact]
    public void Backdrop_precedes_border_title_icon_and_text()
    {
        using var texture = Texture();
        using var iconTexture = Texture();
        var icon = new TooltipIcon(iconTexture, new(0.25f, 0.5f, 0.75f, 1f), 16f);
        var line = new TooltipLine("World", Vector4.One) { Icon = icon };
        var tooltip = Shown(line);
        var plain = new RecordingSink();
        tooltip.DrawTo(ref plain, Font, Font);
        Rect baseline = Tooltip.ComputeBounds(Font, "Hello", Font, new[] { line },
            Anchor, Viewport, TooltipMetrics.Default);
        tooltip.Backdrop = new TooltipBackdrop(texture, 16f, Vector4.One);
        var sink = new RecordingSink();

        tooltip.DrawTo(ref sink, Font, Font);

        int borderIndex = sink.Calls.FindIndex(call => call.Kind == "Border");
        Assert.True(borderIndex > 0);
        Assert.Single(sink.Calls, call => call.Kind == "Border");
        Assert.DoesNotContain(sink.Calls, call => call.Kind == "Fill");
        Assert.Equal(new[] { "Border", "Text", "Sprite", "Text" },
            sink.Calls.Skip(borderIndex).Select(call => call.Kind));
        Assert.Equal(new Rect(253, 129, 94, 61), baseline);
        Assert.Equal(baseline, plain.Calls[1].Bounds);
        Assert.Equal(baseline, sink.Calls[borderIndex].Bounds);
        var tiles = sink.Calls.Take(borderIndex).ToArray();
        Assert.All(tiles, call =>
        {
            Assert.Equal("Sprite", call.Kind);
            Assert.Same(texture, call.Texture);
            Assert.True(call.Bounds.X >= 254 && call.Bounds.Y >= 130);
            Assert.True(call.Bounds.Right <= 346 && call.Bounds.Bottom <= 189);
        });
        Assert.Equal(new Rect(254, 130, 92, 59), new Rect(
            tiles.Min(call => call.Bounds.X), tiles.Min(call => call.Bounds.Y),
            tiles.Max(call => call.Bounds.Right) - tiles.Min(call => call.Bounds.X),
            tiles.Max(call => call.Bounds.Bottom) - tiles.Min(call => call.Bounds.Y)));
        Assert.Equal(92f * 59f, tiles.Sum(call => call.Bounds.Width * call.Bounds.Height));
        Assert.True(sink.Calls[borderIndex + 1].TitleFont);
        Assert.Same(iconTexture, sink.Calls[borderIndex + 2].Texture);
        Assert.Equal(icon.SourceUV, sink.Calls[borderIndex + 2].SourceUV);
        Assert.Equal(new Rect(263, 164, 16, 16), sink.Calls[borderIndex + 2].Bounds);
        Assert.False(sink.Calls[borderIndex + 3].TitleFont);
        Assert.Equal(new Vector2(287, 162), sink.Calls[borderIndex + 3].Position);
    }

    [Fact]
    public void Title_face_runs_follow_separator_and_icon_at_shared_layout_positions()
    {
        using var texture = Texture();
        using var iconTexture = Texture();
        var runs = new[] { new ColoredTextRun("Wo", new(1, 0, 0, 0.8f)), new ColoredTextRun("rld", Vector4.One) };
        var line = new TooltipLine("World", Vector4.One, 0.5f)
        {
            Icon = new(iconTexture, new(0, 0, 1, 1), 16f),
            TitleFont = true,
            GapBefore = 7f,
            Runs = runs,
        };
        var tooltip = Shown(line);
        tooltip.Backdrop = new TooltipBackdrop(texture, 16f, Vector4.One);
        tooltip.ShowTitleSeparator = true;
        tooltip.TitleScale = 1.5f;
        var sink = new RecordingSink();

        tooltip.DrawTo(ref sink, new FixedFont(12f, 30f), Font);

        int borderIndex = sink.Calls.FindIndex(call => call.Kind == "Border");
        Assert.All(sink.Calls.Take(borderIndex), call => Assert.Same(texture, call.Texture));
        Assert.Equal(new[] { "Border", "Text", "Fill", "Sprite", "TextRuns" },
            sink.Calls.Skip(borderIndex).Select(call => call.Kind));
        Assert.Equal(new Rect(245, 101, 110, 89), sink.Calls[borderIndex].Bounds);
        Assert.Equal(new Vector2(255, 109), sink.Calls[borderIndex + 1].Position);
        Assert.Equal(1.5f, sink.Calls[borderIndex + 1].Scale);
        Assert.Equal(new Rect(255, 156, 90, 1), sink.Calls[borderIndex + 2].Bounds);
        Assert.Equal(new Rect(255, 166, 16, 16), sink.Calls[borderIndex + 3].Bounds);
        Drawn body = sink.Calls[borderIndex + 4];
        Assert.True(body.TitleFont);
        Assert.Equal(new Vector2(279, 166), body.Position);
        Assert.Equal(0.5f, body.Scale);
        Assert.Equal(runs, body.Runs);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0f)]
    public void New_visuals_receive_uniform_opacity(float opacity)
    {
        using var texture = Texture();
        using var iconTexture = Texture();
        var runs = new[] { new ColoredTextRun("Rich", new(0.2f, 0.4f, 0.6f, 0.8f)) };
        var lines = new[]
        {
            new TooltipLine("World", new(0.3f, 0.4f, 0.5f, 0.6f))
            { Icon = new(iconTexture, new(0, 0, 1, 1), 16f) },
            new TooltipLine("Rich", Vector4.One) { Runs = runs, TitleFont = true },
        };
        var tooltip = Shown(lines);
        tooltip.Show(LocalizedText.Raw("Hello"), LocalizedText.Raw("99"), lines, Anchor);
        tooltip.Backdrop = new TooltipBackdrop(texture, 16f, new(0.8f, 0.7f, 0.6f, 0.5f));
        tooltip.ShowTitleSeparator = true;
        tooltip.Opacity = opacity;
        var sink = new RecordingSink();

        tooltip.DrawTo(ref sink, Font, Font);

        Assert.All(sink.Calls.Where(call => ReferenceEquals(call.Texture, texture)), call =>
            Assert.Equal(new Vector4(0.8f, 0.7f, 0.6f, 0.5f * opacity), call.Color));
        Assert.Equal(new Vector4(1, 1, 1, opacity),
            Assert.Single(sink.Calls, call => ReferenceEquals(call.Texture, iconTexture)).Color);
        Assert.Equal(new Vector4(0.24f, 0.255f, 0.31f, 0.78f * opacity),
            Assert.Single(sink.Calls, call => call.Kind == "Border").Color);
        Assert.Equal(new Vector4(0.86f, 0.88f, 0.94f, opacity),
            Assert.Single(sink.Calls, call => call.Text == "Hello").Color);
        Drawn right = Assert.Single(sink.Calls, call => call.Text == "99");
        Assert.False(right.TitleFont);
        Assert.Equal(new Vector4(0.71f, 0.71f, 0.75f, opacity), right.Color);
        Assert.Equal(new Vector4(0.3f, 0.4f, 0.5f, 0.6f * opacity),
            Assert.Single(sink.Calls, call => call.Text == "World").Color);
        Assert.Equal(new Vector4(0.20f, 0.22f, 0.27f, 0.63f * opacity),
            Assert.Single(sink.Calls, call => call.Kind == "Fill").Color);
        Drawn rich = Assert.Single(sink.Calls, call => call.Kind == "TextRuns");
        Assert.Equal(opacity, rich.Opacity);
        Assert.Equal(new[] { new ColoredTextRun("Rich", new(0.2f, 0.4f, 0.6f, 0.8f)) }, rich.Runs);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.8f), runs[0].Color);
        if (opacity == 0f)
            Assert.All(sink.Calls, call => Assert.Equal(0f, call.Kind == "TextRuns" ? call.Opacity : call.Color.W));
    }

    [Fact]
    public void Wrapped_body_draws_first_row_icon_and_gap_with_hanging_continuations()
    {
        using var texture = Texture();
        var tooltip = Shown(new TooltipLine("one two", Vector4.One)
        { Icon = new(texture, new(0, 0, 1, 1), 16f), GapBefore = 7f });
        tooltip.MaxWidth = 100f;
        var sink = new RecordingSink();

        tooltip.DrawTo(ref sink, Font, Font);

        Assert.Equal(new[] { "Fill", "Border", "Text", "Sprite", "Text", "Text" },
            sink.Calls.Select(call => call.Kind));
        Assert.Equal(new Rect(263, 99, 74, 91), sink.Calls[0].Bounds);
        Assert.Equal(new Rect(273, 141, 16, 16), sink.Calls[3].Bounds);
        Assert.Equal("one", sink.Calls[4].Text);
        Assert.Equal(new Vector2(297, 139), sink.Calls[4].Position);
        Assert.Equal("two", sink.Calls[5].Text);
        Assert.Equal(new Vector2(297, 162), sink.Calls[5].Position);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Invalid_tile_size_fails_before_emission(float tileSize)
    {
        using var texture = Texture();
        var backdrop = new TooltipBackdrop(texture, tileSize, Vector4.One);
        var sink = new RecordingSink();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBackdropPainter.Draw(ref sink, new Rect(10, 20, 35, 19), backdrop, 1f));
        Assert.Empty(sink.Calls);

        var tooltip = Shown(new TooltipLine("World", Vector4.One));
        tooltip.Backdrop = backdrop;
        Assert.Throws<ArgumentOutOfRangeException>(() => tooltip.DrawTo(ref sink, Font, Font));
        Assert.Empty(sink.Calls);
    }

    [Theory]
    [InlineData(35f, 19f, 1e-6f)]
    [InlineData(35f, 1e-6f, 1e-6f)]
    [InlineData(1e-6f, 35f, 1e-6f)]
    [InlineData(65f, 65f, 1f)]
    [InlineData(float.MaxValue, 1f, float.Epsilon)]
    [InlineData(1f, float.MaxValue, float.Epsilon)]
    public void Excessive_tile_counts_fail_before_emission(float width, float height, float tileSize)
    {
        using var texture = Texture();
        var sink = new RecordingSink { RejectSprites = true };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBackdropPainter.Draw(ref sink, new Rect(10, 20, width, height),
                new TooltipBackdrop(texture, tileSize, Vector4.One), 1f));
        Assert.Empty(sink.Calls);
    }

    [Theory]
    [InlineData(float.NaN, 19f)]
    [InlineData(35f, float.NaN)]
    [InlineData(float.PositiveInfinity, 19f)]
    [InlineData(35f, float.PositiveInfinity)]
    public void Nonfinite_interior_dimensions_fail_before_emission(float width, float height)
    {
        using var texture = Texture();
        var sink = new RecordingSink { RejectSprites = true };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TooltipBackdropPainter.Draw(ref sink, new Rect(10, 20, width, height),
                new TooltipBackdrop(texture, 16f, Vector4.One), 1f));
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Bounded_tile_grid_covers_the_entire_interior()
    {
        using var texture = Texture();
        var sink = new RecordingSink();

        TooltipBackdropPainter.Draw(ref sink, new Rect(10, 20, 64, 64),
            new TooltipBackdrop(texture, 1f, Vector4.One), 1f);

        Assert.Equal(4096, sink.Calls.Count);
        Assert.Equal(new Rect(10, 20, 1, 1), sink.Calls[0].Bounds);
        Assert.Equal(new Rect(73, 20, 1, 1), sink.Calls[63].Bounds);
        Assert.Equal(new Rect(10, 21, 1, 1), sink.Calls[64].Bounds);
        Assert.Equal(new Rect(73, 83, 1, 1), sink.Calls[^1].Bounds);
    }

    [Theory]
    [InlineData(0f, 19f)]
    [InlineData(35f, 0f)]
    [InlineData(0f, 0f)]
    public void Zero_area_interior_emits_no_tiles(float width, float height)
    {
        using var texture = Texture();
        var sink = new RecordingSink();

        TooltipBackdropPainter.Draw(ref sink, new Rect(10, 20, width, height),
            new TooltipBackdrop(texture, 16f, Vector4.One), 1f);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Hidden_and_empty_tooltips_emit_nothing_even_without_a_viewport()
    {
        var tooltip = new Tooltip(null!, null!);
        var sink = new RecordingSink();

        tooltip.DrawTo(ref sink, Font, Font);
        tooltip.Draw(null!, null!);
        tooltip.Show(LocalizedText.Raw(""), Array.Empty<TooltipLine>(), Anchor);
        tooltip.DrawTo(ref sink, Font, Font);
        tooltip.Draw(null!, null!);

        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Visible_content_requires_a_viewport_before_emission()
    {
        var tooltip = Shown(new TooltipLine("World", Vector4.One));
        tooltip.Viewport = Vector2.Zero;
        var sink = new RecordingSink();

        Assert.Throws<InvalidOperationException>(() => tooltip.DrawTo(ref sink, Font, Font));
        Assert.Throws<InvalidOperationException>(() => tooltip.Draw(null!, null!));
        Assert.Empty(sink.Calls);
    }

    sealed record Drawn(string Kind)
    {
        public Texture2D? Texture { get; init; }
        public Rect Bounds { get; init; }
        public float Thickness { get; init; }
        public Vector4 SourceUV { get; init; }
        public bool TitleFont { get; init; }
        public string? Text { get; init; }
        public Vector2 Position { get; init; }
        public Vector4 Color { get; init; }
        public float Scale { get; init; }
        public ColoredTextRun[] Runs { get; init; } = Array.Empty<ColoredTextRun>();
        public float Opacity { get; init; }
    }

    sealed class RecordingSink : ITooltipSink
    {
        public List<Drawn> Calls { get; } = new();
        public bool RejectSprites { get; init; }

        public void Fill(in Rect bounds, Vector4 color) =>
            Calls.Add(new("Fill") { Bounds = bounds, Color = color });

        public void Border(in Rect bounds, float thickness, Vector4 color) =>
            Calls.Add(new("Border") { Bounds = bounds, Thickness = thickness, Color = color });

        public void Sprite(Texture2D texture, in Rect destination, Vector4 sourceUV, Vector4 tint)
        {
            if (RejectSprites)
                throw new InvalidOperationException("Invalid tile geometry must fail before sprite emission.");
            Calls.Add(new("Sprite") { Texture = texture, Bounds = destination, SourceUV = sourceUV, Color = tint });
        }

        public void Text(bool titleFont, string text, Vector2 position, Vector4 color, float scale) =>
            Calls.Add(new("Text") { TitleFont = titleFont, Text = text, Position = position, Color = color, Scale = scale });

        public void TextRuns(bool titleFont, ReadOnlySpan<ColoredTextRun> runs, Vector2 position, float scale, float opacity) =>
            Calls.Add(new("TextRuns") { TitleFont = titleFont, Runs = runs.ToArray(), Position = position, Scale = scale, Opacity = opacity });
    }
}
