using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class SnapStripGeometryTests
    {
        [Fact]
        public void Placed_rectangles_and_point_hits_are_inverses()
        {
            var m = new SnapStripMetrics(40f, 10f);
            var b = new Rect(100f, 200f, 200f, 80f);
            Assert.Equal(new Rect(180f, 200f, 40f, 80f), SnapStripGeometry.ItemBounds(b, 50, 2420f, 100, m));
            Assert.Equal(50, SnapStripGeometry.IndexAt(b, new(200f, 240f), 2420f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(225f, 240f), 2420f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(300f, 240f), 2420f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(200f, 280f), 2420f, 100, m));
            Assert.Equal(50, SnapStripGeometry.IndexAt(b, new(180f, 200f), 2420f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(220f, 240f), 2420f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(99f, 240f), 2420f, 100, m));
        }

        [Fact]
        public void Fractional_placement_keeps_half_open_hits_at_rounded_item_edges()
        {
            var m = new SnapStripMetrics(0.1f, 0.2f);
            var b = new Rect(100f, 200f, 10f, 80f);
            Assert.Equal(new Rect(100.6f, 200f, 0.1f, 80f), SnapStripGeometry.ItemBounds(b, 2, 0f, 100, m));
            Assert.Equal(2, SnapStripGeometry.IndexAt(b, new(100.6f, 240f), 0f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(100.7f, 240f), 0f, 100, m));
        }

        [Fact]
        public void Visible_range_excludes_edge_contacts_and_gap_only_views()
        {
            var m = new SnapStripMetrics(40f, 10f);
            var b = new Rect(100f, 200f, 200f, 80f);
            Assert.Equal((0, 4), SnapStripGeometry.VisibleRange(b, 0f, 100, m));
            Assert.Equal((1, 4), SnapStripGeometry.VisibleRange(b, 45f, 100, m));
            Assert.Equal((1, 2), SnapStripGeometry.VisibleRange(new(100, 200, 100, 80), 40f, 100, m));
            Assert.Equal((0, 0), SnapStripGeometry.VisibleRange(new(100, 200, 5, 80), 40f, 100, m));
            Assert.Equal((0, 1), SnapStripGeometry.VisibleRange(new(100, 200, 50, 80), 0f, 100, m));
            Assert.Equal((99, 1), SnapStripGeometry.VisibleRange(b, 4989f, 100, m));
            Assert.Equal((0, 0), SnapStripGeometry.VisibleRange(b, 4990f, 100, m));
        }

        [Fact]
        public void Visible_range_excludes_rounded_right_edges_touching_the_viewport_left()
        {
            var metrics = new SnapStripMetrics(0.1f, 0.2f);
            var bounds = new Rect(100f, 200f, 10f, 80f);
            var item = SnapStripGeometry.ItemBounds(bounds, 2, 0.7f, 100, metrics);
            Assert.Equal(bounds.X, item.Right);
            Assert.Equal((3, 33), SnapStripGeometry.VisibleRange(bounds, 0.7f, 100, metrics));
        }

        [Fact]
        public void Visible_range_excludes_rounded_left_edges_touching_the_viewport_right()
        {
            var metrics = new SnapStripMetrics(0.1f, 0.2f);
            var bounds = new Rect(100f, 200f, 10.1f, 80f);
            var item = SnapStripGeometry.ItemBounds(bounds, 36, 0.7000001f, 100, metrics);
            Assert.Equal(bounds.Right, item.X);
            Assert.Equal((3, 33), SnapStripGeometry.VisibleRange(bounds, 0.7000001f, 100, metrics));
        }

        [Theory]
        [InlineData(0.69999f, 2, 34)]
        [InlineData(0.70001f, 3, 34)]
        public void Visible_range_keeps_positive_overlaps_near_rounded_edges(float offset, int first, int count)
        {
            var metrics = new SnapStripMetrics(0.1f, 0.2f);
            var bounds = new Rect(100f, 200f, 10.1f, 80f);
            Assert.Equal((first, count), SnapStripGeometry.VisibleRange(bounds, offset, 100, metrics));
        }

        [Fact]
        public void Nearest_stop_uses_actual_distance_and_canonical_ends()
        {
            var m = new SnapStripMetrics(40f, 10f);
            Assert.Equal(50, SnapStripGeometry.NearestIndex(2445f, 200f, 100, m));
            Assert.Equal(51, SnapStripGeometry.NearestIndex(2446f, 200f, 100, m));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(5f, 200f, 100, m));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(10f, 200f, 100, m));
            Assert.Equal(2, SnapStripGeometry.NearestIndex(10.125f, 200f, 100, m));
            Assert.Equal(97, SnapStripGeometry.NearestIndex(4780f, 200f, 100, m));
            Assert.Equal(99, SnapStripGeometry.NearestIndex(4785f, 200f, 100, m));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(0f, 200f, 100, m));
            Assert.Equal(99, SnapStripGeometry.NearestIndex(4790f, 200f, 100, m));
            Assert.Equal(-1, SnapStripGeometry.NearestIndex(0f, 200f, 0, m));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(-100f, 200f, 100, m));
            Assert.Equal(99, SnapStripGeometry.NearestIndex(5000f, 200f, 100, m));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(10f, 200f, 3, m));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(10f, 200f, 1, m));
        }

        [Fact]
        public void Nearest_stop_uses_anchor_with_canonical_ends()
        {
            var anchored = new SnapStripMetrics(69f, 0f, 0.4375f);
            Assert.Equal(0, SnapStripGeometry.NearestIndex(0f, 552f, 100, anchored));
            Assert.Equal(0, SnapStripGeometry.NearestIndex(34.5f, 552f, 100, anchored));
            Assert.Equal(4, SnapStripGeometry.NearestIndex(34.625f, 552f, 100, anchored));
            Assert.Equal(4, SnapStripGeometry.NearestIndex(69f, 552f, 100, anchored));
            Assert.Equal(94, SnapStripGeometry.NearestIndex(6313.5f, 552f, 100, anchored));
            Assert.Equal(99, SnapStripGeometry.NearestIndex(6313.625f, 552f, 100, anchored));
            Assert.Equal(99, SnapStripGeometry.NearestIndex(6348f, 552f, 100, anchored));
        }

        [Fact]
        public void Placement_and_visibility_use_offsets_verbatim_and_allow_negative_origins()
        {
            var m = new SnapStripMetrics(40f, 10f);
            var b = new Rect(-100f, -200f, 100f, 80f);
            Assert.Equal(new Rect(-80f, -200f, 40f, 80f), SnapStripGeometry.ItemBounds(b, 0, -20f, 1, m));
            Assert.Equal(0, SnapStripGeometry.IndexAt(b, new(-80f, -200f), -20f, 1, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(-90f, -200f), -20f, 1, m));
            Assert.Equal((0, 1), SnapStripGeometry.VisibleRange(b, -20f, 1, m));
            Assert.Equal((0, 0), SnapStripGeometry.VisibleRange(b, -100f, 1, m));
            Assert.Equal(new Rect(-150f, -200f, 40f, 80f), SnapStripGeometry.ItemBounds(b, 0, 50f, 1, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(-100f, -200f), 50f, 1, m));
            Assert.Equal((0, 0), SnapStripGeometry.VisibleRange(b, 50f, 1, m));
        }

        [Fact]
        public void Empty_and_zero_area_views_have_no_range_or_hits()
        {
            var m = new SnapStripMetrics(40f, 10f);
            var b = new Rect(100f, 200f, 200f, 80f);
            Assert.Equal((0, 0), SnapStripGeometry.VisibleRange(b, 0f, 0, m));
            Assert.Equal(-1, SnapStripGeometry.IndexAt(b, new(100f, 200f), 0f, 0, m));
            foreach (var hidden in new[] { new Rect(100f, 200f, 0f, 80f), new Rect(100f, 200f, 200f, 0f) })
            {
                Assert.Equal((0, 0), SnapStripGeometry.VisibleRange(hidden, 0f, 100, m));
                Assert.Equal(-1, SnapStripGeometry.IndexAt(hidden, new(100f, 200f), 0f, 100, m));
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(100)]
        public void Placement_rejects_invalid_item_indices(int index)
        {
            var m = new SnapStripMetrics(40f, 10f);
            var b = new Rect(100f, 200f, 200f, 80f);
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, index, 0f, 100, m));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, 0, 0f, 0, m));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Nonfinite_offsets_and_points_throw_even_for_empty_views(float value)
        {
            var m = new SnapStripMetrics(40f, 10f);
            var b = new Rect(100f, 200f, 200f, 80f);
            foreach (int count in new[] { 0, 100 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.NearestIndex(value, 200f, count, m));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, 0, value, count, m));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, Vector2.Zero, value, count, m));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.VisibleRange(b, value, count, m));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, new(value, 200f), 0f, count, m));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, new(100f, value), 0f, count, m));
            }
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Invalid_bounds_dimensions_throw_even_for_empty_views(float value)
        {
            var m = new SnapStripMetrics(40f, 10f);
            foreach (var b in new[] { new Rect(100f, 200f, value, 80f), new Rect(100f, 200f, 200f, value) })
            {
                foreach (int count in new[] { 0, 100 })
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, 0, 0f, count, m));
                    Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, Vector2.Zero, 0f, count, m));
                    Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.VisibleRange(b, 0f, count, m));
                }
            }
        }

        [Fact]
        public void Nonfinite_bounds_origins_throw_even_for_empty_views()
        {
            var m = new SnapStripMetrics(40f, 10f);
            foreach (float origin in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                foreach (var b in new[] { new Rect(origin, 200f, 200f, 80f), new Rect(100f, origin, 200f, 80f) })
                {
                    foreach (int count in new[] { 0, 100 })
                    {
                        Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, 0, 0f, count, m));
                        Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, Vector2.Zero, 0f, count, m));
                        Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.VisibleRange(b, 0f, count, m));
                    }
                }
            }
        }

        [Fact]
        public void New_geometry_queries_validate_metrics_counts_and_extents_before_empty_results()
        {
            var b = new Rect(100f, 200f, 0f, 0f);
            foreach (var metrics in new[] { default(SnapStripMetrics), new(40f, -1f), new(40f, 0f, 2f) })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.NearestIndex(0f, 0f, 0, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, 0, 0f, 0, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, Vector2.Zero, 0f, 0, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.VisibleRange(b, 0f, 0, metrics));
            }
            var m = new SnapStripMetrics(40f, 10f);
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.NearestIndex(0f, -1f, 100, m));
            foreach (int count in new[] { -1, 2 })
            {
                var metrics = count < 0 ? m : new SnapStripMetrics(float.MaxValue);
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.NearestIndex(0f, 0f, count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ItemBounds(b, 0, 0f, count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.IndexAt(b, Vector2.Zero, 0f, count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.VisibleRange(b, 0f, count, metrics));
            }
        }

        [Fact]
        public void Extents_have_no_trailing_gap()
        {
            var m = new SnapStripMetrics(40f, 10f);
            Assert.Equal(4990f, SnapStripGeometry.ContentWidth(100, m));
            Assert.Equal(0f, SnapStripGeometry.ContentWidth(0, m));
            Assert.Equal(40f, SnapStripGeometry.ContentWidth(1, m));
            Assert.Equal(4790f, SnapStripGeometry.MaxScroll(200f, 100, m));
            Assert.Equal(0f, SnapStripGeometry.MaxScroll(200f, 1, m));
            Assert.Equal(0f, SnapStripGeometry.MaxScroll(200f, 0, m));
        }

        [Fact]
        public void Centre_offsets_clamp_at_both_ends()
        {
            var m = new SnapStripMetrics(40f, 10f);
            Assert.Equal(0f, SnapStripGeometry.CenterOffset(-10, 200f, 100, m));
            Assert.Equal(2420f, SnapStripGeometry.CenterOffset(50, 200f, 100, m));
            Assert.Equal(4790f, SnapStripGeometry.CenterOffset(999, 200f, 100, m));
            Assert.Equal(0f, SnapStripGeometry.CenterOffset(50, 0f, 100, m));
            Assert.Equal(0f, SnapStripGeometry.CenterOffset(0, 200f, 0, m));
            Assert.Equal(0f, SnapStripGeometry.CenterOffset(int.MinValue, 200f, 100, m));
            Assert.Equal(4790f, SnapStripGeometry.CenterOffset(int.MaxValue, 200f, 100, m));
            Assert.Equal(0f, SnapStripGeometry.CenterOffset(0, 200f, 1, m));
        }

        [Fact]
        public void Default_anchor_preserves_centring()
        {
            var m = new SnapStripMetrics(40f, 10f);
            Assert.Equal(0.5f, m.Anchor);
            Assert.Equal(2420f, SnapStripGeometry.CenterOffset(50, 200f, 100, new(40f, 10f, 0.5f)));
            Assert.Equal(2420f, SnapStripGeometry.CenterOffset(50, 200f, 100, m));
        }

        [Fact]
        public void Anchored_stops_are_whole_pitches()
        {
            var anchored = new SnapStripMetrics(69f, 0f, 0.4375f);
            Assert.Equal(6348f, SnapStripGeometry.MaxScroll(552f, 100, anchored));
            Assert.Equal(0f, SnapStripGeometry.CenterOffset(3, 552f, 100, anchored));
            Assert.Equal(69f, SnapStripGeometry.CenterOffset(4, 552f, 100, anchored)); // 4 * 69 + 34.5 - 241.5
            for (var i = 0; i < 100; i++)
                Assert.Equal(0f, SnapStripGeometry.CenterOffset(i, 552f, 100, anchored) % 69f);
            Assert.Equal(6348f, SnapStripGeometry.CenterOffset(99, 552f, 100, anchored));
            Assert.Equal(2520f, SnapStripGeometry.CenterOffset(50, 200f, 100, new(40f, 10f, 0f)));
            Assert.Equal(2320f, SnapStripGeometry.CenterOffset(50, 200f, 100, new(40f, 10f, 1f)));
        }

        [Fact]
        public void Invalid_dimensions_metrics_and_extents_throw()
        {
            var m = new SnapStripMetrics(40f, 10f);
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(-1, m));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(0, default));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(2, new(float.MaxValue)));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(float.NaN, 100, m));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(200f, -1, m));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.CenterOffset(0, 200f, -1, m));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(200f, 2, new(float.MaxValue)));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.CenterOffset(0, 0f, 2, new(float.MaxValue)));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(0, new(float.MaxValue, float.MaxValue)));
            Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(1, new(float.MaxValue, float.MaxValue)));
            Assert.Equal(float.MaxValue, SnapStripGeometry.ContentWidth(1, new(float.MaxValue)));
            Assert.Equal(float.MaxValue / 2f, SnapStripGeometry.ContentWidth(1, new(float.MaxValue / 2f, float.MaxValue / 2f)));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Invalid_item_widths_throw_even_for_empty_strips(float width)
        {
            var metrics = new SnapStripMetrics(width);
            foreach (int count in new[] { 0, 100 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(200f, count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.CenterOffset(0, 200f, count, metrics));
            }
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Invalid_item_spacing_throws_even_for_empty_strips(float spacing)
        {
            var metrics = new SnapStripMetrics(40f, spacing);
            foreach (int count in new[] { 0, 100 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(200f, count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.CenterOffset(0, 200f, count, metrics));
            }
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Invalid_viewport_widths_throw_even_for_empty_strips(float viewportWidth)
        {
            var m = new SnapStripMetrics(40f, 10f);
            foreach (int count in new[] { 0, 100 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(viewportWidth, count, m));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.CenterOffset(0, viewportWidth, count, m));
            }
        }

        [Theory]
        [InlineData(-0.001f)]
        [InlineData(1.001f)]
        [InlineData(float.NaN)]
        [InlineData(float.NegativeInfinity)]
        [InlineData(float.PositiveInfinity)]
        public void Invalid_anchors_throw_even_for_empty_strips(float anchor)
        {
            var metrics = new SnapStripMetrics(40f, 10f, anchor);
            foreach (int count in new[] { 0, 100 })
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.ContentWidth(count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.MaxScroll(200f, count, metrics));
                Assert.Throws<ArgumentOutOfRangeException>(() => SnapStripGeometry.CenterOffset(0, 200f, count, metrics));
            }
        }
    }
}
