using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="FreezeZoneCommand"/> and the <see cref="EditorToolController"/>
    /// whole-zone freeze: the frozen placements equal an independent live-generation reference over the whole bounds
    /// (parity, including companions ringing their host layer and the applied exclusions and overrides), the four
    /// procedural collections are all removed, a single undo restores a byte-identical document (deep compare via
    /// <see cref="MapDocumentFile.SaveText"/>), two freezes are identical (determinism), an undo/redo cycle is stable
    /// (captured, not regenerated), and an empty document is a no-op that lands no undo step.</summary>
    public class FreezeZoneTests
    {
        static MapDocRegistry Registry() => MapDocRegistry.CreateDefault();

        // A flat Meadow zone (gentle roll zeroed, no biome bands so the default Meadow band, water at 0) with two
        // dense scatter layers at different seeds, a companion layer ringing the first, one exclusion filtered to a
        // layer, and one density override. Density 1 keeps every candidate cell, so the bounds scatter a full grid.
        static MapDocument Doc()
        {
            var doc = new MapDocument
            {
                Id = "freeze-zone",
                Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f },
            };
            doc.Terrain.GentleAmplitude = 0f;
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees",
                Seed = 4242,
                CellSize = 10f,
                MaxHeight = null,
                Rules = { new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = { new MapPropKind { Id = "pine_a", Weight = 1f } } } },
            });
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "rocks",
                Seed = 9001,
                CellSize = 12f,
                MaxHeight = null,
                Rules = { new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = { new MapPropKind { Id = "rock_a", Weight = 1f } } } },
            });
            doc.CompanionLayers.Add(new MapCompanionLayer
            {
                Name = "understory",
                HostLayer = "trees",
                HostKinds = { "pine_a" },
                Kinds = { new MapPropKind { Id = "fern", Weight = 1f } },
                CountMin = 2,
                CountMax = 4,
                MaxHeight = null,
            });
            doc.Exclusions.Add(new MapExclusion
            {
                Shape = new RectShapeDoc { MinX = 20f, MinZ = 20f, MaxX = 50f, MaxZ = 50f },
                Layers = new List<string> { "trees" },
            });
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc
            {
                Shape = new RectShapeDoc { MinX = -50f, MinZ = -50f, MaxX = 0f, MaxZ = 0f },
                DensityMultiplier = 0.5f,
                Layers = new List<string> { "rocks" },
            });
            doc.Placements.Add(new MapPlacement { Id = "inn", Kind = "building_inn", X = -30f, Z = 20f, Yaw = 1.2f });
            return doc;
        }

        static string Save(MapDocument d) => MapDocumentFile.SaveText(d);

        static List<MapPlacement> Baked(MapDocument d) => d.Placements.Where(p => p.Tags.Contains("baked")).ToList();

        // The independent live-generation reference: every scatter layer over the whole bounds in document order,
        // then every companion layer ringing its host layer's placements, using the SAME runtime calls the freeze
        // reuses. Each generated placement carries the source layer name it should be tagged with. Built from a
        // fresh document so it never sees the freeze's mutation.
        static (List<PropPlacement> Placements, List<string> Sources) Reference(MapDocument doc)
        {
            TerrainField field = MapRuntime.BuildField(doc, Registry());
            var area = new RectArea(doc.Bounds.MinX, doc.Bounds.MinZ, doc.Bounds.MaxX, doc.Bounds.MaxZ);
            var placements = new List<PropPlacement>();
            var sources = new List<string>();
            var hostsByLayer = new Dictionary<string, IReadOnlyList<PropPlacement>>();
            foreach (MapScatterLayer layer in doc.ScatterLayers)
            {
                IReadOnlyList<PropPlacement> hosts =
                    PropScatter.Generate(field, MapRuntime.BuildScatterConfig(doc, layer.Name), area);
                hostsByLayer[layer.Name] = hosts;
                foreach (PropPlacement h in hosts) { placements.Add(h); sources.Add(layer.Name); }
            }
            foreach (MapCompanionLayer cl in doc.CompanionLayers)
            {
                IReadOnlyList<PropPlacement> comp = PropScatter.GenerateCompanions(
                    field, hostsByLayer[cl.HostLayer], MapRuntime.BuildCompanionConfig(doc, cl.Name));
                foreach (PropPlacement c in comp) { placements.Add(c); sources.Add(cl.Name); }
            }
            return (placements, sources);
        }

        // ---- parity with live generation (hosts + companions + exclusions + override) ------------------

        [Fact]
        public void Freeze_FrozenPlacementsEqualLiveGenerationReference()
        {
            var doc = Doc();
            (List<PropPlacement> expected, List<string> sources) = Reference(Doc());
            Assert.NotEmpty(expected);
            Assert.Contains("understory", sources);   // the reference must actually ring companions

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));

            List<MapPlacement> baked = Baked(doc);
            Assert.Equal(expected.Count, baked.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                PropPlacement e = expected[i];
                MapPlacement b = baked[i];
                Assert.Equal(e.Id, b.Kind);              // kind comes from the generated placement's kit id
                Assert.Equal(e.X, b.X);
                Assert.Equal(e.Z, b.Z);
                Assert.True(b.Y.HasValue);               // explicit frozen Y, not ground-snap
                Assert.Equal(e.Y, b.Y!.Value);
                Assert.Equal(e.Scale, b.Scale);
                Assert.Equal(e.Yaw, b.Yaw);
                Assert.Contains("baked", b.Tags);
                Assert.Contains(sources[i], b.Tags);     // per-source tag keeps the diff reviewable
                Assert.StartsWith("baked-" + sources[i] + "-", b.Id);
            }
        }

        // ---- companion layers are baked from their host layer's generation -----------------------------

        [Fact]
        public void Freeze_BakesCompanionsFromHostLayer()
        {
            var doc = Doc();
            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));

            List<MapPlacement> companions = Baked(doc).Where(p => p.Tags.Contains("understory")).ToList();
            Assert.NotEmpty(companions);
            Assert.All(companions, p => Assert.Equal("fern", p.Kind));   // the companion kit

            // Match the count the runtime companion generator produces for the host layer over the bounds.
            (List<PropPlacement> expected, List<string> sources) = Reference(Doc());
            int expectedCompanions = expected.Where((_, i) => sources[i] == "understory").Count();
            Assert.Equal(expectedCompanions, companions.Count);
        }

        // ---- the four procedural collections are all removed -------------------------------------------

        [Fact]
        public void Freeze_RemovesAllScatterInputs()
        {
            var doc = Doc();
            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));

            Assert.Empty(doc.ScatterLayers);
            Assert.Empty(doc.CompanionLayers);
            Assert.Empty(doc.Exclusions);
            Assert.Empty(doc.ScatterOverrides);
            Assert.NotEmpty(Baked(doc));                 // but the props are frozen into placements
            Assert.Contains(doc.Placements, p => p.Id == "inn");   // pre-existing placements untouched
        }

        // ---- an excluded region has no baked props (the exclusion shaped generation) -------------------

        [Fact]
        public void Freeze_AppliesExclusionDuringGeneration()
        {
            var doc = Doc();
            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));

            // The trees exclusion covers [20,50] x [20,50]. No baked tree may fall inside it.
            List<MapPlacement> trees = Baked(doc).Where(p => p.Tags.Contains("trees")).ToList();
            Assert.NotEmpty(trees);
            Assert.DoesNotContain(trees, p => p.X >= 20f && p.X <= 50f && p.Z >= 20f && p.Z <= 50f);
        }

        // ---- single-command undo restores a byte-identical document ------------------------------------

        [Fact]
        public void Undo_RestoresByteIdenticalDocument()
        {
            var doc = Doc();
            string before = Save(doc);

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));
            Assert.NotEqual(before, Save(doc));          // the freeze mutated the document
            Assert.Equal(1, ed.History.UndoDepth);       // exactly one undo step

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));             // exact restore: layers, exclusions, overrides all back
        }

        // ---- undo/redo cycle is stable (captured, not regenerated) -------------------------------------

        [Fact]
        public void UndoRedoCycle_IsStable()
        {
            var doc = Doc();
            string before = Save(doc);

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));
            string afterFreeze = Save(doc);
            Assert.NotEqual(before, afterFreeze);

            ed.Undo();
            Assert.Equal(before, Save(doc));

            ed.Redo();
            Assert.Equal(afterFreeze, Save(doc));        // redo reproduces the exact freeze (reused capture)

            ed.Undo();
            Assert.Equal(before, Save(doc));
            ed.Redo();
            Assert.Equal(afterFreeze, Save(doc));        // stable across repeated cycles
        }

        // ---- determinism: two freezes of the same document are identical -------------------------------

        [Fact]
        public void Freeze_IsDeterministic()
        {
            var a = Doc();
            var b = Doc();
            new EditorDocument(a, Registry()).Execute(new FreezeZoneCommand(Registry()));
            new EditorDocument(b, Registry()).Execute(new FreezeZoneCommand(Registry()));
            Assert.Equal(Save(a), Save(b));              // identical placement lists, order and ids included
        }

        // ---- baked ids stay unique document-wide -------------------------------------------------------

        [Fact]
        public void Freeze_BakedIdsAreUnique()
        {
            var doc = Doc();
            // A prior bake already occupies two baked-trees-N ids under the trees source.
            doc.Placements.Add(new MapPlacement { Id = "baked-trees-1", Kind = "pine_a" });
            doc.Placements.Add(new MapPlacement { Id = "baked-trees-3", Kind = "pine_a" });

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));

            List<string> ids = doc.Placements.Select(p => p.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());       // every id is unique document-wide
            Assert.NotNull(MapDocumentFile.SaveText(doc));         // still savable (no duplicate-id error)
        }

        // ---- world rebuild is forced -------------------------------------------------------------------

        [Fact]
        public void Freeze_ForcesWorldRebuild()
        {
            var doc = Doc();
            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new FreezeZoneCommand(ed.Registry));
            Assert.True(ed.WorldRebuildPending);          // generation inputs changed everywhere
            Assert.Null(ed.PendingRebuildRegion);         // a full rebuild (no bounded dirty region)
        }

        // ---- empty document: HasWork false, no phantom undo entry --------------------------------------

        [Fact]
        public void EmptyZone_IsNoOpWithNoUndoStep()
        {
            var doc = new MapDocument
            {
                Id = "empty",
                Bounds = new MapBounds { MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
            };
            Assert.False(FreezeZoneCommand.HasWork(doc));

            string before = Save(doc);
            var ed = new EditorDocument(doc, Registry());
            var controller = new EditorToolController(ed);

            EditorToolController.FreezeZoneResult? result = controller.FreezeZone();
            Assert.Null(result);                          // nothing to freeze
            Assert.Equal(0, ed.History.UndoDepth);        // no phantom undo entry
            Assert.False(ed.WorldRebuildPending);
            Assert.Equal(before, Save(doc));              // document untouched
        }

        // ---- controller path reports what it froze -----------------------------------------------------

        [Fact]
        public void Controller_FreezeZone_ReportsCounts()
        {
            var doc = Doc();
            var ed = new EditorDocument(doc, Registry());
            var controller = new EditorToolController(ed);

            EditorToolController.FreezeZoneResult? maybe = controller.FreezeZone();
            Assert.NotNull(maybe);
            EditorToolController.FreezeZoneResult r = maybe.Value;
            Assert.Equal(Baked(doc).Count, r.PlacementCount);
            Assert.Equal(2, r.ScatterLayersRemoved);
            Assert.Equal(1, r.CompanionLayersRemoved);
            Assert.Equal(1, r.ExclusionsRemoved);
            Assert.Equal(1, r.ScatterOverridesRemoved);
            Assert.Equal(1, ed.History.UndoDepth);        // one undoable command landed
        }
    }
}
