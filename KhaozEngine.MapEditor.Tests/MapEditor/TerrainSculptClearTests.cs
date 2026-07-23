using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>The undoable sculpt-clear command (T3, #271): tile removal, layer-nulling once emptied, exact
    /// undo restoring the prior grid, and byte-identical round-trips through <see cref="EditorDocument"/>.</summary>
    public class TerrainSculptClearTests
    {
        const int Span = TerrainSculpt.TileSize;

        static MapDocument SampleDoc() => KhaozEngine.Tests.MapDoc.MapDocumentFileTests.SampleDoc();
        static string Save(MapDocument d) => MapDocumentFile.SaveText(d);

        [Fact]
        public void Apply_removes_the_selected_tile_and_nulls_an_emptied_layer()
        {
            MapDocument doc = SampleDoc();
            doc.TerrainOverrides = new MapTerrainOverrides(0.5f);
            doc.TerrainOverrides.SetDelta(5, 5, 3f);
            doc.TerrainOverrides.TryGetTile(0, 0, out MapSculptTile t0);
            var tiles = new List<SculptTileClear> { new(0, 0, (float[])t0.Deltas.Clone()) };

            var command = new TerrainSculptClearCommand(0.5f, tiles, dirty: null);
            command.Apply(doc);

            Assert.Null(doc.TerrainOverrides);   // the only tile was removed, so the layer drops back to null
        }

        [Fact]
        public void Apply_leaves_other_tiles_intact()
        {
            MapDocument doc = SampleDoc();
            doc.TerrainOverrides = new MapTerrainOverrides(0.5f);
            doc.TerrainOverrides.SetDelta(5, 5, 3f);     // tile (0,0)
            doc.TerrainOverrides.SetDelta(40, 5, 7f);    // tile (1,0)
            doc.TerrainOverrides.TryGetTile(0, 0, out MapSculptTile t0);
            var tiles = new List<SculptTileClear> { new(0, 0, (float[])t0.Deltas.Clone()) };

            new TerrainSculptClearCommand(0.5f, tiles, dirty: null).Apply(doc);

            Assert.NotNull(doc.TerrainOverrides);
            Assert.False(doc.TerrainOverrides!.TryGetTile(0, 0, out _));
            Assert.True(doc.TerrainOverrides.TryGetTile(1, 0, out _));
            Assert.Equal(7f, doc.TerrainOverrides.GetDelta(40, 5), 5);
        }

        [Fact]
        public void Revert_restores_the_removed_tiles_grid()
        {
            MapDocument doc = SampleDoc();
            doc.TerrainOverrides = new MapTerrainOverrides(0.5f);
            doc.TerrainOverrides.SetDelta(5, 5, 3f);
            doc.TerrainOverrides.TryGetTile(0, 0, out MapSculptTile t0);
            float[] prior = (float[])t0.Deltas.Clone();
            var tiles = new List<SculptTileClear> { new(0, 0, prior) };

            var command = new TerrainSculptClearCommand(0.5f, tiles, dirty: null);
            command.Apply(doc);
            command.Revert(doc);

            Assert.NotNull(doc.TerrainOverrides);
            Assert.Equal(3f, doc.TerrainOverrides!.GetDelta(5, 5), 5);
        }

        [Fact]
        public void Clear_then_undo_is_byte_identical()
        {
            MapDocument doc = SampleDoc();
            doc.TerrainOverrides = new MapTerrainOverrides(0.5f);
            doc.TerrainOverrides.SetDelta(5, 5, 3f);
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            doc.TerrainOverrides.TryGetTile(0, 0, out MapSculptTile t0);
            var tiles = new List<SculptTileClear> { new(0, 0, (float[])t0.Deltas.Clone()) };
            ed.Execute(new TerrainSculptClearCommand(0.5f, tiles, dirty: null));

            Assert.Null(doc.TerrainOverrides);
            Assert.NotEqual(before, Save(doc));
            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));
        }

        [Fact]
        public void Apply_on_a_document_with_no_layer_is_a_no_op()
        {
            MapDocument doc = SampleDoc();
            Assert.Null(doc.TerrainOverrides);
            var tiles = new List<SculptTileClear> { new(0, 0, new float[Span * Span]) };
            new TerrainSculptClearCommand(0.5f, tiles, dirty: null).Apply(doc);
            Assert.Null(doc.TerrainOverrides);
        }

        [Fact]
        public void DirtyRegion_reports_the_captured_rect()
        {
            var rect = new RectArea(-2f, -2f, 18f, 18f);
            var command = new TerrainSculptClearCommand(0.5f, new List<SculptTileClear>(), dirty: rect);
            Assert.True(command.DirtyRegion.HasValue);
            Assert.Equal(-2f, command.DirtyRegion!.Value.MinX);
        }
    }
}
