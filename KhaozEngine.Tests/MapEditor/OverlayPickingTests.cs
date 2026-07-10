using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="OverlayPicking"/>, the GPU-free containment pick over the editor's
    /// ground overlays: it maps a terrain (x, z) point to the exclusion / region / feature-marker under it. Pins the
    /// deterministic priority (features beat exclusions beat regions, primary over distance) and the nearest-center
    /// tiebreak within one category, plus the empty-point and null-shape guards.</summary>
    public class OverlayPickingTests
    {
        static MapDocument Doc() => new MapDocument
        {
            Id = "overlay-zone",
            Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
        };

        [Fact]
        public void Pick_Priority_FeatureThenExclusionThenRegion()
        {
            MapDocument doc = Doc();
            doc.Regions.Add(new MapRegion { Name = "town", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 30f } });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f } });
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 8f, Depth = 2f });

            // Inside all three (< 1.5 m of the feature center): the feature wins.
            Assert.True(OverlayPicking.Pick(doc, 0.5f, 0.5f, out OverlayPicking.OverlayPickResult a));
            Assert.Equal(SelectionKind.Feature, a.Kind);
            Assert.Equal("0", a.Id);

            // Inside the exclusion and region, clear of the feature marker: the exclusion wins.
            Assert.True(OverlayPicking.Pick(doc, 5f, 0f, out OverlayPicking.OverlayPickResult b));
            Assert.Equal(SelectionKind.Exclusion, b.Kind);
            Assert.Equal("0", b.Id);

            // Inside the region only: the region wins.
            Assert.True(OverlayPicking.Pick(doc, 20f, 0f, out OverlayPicking.OverlayPickResult c));
            Assert.Equal(SelectionKind.Region, c.Kind);
            Assert.Equal("town", c.Id);
        }

        [Fact]
        public void Pick_PriorityIsPrimaryOverDistance()
        {
            MapDocument doc = Doc();
            // A region centered right at the click and a lake marker whose center is farther but still covers it.
            // Priority is primary, so the feature wins even though the region center is nearer the point.
            doc.Regions.Add(new MapRegion { Name = "r", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 1f, CenterZ = 0f, Radius = 3f, Depth = 1f });

            Assert.True(OverlayPicking.Pick(doc, 0.2f, 0f, out OverlayPicking.OverlayPickResult r));
            Assert.Equal(SelectionKind.Feature, r.Kind);
        }

        [Fact]
        public void Pick_WithinCategory_NearestCenterWins()
        {
            MapDocument doc = Doc();
            // Two overlapping exclusion discs both containing the click; the nearer center wins.
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = -4f, CenterZ = 0f, Radius = 10f } });   // index 0, center 5 away
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 2f, CenterZ = 0f, Radius = 10f } });     // index 1, center 1 away

            Assert.True(OverlayPicking.Pick(doc, 1f, 0f, out OverlayPicking.OverlayPickResult r));
            Assert.Equal(SelectionKind.Exclusion, r.Kind);
            Assert.Equal("1", r.Id);   // the nearer-centered exclusion
        }

        [Fact]
        public void Pick_OutsideEverything_ReturnsFalse()
        {
            MapDocument doc = Doc();
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });

            Assert.False(OverlayPicking.Pick(doc, 50f, 50f, out OverlayPicking.OverlayPickResult r));
            Assert.Equal(SelectionKind.None, r.Kind);
        }

        [Fact]
        public void Pick_SkipsNullShapesAndUncenterableFeatures()
        {
            MapDocument doc = Doc();
            doc.Exclusions.Add(new MapExclusion { Shape = null });                        // no shape: skipped
            doc.Regions.Add(new MapRegion { Name = "empty", Shape = null });              // no shape: skipped
            doc.Terrain.Features.Add(new PolygonlessFeature());                            // no known center: skipped

            Assert.False(OverlayPicking.Pick(doc, 0f, 0f, out _));
        }

        // A custom feature type with no known center field (an unknown discriminator), so the overlay pick skips it
        // rather than guessing a center by reflection.
        sealed class PolygonlessFeature : MapFeature
        {
            public override string Type => "custom-unknown";
        }
    }
}
