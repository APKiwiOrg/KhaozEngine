using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the IArea2D shape set used by scatter exclusions/overrides.</summary>
    public class Area2DTests
    {
        [Fact]
        public void Disc_ContainsCenterAndEdge_ExcludesOutside()
        {
            var disc = new DiscArea2D(10f, -5f, 3f);
            Assert.True(disc.Contains(10f, -5f));
            Assert.True(disc.Contains(13f, -5f));      // on the radius (inclusive)
            Assert.False(disc.Contains(13.01f, -5f));
        }

        [Fact]
        public void Box_ContainsInsideAndEdges_ExcludesOutside()
        {
            var box = new BoxArea2D(0f, 0f, 4f, 2f);
            Assert.True(box.Contains(2f, 1f));
            Assert.True(box.Contains(0f, 0f));
            Assert.True(box.Contains(4f, 2f));         // max edge inclusive
            Assert.False(box.Contains(4.01f, 1f));
            Assert.False(box.Contains(-0.01f, 1f));
        }

        [Fact]
        public void Polygon_ContainsInside_ExcludesOutside()
        {
            // L-shape: (0,0)-(4,0)-(4,2)-(2,2)-(2,4)-(0,4)
            var poly = new PolygonArea2D(new[]
            {
                new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 2),
                new Vector2(2, 2), new Vector2(2, 4), new Vector2(0, 4),
            });
            Assert.True(poly.Contains(1f, 1f));
            Assert.True(poly.Contains(3f, 1f));
            Assert.True(poly.Contains(1f, 3f));
            Assert.False(poly.Contains(3f, 3f));       // the notch
            Assert.False(poly.Contains(5f, 1f));
        }

        [Fact]
        public void Polygon_FewerThanThreePoints_ContainsNothing()
        {
            var poly = new PolygonArea2D(new[] { new Vector2(0, 0), new Vector2(1, 0) });
            Assert.False(poly.Contains(0.5f, 0f));
        }
    }
}
