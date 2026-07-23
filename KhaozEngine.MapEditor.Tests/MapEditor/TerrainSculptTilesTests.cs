using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Groups a dab's per-cell writes into tiles (T2, promoted to a shared helper for T3, #271): prior/
    /// final capture for a pre-existing tile, tile creation for a fresh cell, cross-tile grouping, and the padded
    /// dab-bounds footprint. Shared by the interactive sculpt tool and the sculpt_apply/sculpt_flatten_region MCP
    /// verbs, so this is the one place that logic is tested.</summary>
    public class TerrainSculptTilesTests
    {
        const int Span = TerrainSculpt.TileSize;

        static TerrainSculptBrush.CellWrite Write(int cx, int cz, float delta) => new(cx, cz, delta);

        [Fact]
        public void A_write_on_a_fresh_cell_creates_a_tile_with_null_prior()
        {
            var writes = new List<TerrainSculptBrush.CellWrite> { Write(5, 5, 3f) };
            List<SculptTileDelta> tiles = TerrainSculptTiles.BuildTileDeltas(null, 0.5f, writes, out RectArea dab);

            Assert.Single(tiles);
            SculptTileDelta t = tiles[0];
            Assert.Equal((0, 0), (t.TileX, t.TileZ));
            Assert.Null(t.Prior);
            Assert.Equal(3f, t.Final[5 * Span + 5]);
            // Dab bounds pad one cell around the touched extent.
            Assert.True(dab.MinX <= 5f * 0.5f && dab.MaxX >= 5f * 0.5f);
        }

        [Fact]
        public void A_write_on_an_existing_tile_captures_its_current_grid_as_prior()
        {
            var overrides = new MapTerrainOverrides(0.5f);
            overrides.SetDelta(5, 5, 1f);   // tile (0,0) pre-existing, one nonzero cell
            var writes = new List<TerrainSculptBrush.CellWrite> { Write(6, 5, 4f) };   // same tile, a different cell

            List<SculptTileDelta> tiles = TerrainSculptTiles.BuildTileDeltas(overrides, 0.5f, writes, out _);

            Assert.Single(tiles);
            SculptTileDelta t = tiles[0];
            Assert.NotNull(t.Prior);
            Assert.Equal(1f, t.Prior![5 * Span + 5]);    // the pre-existing cell carried over into prior
            Assert.Equal(1f, t.Final[5 * Span + 5]);     // ...and into final (untouched by this write)
            Assert.Equal(4f, t.Final[5 * Span + 6]);     // the new write landed
        }

        [Fact]
        public void Writes_spanning_two_tiles_group_into_two_entries()
        {
            var writes = new List<TerrainSculptBrush.CellWrite> { Write(5, 5, 1f), Write(40, 5, 2f) };   // tiles (0,0) and (1,0)
            List<SculptTileDelta> tiles = TerrainSculptTiles.BuildTileDeltas(null, 0.5f, writes, out _);
            Assert.Equal(2, tiles.Count);
        }

        [Fact]
        public void A_write_on_a_negative_cell_floor_divides_correctly()
        {
            var writes = new List<TerrainSculptBrush.CellWrite> { Write(-1, -1, 5f) };
            List<SculptTileDelta> tiles = TerrainSculptTiles.BuildTileDeltas(null, 0.5f, writes, out _);
            Assert.Single(tiles);
            Assert.Equal((-1, -1), (tiles[0].TileX, tiles[0].TileZ));   // tile (-1,-1) covers cells [-32..-1]
            Assert.Equal(5f, tiles[0].Final[(Span - 1) * Span + (Span - 1)]);   // local (31, 31)
        }

        [Fact]
        public void Empty_writes_produce_no_tiles_and_a_default_dab_bounds()
        {
            List<SculptTileDelta> tiles =
                TerrainSculptTiles.BuildTileDeltas(null, 0.5f, new List<TerrainSculptBrush.CellWrite>(), out RectArea dab);
            Assert.Empty(tiles);
            Assert.Equal(default, dab);
        }
    }
}
