using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for <see cref="MutationService.ElementDuplicate"/>, the MCP verb behind
    /// <c>element_duplicate</c> (decision 10). Every kind's clone + fresh-identity scheme is asserted to match
    /// <c>KhaozEngine.MapEditor.EditorToolController.DuplicateSelection</c> exactly (same prefixes, same +2/+2
    /// offset, same named-vs-unnamed uniquify rule), since the two surfaces must stay indistinguishable to a
    /// caller. All mutations run against <see cref="SampleDocs.SampleDoc"/> opened through a fresh session.</summary>
    public class MutationServiceDuplicateTests
    {
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

        [Fact]
        public void ElementDuplicate_Placement_ClonesWithNewIdAndOffset()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("placement", id: "inn");

                Assert.Equal("element_duplicate", result.Verb);
                Assert.False(result.WorldChanged);
                Assert.Equal("placement-1", result.Id);

                MapPlacement clone = session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "placement-1"));
                Assert.Equal("building_inn", clone.Kind);
                Assert.Equal(-28f, clone.X);
                Assert.Equal(22f, clone.Z);
                Assert.Equal(1.2f, clone.Yaw);

                // The source placement is untouched.
                MapPlacement original = session.WithDocument((doc, _) => doc.Placements.Single(p => p.Id == "inn"));
                Assert.Equal(-30f, original.X);
                Assert.Equal(20f, original.Z);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_Spawn_ClonesWithNewIdAndOffset()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("spawn", id: "wolf-1");

                Assert.Equal("spawn-1", result.Id);
                MapSpawn clone = session.WithDocument((doc, _) => doc.Spawns.Single(s => s.Id == "spawn-1"));
                Assert.Equal("wolf", clone.ArchetypeId);
                Assert.Equal(22f, clone.X);
                Assert.Equal(22f, clone.Z);
                Assert.True(clone.Enabled);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_PlayerSpawn_ClonesWithNewIdAndOffset()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                mutation.PlayerSpawnAdd(5f, 6f, yaw: 0.5f, id: "start");

                MutationResult result = mutation.ElementDuplicate("player_spawn", id: "start");

                Assert.Equal("element_duplicate", result.Verb);
                Assert.False(result.WorldChanged);
                Assert.Equal("player-1", result.Id);

                MapPlayerSpawn clone = session.WithDocument((doc, _) => doc.PlayerSpawns.Single(s => s.Id == "player-1"));
                Assert.Equal(7f, clone.X);
                Assert.Equal(8f, clone.Z);
                Assert.Equal(0.5f, clone.Yaw);
                Assert.True(clone.Enabled);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_Region_FreshNameAndOffsetShape()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("region", id: "town");

                Assert.Equal("region-1", result.Id);
                MapRegion clone = session.WithDocument((doc, _) => doc.Regions.Single(r => r.Name == "region-1"));
                var disc = Assert.IsType<DiscShapeDoc>(clone.Shape);
                Assert.Equal(-30f, disc.CenterX);
                Assert.Equal(24f, disc.CenterZ);
                Assert.Equal(34f, disc.Radius);
                Assert.Contains("safe", clone.Tags);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_Feature_NamedGetsCopySuffix_UnnamedStaysUnnamed()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                // idx0 (lake) is unnamed in SampleDoc: its clone must stay unnamed too.
                MutationResult unnamedDup = mutation.ElementDuplicate("feature", index: 0);
                Assert.Equal("element_duplicate", unnamedDup.Verb);
                Assert.True(unnamedDup.WorldChanged);
                Assert.Equal(2, unnamedDup.Index);
                MapFeature unnamedClone = session.WithDocument((doc, _) => doc.Terrain.Features[2]);
                Assert.Null(unnamedClone.Name);
                var lakeClone = Assert.IsType<LakeFeatureDoc>(unnamedClone);
                Assert.Equal(36f, lakeClone.CenterX);
                Assert.Equal(-12f, lakeClone.CenterZ);

                // Name idx1 (flatten), then duplicate it: the clone gets a uniquified "<name>-copy-1".
                mutation.FeatureRename(1, "central-flat");
                MutationResult namedDup = mutation.ElementDuplicate("feature", index: 1);
                Assert.Equal(3, namedDup.Index);
                MapFeature namedClone = session.WithDocument((doc, _) => doc.Terrain.Features[3]);
                Assert.Equal("central-flat-copy-1", namedClone.Name);
                var flattenClone = Assert.IsType<FlattenFeatureDoc>(namedClone);
                Assert.Equal(-30f, flattenClone.CenterX);
                Assert.Equal(24f, flattenClone.CenterZ);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_Exclusion_UnnamedStaysUnnamed_ShapeOffset()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("exclusion", index: 0);

                Assert.True(result.WorldChanged);
                Assert.Equal(1, result.Index);
                MapExclusion clone = session.WithDocument((doc, _) => doc.Exclusions[1]);
                Assert.Null(clone.Name);
                var disc = Assert.IsType<DiscShapeDoc>(clone.Shape);
                Assert.Equal(-30f, disc.CenterX);
                Assert.Equal(24f, disc.CenterZ);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_ScatterOverride_UnnamedStaysUnnamed_ThenNamedGetsCopySuffix()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                // SampleDoc's one scatter override (index 0) is unnamed: its clone must stay unnamed too, and its
                // shape/values are offset-cloned like the exclusion case above.
                MutationResult unnamedDup = mutation.ElementDuplicate("scatter_override", index: 0);
                Assert.Equal("element_duplicate", unnamedDup.Verb);
                Assert.True(unnamedDup.WorldChanged);
                Assert.Equal(1, unnamedDup.Index);

                MapScatterOverrideDoc unnamedClone = session.WithDocument((doc, _) => doc.ScatterOverrides[1]);
                Assert.Null(unnamedClone.Name);
                var rect = Assert.IsType<RectShapeDoc>(unnamedClone.Shape);
                Assert.Equal(2f, rect.MinX);
                Assert.Equal(2f, rect.MinZ);
                Assert.Equal(0.5f, unnamedClone.DensityMultiplier);
                Assert.Equal(new[] { "trees" }, unnamedClone.Layers);

                // Name a new override, then duplicate it: the clone gets a uniquified "<name>-copy-1", and its
                // Kinds list is a fresh copy (each MapPropKind element rebuilt, not shared) rather than aliasing
                // the source.
                mutation.ScatterOverrideAdd("{\"type\":\"disc\",\"centerX\":0,\"centerZ\":0,\"radius\":5}",
                    kinds: new[] { "rock_a:2" });
                mutation.ScatterOverrideRename(2, "no-scatter-camp");
                MutationResult namedDup = mutation.ElementDuplicate("scatter_override", index: 2);
                Assert.Equal(3, namedDup.Index);

                MapScatterOverrideDoc namedClone = session.WithDocument((doc, _) => doc.ScatterOverrides[3]);
                Assert.Equal("no-scatter-camp-copy-1", namedClone.Name);
                MapPropKind cloneKind = Assert.Single(namedClone.Kinds!);
                Assert.Equal("rock_a", cloneKind.Id);
                Assert.Equal(2f, cloneKind.Weight);

                MapScatterOverrideDoc namedSource = session.WithDocument((doc, _) => doc.ScatterOverrides[2]);
                Assert.NotSame(namedSource.Kinds, namedClone.Kinds);
                Assert.NotSame(namedSource.Kinds![0], namedClone.Kinds![0]);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_BiomeBand_VerbatimClone()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("biome_band", index: 0);

                Assert.Equal("element_duplicate", result.Verb);
                Assert.True(result.WorldChanged);
                Assert.Equal(1, result.Index);

                MapBiomeBand clone = session.WithDocument((doc, _) => doc.Terrain.Biomes[1]);
                Assert.Equal(BiomeId.Marsh, clone.Biome);
                Assert.Equal(1.5f, clone.BaseHeight);
                Assert.Equal(1.2f, clone.HillAmplitude);
                Assert.Null(clone.Start);
                Assert.Null(clone.End);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_ScatterLayer_NameCopy_CompanionsNotRetargeted()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("scatter_layer", id: "trees");

                Assert.Equal("trees-copy-1", result.Id);
                Assert.True(result.WorldChanged);

                MapScatterLayer clone = session.WithDocument((doc, _) => doc.ScatterLayers.Single(l => l.Name == "trees-copy-1"));
                Assert.Equal(0x52424E, clone.Seed);
                Assert.Equal(5f, clone.CellSize);
                Assert.Single(clone.Rules);
                Assert.Equal(0.35f, clone.Rules[0].Density);
                Assert.Equal("pine_a", clone.Rules[0].Kinds[0].Id);

                // The original layer survives, and no companion layer gets cascaded onto the new copy: a
                // duplicate is a copy, not a rename, so nothing should retarget.
                Assert.True(session.WithDocument((doc, _) => doc.ScatterLayers.Any(l => l.Name == "trees")));
                MapCompanionLayer companion = session.WithDocument((doc, _) => doc.CompanionLayers.Single(l => l.Name == "understory"));
                Assert.Equal("trees", companion.HostLayer);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ElementDuplicate_CompanionLayer_NameCopy()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.ElementDuplicate("companion_layer", id: "understory");

                Assert.Equal("understory-copy-1", result.Id);
                Assert.True(result.WorldChanged);

                MapCompanionLayer clone = session.WithDocument((doc, _) => doc.CompanionLayers.Single(l => l.Name == "understory-copy-1"));
                Assert.Equal("trees", clone.HostLayer);
                Assert.Contains("pine_a", clone.HostKinds);
                Assert.Single(clone.Kinds);
                Assert.Equal("fern", clone.Kinds[0].Id);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        /// <summary>Every ref-shaped failure the choke point must reject cleanly: an unknown kind (including
        /// "terrain", intentionally absent since it is a document singleton), a missing ref, the wrong ref kind
        /// for that element (an index for an id-keyed kind or vice versa), an out-of-range index, and an
        /// unresolved id. None of these may silently no-op: each throws, and the document is left exactly as it
        /// was (the binding review constraint that the MCP verb never reports a silent success).</summary>
        [Fact]
        public void ElementDuplicate_UnknownKindOrRef_FailsClean()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));

                ArgumentException unknownKind = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("waypoint", id: "inn"));
                Assert.Contains("Unknown element kind 'waypoint'", unknownKind.Message);

                ArgumentException terrainKind = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("terrain", id: "anything"));
                Assert.Contains("Unknown element kind 'terrain'", terrainKind.Message);

                ArgumentException missingId = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("placement"));
                Assert.Contains("requires id", missingId.Message);

                ArgumentException wrongRefIsIndex = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("placement", index: 0));
                Assert.Contains("addressed by id, not index", wrongRefIsIndex.Message);

                ArgumentException missingIndex = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("feature"));
                Assert.Contains("requires index", missingIndex.Message);

                ArgumentException wrongRefIsId = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("feature", id: "lake"));
                Assert.Contains("addressed by index, not id", wrongRefIsId.Message);

                ArgumentException outOfRange = Assert.Throws<ArgumentException>(() =>
                    mutation.ElementDuplicate("feature", index: 99));
                Assert.Contains("out of range", outOfRange.Message);

                InvalidOperationException unresolvedId = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ElementDuplicate("placement", id: "does-not-exist"));
                Assert.Contains("No placement with id 'does-not-exist'", unresolvedId.Message);

                string after = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                Assert.Equal(before, after);
                Assert.False(session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
