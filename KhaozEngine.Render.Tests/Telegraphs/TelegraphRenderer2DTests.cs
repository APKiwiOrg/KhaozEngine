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
    }
}
