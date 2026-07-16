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

            List<OverlayDraw> list = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: new EditorVisibility());

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
        public void OverlayDrawList_CoversScatterOverrides_RespectsGroupAndElementHides()
        {
            MapDocument doc = ValidDoc("overrides");
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new DiscShapeDoc { CenterX = 2f, CenterZ = 3f, Radius = 6f } });
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new RectShapeDoc { MinX = -4f, MinZ = -2f, MaxX = 8f, MaxZ = 6f } });
            // An exclusion too, so the override color can be checked distinct from the exclusion color.
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 20f, CenterZ = 20f, Radius = 4f } });

            // ComputeOverlayDrawList hands back a shared cached list (rebuilt, not reallocated, on every call - see
            // its doc comment), so every result below that must survive past a LATER call in this test is
            // materialized with ToList() right where it is captured, the same discipline TreeView.VisibleRows'
            // own callers use.
            List<OverlayDraw> list = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: new EditorVisibility()).ToList();

            // Both overrides draw: a disc and a rect, tagged with the scatter-override category.
            List<OverlayDraw> overrides = list.Where(o => o.Category == OverlayCategory.ScatterOverride).ToList();
            Assert.Equal(2, overrides.Count);
            OverlayDraw disc = overrides.First(o => o.Shape == OverlayShape.Disc);
            Near(2f, disc.Center.X);
            Near(3f, disc.Center.Z);
            Assert.True(disc.Center.Y > Ground && disc.Center.Y < Ground + 1f, "center lifted a small epsilon above ground");
            Near(6f, disc.Radius);
            OverlayDraw rect = overrides.First(o => o.Shape == OverlayShape.Rect);
            Near(2f, rect.Center.X);        // (-4 + 8) / 2
            Near(2f, rect.Center.Z);        // (-2 + 6) / 2
            Near(6f, rect.HalfExtents.X);
            Near(4f, rect.HalfExtents.Y);

            // Orange fill (R > G > B), distinct from the exclusion's red.
            OverlayDraw exclusion = list.First(o => o.Category == OverlayCategory.Exclusion);
            Assert.True(disc.Color.R > disc.Color.G && disc.Color.G > disc.Color.B, "scatter override fill is orange");
            Assert.NotEqual(exclusion.Color, disc.Color);

            // Group toggle: hiding the whole ScatterOverrides group drops every override overlay, others stay.
            var groupOff = new EditorVisibility();
            groupOff.SetGroup(VisibilityGroup.ScatterOverrides, false);
            List<OverlayDraw> withGroupOff = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: groupOff).ToList();
            Assert.DoesNotContain(withGroupOff, o => o.Category == OverlayCategory.ScatterOverride);
            Assert.Single(withGroupOff, o => o.Category == OverlayCategory.Exclusion);

            // Per-element hide: hiding index 0 drops only the first override, leaving index 1 (the rect).
            var elementHidden = new EditorVisibility();
            elementHidden.SetElementHidden(SelectionKind.ScatterOverride, "0", true);
            List<OverlayDraw> withElementHidden = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: elementHidden).ToList();
            List<OverlayDraw> remaining = withElementHidden.Where(o => o.Category == OverlayCategory.ScatterOverride).ToList();
            Assert.Single(remaining);
            Assert.Equal(OverlayShape.Rect, remaining[0].Shape);   // the surviving index-1 rect

            // Selection highlight is index-keyed: selecting override 1 brightens only that overlay.
            List<OverlayDraw> withSel = MapEditorScene.ComputeOverlayDrawList(
                doc, Select(SelectionKind.ScatterOverride, "1"), FlatGround, showOverlays: true, visibility: new EditorVisibility()).ToList();
            List<OverlayDraw> selOverrides = withSel.Where(o => o.Category == OverlayCategory.ScatterOverride).ToList();
            Assert.False(selOverrides.First(o => o.Shape == OverlayShape.Disc).Selected);   // index 0
            Assert.True(selOverrides.First(o => o.Shape == OverlayShape.Rect).Selected);    // index 1, selected
        }

        [Fact]
        public void OverlayDrawList_HighlightsSelection()
        {
            MapDocument doc = ValidDoc("selection");
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 4f } });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 9f, CenterZ = 0f, Radius = 4f } });
            doc.Regions.Add(new MapRegion { Name = "zone", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 9f, Radius = 3f } });
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 5f, CenterZ = 5f, Radius = 3f, Depth = 1f });

            // Nothing selected: no overlay is flagged, and both exclusions share the same base color. Every value
            // kept past a later ComputeOverlayDrawList call below is extracted here (struct copies, or a Color
            // field), never the shared list reference itself - see the method's doc comment on the reuse contract.
            List<OverlayDraw> baseline = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: new EditorVisibility());
            Assert.All(baseline, o => Assert.False(o.Selected));
            Color baseColor = baseline[0].Color;
            Assert.Equal(baseColor, baseline[1].Color);
            Color featureBaseColor = baseline.First(o => o.Category == OverlayCategory.Feature).Color;

            // Selecting exclusion index 1 brightens only that overlay; its unselected sibling is untouched.
            List<OverlayDraw> withSel = MapEditorScene.ComputeOverlayDrawList(
                doc, Select(SelectionKind.Exclusion, "1"), FlatGround, showOverlays: true, visibility: new EditorVisibility());
            Assert.False(withSel[0].Selected);
            Assert.True(withSel[1].Selected);
            Assert.Equal(baseColor, withSel[0].Color);
            Assert.True(withSel[1].Color.R > baseColor.R, "selected overlay reads brighter");
            Assert.True(withSel[1].Color.G >= baseColor.G && withSel[1].Color.B >= baseColor.B);

            // Region selection is keyed by name and highlights only the region overlay.
            List<OverlayDraw> regionSel = MapEditorScene.ComputeOverlayDrawList(
                doc, Select(SelectionKind.Region, "zone"), FlatGround, showOverlays: true, visibility: new EditorVisibility());
            OverlayDraw region = regionSel.First(o => o.Category == OverlayCategory.Region);
            Assert.True(region.Selected);
            Assert.All(regionSel.Where(o => o.Category == OverlayCategory.Exclusion), o => Assert.False(o.Selected));

            // Feature selection is index-keyed like exclusions, and its marker goes through the same Tint helper:
            // the selected feature marker brightens while an unselected one would not (only one feature here).
            List<OverlayDraw> featureSel = MapEditorScene.ComputeOverlayDrawList(
                doc, Select(SelectionKind.Feature, "0"), FlatGround, showOverlays: true, visibility: new EditorVisibility());
            OverlayDraw feature = featureSel.First(o => o.Category == OverlayCategory.Feature);
            Assert.True(feature.Selected);
            Assert.True(feature.Color.R > featureBaseColor.R, "selected feature marker reads brighter");
            Assert.True(feature.Color.G >= featureBaseColor.G && feature.Color.B >= featureBaseColor.B);
        }

        // ComputeOverlayDrawList hands back a shared cached list, rebuilt (not reallocated) on every call: the
        // SAME instance is returned each time (no per-call allocation), so a caller that wants to keep a result
        // across a LATER call must materialize it (ToList/ToArray) first, or the earlier reference's content
        // changes out from under it - the TreeView.VisibleRows precedent (see
        // VisibleRows_IsASharedListThatMustBeMaterializedBeforeTheNextCall in TreeViewTests).
        [Fact]
        public void ComputeOverlayDrawList_IsASharedListThatMustBeMaterializedBeforeTheNextCall()
        {
            MapDocument doc = ValidDoc("reuse");
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 4f } });

            List<OverlayDraw> first = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: new EditorVisibility());
            Assert.Single(first);

            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 5f } });
            List<OverlayDraw> second = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: true, visibility: new EditorVisibility());

            Assert.Same(first, second);       // the exact same list instance, not a fresh allocation
            Assert.Equal(2, first.Count);      // and `first`'s content changed out from under the earlier reference
            Assert.Equal(2, second.Count);
        }

        [Fact]
        public void OverlayDrawList_ShowOverlaysFalse_ReturnsEmpty()
        {
            MapDocument doc = ValidDoc("hidden");
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { Radius = 4f } });
            doc.Regions.Add(new MapRegion { Name = "zone", Shape = new DiscShapeDoc { Radius = 3f } });
            doc.Terrain.Features.Add(new LakeFeatureDoc { Radius = 5f });

            List<OverlayDraw> list = MapEditorScene.ComputeOverlayDrawList(doc, Nothing(), FlatGround, showOverlays: false, visibility: new EditorVisibility());

            Assert.Empty(list);
        }
    }
}
