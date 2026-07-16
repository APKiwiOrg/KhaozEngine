using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="ShapeGeometry.TryBounds"/>: the padded AABB an exclusion / scatter
    /// override shape edit reports as its dirty region (a disc, a rect including an inverted-authored one, a
    /// polygon, and the false cases: null and an empty polygon).</summary>
    public class ShapeGeometryTests
    {
        static void AssertBounds(RectArea area, float minX, float minZ, float maxX, float maxZ)
        {
            Assert.Equal(minX, area.MinX, 3);
            Assert.Equal(minZ, area.MinZ, 3);
            Assert.Equal(maxX, area.MaxX, 3);
            Assert.Equal(maxZ, area.MaxZ, 3);
        }

        [Fact]
        public void TryBounds_Disc_IsCenterPlusMinusRadiusPlusMargin()
        {
            var disc = new DiscShapeDoc { CenterX = 10f, CenterZ = -5f, Radius = 4f };

            Assert.True(ShapeGeometry.TryBounds(disc, out RectArea area));
            float r = 4f + ShapeGeometry.ShapeBoundsMargin;
            AssertBounds(area, 10f - r, -5f - r, 10f + r, -5f + r);
        }

        [Fact]
        public void TryBounds_Rect_IsMinMaxPlusMargin()
        {
            var rect = new RectShapeDoc { MinX = -3f, MinZ = 2f, MaxX = 7f, MaxZ = 9f };

            Assert.True(ShapeGeometry.TryBounds(rect, out RectArea area));
            float m = ShapeGeometry.ShapeBoundsMargin;
            AssertBounds(area, -3f - m, 2f - m, 7f + m, 9f + m);
        }

        [Fact]
        public void TryBounds_InvertedRect_Normalizes()
        {
            // Authored with Min > Max (a degenerate / mis-authored rect): the bounds still come out right-way-up,
            // the same normalization RectShapeDoc.ToArea's BoxArea2D would need to not silently mis-test everything.
            var rect = new RectShapeDoc { MinX = 7f, MinZ = 9f, MaxX = -3f, MaxZ = 2f };

            Assert.True(ShapeGeometry.TryBounds(rect, out RectArea area));
            float m = ShapeGeometry.ShapeBoundsMargin;
            AssertBounds(area, -3f - m, 2f - m, 7f + m, 9f + m);
        }

        [Fact]
        public void TryBounds_Polygon_IsMinMaxOverPointsPlusMargin()
        {
            var polygon = new PolygonShapeDoc
            {
                Points = new List<float[]> { new[] { 0f, 0f }, new[] { 6f, 1f }, new[] { 2f, -4f } },
            };

            Assert.True(ShapeGeometry.TryBounds(polygon, out RectArea area));
            float m = ShapeGeometry.ShapeBoundsMargin;
            AssertBounds(area, 0f - m, -4f - m, 6f + m, 1f + m);
        }

        [Fact]
        public void TryBounds_EmptyPolygon_ReturnsFalse()
        {
            var polygon = new PolygonShapeDoc { Points = new List<float[]>() };

            Assert.False(ShapeGeometry.TryBounds(polygon, out RectArea area));
            Assert.Equal(default, area);
        }

        [Fact]
        public void TryBounds_Null_ReturnsFalse()
        {
            Assert.False(ShapeGeometry.TryBounds(null, out RectArea area));
            Assert.Equal(default, area);
        }

        [Fact]
        public void TryBounds_MarginIsTwo()
        {
            // The margin arithmetic other tests lean on: 2 m, comfortably under FeatureGeometry.FootprintMargin's
            // 8 m (this shape-only case has no height/normal reach to cover).
            Assert.Equal(2f, ShapeGeometry.ShapeBoundsMargin);
            Assert.True(ShapeGeometry.ShapeBoundsMargin < FeatureGeometry.FootprintMargin);
        }
    }
}
