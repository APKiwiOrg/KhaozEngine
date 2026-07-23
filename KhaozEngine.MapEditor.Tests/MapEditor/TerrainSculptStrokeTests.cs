using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>The undoable sculpt stroke command (T2, #271): exact undo round-trips (document deep-equal), redo
    /// reuse, tile creation and removal, per-tile earliest-prior / latest-final merge, and the bounded dirty
    /// region.</summary>
    public class TerrainSculptStrokeTests
    {
        const int Span = TerrainSculpt.TileSize;

        static MapDocument SampleDoc() => KhaozEngine.Tests.MapDoc.MapDocumentFileTests.SampleDoc();
        static string Save(MapDocument d) => MapDocumentFile.SaveText(d);

        static float[] Grid(int localX, int localZ, float value)
        {
            var g = new float[Span * Span];
            g[localZ * Span + localX] = value;
            return g;
        }

        static TerrainSculptStrokeCommand Stroke(bool createdLayer, float cellSize, RectArea dab,
            params SculptTileDelta[] tiles) =>
            new(createdLayer, cellSize, new List<SculptTileDelta>(tiles), dab);

        [Fact]
        public void Stroke_that_created_the_layer_undoes_to_byte_identical()
        {
            MapDocument doc = SampleDoc();
            var ed = new EditorDocument(doc);
            string before = Save(doc);

            ed.Execute(Stroke(createdLayer: true, 0.5f, new RectArea(0f, 0f, 16f, 16f),
                new SculptTileDelta(0, 0, null, Grid(5, 5, 3f))));
            Assert.NotNull(doc.TerrainOverrides);
            Assert.Equal(3f, doc.TerrainOverrides!.GetDelta(5, 5), 5);
            Assert.Equal(1, ed.History.UndoDepth);

            Assert.True(ed.Undo());
            Assert.Null(doc.TerrainOverrides);            // a created layer returns to null
            Assert.Equal(before, Save(doc));             // exact restore
        }

        [Fact]
        public void Redo_reapplies_the_captured_final()
        {
            MapDocument doc = SampleDoc();
            var ed = new EditorDocument(doc);
            ed.Execute(Stroke(true, 0.5f, new RectArea(0f, 0f, 16f, 16f),
                new SculptTileDelta(0, 0, null, Grid(5, 5, 3f))));
            string after = Save(doc);

            Assert.True(ed.Undo());
            Assert.True(ed.Redo());
            Assert.Equal(after, Save(doc));              // redo reproduces the exact final state
        }

        [Fact]
        public void Apply_creates_the_layer_at_the_commands_cell_size()
        {
            MapDocument doc = SampleDoc();
            Stroke(true, 0.25f, new RectArea(0f, 0f, 8f, 8f), new SculptTileDelta(0, 0, null, Grid(1, 1, 2f)))
                .Apply(doc);
            Assert.NotNull(doc.TerrainOverrides);
            Assert.Equal(0.25f, doc.TerrainOverrides!.CellSize);
        }

        [Fact]
        public void Revert_restores_a_pre_existing_tiles_prior_grid()
        {
            MapDocument doc = SampleDoc();
            doc.TerrainOverrides = new MapTerrainOverrides(0.5f);
            doc.TerrainOverrides.SetDelta(5, 5, 1f);     // tile (0,0) already carries a delta
            doc.TerrainOverrides.TryGetTile(0, 0, out MapSculptTile t0);
            float[] prior = (float[])t0.Deltas.Clone();
            float[] final = (float[])t0.Deltas.Clone();
            final[5 * Span + 5] = 9f;

            var cmd = Stroke(createdLayer: false, 0.5f, new RectArea(0f, 0f, 16f, 16f),
                new SculptTileDelta(0, 0, prior, final));
            cmd.Apply(doc);
            Assert.Equal(9f, doc.TerrainOverrides.GetDelta(5, 5), 5);
            cmd.Revert(doc);
            Assert.Equal(1f, doc.TerrainOverrides.GetDelta(5, 5), 5);   // prior grid restored
            Assert.NotNull(doc.TerrainOverrides);                       // a pre-existing layer is never nulled
        }

        [Fact]
        public void Merge_keeps_earliest_prior_and_latest_final_and_adds_new_tiles()
        {
            // First dab: tile (0,0) created (prior null) with final 1 at local (1,1).
            var cmd1 = Stroke(createdLayer: true, 0.5f, new RectArea(0f, 0f, 1f, 1f),
                new SculptTileDelta(0, 0, null, Grid(1, 1, 1f)));
            // Second dab: revisits tile (0,0) (final 2, plus a bogus non-null prior that must be discarded) and
            // reaches a new tile (1,0).
            var cmd2 = Stroke(createdLayer: false, 0.5f, new RectArea(1f, 0f, 2f, 2f),
                new SculptTileDelta(0, 0, Grid(1, 1, 7f), Grid(1, 1, 2f)),
                new SculptTileDelta(1, 0, null, Grid(2, 2, 5f)));
            Assert.True(cmd1.TryMerge(cmd2));

            MapDocument doc = SampleDoc();
            var ed = new EditorDocument(doc);
            string before = Save(doc);
            ed.Execute(cmd1);

            Assert.Equal(2f, doc.TerrainOverrides!.GetDelta(1, 1), 5);          // latest final wins
            Assert.Equal(5f, doc.TerrainOverrides.GetDelta(Span + 2, 2), 5);    // tile (1,0), local (2,2)
            Assert.Equal(1, ed.History.UndoDepth);

            Assert.True(ed.Undo());
            // Earliest prior for tile (0,0) was null, so undo removed it (did not restore the discarded 7); both
            // tiles were created, so the layer returns to null and the document is byte-identical.
            Assert.Null(doc.TerrainOverrides);
            Assert.Equal(before, Save(doc));
        }

        [Fact]
        public void Merge_unions_the_dirty_region()
        {
            var cmd1 = Stroke(true, 0.5f, new RectArea(0f, 0f, 1f, 1f),
                new SculptTileDelta(0, 0, null, Grid(0, 0, 1f)));
            var cmd2 = Stroke(false, 0.5f, new RectArea(4f, 4f, 6f, 6f),
                new SculptTileDelta(0, 0, null, Grid(9, 9, 1f)));
            cmd1.TryMerge(cmd2);

            RectArea? dirty = cmd1.DirtyRegion;
            Assert.True(dirty.HasValue);
            Assert.True(dirty!.Value.MinX <= 0f && dirty.Value.MinZ <= 0f);
            Assert.True(dirty.Value.MaxX >= 6f && dirty.Value.MaxZ >= 6f);
        }
    }
}
