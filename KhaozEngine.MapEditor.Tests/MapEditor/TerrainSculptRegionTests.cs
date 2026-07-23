using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless region-scale sculpt operations (T3, #271): <see cref="TerrainSculptRegion.ComputeFlattenRegion"/>
    /// (exact, no-falloff region flatten) and <see cref="TerrainSculptRegion.SelectClearTiles"/> (tile-granularity
    /// clear selection).</summary>
    public class TerrainSculptRegionTests
    {
        static readonly SculptBounds Wide = new(-100_000, -100_000, 100_000, 100_000);
        static readonly Func<int, int, float> Zero = static (_, _) => 0f;
        static readonly Func<float, float, float> FlatBase = static (_, _) => 0f;

        static float Delta(IReadOnlyList<TerrainSculptBrush.CellWrite> writes, int cx, int cz)
        {
            foreach (TerrainSculptBrush.CellWrite w in writes)
                if (w.CellX == cx && w.CellZ == cz) return w.Delta;
            throw new Xunit.Sdk.XunitException($"no write for cell ({cx}, {cz})");
        }

        static bool Has(IReadOnlyList<TerrainSculptBrush.CellWrite> writes, int cx, int cz) =>
            writes.Any(w => w.CellX == cx && w.CellZ == cz);

        [Fact]
        public void Every_cell_in_the_rect_gets_the_exact_target_delta()
        {
            // cellSize 1, rect [0..2] x [0..2] on a flat 0 base: every covered cell centre (0,1,2 on each axis, 9
            // cells) must read exactly target - base = 5 - 0 = 5, no falloff.
            var writes = TerrainSculptRegion.ComputeFlattenRegion(0f, 0f, 2f, 2f, targetHeight: 5f, cellSize: 1f,
                Wide, Zero, FlatBase);
            Assert.Equal(9, writes.Count);
            for (int cz = 0; cz <= 2; cz++)
                for (int cx = 0; cx <= 2; cx++)
                    Assert.Equal(5f, Delta(writes, cx, cz), 5);
        }

        [Fact]
        public void Cells_outside_the_rect_are_not_written()
        {
            var writes = TerrainSculptRegion.ComputeFlattenRegion(0f, 0f, 1f, 1f, 5f, 1f, Wide, Zero, FlatBase);
            Assert.False(Has(writes, 2, 0));
            Assert.False(Has(writes, 0, 2));
            Assert.False(Has(writes, -1, 0));
        }

        [Fact]
        public void An_already_flat_cell_is_skipped()
        {
            // Cell (1,1) already carries delta 5 (matching target - base); every other covered cell is 0.
            Func<int, int, float> mostlyZero = (cx, cz) => cx == 1 && cz == 1 ? 5f : 0f;
            var writes = TerrainSculptRegion.ComputeFlattenRegion(0f, 0f, 2f, 2f, 5f, 1f, Wide, mostlyZero, FlatBase);
            Assert.Equal(8, writes.Count);   // 9 covered cells minus the one already-flat cell
            Assert.False(Has(writes, 1, 1));
        }

        [Fact]
        public void Targets_height_over_a_nonzero_analytic_base()
        {
            Func<float, float, float> baseThree = static (_, _) => 3f;
            var writes = TerrainSculptRegion.ComputeFlattenRegion(0f, 0f, 0f, 0f, 10f, 1f, Wide, Zero, baseThree);
            Assert.Equal(10f - 3f, Delta(writes, 0, 0), 5);
        }

        [Fact]
        public void Degenerate_rect_writes_nothing()
        {
            Assert.Empty(TerrainSculptRegion.ComputeFlattenRegion(2f, 0f, 0f, 2f, 5f, 1f, Wide, Zero, FlatBase));
            Assert.Empty(TerrainSculptRegion.ComputeFlattenRegion(0f, 2f, 2f, 0f, 5f, 1f, Wide, Zero, FlatBase));
        }

        [Fact]
        public void Bounds_with_no_area_writes_nothing()
        {
            var empty = new SculptBounds(0, 0, -1, -1);
            Assert.Empty(TerrainSculptRegion.ComputeFlattenRegion(0f, 0f, 5f, 5f, 5f, 1f, empty, Zero, FlatBase));
        }

        [Fact]
        public void The_rect_is_clamped_to_bounds()
        {
            var bounds = new SculptBounds(0, 0, 1000, 1000);
            var writes = TerrainSculptRegion.ComputeFlattenRegion(-2f, -2f, 2f, 2f, 5f, 1f, bounds, Zero, FlatBase);
            Assert.All(writes, w => Assert.True(w.CellX >= 0 && w.CellZ >= 0));
            Assert.False(Has(writes, -1, 0));
            Assert.True(Has(writes, 0, 0));
        }

        [Fact]
        public void ComputeFlattenRegion_is_deterministic()
        {
            var a = TerrainSculptRegion.ComputeFlattenRegion(-1.5f, -1.5f, 1.5f, 1.5f, 4f, 0.5f, Wide, Zero, FlatBase);
            var b = TerrainSculptRegion.ComputeFlattenRegion(-1.5f, -1.5f, 1.5f, 1.5f, 4f, 0.5f, Wide, Zero, FlatBase);
            Assert.Equal(a.Count, b.Count);
            Assert.True(a.SequenceEqual(b));
        }

        static MapTerrainOverrides ThreeTileLayer()
        {
            var ov = new MapTerrainOverrides(0.5f);
            ov.SetDelta(5, 5, 1f);       // tile (0, 0)
            ov.SetDelta(40, 5, 2f);      // tile (1, 0) (cell 40 = tile 1 * 32 + local 8)
            ov.SetDelta(5, 40, 3f);      // tile (0, 1)
            return ov;
        }

        [Fact]
        public void No_region_selects_every_tile()
        {
            MapTerrainOverrides ov = ThreeTileLayer();
            IReadOnlyList<SculptTileClear> selected = TerrainSculptRegion.SelectClearTiles(ov, null, null, null, null);
            Assert.Equal(3, selected.Count);
        }

        [Fact]
        public void A_region_selects_only_intersecting_tiles()
        {
            MapTerrainOverrides ov = ThreeTileLayer();
            // Tile (0,0) covers world [0..16) x [0..16) at cellSize 0.5. A rect fully inside it hits only that tile.
            IReadOnlyList<SculptTileClear> selected = TerrainSculptRegion.SelectClearTiles(ov, 1f, 1f, 2f, 2f);
            Assert.Single(selected);
            Assert.Equal((0, 0), (selected[0].TileX, selected[0].TileZ));
        }

        [Fact]
        public void A_region_missing_every_tile_selects_nothing()
        {
            MapTerrainOverrides ov = ThreeTileLayer();
            IReadOnlyList<SculptTileClear> selected =
                TerrainSculptRegion.SelectClearTiles(ov, 10_000f, 10_000f, 10_001f, 10_001f);
            Assert.Empty(selected);
        }

        [Fact]
        public void Selected_prior_is_a_defensive_clone()
        {
            var ov = new MapTerrainOverrides(0.5f);
            ov.SetDelta(5, 5, 1f);
            IReadOnlyList<SculptTileClear> selected = TerrainSculptRegion.SelectClearTiles(ov, null, null, null, null);
            ov.SetDelta(5, 5, 99f);   // mutate the live layer after selection
            Assert.Equal(1f, selected[0].Prior[5 * TerrainSculpt.TileSize + 5], 5);   // the clone is untouched
        }
    }
}
