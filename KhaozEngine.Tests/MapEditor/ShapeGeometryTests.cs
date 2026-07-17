using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <c>ShapeGeometry.TryBounds</c> and <c>BoundsMarginFor</c>: the padded AABB an
    /// exclusion / scatter override shape edit reports as its dirty region (a disc, a rect including an
    /// inverted-authored one, a polygon, and the false cases: null and an empty polygon), and the jitter-aware
    /// margin the commands capture at Apply.</summary>
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

        [Fact]
        public void TryBounds_ExplicitMarginOverload_PadsByThatMargin()
        {
            // The commands pass their captured jitter-aware margin here instead of the bare constant.
            var disc = new DiscShapeDoc { CenterX = 10f, CenterZ = -5f, Radius = 4f };

            Assert.True(ShapeGeometry.TryBounds(disc, 7f, out RectArea area));
            AssertBounds(area, 10f - 11f, -5f - 11f, 10f + 11f, -5f + 11f);
        }

        // ---- BoundsMarginFor: the jitter-aware dirty-region margin --------------------------------------

        static MapDocument Doc(params float[] jitters)
        {
            var doc = new MapDocument { Id = "margin-test", Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f } };
            for (int i = 0; i < jitters.Length; i++)
                doc.ScatterLayers.Add(new MapScatterLayer { Name = $"layer-{i}", Jitter = jitters[i] });
            return doc;
        }

        [Fact]
        public void BoundsMarginFor_NoLayers_IsBareConstant()
        {
            // No scatter layers means no candidates to flip, so the base constant alone is already conservative.
            Assert.Equal(ShapeGeometry.ShapeBoundsMargin, ShapeGeometry.BoundsMarginFor(Doc()), 3);
        }

        [Fact]
        public void BoundsMarginFor_AddsLargestLayerJitter()
        {
            // Scatter tests exclusion/override membership at the JITTERED candidate position while chunk
            // assignment uses the un-jittered cell centre, so the margin must grow by the largest jitter. The
            // authored Jitter field has no clamp, so a 5 m layer must be honoured, not squashed to a constant.
            Assert.Equal(ShapeGeometry.ShapeBoundsMargin + 5f, ShapeGeometry.BoundsMarginFor(Doc(1.6f, 5f, 0.5f)), 3);
        }

        [Fact]
        public void BoundsMarginFor_NegativeJitter_UsesMagnitude()
        {
            // A degenerate negative-authored jitter displaces candidates by the same magnitude (the hash offset
            // is symmetric in |Jitter|), so the margin uses the absolute value.
            Assert.Equal(ShapeGeometry.ShapeBoundsMargin + 3f, ShapeGeometry.BoundsMarginFor(Doc(-3f, 1.6f)), 3);
        }
    }
}
