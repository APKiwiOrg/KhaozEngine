using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="BakeRegionCommand"/> and the <see cref="EditorToolController"/> bake
    /// gesture: the frozen placements equal the pre-bake scatter enumeration for the region (the determinism guard),
    /// revert restores a byte-identical document (deep compare via <see cref="MapDocumentFile.SaveText"/>), a
    /// zero-scatter region adds only the exclusion, ids stay unique against pre-existing <c>baked-</c> ids, an
    /// undo/redo cycle is stable (captured, not regenerated), and a rect drag over the ground emits the command.</summary>
    public class BakeRegionTests
    {
        const string Layer = "trees";

        // A region that straddles the origin. With the CellSize below and a dense Meadow layer this scatters a
        // non-empty grid, so the equality guard has something to compare.
        static readonly RectArea Region = new(-20f, -20f, 20f, 20f);

        static readonly Vector3 Down = new(0f, -1f, 0f);

        static MapDocRegistry Registry() => MapDocRegistry.CreateDefault();

        // Flat Meadow field at y = 0 (gentle roll zeroed, no biome bands -> the default Meadow band), water at 0.
        // The single Meadow rule at density 1 keeps every candidate cell, so the region scatters a full grid.
        static MapDocument Doc()
        {
            var doc = new MapDocument
            {
                Id = "bake-zone",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.Terrain.GentleAmplitude = 0f;
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = Layer,
                Seed = 4242,
                CellSize = 10f,
                MaxHeight = null,
                Rules = { new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = { new MapPropKind { Id = "pine_a", Weight = 1f } } } },
            });
            return doc;
        }

        static IReadOnlyList<PropPlacement> PreBakeScatter(MapDocument doc)
        {
            TerrainField field = MapRuntime.BuildField(doc, Registry());
            ScatterConfig config = MapRuntime.BuildScatterConfig(doc, Layer);
            return PropScatter.Generate(field, config, Region);
        }

        static string Save(MapDocument d) => MapDocumentFile.SaveText(d);

        static List<MapPlacement> Baked(MapDocument d) => d.Placements.Where(p => p.Tags.Contains("baked")).ToList();

        static void Near(float expected, float actual, float eps = 1e-3f) =>
            Assert.True(System.MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        // ---- freeze equals the pre-bake enumeration ----------------------------------------------------

        [Fact]
        public void Bake_FreezesEqualToPreBakeScatterEnumeration()
        {
            var doc = Doc();
            IReadOnlyList<PropPlacement> expected = PreBakeScatter(doc);
            Assert.NotEmpty(expected);   // the region must actually scatter something for this to mean anything

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new BakeRegionCommand(Region, Layer, ed.Registry));

            List<MapPlacement> baked = Baked(doc);
            Assert.Equal(expected.Count, baked.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                PropPlacement e = expected[i];
                MapPlacement b = baked[i];
                Assert.Equal(e.Id, b.Kind);              // kind comes from the scatter placement's kit id
                Assert.Equal(e.X, b.X);
                Assert.Equal(e.Z, b.Z);
                Assert.True(b.Y.HasValue);               // explicit Y (frozen), not ground-snap
                Assert.Equal(e.Y, b.Y!.Value);
                Assert.Equal(e.Scale, b.Scale);
                Assert.Equal(e.Yaw, b.Yaw);
                Assert.Contains("baked", b.Tags);
                Assert.StartsWith("baked-" + Layer + "-", b.Id);
            }
        }

        // ---- revert restores byte-identical document ---------------------------------------------------

        [Fact]
        public void Revert_RestoresByteIdenticalDocument()
        {
            var doc = Doc();
            string before = Save(doc);

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new BakeRegionCommand(Region, Layer, ed.Registry));
            Assert.NotEqual(before, Save(doc));          // the bake mutated the document

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));             // exact restore, placements and exclusion both gone
        }

        // ---- zero-scatter region adds only the exclusion -----------------------------------------------

        [Fact]
        public void ZeroScatterRegion_AddsOnlyTheExclusion()
        {
            var doc = Doc();
            // The only rule now targets a biome the flat Meadow field never reports, so nothing scatters.
            doc.ScatterLayers[0].Rules[0].Biome = BiomeId.Marsh;
            Assert.Empty(PreBakeScatter(doc));           // sanity: the region really is empty

            int placementsBefore = doc.Placements.Count;
            int exclusionsBefore = doc.Exclusions.Count;

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new BakeRegionCommand(Region, Layer, ed.Registry));

            Assert.Equal(placementsBefore, doc.Placements.Count);       // no props baked
            Assert.Equal(exclusionsBefore + 1, doc.Exclusions.Count);   // just the region exclusion

            MapExclusion added = doc.Exclusions[^1];
            var rect = Assert.IsType<RectShapeDoc>(added.Shape);
            Near(Region.MinX, rect.MinX);
            Near(Region.MinZ, rect.MinZ);
            Near(Region.MaxX, rect.MaxX);
            Near(Region.MaxZ, rect.MaxZ);
            Assert.NotNull(added.Layers);
            Assert.Equal(new[] { Layer }, added.Layers!);               // filtered to the baked layer only
        }

        // ---- unique ids against pre-existing baked- ids ------------------------------------------------

        [Fact]
        public void BakedIds_AreUniqueAgainstExistingBakedIds()
        {
            var doc = Doc();
            // A prior bake already occupies two baked-<layer>-N ids (untagged here, so they stay out of Baked()).
            doc.Placements.Add(new MapPlacement { Id = "baked-" + Layer + "-1", Kind = "pine_a" });
            doc.Placements.Add(new MapPlacement { Id = "baked-" + Layer + "-3", Kind = "pine_a" });

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new BakeRegionCommand(Region, Layer, ed.Registry));

            List<string> ids = doc.Placements.Select(p => p.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());           // every id is still unique document-wide

            List<MapPlacement> baked = Baked(doc);
            Assert.NotEmpty(baked);
            Assert.DoesNotContain(baked, p => p.Id == "baked-" + Layer + "-1");   // never reuses a taken id
            Assert.DoesNotContain(baked, p => p.Id == "baked-" + Layer + "-3");
            Assert.All(baked, p => Assert.StartsWith("baked-" + Layer + "-", p.Id));

            // The document is still savable (no duplicate-id validation error).
            Assert.NotNull(Save(doc));
        }

        // ---- undo/redo cycle is stable (captured, not regenerated) -------------------------------------

        [Fact]
        public void UndoRedoCycle_IsStable()
        {
            var doc = Doc();
            string before = Save(doc);

            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new BakeRegionCommand(Region, Layer, ed.Registry));
            string afterBake = Save(doc);
            Assert.NotEqual(before, afterBake);

            ed.Undo();
            Assert.Equal(before, Save(doc));

            ed.Redo();
            Assert.Equal(afterBake, Save(doc));          // redo reproduces the exact bake (reused capture)

            ed.Undo();
            Assert.Equal(before, Save(doc));
            ed.Redo();
            Assert.Equal(afterBake, Save(doc));          // stable across repeated cycles
        }

        [Fact]
        public void Bake_ForcesWorldRebuild()
        {
            var doc = Doc();
            var ed = new EditorDocument(doc, Registry());
            ed.Execute(new BakeRegionCommand(Region, Layer, ed.Registry));
            Assert.True(ed.WorldRebuildPending);          // scatter inputs changed (an exclusion was added)
        }

        // ---- controller gesture ------------------------------------------------------------------------

        [Fact]
        public void BakeDrag_EmitsBakeCommand()
        {
            var md = Doc();
            var ed = new EditorDocument(md, Registry());
            var c = new EditorToolController(ed)
            {
                Field = new TerrainField(new TerrainConfig { GentleAmplitude = 0f }),
            };
            c.Mode = EditorToolMode.BakeRegion;

            // Drag a rect on the ground: two straight-down terrain ray hits at the opposing corners.
            c.Update(new EditorFrameInput(new Vector3(-15f, 100f, -15f), Down, pointerPressed: true, pointerDown: true, dt: 0.016f));
            Assert.True(c.IsDrawing);
            c.Update(new EditorFrameInput(new Vector3(15f, 100f, 15f), Down, pointerReleased: true, dt: 0.016f));

            Assert.False(c.IsDrawing);
            Assert.Equal(1, ed.History.UndoDepth);        // exactly one bake command
            Assert.True(ed.WorldRebuildPending);

            Assert.Single(md.Exclusions);
            var rect = Assert.IsType<RectShapeDoc>(md.Exclusions[0].Shape);
            Near(-15f, rect.MinX);
            Near(-15f, rect.MinZ);
            Near(15f, rect.MaxX);
            Near(15f, rect.MaxZ);
            Assert.Equal(new[] { Layer }, md.Exclusions[0].Layers!);

            Assert.NotEmpty(Baked(md));                   // the region's scatter was frozen into placements
        }

        [Fact]
        public void BakeLayer_DefaultsToFirstScatterLayer()
        {
            var md = Doc();
            var ed = new EditorDocument(md, Registry());
            var c = new EditorToolController(ed);
            Assert.Equal(Layer, c.BakeLayer);             // resolves to the document's first scatter layer

            c.BakeLayer = "explicit";
            Assert.Equal("explicit", c.BakeLayer);        // an explicit set overrides the default
        }
    }
}
