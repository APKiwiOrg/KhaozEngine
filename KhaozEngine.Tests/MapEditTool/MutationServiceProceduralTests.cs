using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Terrain;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless tests for the Task 5 verb surface added to <see cref="MutationService"/> and
    /// <see cref="QueryService"/>: feature/exclusion rename, exclusion layer targeting, the widened terrain-scalar
    /// edit, the biome band triad, the scatter/companion layer triads plus their rename verbs, and the
    /// <see cref="QueryService.ProceduralInfo"/> read path, plus the Task 6 companion host-kinds mismatch note on
    /// companion_layer_add/edit and the computed <see cref="CompanionLayerInfo.HostKindsMatchHost"/> flag, plus
    /// <see cref="MutationService.ScatterOverrideRename"/> (the scatter-override-overrides batch): unlike
    /// <see cref="MutationService.ExclusionRename"/>'s empty-only convention, both null and empty clear a scatter
    /// override's name back to unnamed. Every mutation runs against <see cref="SampleDocs.SampleDoc"/> opened
    /// through a fresh session and holds the shared apply, validate, revert-on-error invariant the rest of the
    /// suite already exercises.</summary>
    public class MutationServiceProceduralTests
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

        // ---- feature / exclusion rename ------------------------------------------------------------------------

        [Fact]
        public void FeatureRename_RoundTrip_ThenDuplicateRejected()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult rename = mutation.FeatureRename(0, "big-lake");
                Assert.Equal("feature_rename", rename.Verb);
                Assert.False(rename.WorldChanged);
                Assert.Equal(0, rename.Index);
                Assert.Equal("big-lake", session.WithDocument((doc, _) => doc.Terrain.Features[0].Name));

                mutation.FeatureRename(1, "flat-zone");
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.FeatureRename(1, "big-lake"));
                Assert.Contains("already exists", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);

                // Clearing back to unnamed is legal.
                mutation.FeatureRename(0, "");
                Assert.Null(session.WithDocument((doc, _) => doc.Terrain.Features[0].Name));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ExclusionRename_RoundTrip_ThenDuplicateRejected()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                mutation.ExclusionAdd("{\"type\":\"disc\",\"centerX\":10,\"centerZ\":10,\"radius\":5}");

                MutationResult rename = mutation.ExclusionRename(0, "no-scatter-camp");
                Assert.Equal("exclusion_rename", rename.Verb);
                Assert.False(rename.WorldChanged);
                Assert.Equal(0, rename.Index);
                Assert.Equal("no-scatter-camp", session.WithDocument((doc, _) => doc.Exclusions[0].Name));

                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ExclusionRename(1, "no-scatter-camp"));
                Assert.Contains("already exists", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);

                mutation.ExclusionRename(0, "");
                Assert.Null(session.WithDocument((doc, _) => doc.Exclusions[0].Name));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ExclusionSetLayers_NullVsExplicitList_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                Assert.Null(session.WithDocument((doc, _) => doc.Exclusions[0].Layers));

                MutationResult toExplicit = mutation.ExclusionSetLayers(0, new[] { "trees" });
                Assert.Equal("exclusion_set_layers", toExplicit.Verb);
                Assert.True(toExplicit.WorldChanged);
                Assert.Equal(0, toExplicit.Index);
                Assert.Equal(new[] { "trees" }, session.WithDocument((doc, _) => doc.Exclusions[0].Layers));

                mutation.ExclusionSetLayers(0, Array.Empty<string>());
                Assert.Empty(session.WithDocument((doc, _) => doc.Exclusions[0].Layers)!);

                mutation.ExclusionSetLayers(0, null);
                Assert.Null(session.WithDocument((doc, _) => doc.Exclusions[0].Layers));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- scatter override rename ---------------------------------------------------------------------------

        [Fact]
        public void ScatterOverrideRename_NullAndEmptyBothClear_ThenDuplicateRejected()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                mutation.ScatterOverrideAdd("{\"type\":\"disc\",\"centerX\":10,\"centerZ\":10,\"radius\":5}");

                MutationResult rename = mutation.ScatterOverrideRename(0, "no-scatter-camp");
                Assert.Equal("scatter_override_rename", rename.Verb);
                Assert.False(rename.WorldChanged);
                Assert.Equal(0, rename.Index);
                Assert.Equal("no-scatter-camp", session.WithDocument((doc, _) => doc.ScatterOverrides[0].Name));

                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ScatterOverrideRename(1, "no-scatter-camp"));
                Assert.Contains("already exists", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);

                // Unlike RenameExclusionCommand's empty-only convention, RenameScatterOverrideCommand treats both
                // null and empty as clearing back to unnamed.
                mutation.ScatterOverrideRename(0, "");
                Assert.Null(session.WithDocument((doc, _) => doc.ScatterOverrides[0].Name));

                mutation.ScatterOverrideRename(1, "temp-name");
                mutation.ScatterOverrideRename(1, null);
                Assert.Null(session.WithDocument((doc, _) => doc.ScatterOverrides[1].Name));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- terrain globals (widened) --------------------------------------------------------------------------

        [Fact]
        public void TerrainEdit_WidenedScalars_AllParamsRoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult result = mutation.TerrainEdit(
                    waterLevel: 2f, seed: 999, biomeBlend: 30f, gentleFrequency: 0.05f,
                    gentleAmplitude: 2.5f, detailFrequency: 0.08f, detailOctaves: 6);

                Assert.Equal("terrain_edit", result.Verb);
                Assert.True(result.WorldChanged);

                session.WithDocument((doc, _) =>
                {
                    MapTerrain t = doc.Terrain;
                    Assert.Equal(2f, t.WaterLevel);
                    Assert.Equal(999, t.Seed);
                    Assert.Equal(30f, t.BiomeBlend);
                    Assert.Equal(0.05f, t.GentleFrequency);
                    Assert.Equal(2.5f, t.GentleAmplitude);
                    Assert.Equal(0.08f, t.DetailFrequency);
                    Assert.Equal(6, t.DetailOctaves);
                    return 0;
                });

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void TerrainEdit_NoParams_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                Assert.Throws<ArgumentException>(() => mutation.TerrainEdit());
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- biome bands ------------------------------------------------------------------------------------

        [Fact]
        public void BiomeBandAdd_Edit_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                Assert.Equal(1, session.WithDocument((doc, _) => doc.Terrain.Biomes.Count));

                MutationResult add = mutation.BiomeBandAdd(start: 5f, end: 40f, biome: "Forest", baseHeight: 3f, hillAmplitude: 1f);
                Assert.Equal("biome_band_add", add.Verb);
                Assert.True(add.WorldChanged);
                Assert.Equal(1, add.Index);

                MapBiomeBand added = session.WithDocument((doc, _) => doc.Terrain.Biomes[1]);
                Assert.Equal(5f, added.Start);
                Assert.Equal(40f, added.End);
                Assert.Equal(BiomeId.Forest, added.Biome);
                Assert.Equal(3f, added.BaseHeight);
                Assert.Equal(1f, added.HillAmplitude);

                MutationResult edit = mutation.BiomeBandEdit(1, start: null, end: null, biome: "Desert",
                    baseHeight: 10f, hillAmplitude: 0.5f);
                Assert.Equal("biome_band_edit", edit.Verb);
                Assert.Equal(1, edit.Index);

                MapBiomeBand edited = session.WithDocument((doc, _) => doc.Terrain.Biomes[1]);
                Assert.Null(edited.Start);
                Assert.Null(edited.End);
                Assert.Equal(BiomeId.Desert, edited.Biome);
                Assert.Equal(10f, edited.BaseHeight);
                Assert.Equal(0.5f, edited.HillAmplitude);

                MutationResult remove = mutation.BiomeBandRemove(1);
                Assert.Equal("biome_band_remove", remove.Verb);
                Assert.Equal(1, session.WithDocument((doc, _) => doc.Terrain.Biomes.Count));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void BiomeBandEdit_IndexOutOfRange_ThrowsArgumentException()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                    mutation.BiomeBandEdit(99, null, null, "Meadow", 0f, 0f));
                Assert.Contains("out of range", ex.Message);

                Assert.Throws<ArgumentException>(() => mutation.BiomeBandRemove(99));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void BiomeBandAdd_UnknownBiome_ThrowsArgumentException()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                Assert.Throws<ArgumentException>(() => mutation.BiomeBandAdd(biome: "Volcano"));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- scatter layers -----------------------------------------------------------------------------------

        [Fact]
        public void ScatterLayerAdd_Edit_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult add = mutation.ScatterLayerAdd("shrubs", seed: 42, cellSize: 3f, jitter: 0.5f,
                    maxHeight: 20f, scaleMin: 0.5f, scaleMax: 1.0f);
                Assert.Equal("scatter_layer_add", add.Verb);
                Assert.True(add.WorldChanged);

                MapScatterLayer added = session.WithDocument((doc, _) => doc.ScatterLayers.Single(l => l.Name == "shrubs"));
                Assert.Equal(42, added.Seed);
                Assert.Equal(3f, added.CellSize);
                Assert.Equal(0.5f, added.Jitter);
                Assert.Equal(20f, added.MaxHeight);
                Assert.Equal(0.5f, added.ScaleMin);
                Assert.Equal(1.0f, added.ScaleMax);
                Assert.Empty(added.Rules);

                MutationResult edit = mutation.ScatterLayerEdit("shrubs", seed: 7, cellSize: 4f);
                Assert.Equal("scatter_layer_edit", edit.Verb);
                MapScatterLayer edited = session.WithDocument((doc, _) => doc.ScatterLayers.Single(l => l.Name == "shrubs"));
                Assert.Equal(7, edited.Seed);
                Assert.Equal(4f, edited.CellSize);
                // Untouched fields are preserved by the read-modify pattern.
                Assert.Equal(0.5f, edited.Jitter);
                Assert.Equal(20f, edited.MaxHeight);

                mutation.ScatterLayerEdit("shrubs", clearMaxHeight: true);
                Assert.Null(session.WithDocument((doc, _) => doc.ScatterLayers.Single(l => l.Name == "shrubs").MaxHeight));

                MutationResult remove = mutation.ScatterLayerRemove("shrubs");
                Assert.Equal("scatter_layer_remove", remove.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.ScatterLayers.Any(l => l.Name == "shrubs")));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterLayerAdd_DuplicateName_RejectedAndReverted()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ScatterLayerAdd("trees"));
                Assert.Contains("already exists", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterLayerRename_CascadesHostLayerAndFilters()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult rename = mutation.ScatterLayerRename("trees", "forest");
                Assert.Equal("scatter_layer_rename", rename.Verb);
                Assert.False(rename.WorldChanged);
                Assert.Equal("forest", rename.Id);

                session.WithDocument((doc, _) =>
                {
                    Assert.Contains(doc.ScatterLayers, l => l.Name == "forest");
                    Assert.Equal("forest", doc.CompanionLayers.Single(l => l.Name == "understory").HostLayer);
                    Assert.Equal(new List<string> { "forest" }, doc.ScatterOverrides[0].Layers);
                    return 0;
                });

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterLayerRename_ReferencedLayer_DetailReportsCascadedCount()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                // SampleDoc's "trees" layer is referenced by companion layer "understory" (host) and
                // ScatterOverrides[0] (layer filter): two cascaded references.
                MutationResult rename = mutation.ScatterLayerRename("trees", "forest");
                Assert.Contains(", cascaded 2 reference(s)", rename.Detail);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterLayerRemove_Referenced_RejectedAndReverted()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ScatterLayerRemove("trees"));
                Assert.Contains("Cannot remove scatter layer 'trees'", ex.Message);
                Assert.Contains("companion layer 'understory'", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- scatter rules --------------------------------------------------------------------------------------

        [Fact]
        public void ScatterRuleAdd_Edit_Remove_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                var query = new QueryService(session);

                // SampleDoc's "trees" layer already carries one rule (Marsh, index 0).
                MutationResult add = mutation.ScatterRuleAdd("trees", "Forest", density: 0.7f, kinds: new[] { "oak:2" });
                Assert.Equal("scatter_rule_add", add.Verb);
                Assert.True(add.WorldChanged);
                Assert.Equal(1, add.Index);

                ScatterLayerInfo trees = query.ProceduralInfo().ScatterLayers.Single(l => l.Name == "trees");
                Assert.Equal(2, trees.Rules.Count);
                ScatterRuleInfo addedRule = trees.Rules[1];
                Assert.Equal("Forest", addedRule.Biome);
                Assert.Equal(0.7f, addedRule.Density);
                Assert.Equal(new[] { "oak:2" }, addedRule.Kinds);
                // The pre-existing rule at index 0 is untouched.
                Assert.Equal("Marsh", trees.Rules[0].Biome);

                MutationResult edit = mutation.ScatterRuleEdit("trees", 1, density: 0.9f);
                Assert.Equal("scatter_rule_edit", edit.Verb);
                Assert.Equal(1, edit.Index);
                ScatterRuleInfo edited = query.ProceduralInfo().ScatterLayers.Single(l => l.Name == "trees").Rules[1];
                Assert.Equal(0.9f, edited.Density);
                // Untouched fields preserved by the read-modify pattern.
                Assert.Equal("Forest", edited.Biome);
                Assert.Equal(new[] { "oak:2" }, edited.Kinds);

                MutationResult remove = mutation.ScatterRuleRemove("trees", 0);
                Assert.Equal("scatter_rule_remove", remove.Verb);
                Assert.Equal(0, remove.Index);
                ScatterLayerInfo afterRemove = query.ProceduralInfo().ScatterLayers.Single(l => l.Name == "trees");
                ScatterRuleInfo onlyRule = Assert.Single(afterRemove.Rules);
                Assert.Equal("Forest", onlyRule.Biome);
                Assert.Equal(0.9f, onlyRule.Density);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterRuleEdit_OnlySetFieldsChange()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                var query = new QueryService(session);

                mutation.ScatterRuleEdit("trees", 0, biome: "Desert");

                ScatterRuleInfo edited = query.ProceduralInfo().ScatterLayers.Single(l => l.Name == "trees").Rules[0];
                Assert.Equal("Desert", edited.Biome);
                // Density and Kinds untouched.
                Assert.Equal(0.35f, edited.Density);
                Assert.Equal(new[] { "pine_a" }, edited.Kinds);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterRuleEdit_NoParams_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                Assert.Throws<ArgumentException>(() => mutation.ScatterRuleEdit("trees", 0));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterRuleEdit_IndexOutOfRange_ThrowsArgumentException()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                    mutation.ScatterRuleEdit("trees", 99, density: 0.5f));
                Assert.Contains("out of range", ex.Message);

                Assert.Throws<ArgumentException>(() => mutation.ScatterRuleRemove("trees", 99));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterRuleAdd_UnknownLayer_ThrowsWithPreciseMessage()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.ScatterRuleAdd("ghost-layer", "Meadow"));
                Assert.Equal("No scatter layer named 'ghost-layer' in the document.", ex.Message);

                Assert.Throws<InvalidOperationException>(() => mutation.ScatterRuleEdit("ghost-layer", 0, density: 0.5f));
                Assert.Throws<InvalidOperationException>(() => mutation.ScatterRuleRemove("ghost-layer", 0));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterRuleAdd_GarbageKindWeight_RejectedAndReverted()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                    mutation.ScatterRuleAdd("trees", "Meadow", kinds: new[] { "oak:notanumber" }));
                Assert.Contains("is not a number", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- companion layers ---------------------------------------------------------------------------------

        [Fact]
        public void CompanionLayerAdd_Edit_Remove_Rename_RoundTrip()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                MutationResult add = mutation.CompanionLayerAdd("undergrowth", "trees", seed: 5,
                    hostKinds: new[] { "pine_a" }, kinds: new[] { "moss:2" }, countMin: 1, countMax: 3,
                    radiusMin: 0.3f, radiusMax: 1.0f, scaleMin: 0.6f, scaleMax: 0.9f, maxHeight: 15f);
                Assert.Equal("companion_layer_add", add.Verb);
                Assert.True(add.WorldChanged);

                MapCompanionLayer added = session.WithDocument((doc, _) => doc.CompanionLayers.Single(l => l.Name == "undergrowth"));
                Assert.Equal("trees", added.HostLayer);
                Assert.Equal(5, added.Seed);
                Assert.Equal(new[] { "pine_a" }, added.HostKinds);
                MapPropKind moss = Assert.Single(added.Kinds);
                Assert.Equal("moss", moss.Id);
                Assert.Equal(2f, moss.Weight);
                Assert.Equal(1, added.CountMin);
                Assert.Equal(3, added.CountMax);
                Assert.Equal(15f, added.MaxHeight);

                mutation.CompanionLayerEdit("undergrowth", countMin: 2, kinds: new[] { "moss" });
                MapCompanionLayer edited = session.WithDocument((doc, _) => doc.CompanionLayers.Single(l => l.Name == "undergrowth"));
                Assert.Equal(2, edited.CountMin);
                Assert.Equal(1f, edited.Kinds[0].Weight);
                // Untouched field preserved by the read-modify pattern.
                Assert.Equal("trees", edited.HostLayer);

                mutation.CompanionLayerEdit("undergrowth", clearMaxHeight: true);
                Assert.Null(session.WithDocument((doc, _) => doc.CompanionLayers.Single(l => l.Name == "undergrowth").MaxHeight));

                MutationResult rename = mutation.CompanionLayerRename("undergrowth", "underbrush");
                Assert.Equal("underbrush", rename.Id);

                MutationResult remove = mutation.CompanionLayerRemove("underbrush");
                Assert.Equal("companion_layer_remove", remove.Verb);
                Assert.False(session.WithDocument((doc, _) => doc.CompanionLayers.Any(l => l.Name == "underbrush")));

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void CompanionLayerAdd_UnknownHostLayer_RejectedAndReverted()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                string before = session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r));
                bool dirtyBefore = session.IsDirty;

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                    mutation.CompanionLayerAdd("ghost-fans", "ghost-layer"));
                Assert.StartsWith("mutation rejected:", ex.Message);
                Assert.Contains("not a scatter layer", ex.Message);

                Assert.Equal(before, session.WithDocument((doc, r) => MapDocumentFile.SaveText(doc, r)));
                Assert.Equal(dirtyBefore, session.IsDirty);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void CompanionLayerAdd_MismatchWarnsInDetail()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                // SampleDoc's "trees" layer only ever places "pine_a" (its one rule), so "oak_a" matches nothing.
                MutationResult add = mutation.CompanionLayerAdd("mismatched", "trees", hostKinds: new[] { "oak_a" });
                Assert.Equal("companion_layer_add", add.Verb);
                Assert.Contains("host kinds match no kind in the host layer", add.Detail);

                // A host kind that IS one of "trees"'s rule kinds carries no warning.
                MutationResult matching = mutation.CompanionLayerAdd("matched", "trees", hostKinds: new[] { "pine_a" });
                Assert.DoesNotContain("host kinds match no kind in the host layer", matching.Detail);

                // An empty HostKinds (match-all) never warns either.
                MutationResult empty = mutation.CompanionLayerAdd("catch-all", "trees");
                Assert.DoesNotContain("host kinds match no kind in the host layer", empty.Detail);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void CompanionLayerEdit_MismatchWarnsInDetail()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);

                // SampleDoc's "understory" starts matching (HostKinds=[pine_a] intersects "trees"'s one rule).
                // Editing HostKinds to something "trees" never places should surface the mismatch note.
                MutationResult edit = mutation.CompanionLayerEdit("understory", hostKinds: new[] { "oak_a" });
                Assert.Equal("companion_layer_edit", edit.Verb);
                Assert.Contains("host kinds match no kind in the host layer", edit.Detail);

                // Editing back to a matching kind clears the warning.
                MutationResult fixedEdit = mutation.CompanionLayerEdit("understory", hostKinds: new[] { "pine_a" });
                Assert.DoesNotContain("host kinds match no kind in the host layer", fixedEdit.Detail);

                // Clearing HostKinds to empty (match-all) never warns.
                MutationResult cleared = mutation.CompanionLayerEdit("understory", hostKinds: Array.Empty<string>());
                Assert.DoesNotContain("host kinds match no kind in the host layer", cleared.Detail);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- procedural_info read path --------------------------------------------------------------------------

        [Fact]
        public void ProceduralInfo_HostKindsMatchFlag()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                var query = new QueryService(session);

                // SampleDoc's "understory" HostKinds=[pine_a] intersects "trees"'s one rule (pine_a): matches.
                CompanionLayerInfo understory = query.ProceduralInfo().CompanionLayers.Single(l => l.Name == "understory");
                Assert.True(understory.HostKindsMatchHost);

                // Empty HostKinds means match-all regardless of the host's rule kinds.
                mutation.CompanionLayerEdit("understory", hostKinds: Array.Empty<string>());
                CompanionLayerInfo emptyHostKinds = query.ProceduralInfo().CompanionLayers.Single(l => l.Name == "understory");
                Assert.True(emptyHostKinds.HostKindsMatchHost);

                // A populated HostKinds with zero intersection against the host's rule kinds does not match.
                mutation.CompanionLayerEdit("understory", hostKinds: new[] { "oak_a" });
                CompanionLayerInfo mismatched = query.ProceduralInfo().CompanionLayers.Single(l => l.Name == "understory");
                Assert.False(mismatched.HostKindsMatchHost);

                Assert.Empty(session.Validate().StructuralErrors);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ProceduralInfo_ReturnsFullSetup()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService _) = OpenSample(dir);
                var query = new QueryService(session);

                ProceduralInfo info = query.ProceduralInfo();

                Assert.Equal(7345, info.Terrain.Seed);
                Assert.Equal(-0.5f, info.Terrain.WaterLevel);
                Assert.Equal(24f, info.Terrain.BiomeBlend);

                BiomeBandInfo band = Assert.Single(info.Bands);
                Assert.Equal(0, band.Index);
                Assert.Equal("Marsh", band.Biome);
                Assert.Equal(1.5f, band.BaseHeight);
                Assert.Equal(1.2f, band.HillAmplitude);

                ScatterLayerInfo trees = Assert.Single(info.ScatterLayers);
                Assert.Equal("trees", trees.Name);
                Assert.Equal(5f, trees.CellSize);
                ScatterRuleInfo rule = Assert.Single(trees.Rules);
                Assert.Equal("Marsh", rule.Biome);
                Assert.Equal(0.35f, rule.Density);
                Assert.Equal(new[] { "pine_a" }, rule.Kinds);

                CompanionLayerInfo understory = Assert.Single(info.CompanionLayers);
                Assert.Equal("understory", understory.Name);
                Assert.Equal("trees", understory.HostLayer);
                Assert.Equal(new[] { "pine_a" }, understory.HostKinds);
                Assert.Equal(new[] { "fern" }, understory.Kinds);
                Assert.True(understory.HostKindsMatchHost);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // ---- exclusions_info / scatter_overrides_info read paths ------------------------------------------------

        [Fact]
        public void ExclusionsInfo_ReturnsDocumentOrderWithShapeSummaries()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                var query = new QueryService(session);

                // index 1: rect, layer-targeted, unnamed.
                mutation.ExclusionAdd("{\"type\":\"rect\",\"minX\":0,\"minZ\":0,\"maxX\":20,\"maxZ\":15}",
                    layers: new[] { "trees" });
                // index 2: polygon, all-layers, named.
                mutation.ExclusionAdd(
                    "{\"type\":\"polygon\",\"points\":[[-10,-10],[10,-10],[10,10],[-10,10]]}");
                mutation.ExclusionRename(2, "camp");

                IReadOnlyList<ExclusionInfo> exclusions = query.ExclusionsInfo().Exclusions;
                Assert.Equal(3, exclusions.Count);

                // index 0: from SampleDoc, a disc, unnamed, all-layers (null Layers).
                ExclusionInfo disc = exclusions[0];
                Assert.Equal(0, disc.Index);
                Assert.Null(disc.Name);
                Assert.Equal("disc", disc.ShapeKind);
                Assert.Equal("center (-32, 22), radius 30", disc.ShapeSummary);
                Assert.Null(disc.Layers);

                ExclusionInfo rect = exclusions[1];
                Assert.Equal(1, rect.Index);
                Assert.Null(rect.Name);
                Assert.Equal("rect", rect.ShapeKind);
                Assert.Equal("min (0, 0), max (20, 15)", rect.ShapeSummary);
                Assert.Equal(new[] { "trees" }, rect.Layers);

                ExclusionInfo polygon = exclusions[2];
                Assert.Equal(2, polygon.Index);
                Assert.Equal("camp", polygon.Name);
                Assert.Equal("polygon", polygon.ShapeKind);
                Assert.Equal("4 points", polygon.ShapeSummary);
                Assert.Null(polygon.Layers);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void ScatterOverridesInfo_ReturnsDocumentOrderWithKindsAndShapeSummaries()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, MutationService mutation) = OpenSample(dir);
                var query = new QueryService(session);

                // index 1: disc, all-layers, kind substitution, named.
                mutation.ScatterOverrideAdd("{\"type\":\"disc\",\"centerX\":5,\"centerZ\":5,\"radius\":8}",
                    densityMultiplier: 1.2f, kinds: new[] { "pine_a:2", "oak_a" });
                mutation.ScatterOverrideRename(1, "meadow-boost");
                // index 2: polygon, all-layers, no kind substitution, unnamed.
                mutation.ScatterOverrideAdd("{\"type\":\"polygon\",\"points\":[[0,0],[10,0],[5,10]]}");

                IReadOnlyList<ScatterOverrideInfo> overrides = query.ScatterOverridesInfo().ScatterOverrides;
                Assert.Equal(3, overrides.Count);

                // index 0: from SampleDoc, a rect, layer-targeted, no Kinds, unnamed.
                ScatterOverrideInfo rect = overrides[0];
                Assert.Equal(0, rect.Index);
                Assert.Null(rect.Name);
                Assert.Equal("rect", rect.ShapeKind);
                Assert.Equal("min (0, 0), max (50, 50)", rect.ShapeSummary);
                Assert.Equal(0.5f, rect.DensityMultiplier);
                Assert.Null(rect.Kinds);
                Assert.Equal(new[] { "trees" }, rect.Layers);

                ScatterOverrideInfo disc = overrides[1];
                Assert.Equal(1, disc.Index);
                Assert.Equal("meadow-boost", disc.Name);
                Assert.Equal("disc", disc.ShapeKind);
                Assert.Equal("center (5, 5), radius 8", disc.ShapeSummary);
                Assert.Equal(1.2f, disc.DensityMultiplier);
                Assert.Equal(new[] { "pine_a:2", "oak_a" }, disc.Kinds);
                Assert.Null(disc.Layers);

                ScatterOverrideInfo polygon = overrides[2];
                Assert.Equal(2, polygon.Index);
                Assert.Null(polygon.Name);
                Assert.Equal("polygon", polygon.ShapeKind);
                Assert.Equal("3 points", polygon.ShapeSummary);
                Assert.Equal(1f, polygon.DensityMultiplier);
                Assert.Null(polygon.Kinds);
                Assert.Null(polygon.Layers);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
