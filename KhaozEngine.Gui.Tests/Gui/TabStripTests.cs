using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The instance-free half of the tab strip: <see cref="TabStrip"/>'s rects, its inverse, and the wrapped
    /// block's alignment. A consumer that hit-tests a strip without retaining a widget calls exactly these, so
    /// they carry the arithmetic <see cref="TabBar.TabRect"/> and <see cref="TabBar.ContentBounds"/> run on: the
    /// last facts here pin that there is ONE copy of it rather than two that can drift.
    /// </summary>
    public class TabStripTests
    {
        // Synthetic geometry, not any game's placement rule: a band wider than the block it holds, so a centred
        // block has somewhere to move to.
        static readonly Rect Band = new(100f, 200f, 400f, 60f);

        // Two rows of five: the side-panel shape, in numbers that make the arithmetic readable.
        static readonly TabStripMetrics TwoRows = new(
            TabWidth: 56f, TabHeight: 26f, Spacing: 4f, Columns: 5, Align: GuiAlign.Center);

        const int Ten = 10;

        static Vector2 CentreOf(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);

        static void AssertApart(in Rect a, in Rect b)
        {
            bool apart = a.Right <= b.X || b.Right <= a.X || a.Bottom <= b.Y || b.Bottom <= a.Y;
            Assert.True(apart, $"({a.X},{a.Y},{a.Width},{a.Height}) overlaps ({b.X},{b.Y},{b.Width},{b.Height})");
        }

        [Fact]
        public void The_strip_draws_two_rows_of_five_that_never_meet()
        {
            var rects = new List<Rect>();
            for (int i = 0; i < Ten; i++) rects.Add(TabStrip.TabRect(Band, i, Ten, TwoRows));

            // Five a row, and the sixth tab starts the second row rather than running off the edge.
            Assert.Equal(rects[0].Y, rects[4].Y, 3);
            Assert.Equal(rects[0].Y + 26f + 4f, rects[5].Y, 3);
            Assert.Equal(rects[0].X, rects[5].X, 3);
            for (int i = 0; i < rects.Count; i++)
            {
                Assert.Equal(56f, rects[i].Width, 3);
                Assert.Equal(26f, rects[i].Height, 3);
                Assert.True(rects[i].X >= Band.X && rects[i].Right <= Band.Right,
                    "a tab ran outside the strip band");
                Assert.True(rects[i].Y >= Band.Y && rects[i].Bottom <= Band.Bottom,
                    "a tab ran outside the strip band");
                for (int j = i + 1; j < rects.Count; j++) AssertApart(rects[i], rects[j]);
            }

            // The block is five tabs and four gutters wide, two tabs and one gutter tall.
            Assert.Equal(5f * 56f + 4f * 4f, TwoRows.BlockWidth(Ten), 3);
            Assert.Equal(2f * 26f + 4f, TwoRows.BlockHeight(Ten), 3);
            Assert.Equal(2, TwoRows.RowsFor(Ten));
        }

        [Fact]
        public void Every_tab_rect_answers_its_own_index_and_a_gutter_answers_none()
        {
            for (int i = 0; i < Ten; i++)
            {
                Rect rect = TabStrip.TabRect(Band, i, Ten, TwoRows);
                Assert.Equal(i, TabStrip.TabAt(Band, CentreOf(rect), Ten, TwoRows));
            }

            // The gutter between the two rows is chrome, not a tab, and so is the band outside the block.
            Rect first = TabStrip.TabRect(Band, 0, Ten, TwoRows);
            Assert.Equal(-1, TabStrip.TabAt(Band, new Vector2(first.X + 2f, first.Bottom + 1f), Ten, TwoRows));
            Assert.Equal(-1, TabStrip.TabAt(Band, new Vector2(first.Right + 1f, first.Y + 2f), Ten, TwoRows));
            Assert.Equal(-1, TabStrip.TabAt(Band, new Vector2(Band.X + 1f, Band.Y + 1f), Ten, TwoRows));

            // A tab beyond the drawn count is not hit even though its rect exists.
            Rect sixth = TabStrip.TabRect(Band, 5, Ten, TwoRows);
            Assert.Equal(-1, TabStrip.TabAt(Band, CentreOf(sixth), 5, TwoRows));
        }

        [Fact]
        public void The_block_is_centred_in_its_band_and_left_anchored_by_default()
        {
            Rect centred = TabStrip.BlockRect(Band, Ten, TwoRows);
            Rect left = TabStrip.BlockRect(Band, Ten, TwoRows with { Align = GuiAlign.Left });
            Rect right = TabStrip.BlockRect(Band, Ten, TwoRows with { Align = GuiAlign.Right });

            Assert.Equal(Band.X + (Band.Width - centred.Width) * 0.5f, centred.X, 3);
            Assert.Equal(Band.X, left.X, 3);
            Assert.Equal(Band.Right, right.Right, 3);
            Assert.Equal(Band.Y, centred.Y, 3);

            // The TABS move with the block, not just the footprint: the first one starts where the block does and
            // the last one ends where it ends, under every alignment.
            Assert.Equal(centred.X, TabStrip.TabRect(Band, 0, Ten, TwoRows).X, 3);
            Assert.Equal(centred.Right, TabStrip.TabRect(Band, 4, Ten, TwoRows).Right, 3);
            Assert.Equal(Band.X, TabStrip.TabRect(Band, 0, Ten, TwoRows with { Align = GuiAlign.Left }).X, 3);

            // Left is what a metrics value with no alignment named says, so the anchored layout is the default.
            Assert.Equal(GuiAlign.Left, new TabStripMetrics(56f, 26f, 4f, 5).Align);
            Assert.Equal(GuiAlign.Left, default(TabStripMetrics).Align);

            // A block wider than its band starts at the band's edge rather than outside it.
            var narrow = new Rect(0f, 0f, 40f, 60f);
            Assert.Equal(0f, TabStrip.BlockRect(narrow, Ten, TwoRows).X, 3);
            Assert.Equal(0f, TabStrip.BlockRect(narrow, Ten, TwoRows with { Align = GuiAlign.Right }).X, 3);
        }

        [Fact]
        public void A_column_count_of_zero_or_more_than_the_roster_is_one_row()
        {
            var oneRow = new TabStripMetrics(20f, 10f, 2f, Columns: 0);
            Assert.Equal(3, oneRow.ColumnsFor(3));
            Assert.Equal(1, oneRow.RowsFor(3));
            Assert.Equal(3, new TabStripMetrics(20f, 10f, 2f, Columns: 99).ColumnsFor(3));
            Assert.Equal(0, oneRow.RowsFor(0));
            Assert.Equal(0f, oneRow.BlockHeight(0), 3);

            // Left-anchored by default, so the third tab sits two tab-plus-gutter steps in from the band's edge.
            Rect third = TabStrip.TabRect(Band, 2, 3, oneRow);
            Assert.Equal(Band.X + 2f * (20f + 2f), third.X, 3);
            Assert.Equal(Band.Y, third.Y, 3);
        }

        [Fact]
        public void The_even_layout_splits_the_band_with_no_gutter_and_inverts()
        {
            Rect t0 = TabStrip.EvenTabRect(Band, 0, 3);
            Rect t1 = TabStrip.EvenTabRect(Band, 1, 3);
            Rect t2 = TabStrip.EvenTabRect(Band, 2, 3);

            Assert.Equal(Band.X, t0.X, 3);
            Assert.Equal(t0.Right, t1.X, 3);
            Assert.Equal(t1.Right, t2.X, 3);
            Assert.Equal(Band.Right, t2.Right, 3);
            Assert.Equal(Band.Height, t0.Height, 3);

            Assert.Equal(1, TabStrip.EvenTabAt(Band, CentreOf(t1), 3));
            Assert.Equal(-1, TabStrip.EvenTabAt(Band, new Vector2(Band.X - 1f, Band.Y + 1f), 3));
        }

        [Fact]
        public void Out_of_range_indices_and_counts_throw()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => TabStrip.TabRect(Band, -1, Ten, TwoRows));
            Assert.Throws<ArgumentOutOfRangeException>(() => TabStrip.TabRect(Band, Ten, Ten, TwoRows));
            Assert.Throws<ArgumentOutOfRangeException>(() => TabStrip.EvenTabRect(Band, 0, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => TabStrip.EvenTabRect(Band, 3, 3));
            Assert.Throws<ArgumentOutOfRangeException>(() => TabStrip.EvenTabAt(Band, Vector2.Zero, 0));
        }

        [Fact]
        public void The_widget_and_the_statics_run_the_same_arithmetic()
        {
            var items = new TabBarItem[Ten];
            for (int i = 0; i < items.Length; i++) items[i] = new TabBarItem(LocalizedText.Raw($"Tab {i}"));
            var bar = new TabBar(items, font: null, Band)
            {
                TabWidth = TwoRows.TabWidth,
                TabHeight = TwoRows.TabHeight,
                Columns = TwoRows.Columns,
                Spacing = TwoRows.Spacing,
                BlockAlign = GuiAlign.Center,
            };

            Assert.Equal(TwoRows, bar.Metrics);
            for (int i = 0; i < Ten; i++) Assert.Equal(TabStrip.TabRect(Band, i, Ten, TwoRows), bar.TabRect(i));
            Assert.Equal(TabStrip.BlockRect(Band, Ten, TwoRows), bar.ContentBounds);

            // The even-split bar runs the static's other layout, unchanged.
            var even = new TabBar(new[] { LocalizedText.Raw("A"), LocalizedText.Raw("B") }, font: null, Band);
            Assert.Equal(TabStrip.EvenTabRect(Band, 1, 2), even.TabRect(1));
            Assert.Equal(Band, even.ContentBounds);
        }
    }
}
