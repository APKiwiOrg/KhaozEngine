using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>GPU-free brush math for the terrain sculpt layer (T2, #271): the smoothstep falloff, exact per-brush
    /// deltas on a flat base, the tile-safe footprint clamp, and determinism.</summary>
    public class TerrainSculptBrushTests
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
        public void Falloff_is_smoothstep_from_one_to_zero()
        {
            Assert.Equal(1f, TerrainSculptBrush.Falloff(0f));
            Assert.Equal(0f, TerrainSculptBrush.Falloff(1f));
            Assert.Equal(0.5f, TerrainSculptBrush.Falloff(0.5f), 5);   // symmetric midpoint
            Assert.Equal(1f, TerrainSculptBrush.Falloff(-1f));          // clamped below 0
            Assert.Equal(0f, TerrainSculptBrush.Falloff(2f));          // clamped above 1
            // Monotonically decreasing across the disc.
            float prev = 1f;
            for (float t = 0.1f; t <= 1f; t += 0.1f)
            {
                float v = TerrainSculptBrush.Falloff(t);
                Assert.True(v <= prev);
                prev = v;
            }
        }

        [Fact]
        public void Raise_adds_strength_times_falloff_times_dt()
        {
            const float radius = 2.5f, strength = 4f, dt = 0.5f, cellSize = 1f;
            var writes = TerrainSculptBrush.ComputeDab(SculptBrush.Raise, 0f, 0f, radius, strength, dt,
                setHeight: 0f, flattenTarget: 0f, cellSize, Wide, Zero, FlatBase);

            // Centre cell: full weight -> strength * 1 * dt.
            Assert.Equal(strength * dt, Delta(writes, 0, 0), 5);
            // A cell one unit out: strength * Falloff(1/radius) * dt.
            Assert.Equal(strength * TerrainSculptBrush.Falloff(1f / radius) * dt, Delta(writes, 1, 0), 5);
            // No cell beyond the radius (cell (3,0) is 3 units out, radius 2.5).
            Assert.False(Has(writes, 3, 0));
        }

        [Fact]
        public void Lower_subtracts_symmetrically()
        {
            var writes = TerrainSculptBrush.ComputeDab(SculptBrush.Lower, 0f, 0f, 2.5f, 4f, 0.5f,
                0f, 0f, 1f, Wide, Zero, FlatBase);
            Assert.Equal(-4f * 0.5f, Delta(writes, 0, 0), 5);
        }

        [Fact]
        public void Zero_dt_and_off_bounds_footprint_write_nothing()
        {
            Assert.Empty(TerrainSculptBrush.ComputeDab(SculptBrush.Raise, 0f, 0f, 2f, 4f, dt: 0f,
                0f, 0f, 1f, Wide, Zero, FlatBase));
            // Footprint entirely outside the paintable range.
            var narrow = new SculptBounds(1000, 1000, 1031, 1031);
            Assert.Empty(TerrainSculptBrush.ComputeDab(SculptBrush.Raise, 0f, 0f, 2f, 4f, 0.5f,
                0f, 0f, 1f, narrow, Zero, FlatBase));
        }

        [Fact]
        public void Footprint_is_clamped_to_bounds()
        {
            // Paintable cells only >= 0 on each axis; the disc reaches into negative cells, which must be dropped.
            var bounds = new SculptBounds(0, 0, 1000, 1000);
            var writes = TerrainSculptBrush.ComputeDab(SculptBrush.Raise, 0f, 0f, 3f, 4f, 0.5f,
                0f, 0f, 1f, bounds, Zero, FlatBase);
            Assert.NotEmpty(writes);
            Assert.All(writes, w => Assert.True(w.CellX >= 0 && w.CellZ >= 0));
            Assert.False(Has(writes, -1, 0));
        }

        [Fact]
        public void Smooth_moves_a_spike_to_its_neighbourhood_mean()
        {
            // A single spike of 9 at the centre, zeros around it: the 3x3 mean is 1. A strength/dt large enough to
            // saturate the blend (alpha clamps to 1) pulls the centre exactly to that mean.
            Func<int, int, float> spike = (cx, cz) => cx == 0 && cz == 0 ? 9f : 0f;
            var writes = TerrainSculptBrush.ComputeDab(SculptBrush.Smooth, 0f, 0f, 4f, strength: 100f, dt: 1f,
                0f, 0f, 1f, Wide, spike, FlatBase);
            Assert.Equal(1f, Delta(writes, 0, 0), 5);   // 9 -> mean 1
        }

        [Fact]
        public void Flatten_targets_the_press_height_over_the_base()
        {
            // Base height 5, flatten target 12: with the blend saturated the delta becomes target - base = 7, so the
            // composited surface (base + delta) reaches the target.
            Func<float, float, float> baseFive = static (_, _) => 5f;
            var writes = TerrainSculptBrush.ComputeDab(SculptBrush.Flatten, 0f, 0f, 4f, 100f, 1f,
                setHeight: 0f, flattenTarget: 12f, 1f, Wide, Zero, baseFive);
            Assert.Equal(7f, Delta(writes, 0, 0), 5);
        }

        [Fact]
        public void SetHeight_targets_the_inspector_height_over_the_base()
        {
            Func<float, float, float> baseTwo = static (_, _) => 2f;
            var writes = TerrainSculptBrush.ComputeDab(SculptBrush.SetHeight, 0f, 0f, 4f, 100f, 1f,
                setHeight: 10f, flattenTarget: 0f, 1f, Wide, Zero, baseTwo);
            Assert.Equal(8f, Delta(writes, 0, 0), 5);   // 10 - 2
        }

        [Fact]
        public void ComputeDab_is_deterministic()
        {
            var a = TerrainSculptBrush.ComputeDab(SculptBrush.Raise, 1.3f, -2.7f, 3.1f, 2.5f, 0.016f,
                0f, 0f, 0.5f, Wide, Zero, FlatBase);
            var b = TerrainSculptBrush.ComputeDab(SculptBrush.Raise, 1.3f, -2.7f, 3.1f, 2.5f, 0.016f,
                0f, 0f, 0.5f, Wide, Zero, FlatBase);
            Assert.Equal(a.Count, b.Count);
            Assert.True(a.SequenceEqual(b));
        }

        [Fact]
        public void SculptBounds_covers_whole_tiles_only()
        {
            // 512 x 512 at cellSize 0.5 = tiles 0..31 (cells 0..1023), each tile's cell-centre extent within bounds.
            var b = SculptBounds.FromBounds(0f, 0f, 512f, 512f, 0.5f);
            Assert.True(b.HasArea);
            Assert.Equal(0, b.MinCellX);
            Assert.Equal(1023, b.MaxCellX);

            // A document smaller than one tile (16 world units at 0.5) has no paintable region.
            var tiny = SculptBounds.FromBounds(0f, 0f, 4f, 4f, 0.5f);
            Assert.False(tiny.HasArea);
        }
    }
}
