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
    }
}
