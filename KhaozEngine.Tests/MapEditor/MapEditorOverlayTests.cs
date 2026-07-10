using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for the pure doc-to-overlay-draw-list computation
    /// (<see cref="MapEditorScene.ComputeOverlayDrawList"/>) that makes exclusions, regions, and terrain features
    /// visible in the viewport. Only the computation is covered here; the per-entry Scene3D debug-fill submission is
    /// GPU work and stays untested.</summary>
    public class MapEditorOverlayTests
    {
        // A flat ground sampler: every (x, z) sits at this height, so an overlay's lifted Center.Y is height plus a
        // small positive epsilon, which the coverage test checks without coupling to the exact lift constant.
        const float Ground = 4f;
        static float FlatGround(float x, float z) => Ground;

        // An unknown feature type (not one of the four built-ins), so the computation must skip it (no center).
        sealed class UnknownFeatureDoc : MapFeature
        {
            public override string Type => "unknown";
        }

        static EditorSelection Nothing() => new EditorSelection();

        static EditorSelection Select(SelectionKind kind, string id)
        {
            var sel = new EditorSelection();
            sel.Set(kind, id);
            return sel;
        }

        static void Near(float expected, float actual, float eps = 1e-3f) =>
            Assert.True(MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        static MapDocument ValidDoc(string id) => new MapDocument
        {
            Id = id,
            Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f },
        };

        [Fact]
        public void OverlayDrawList_CoversExclusionsRegionsFeatures()
        {
            MapDocument doc = ValidDoc("overlays");
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 2f, CenterZ = 3f, Radius = 6f } });
            doc.Exclusions.Add(new MapExclusion { Shape = new RectShapeDoc { MinX = -4f, MinZ = -2f, MaxX = 8f, MaxZ = 6f } });
            doc.Regions.Add(new MapRegion
            {
                Name = "zone",
                Shape = new PolygonShapeDoc { Points = { new[] { 0f, 0f }, new[] { 10f, 0f }, new[] { 5f, 8f } } },
            });
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 1f, CenterZ = 2f, Radius = 5f, Depth = 2f });
            doc.Terrain.Features.Add(new RidgeFeatureDoc { PointX = -3f, PointZ = 7f, Height = 3f });
            doc.Terrain.Features.Add(new UnknownFeatureDoc());   // no derivable center: must be skipped

            List<OverlayDraw> list = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true);

            // Two exclusions + one region + two known features = five overlays; the unknown feature is skipped.
            Assert.Equal(5, list.Count);
            Assert.Equal(2, list.Count(o => o.Category == OverlayCategory.Exclusion));
            Assert.Equal(1, list.Count(o => o.Category == OverlayCategory.Region));
            Assert.Equal(2, list.Count(o => o.Category == OverlayCategory.Feature));

            // Exclusion disc: center at the shape center, lifted a small epsilon above the sampled ground.
            OverlayDraw disc = list.First(o => o.Category == OverlayCategory.Exclusion && o.Shape == OverlayShape.Disc);
            Near(2f, disc.Center.X);
            Near(3f, disc.Center.Z);
            Assert.True(disc.Center.Y > Ground && disc.Center.Y < Ground + 1f, "center lifted a small epsilon above ground");
            Near(6f, disc.Radius);

            // Exclusion rect: center at the midpoint, half-extents half the span.
            OverlayDraw rect = list.First(o => o.Category == OverlayCategory.Exclusion && o.Shape == OverlayShape.Rect);
            Near(2f, rect.Center.X);        // (-4 + 8) / 2
            Near(2f, rect.Center.Z);        // (-2 + 6) / 2
            Near(6f, rect.HalfExtents.X);   // (8 - -4) / 2
            Near(4f, rect.HalfExtents.Y);   // (6 - -2) / 2

            // Region polygon: a fan rim with one vertex per polygon point.
            OverlayDraw poly = list.First(o => o.Category == OverlayCategory.Region);
            Assert.Equal(OverlayShape.Polygon, poly.Shape);
            Assert.NotNull(poly.Rim);
            Assert.Equal(3, poly.Rim!.Count);

            // Feature markers: discs at each known feature center (built-ins expose CenterX/CenterZ or PointX/PointZ).
            List<OverlayDraw> features = list.Where(o => o.Category == OverlayCategory.Feature).ToList();
            Assert.All(features, f => Assert.Equal(OverlayShape.Disc, f.Shape));
            Assert.All(features, f => Assert.True(f.Radius > 0f));
            Assert.Contains(features, f => MathF.Abs(f.Center.X - 1f) < 1e-3f && MathF.Abs(f.Center.Z - 2f) < 1e-3f);   // lake center
            Assert.Contains(features, f => MathF.Abs(f.Center.X + 3f) < 1e-3f && MathF.Abs(f.Center.Z - 7f) < 1e-3f);   // ridge point

            // Distinct, category-signalling colors: exclusions red-ish (R dominant), regions blue-ish (B dominant).
            Assert.True(disc.Color.R > disc.Color.G && disc.Color.R > disc.Color.B, "exclusion fill is red-ish");
            Assert.True(poly.Color.B > poly.Color.R && poly.Color.B > poly.Color.G, "region fill is blue-ish");
            Assert.NotEqual(disc.Color, poly.Color);
            Assert.NotEqual(disc.Color, features[0].Color);
        }

        [Fact]
        public void OverlayDrawList_HighlightsSelection()
        {
            MapDocument doc = ValidDoc("selection");
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 4f } });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 9f, CenterZ = 0f, Radius = 4f } });
            doc.Regions.Add(new MapRegion { Name = "zone", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 9f, Radius = 3f } });

            // Nothing selected: no overlay is flagged, and both exclusions share the same base color.
            List<OverlayDraw> baseline = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true);
            Assert.All(baseline, o => Assert.False(o.Selected));
            Color baseColor = baseline[0].Color;
            Assert.Equal(baseColor, baseline[1].Color);

            // Selecting exclusion index 1 brightens only that overlay; its unselected sibling is untouched.
            List<OverlayDraw> withSel = MapEditorScene.ComputeOverlayDrawList(
                doc, Select(SelectionKind.Exclusion, "1"), FlatGround, showOverlays: true);
            Assert.False(withSel[0].Selected);
            Assert.True(withSel[1].Selected);
            Assert.Equal(baseColor, withSel[0].Color);
            Assert.True(withSel[1].Color.R > baseColor.R, "selected overlay reads brighter");
            Assert.True(withSel[1].Color.G >= baseColor.G && withSel[1].Color.B >= baseColor.B);

            // Region selection is keyed by name and highlights only the region overlay.
            List<OverlayDraw> regionSel = MapEditorScene.ComputeOverlayDrawList(
                doc, Select(SelectionKind.Region, "zone"), FlatGround, showOverlays: true);
            OverlayDraw region = regionSel.First(o => o.Category == OverlayCategory.Region);
            Assert.True(region.Selected);
            Assert.All(regionSel.Where(o => o.Category == OverlayCategory.Exclusion), o => Assert.False(o.Selected));
        }

        [Fact]
        public void OverlayDrawList_ShowOverlaysFalse_ReturnsEmpty()
        {
            MapDocument doc = ValidDoc("hidden");
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 4f } });
            doc.Regions.Add(new MapRegion { Name = "zone", Shape = new DiscShapeDoc { Radius = 3f } });
            doc.Terrain.Features.Add(new LakeFeatureDoc { Radius = 5f });

            List<OverlayDraw> list = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: false);

            Assert.Empty(list);
        }
    }
}
