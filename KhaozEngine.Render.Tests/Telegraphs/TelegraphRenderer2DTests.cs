using System;
using System.Numerics;
using KhaozEngine.Telegraphs;
using Xunit;

namespace KhaozEngine.Tests.Telegraphs
{
    public class TelegraphRenderer2DTests
    {
        [Fact]
        public void Drawing_before_Begin_throws()
        {
            var tg = new TelegraphRenderer2D();
            Assert.Throws<InvalidOperationException>(() =>
                tg.Circle(Vector2.Zero, 10f, 0.5f, TelegraphStyle.Generic));
        }

        [Fact]
        public void End_without_Begin_throws()
        {
            var tg = new TelegraphRenderer2D();
            Assert.Throws<InvalidOperationException>(() => tg.End());
        }

        [Fact]
        public void DotLane_before_Begin_throws()
        {
            var tg = new TelegraphRenderer2D();
            Assert.Throws<InvalidOperationException>(() =>
                tg.DotLane(Vector2.Zero, Vector2.UnitX, 100f, 10f, 2f, 0.5f, 0f, TelegraphStyle.Generic));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void DotLane_non_positive_or_non_finite_spacing_throws(float spacing)
        {
            // No Begin: the spacing guard runs before the Begin check, so this needs no SpriteBatch.
            var tg = new TelegraphRenderer2D();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                tg.DotLane(Vector2.Zero, Vector2.UnitX, 100f, spacing, 2f, 0.5f, 0f, TelegraphStyle.Generic));
        }

        [Theory]
        [InlineData(100f, 10f, 10)]
        [InlineData(100f, 30f, 3)]
        [InlineData(5f, 10f, 0)]
        public void DotLane_last_index_is_the_floor_of_length_over_spacing(float length, float spacing, int expected)
        {
            Assert.Equal(expected, TelegraphRenderer2D.DotLaneLastIndex(length, spacing));
        }

        [Theory]
        [InlineData(100f, 1e-8f)]
        [InlineData(3e9f, 1f)]
        [InlineData(float.MaxValue, float.Epsilon)]
        [InlineData(4096f, 1f)]
        public void DotLane_last_index_is_capped_at_the_dot_limit(float length, float spacing)
        {
            Assert.Equal(TelegraphRenderer2D.MaxDotLaneDots - 1, TelegraphRenderer2D.DotLaneLastIndex(length, spacing));
        }

        [Fact]
        public void DotLane_last_index_just_under_the_cap_is_not_clamped()
        {
            Assert.Equal(TelegraphRenderer2D.MaxDotLaneDots - 2,
                TelegraphRenderer2D.DotLaneLastIndex(TelegraphRenderer2D.MaxDotLaneDots - 2, 1f));
        }
    }
}
