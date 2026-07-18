using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class ProgressBarTests
    {
        static readonly Rect Bar = new(10, 10, 200, 12);

        [Fact]
        public void Fraction_clamps_to_the_unit_range_on_assignment()
        {
            var bar = new ProgressBar(Bar);
            bar.Fraction = 1.5f;
            Assert.Equal(1f, bar.Fraction, 3);
            bar.Fraction = -0.5f;
            Assert.Equal(0f, bar.Fraction, 3);
            bar.Fraction = 0.3f;
            Assert.Equal(0.3f, bar.Fraction, 3);
        }

        [Fact]
        public void Constructor_clamps_the_initial_fraction()
        {
            Assert.Equal(1f, new ProgressBar(Bar, 2f).Fraction, 3);
            Assert.Equal(0f, new ProgressBar(Bar, -1f).Fraction, 3);
            Assert.Equal(0.5f, new ProgressBar(Bar, 0.5f).Fraction, 3);
        }

        [Fact]
        public void FillRect_width_scales_with_fraction_inside_the_border()
        {
            var bar = new ProgressBar(Bar);
            float innerW = bar.InnerBounds.Width;   // Bounds width less the border on both sides
            bar.Fraction = 0.5f;
            Assert.Equal(innerW * 0.5f, bar.FillRect.Width, 3);
            bar.Fraction = 0.25f;
            Assert.Equal(innerW * 0.25f, bar.FillRect.Width, 3);
        }

        [Fact]
        public void FillRect_is_empty_at_zero_and_full_inner_width_at_one()
        {
            var bar = new ProgressBar(Bar);
            bar.Fraction = 0f;
            Assert.Equal(0f, bar.FillRect.Width, 3);
            bar.Fraction = 1f;
            Assert.Equal(bar.InnerBounds.Width, bar.FillRect.Width, 3);
        }

        [Fact]
        public void FillRect_sits_within_the_bar_frame()
        {
            var bar = new ProgressBar(Bar) { Fraction = 1f };
            Rect fill = bar.FillRect;
            Assert.True(fill.X >= Bar.X);
            Assert.True(fill.Y >= Bar.Y);
            Assert.True(fill.Right <= Bar.Right + 1e-3f);
            Assert.True(fill.Bottom <= Bar.Bottom + 1e-3f);
        }

        [Fact]
        public void InnerBounds_insets_by_the_style_border_thickness()
        {
            var bar = new ProgressBar(Bar);
            float bt = bar.Style.BorderThickness;
            Assert.Equal(Bar.X + bt, bar.InnerBounds.X, 3);
            Assert.Equal(Bar.Width - 2f * bt, bar.InnerBounds.Width, 3);
        }

        static GuiSkin SkinWithInset(float inset) => new()
        {
            SourcePixelWidth = 48f,
            SourcePixelHeight = 48f,
            InsetLeft = inset,
            InsetTop = inset,
            InsetRight = inset,
            InsetBottom = inset,
        };

        [Fact]
        public void Skinned_InnerBounds_insets_by_the_skin_frame_not_the_border_thickness()
        {
            // A 12px-inset skin on a 200x40 bar: the fill lives inside the painted frame, so it can never
            // overpaint the nine-slice edges (this was the release-blocking bug: it inset by BorderThickness only).
            var bar = new ProgressBar(new Rect(10, 10, 200, 40)) { Style = new GuiStyle { BorderThickness = 1f, Skin = SkinWithInset(12f) } };
            Assert.Equal(new Rect(22, 22, 176, 16), bar.InnerBounds);
            bar.Fraction = 1f;
            Assert.Equal(bar.InnerBounds, bar.FillRect);   // full fill stays inside the frame
        }

        [Fact]
        public void Skinned_InnerBounds_with_a_zero_inset_skin_is_the_whole_bounds()
        {
            // The skin owns the frame; zero insets mean no frame, so content gets everything (BorderThickness is
            // NOT applied on the skinned path).
            var bar = new ProgressBar(Bar) { Style = new GuiStyle { BorderThickness = 3f, Skin = SkinWithInset(0f) } };
            Assert.Equal(Bar, bar.InnerBounds);
        }

        [Fact]
        public void Skinned_InnerBounds_collapses_when_the_inset_exceeds_half_the_bar()
        {
            // An 18-tall bar under 12+12 vertical insets: the destination insets clamp to 9+9 (corners meet), so
            // the inner height is exactly zero, never negative, while the width keeps its full 12+12 inset.
            var bar = new ProgressBar(new Rect(0, 0, 200, 18)) { Style = new GuiStyle { Skin = SkinWithInset(12f) } };
            Assert.Equal(0f, bar.InnerBounds.Height, 3);
            Assert.Equal(200f - 24f, bar.InnerBounds.Width, 3);
            bar.Fraction = 0.5f;
            Assert.Equal(0f, bar.FillRect.Height, 3);      // nothing to fill, nothing overpainted
        }

        [Fact]
        public void Skinned_segments_partition_the_skin_content_rect()
        {
            var bar = new ProgressBar(new Rect(0, 0, 200, 40))
            { Style = new GuiStyle { Skin = SkinWithInset(12f) }, SegmentCount = 4, SegmentSpacing = 4f };
            Rect inner = bar.InnerBounds;                  // (12, 12, 176, 16)
            Rect[] segs = bar.SegmentRects();
            Assert.Equal(inner.X, segs[0].X, 3);
            Assert.Equal(inner.Right, segs[3].Right, 3);   // segments span the content rect, inside the frame
            Assert.Equal((inner.Width - 3 * 4f) / 4f, segs[0].Width, 3);
        }

        // ---- Item 1: FillDirection orientation -------------------------------------------------------------

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(1f)]
        public void LeftToRight_is_the_default_and_grows_from_the_left_edge(float f)
        {
            var bar = new ProgressBar(Bar) { Fraction = f };
            Assert.Equal(FillDirection.LeftToRight, bar.FillDirection);
            Rect inner = bar.InnerBounds, fill = bar.FillRect;
            Assert.Equal(inner.X, fill.X, 3);
            Assert.Equal(inner.Y, fill.Y, 3);
            Assert.Equal(inner.Width * f, fill.Width, 3);
            Assert.Equal(inner.Height, fill.Height, 3);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(1f)]
        public void RightToLeft_grows_from_the_right_edge(float f)
        {
            var bar = new ProgressBar(Bar) { FillDirection = FillDirection.RightToLeft, Fraction = f };
            Rect inner = bar.InnerBounds, fill = bar.FillRect;
            Assert.Equal(inner.Width * f, fill.Width, 3);
            Assert.Equal(inner.Height, fill.Height, 3);
            Assert.Equal(inner.Right, fill.Right, 3);   // pinned to the right edge
            Assert.Equal(inner.Y, fill.Y, 3);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(1f)]
        public void BottomToTop_grows_from_the_bottom_edge(float f)
        {
            var bar = new ProgressBar(new Rect(10, 10, 16, 200)) { FillDirection = FillDirection.BottomToTop, Fraction = f };
            Rect inner = bar.InnerBounds, fill = bar.FillRect;
            Assert.Equal(inner.Width, fill.Width, 3);
            Assert.Equal(inner.Height * f, fill.Height, 3);
            Assert.Equal(inner.Bottom, fill.Bottom, 3);   // pinned to the bottom edge
            Assert.Equal(inner.X, fill.X, 3);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(1f)]
        public void TopToBottom_grows_from_the_top_edge(float f)
        {
            var bar = new ProgressBar(new Rect(10, 10, 16, 200)) { FillDirection = FillDirection.TopToBottom, Fraction = f };
            Rect inner = bar.InnerBounds, fill = bar.FillRect;
            Assert.Equal(inner.Width, fill.Width, 3);
            Assert.Equal(inner.Height * f, fill.Height, 3);
            Assert.Equal(inner.Y, fill.Y, 3);   // pinned to the top edge
        }

        [Fact]
        public void Fraction_still_clamps_regardless_of_direction()
        {
            var bar = new ProgressBar(Bar) { FillDirection = FillDirection.BottomToTop };
            bar.Fraction = 2f;
            Assert.Equal(1f, bar.Fraction, 3);
            bar.Fraction = -1f;
            Assert.Equal(0f, bar.Fraction, 3);
        }

        // ---- Item 2: segmented fill ------------------------------------------------------------------------

        [Fact]
        public void SegmentRects_partitions_the_inner_track_by_count_and_spacing()
        {
            var bar = new ProgressBar(Bar) { SegmentCount = 4, SegmentSpacing = 6f };
            Rect inner = bar.InnerBounds;
            Rect[] segs = bar.SegmentRects();
            Assert.Equal(4, segs.Length);

            float expectedSegW = (inner.Width - 6f * 3) / 4f;
            foreach (Rect s in segs)
            {
                Assert.Equal(expectedSegW, s.Width, 3);
                Assert.Equal(inner.Height, s.Height, 3);   // full cross-axis extent
            }
            // Segments abut with exactly SegmentSpacing between them, spanning the inner track end to end.
            Assert.Equal(inner.X, segs[0].X, 3);
            Assert.Equal(inner.Right, segs[3].Right, 3);
            Assert.Equal(6f, segs[1].X - segs[0].Right, 3);
        }

        [Fact]
        public void SegmentCount_of_one_or_zero_is_a_single_continuous_segment()
        {
            var bar = new ProgressBar(Bar) { SegmentCount = 0 };
            Assert.Single(bar.SegmentRects());
            Assert.Equal(bar.InnerBounds, bar.SegmentRects()[0]);
            bar.SegmentCount = 1;
            Assert.Single(bar.SegmentRects());
        }

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(0.24f, 0)]
        [InlineData(0.25f, 1)]     // exactly the first-segment boundary lights it
        [InlineData(0.5f, 2)]
        [InlineData(0.74f, 2)]
        [InlineData(0.75f, 3)]
        [InlineData(1f, 4)]
        public void Discrete_lights_a_segment_only_when_fully_covered(float f, int expected)
        {
            var bar = new ProgressBar(Bar) { SegmentCount = 4, SegmentFillMode = SegmentFillMode.Discrete, Fraction = f };
            Assert.Equal(expected, bar.FilledSegmentCount);
        }

        [Fact]
        public void Discrete_fill_order_follows_the_direction_origin_edge()
        {
            // In every direction, SegmentRects index 0 is the segment at the fill origin, so the first-lit segment
            // hugs that edge. Verify by geometry: seg[0] touches the origin edge for each direction.
            Rect h = new(10, 10, 200, 12), v = new(10, 10, 12, 200);

            var ltr = new ProgressBar(h) { SegmentCount = 3, FillDirection = FillDirection.LeftToRight };
            Assert.Equal(ltr.InnerBounds.X, ltr.SegmentRects()[0].X, 3);

            var rtl = new ProgressBar(h) { SegmentCount = 3, FillDirection = FillDirection.RightToLeft };
            Assert.Equal(rtl.InnerBounds.Right, rtl.SegmentRects()[0].Right, 3);

            var btt = new ProgressBar(v) { SegmentCount = 3, FillDirection = FillDirection.BottomToTop };
            Assert.Equal(btt.InnerBounds.Bottom, btt.SegmentRects()[0].Bottom, 3);

            var ttb = new ProgressBar(v) { SegmentCount = 3, FillDirection = FillDirection.TopToBottom };
            Assert.Equal(ttb.InnerBounds.Y, ttb.SegmentRects()[0].Y, 3);
        }

        [Fact]
        public void Vertical_segments_partition_along_the_y_axis()
        {
            var bar = new ProgressBar(new Rect(10, 10, 16, 200)) { SegmentCount = 5, SegmentSpacing = 4f, FillDirection = FillDirection.BottomToTop };
            Rect inner = bar.InnerBounds;
            Rect[] segs = bar.SegmentRects();
            Assert.Equal(5, segs.Length);
            float expectedH = (inner.Height - 4f * 4) / 5f;
            foreach (Rect s in segs)
            {
                Assert.Equal(inner.Width, s.Width, 3);     // full cross-axis extent
                Assert.Equal(expectedH, s.Height, 3);
            }
            Assert.Equal(inner.Bottom, segs[0].Bottom, 3); // seg 0 at the bottom origin
            Assert.Equal(inner.Y, segs[4].Y, 3);           // last seg reaches the top
        }
    }
}
