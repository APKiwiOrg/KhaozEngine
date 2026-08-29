using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class ContextMenuTests
    {
        // 10px/char, 20px line height (the fake-measurer idiom from TooltipTests).
        sealed class FixedFont : ITextMeasurer
        {
            public float LineHeight => 20f;
            public Vector2 Measure(string text) => new(text.Length * 10f, 20f);
        }

        static readonly FixedFont Font = new();
        static readonly Vector2 View = new(960, 540);
        static readonly ContextMenuMetrics M = ContextMenuMetrics.Default;

        // With the fixed font and the default metrics: the title band is LineHeight(20) + RowPadY*2(8) +
        // TitleGap(5) = 33, and each entry row is LineHeight(20) + RowPadY*2(8) = 28.
        const float TitleBand = 33f;
        const float RowH = 28f;

        // "Attack" is the widest LABEL (60), but the "Use" row carries a right detail, so its total is
        // 30 + DetailGap(16) + 40 = 86 and it is the row that drives the width.
        static ContextMenuEntry[] Two() => new[]
        {
            new ContextMenuEntry("Attack"),
            new ContextMenuEntry("Use", "Rope"),
        };

        [Fact]
        public void Bounds_size_to_widest_row_including_right_detail()
        {
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), new Vector2(300, 200), View, M);
            // PadX*2(20) + label "Use"(30) + DetailGap(16) + detail "Rope"(40) = 106, wider than the
            // detail-less "Attack" row at 20 + 60 = 80.
            Assert.Equal(106f, r.Width);
        }

        [Fact]
        public void Bounds_top_left_sits_at_the_point()
        {
            var point = new Vector2(300, 200);
            Rect r = ContextMenu.ComputeBounds(Font, "Options", Font, Two(), point, View, M);
            Assert.Equal(point.X, r.X);
            Assert.Equal(point.Y, r.Y);
            // Title band plus two rows. The title band is always present, even with an empty title.
            Assert.Equal(TitleBand + 2f * RowH, r.Height);
        }

        [Fact]
        public void Bounds_clamp_inside_the_right_edge()
        {
            // The menu is 106 wide, so opening at x=950 would run to 1056, well past the viewport.
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), new Vector2(950, 200), View, M);
            Assert.True(r.Right <= View.X - M.Margin);
            Assert.Equal(View.X - M.Margin, r.Right);
        }

        [Fact]
        public void Bounds_flip_above_when_the_bottom_would_overflow()
        {
            // Height is 89. Opening down from y=500 would reach 589, past the 536 bottom limit, so the menu
            // flips and sits with its bottom on the point instead.
            var point = new Vector2(300, 500);
            Rect r = ContextMenu.ComputeBounds(Font, "", Font, Two(), point, View, M);
            Assert.Equal(point.Y, r.Bottom);
            Assert.Equal(point.Y - (TitleBand + 2f * RowH), r.Y);
        }

        [Fact]
        public void Row_rects_stack_below_the_title_band_without_gaps()
        {
            var entries = new[]
            {
                new ContextMenuEntry("One"),
                new ContextMenuEntry("Two"),
                new ContextMenuEntry("Three"),
            };
            Rect b = ContextMenu.ComputeBounds(Font, "Options", Font, entries, new Vector2(100, 100), View, M);
            Rect r0 = ContextMenu.RowBounds(b, Font, Font, 0, M);
            Rect r1 = ContextMenu.RowBounds(b, Font, Font, 1, M);
            Rect r2 = ContextMenu.RowBounds(b, Font, Font, 2, M);

            Assert.Equal(b.Y + TitleBand, r0.Y);
            Assert.Equal(RowH, r0.Height);
            Assert.Equal(r0.Bottom, r1.Y);
            Assert.Equal(r1.Bottom, r2.Y);
            Assert.Equal(b.Bottom, r2.Bottom);   // the rows fill the bounds under the title band exactly
            Assert.Equal(b.X, r0.X);
            Assert.Equal(b.Width, r0.Width);
        }

        [Fact]
        public void Entry_Of_resolves_localized_text_and_a_default_detail_is_empty()
        {
            // default(LocalizedText).Resolve() is the empty string, so the optional detail needs no null guard.
            ContextMenuEntry bare = ContextMenuEntry.Of(LocalizedText.Raw("Attack"));
            Assert.Equal("Attack", bare.Label);
            Assert.Equal("", bare.RightDetail);
            Assert.True(bare.Enabled);
            Assert.Equal(0L, bare.Tag);
            Assert.Null(bare.LabelColor);
            Assert.Null(bare.DetailColor);

            ContextMenuEntry full = ContextMenuEntry.Of(LocalizedText.Raw("Use"), LocalizedText.Raw("Rope"),
                labelColor: Vector4.One, tag: 7, enabled: false);
            Assert.Equal("Use", full.Label);
            Assert.Equal("Rope", full.RightDetail);
            Assert.Equal(7L, full.Tag);
            Assert.False(full.Enabled);
            Assert.True(full.LabelColor.HasValue);
            Assert.Equal(Vector4.One, full.LabelColor.Value);
        }
    }
}
