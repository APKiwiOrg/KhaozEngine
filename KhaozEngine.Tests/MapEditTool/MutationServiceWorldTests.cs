using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for the world-shaping half of <see cref="MutationService"/>: terrain globals,
    /// terrain features, exclusions, scatter overrides, and region bake. Every mutation runs against
    /// <see cref="SampleDocs.SampleDoc"/> opened through a fresh session and holds the shared apply, validate,
    /// revert-on-error invariant. The scatter-dependent tests target a dry southern band whose scatter cell
    /// centres sit far enough (more than one jitter radius) inside the band edges that no generated point can
    /// jitter outside it, so a covering exclusion suppresses every point in that band with no edge leakage.</summary>
    public class MutationServiceWorldTests
    {
        // A dry southern band clear of the sample doc's lake, disc exclusion, and density override. Its edges are
        // offset off the 5-unit scatter grid so the outermost cell centres (multiples of 5, in [-80, 80] x
        // [-90, -70]) plus the layer's 1.6 jitter stay strictly inside the band. That lets the bake test assert a
        // covering rect exclusion drops the re-scatter to zero with no boundary points escaping.
        const float RectMinX = -82.5f, RectMinZ = -92.5f, RectMaxX = 82.5f, RectMaxZ = -67.5f;

        // A shape that fully contains the band plus any jitter, for the polygon-suppress and density-0 tests.
        const string CoverPolygonJson = "{\"type\":\"polygon\",\"points\":[[-100,-100],[100,-100],[100,-60],[-100,-60]]}";
        const string CoverRectJson = "{\"type\":\"rect\",\"minX\":-100,\"minZ\":-100,\"maxX\":100,\"maxZ\":-60}";

        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static (MapEditSession session, MutationService mutation) OpenSample(string dir)
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new MutationService(session));
        }

        static int TreesTotal(MapEditSession session)
            => new QueryService(session)
                .ScatterPreviewInRect("trees", RectMinX, RectMinZ, RectMaxX, RectMaxZ, maxResults: 100000).Total;

        // ---- DocJson helpers ----------------------------------------------------------------------------------

        [Fact]
        public void DocJson_FeatureToJson_RoundTripsThroughParse()
        {
            MapDocRegistry registry = MapDocRegistry.CreateDefault();

            MapFeature parsed = DocJson.ParseFeature(
                "{\"type\":\"lake\",\"centerX\":5,\"centerZ\":6,\"radius\":7,\"depth\":8}", registry);

            string json = DocJson.FeatureToJson(parsed, registry);
            Assert.Contains("\"type\":\"lake\"", json);

            LakeFeatureDoc back = Assert.IsType<LakeFeatureDoc>(DocJson.ParseFeature(json, registry));
            Assert.Equal(5f, back.CenterX);
            Assert.Equal(6f, back.CenterZ);
            Assert.Equal(7f, back.Radius);
            Assert.Equal(8f, back.Depth);
        }

        // ---- features -----------------------------------------------------------------------------------------

        [Fact]
        public void FeatureAdd_LakeJson_AppendsAndRebuildsField()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                TerrainField before = session.Field();
                float beforeHeight = before.SampleHeight(60f, 60f);

                MutationResult result = mutation.FeatureAdd(
                    "{\"type\":\"lake\",\"centerX\":60,\"centerZ\":60,\"radius\":20,\"depth\":8}");

                Assert.Equal("feature_add", result.Verb);
                Assert.True(result.WorldChanged);
                Assert.Equal(2, result.Index);

                LakeFeatureDoc lake = Assert.IsType<LakeFeatureDoc>(
                    session.WithDocument((doc, _) => doc.Terrain.Features[^1]));
                Assert.Equal(60f, lake.CenterX);
                Assert.Equal(20f, lake.Radius);
                Assert.Equal(3, session.WithDocument((doc, _) => doc.Terrain.Features.Count));

                TerrainField after = session.Field();
                Assert.NotSame(before, after);
                Assert.True(after.SampleHeight(60f, 60f) < beforeHeight,
                    "adding a lake at (60, 60) should lower the sampled ground there");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FeatureAdd_UnknownType_ThrowsPreciseMessage()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                JsonException ex = Assert.Throws<JsonException>(() =>
                    mutation.FeatureAdd("{\"type\":\"volcano\",\"centerX\":0,\"centerZ\":0}"));

                Assert.Contains("Unknown terrain feature type 'volcano'", ex.Message);
                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FeatureEdit_Reorder_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                // Sample features fold in list order: lake at index 0, flatten at index 1.
                Assert.Equal(new[] { "lake", "flatten" }, session.Summary().FeatureTypes.ToArray());

                MutationResult edit = mutation.FeatureEdit(0,
                    "{\"type\":\"lake\",\"centerX\":34,\"centerZ\":-14,\"radius\":25,\"depth\":10}");
                Assert.Equal("feature_edit", edit.Verb);
                Assert.True(edit.WorldChanged);
                Assert.Equal(0, edit.Index);
                Assert.Equal(new[] { "lake", "flatten" }, session.Summary().FeatureTypes.ToArray());
                Assert.Equal(25f, session.WithDocument((doc, _) => ((LakeFeatureDoc)doc.Terrain.Features[0]).Radius));

                MutationResult reorder = mutation.FeatureReorder(0, 1);
                Assert.Equal("feature_reorder", reorder.Verb);
                // Fold order flips: moving the lake to the back makes the flatten fold first.
                Assert.Equal(new[] { "flatten", "lake" }, session.Summary().FeatureTypes.ToArray());

                MutationResult remove = mutation.FeatureRemove(1);
                Assert.Equal("feature_remove", remove.Verb);
                Assert.Equal(new[] { "flatten" }, session.Summary().FeatureTypes.ToArray());

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void FeatureEdit_IndexOutOfRange_ThrowsArgumentException()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                    mutation.FeatureEdit(99, "{\"type\":\"lake\",\"centerX\":0,\"centerZ\":0,\"radius\":5,\"depth\":2}"));
                Assert.Contains("out of range", ex.Message);

                // The same up-front guard protects remove and both reorder endpoints.
                Assert.Throws<ArgumentException>(() => mutation.FeatureRemove(99));
                Assert.Throws<ArgumentException>(() => mutation.FeatureReorder(0, 99));
                Assert.Throws<ArgumentException>(() => mutation.FeatureReorder(99, 0));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- terrain globals ----------------------------------------------------------------------------------

        [Fact]
        public void TerrainEdit_WaterLevelAndSeed_ApplyAndInvalidateField()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                TerrainField before = session.Field();

                MutationResult result = mutation.TerrainEdit(waterLevel: 3f, seed: 12345);

                Assert.Equal("terrain_edit", result.Verb);
                Assert.True(result.WorldChanged);
                Assert.Equal(12345, session.WithDocument((doc, _) => doc.Terrain.Seed));
                Assert.Equal(3f, session.WithDocument((doc, _) => doc.Terrain.WaterLevel));

                TerrainField after = session.Field();
                Assert.NotSame(before, after);
                Assert.Equal(3f, after.WaterLevel);

                // The seed drives the height noise, so at least one sampled point must change.
                (float, float)[] probes = { (50f, -70f), (10f, 10f), (-40f, -40f), (70f, -80f) };
                Assert.Contains(probes, p => before.SampleHeight(p.Item1, p.Item2) != after.SampleHeight(p.Item1, p.Item2));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- exclusions ---------------------------------------------------------------------------------------

        [Fact]
        public void ExclusionAdd_PolygonShape_SuppressesScatterInside()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                int baseline = TreesTotal(session);
                Assert.True(baseline > 0, "expected the sample doc to scatter trees in the test band");

                MutationResult result = mutation.ExclusionAdd(CoverPolygonJson);
                Assert.Equal("exclusion_add", result.Verb);
                Assert.True(result.WorldChanged);
                Assert.IsType<PolygonShapeDoc>(session.WithDocument((doc, _) => doc.Exclusions[^1].Shape));

                Assert.Equal(0, TreesTotal(session));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ExclusionAdd_UnknownLayerFilter_RejectedAndReverted()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                int before = session.WithDocument((doc, _) => doc.Exclusions.Count);
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ExclusionAdd("{\"type\":\"disc\",\"centerX\":0,\"centerZ\":0,\"radius\":5}",
                        layers: new[] { "ghost" }));

                Assert.StartsWith("mutation rejected:", ex.Message);
                Assert.Contains("unknown scatter layer 'ghost'", ex.Message);
                Assert.Equal(before, session.WithDocument((doc, _) => doc.Exclusions.Count));
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- scatter overrides --------------------------------------------------------------------------------

        [Fact]
        public void ScatterOverrideAdd_Edit_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                int baseline = TreesTotal(session);
                Assert.True(baseline > 0, "expected the sample doc to scatter trees in the test band");

                // A density-0 override over the band empties the preview there, and "rock_a:2" parses to a
                // weighted kind.
                MutationResult add = mutation.ScatterOverrideAdd(CoverRectJson, densityMultiplier: 0f,
                    kinds: new[] { "rock_a:2" }, layers: new[] { "trees" });
                Assert.Equal("scatter_override_add", add.Verb);
                Assert.True(add.WorldChanged);
                int index = Assert.IsType<int>(add.Index);

                MapScatterOverrideDoc parsed = session.WithDocument((doc, _) => doc.ScatterOverrides[index]);
                MapPropKind kind = Assert.Single(parsed.Kinds!);
                Assert.Equal("rock_a", kind.Id);
                Assert.Equal(2f, kind.Weight);

                Assert.Equal(0, TreesTotal(session));

                // Editing the multiplier back to 1 restores the full preview count.
                MutationResult edit = mutation.ScatterOverrideEdit(index, densityMultiplier: 1f);
                Assert.Equal("scatter_override_edit", edit.Verb);
                Assert.Equal(baseline, TreesTotal(session));

                MutationResult remove = mutation.ScatterOverrideRemove(index);
                Assert.Equal("scatter_override_remove", remove.Verb);
                Assert.Equal(baseline, TreesTotal(session));
                Assert.Equal(1, session.WithDocument((doc, _) => doc.ScatterOverrides.Count));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- bake ---------------------------------------------------------------------------------------------

        [Fact]
        public void BakeRegion_MatchesBakeRegionCommandSemantics()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                var query = new QueryService(session);

                // Capture the scatter the bake will freeze, in generation order, before it runs.
                ScatterPreviewResult pre = query.ScatterPreviewInRect("trees", RectMinX, RectMinZ, RectMaxX, RectMaxZ,
                    maxResults: 100000);
                Assert.True(pre.Total > 0, "expected the sample doc to scatter trees in the bake region");

                BakeResult result = mutation.BakeRegion("trees", RectMinX, RectMinZ, RectMaxX, RectMaxZ);

                Assert.Equal("trees", result.Layer);
                Assert.Equal(pre.Total, result.BakedCount);
                Assert.Equal(result.BakedCount, result.BakedIds.Count);
                Assert.True(result.ExclusionAdded);
                Assert.All(result.BakedIds, id => Assert.StartsWith("baked-trees-", id));

                // Every baked placement carries the "baked" tag and an explicit Y equal to the pre-bake scatter Y.
                var baked = session.WithDocument((doc, _) =>
                    doc.Placements.Where(p => p.Tags.Contains("baked")).ToList());
                Assert.Equal(pre.Entries.Count, baked.Count);
                for (int i = 0; i < baked.Count; i++)
                {
                    Assert.Contains("baked", baked[i].Tags);
                    Assert.NotNull(baked[i].Y);
                    Assert.Equal(pre.Entries[i].X, baked[i].X);
                    Assert.Equal(pre.Entries[i].Z, baked[i].Z);
                    Assert.Equal(pre.Entries[i].Y, baked[i].Y!.Value);
                }

                // A covering rect exclusion limited to the layer was added.
                MapExclusion cover = session.WithDocument((doc, _) =>
                    doc.Exclusions.Single(e => e.Layers is { Count: 1 } && e.Layers[0] == "trees"));
                RectShapeDoc rect = Assert.IsType<RectShapeDoc>(cover.Shape);
                Assert.Equal(RectMinX, rect.MinX);
                Assert.Equal(RectMaxZ, rect.MaxZ);

                // Re-running the scatter preview over the region now returns zero (the exclusion suppresses it).
                Assert.Equal(0, query.ScatterPreviewInRect("trees", RectMinX, RectMinZ, RectMaxX, RectMaxZ,
                    maxResults: 100000).Total);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void BakeRegion_UnknownLayer_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                bool dirtyBefore = session.IsDirty;

                MapDocumentException ex = Assert.Throws<MapDocumentException>(() =>
                    mutation.BakeRegion("ghost_layer", RectMinX, RectMinZ, RectMaxX, RectMaxZ));

                Assert.Contains("ghost_layer", ex.Message);
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
