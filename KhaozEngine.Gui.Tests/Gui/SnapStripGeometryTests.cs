using System;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class SnapStripGeometryTests
    {
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
