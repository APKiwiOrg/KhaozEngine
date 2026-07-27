using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers the sculpt handoff residency needs: <see cref="TerrainField.SetSculpt"/>'s atomic snapshot
    /// swap, its reproduction of the constructor's empty-normalizes-to-null rule, the read-ONCE discipline that
    /// makes a normal belong to exactly one snapshot, and <see cref="TerrainSculpt.With"/>'s array sharing.</summary>
    public class TerrainSculptSwapTests
    {
        const int N = TerrainSculpt.TileSize;

        // A delta tile ramping linearly in local X, so a sampled height names which snapshot produced it.
        static float[] Ramp(float slope)
        {
            var d = new float[N * N];
            for (int z = 0; z < N; z++)
            for (int x = 0; x < N; x++)
                d[z * N + x] = slope * x;
            return d;
        }

        static TerrainSculpt SculptRamp(float cellSize, float slope) =>
            new(cellSize, new[] { new TerrainSculptTile(0, 0, Ramp(slope)) });

        // Flat analytic terrain, so the sampled height IS the sculpt delta and nothing else can move it.
        static TerrainConfig FlatConfig(ITerrainFeature[]? features = null) => new()
        {
            GentleAmplitude = 0f,
            WaterLevel = 0f,
            Features = features,
            Biomes = new[]
            {
                new BiomeBand
                {
                    Start = float.NegativeInfinity, End = float.PositiveInfinity,
                    Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f,
                },
            },
        };

        // Analytic height = |x|, a KINK rather than a slope, which is the only shape whose central difference
        // depends on the step size. That is what makes the 1 m analytic epsilon observable from outside.
        sealed class AbsXFeature : ITerrainFeature
        {
            public float Apply(float x, float z, float h) => MathF.Abs(x);
        }

        // Swaps the field's sculpt from inside SampleHeight, on the first Apply call only. TerrainField folds
        // features BEFORE adding the sculpt delta, so this lands a swap in the middle of a single SampleNormal.
        sealed class SwapOnFirstApply : ITerrainFeature
        {
            public TerrainField? Field;
            public TerrainSculpt? To;
            int _fired;

            public float Apply(float x, float z, float h)
            {
                if (Interlocked.Exchange(ref _fired, 1) == 0) Field!.SetSculpt(To);
                return h;
            }

            public int Fired => _fired;
        }

        [Fact]
        public void SetSculpt_SwapsTheSampledLayer()
        {
            TerrainSculpt a = SculptRamp(1f, 2f);
            TerrainSculpt b = SculptRamp(1f, -2f);
            var field = new TerrainField(FlatConfig(), a);

            Assert.Equal(2f * 15.5f, field.SampleHeight(15.5f, 15.5f), 3);
            field.SetSculpt(b);
            Assert.Equal(-2f * 15.5f, field.SampleHeight(15.5f, 15.5f), 3);
            field.SetSculpt(a);
            Assert.Equal(2f * 15.5f, field.SampleHeight(15.5f, 15.5f), 3);
        }

        [Fact]
        public void SetSculpt_NormalizesAnEmptySculptToNull()
        {
            // The observable consequence of the constructor's rule is the NORMAL EPSILON, not the height: a stored
            // empty sculpt would keep the cell-size step where a null one goes back to the analytic 1 m.
            var field = new TerrainField(FlatConfig(new ITerrainFeature[] { new AbsXFeature() }), SculptRamp(4f, 0f));

            // eps = CellSize = 4: the kink at x = 0 sits inside the +/-4 m taps, so the slope reads (6 - 2) / 8.
            Assert.Equal(4f, SlopeStepOf(field), 3);

            field.SetSculpt(new TerrainSculpt(4f, Array.Empty<TerrainSculptTile>()));   // EMPTY, not null
            Assert.Equal(1f, SlopeStepOf(field), 3);

            field.SetSculpt(SculptRamp(4f, 0f));
            Assert.Equal(4f, SlopeStepOf(field), 3);

            field.SetSculpt(null);
            Assert.Equal(1f, SlopeStepOf(field), 3);
        }

        // Recovers the epsilon SampleNormal used, by inverting the central difference over the |x| kink at a probe
        // 2 m to the +X side: (|2 + eps| - |2 - eps|) / (2 * eps) is 1 while eps <= 2 and 2 / eps beyond it.
        static float SlopeStepOf(TerrainField field)
        {
            Vector3 n = field.SampleNormal(2f, 0f);
            float slope = -n.X / n.Y;
            return slope >= 0.999f ? 1f : 2f / slope;
        }

        [Fact]
        public void SampleNormal_UsesOneSnapshotEvenWhenSwappedMidCall()
        {
            // The load-bearing half of the concurrency story, made DETERMINISTIC: the feature swaps the sculpt
            // during the normal's first height tap. Reading the field once per call means all four taps and the
            // epsilon come from the pre-swap snapshot, so the normal is exactly the old one. An implementation
            // that re-read the field per tap would mix two snapshots into a third surface belonging to neither.
            var swapper = new SwapOnFirstApply();
            TerrainSculpt a = SculptRamp(1f, 2f);
            TerrainSculpt b = SculptRamp(1f, -2f);
            var field = new TerrainField(FlatConfig(new ITerrainFeature[] { swapper }), a);
            swapper.Field = field;
            swapper.To = b;

            Vector3 expectedA = Vector3.Normalize(new Vector3(-2f, 1f, 0f));
            Vector3 got = field.SampleNormal(15.5f, 15.5f);

            Assert.Equal(1, swapper.Fired);                                  // the swap really happened mid-call
            Assert.Equal(expectedA.X, got.X, 4);
            Assert.Equal(expectedA.Y, got.Y, 4);
            Assert.Equal(expectedA.Z, got.Z, 4);
            // And the NEXT call sees the new snapshot whole.
            Vector3 expectedB = Vector3.Normalize(new Vector3(2f, 1f, 0f));
            Vector3 after = field.SampleNormal(15.5f, 15.5f);
            Assert.Equal(expectedB.X, after.X, 4);
        }

        [Fact]
        public async Task SetSculpt_IsSafeAgainstAConcurrentSampler()
        {
            // Hammer SampleHeight AND SampleNormal on worker threads across a stream of swaps, and assert every
            // sampled height and every sampled normal belongs to one of the two snapshots. The SampleNormal arm is
            // the one that matters: a SampleHeight-only test passes against an implementation that reads the field
            // five times per normal.
            TerrainSculpt a = SculptRamp(1f, 2f);
            TerrainSculpt b = SculptRamp(1f, -2f);
            var field = new TerrainField(FlatConfig(), a);

            const float px = 15.5f, pz = 15.5f;
            float heightA = 2f * px, heightB = -2f * px;
            Vector3 normalA = Vector3.Normalize(new Vector3(-2f, 1f, 0f));
            Vector3 normalB = Vector3.Normalize(new Vector3(2f, 1f, 0f));

            const int perWorker = 50_000;
            var torn = 0;
            var samples = 0L;

            // Fixed iteration counts on the workers, and the swapper runs until they are done. A cancellation
            // token would let the whole test finish before a worker thread even started, and a green run that
            // sampled nothing is worse than a red one.
            Task[] workers = new Task[4];
            for (int w = 0; w < workers.Length; w++)
            {
                workers[w] = Task.Run(() =>
                {
                    for (int i = 0; i < perWorker; i++)
                    {
                        float h = field.SampleHeight(px, pz);
                        if (MathF.Abs(h - heightA) > 1e-3f && MathF.Abs(h - heightB) > 1e-3f)
                            Interlocked.Increment(ref torn);

                        Vector3 n = field.SampleNormal(px, pz);
                        if (Vector3.Distance(n, normalA) > 1e-3f && Vector3.Distance(n, normalB) > 1e-3f)
                            Interlocked.Increment(ref torn);
                    }
                    Interlocked.Add(ref samples, perWorker);
                });
            }

            Task all = Task.WhenAll(workers);
            for (long i = 0; !all.IsCompleted; i++) field.SetSculpt(i % 2 == 0 ? b : a);
            await all;

            Assert.Equal(0, Volatile.Read(ref torn));
            Assert.Equal(workers.Length * (long)perWorker, Interlocked.Read(ref samples));
        }

        [Fact]
        public void With_SharesUnchangedTileArrays()
        {
            // Sharing is the whole point (O(tile count), not O(cell count)), and the way to OBSERVE it is the
            // documented ownership rule read backwards: mutate the array afterwards and the new snapshot sees it.
            float[] kept = Ramp(2f);
            var original = new TerrainSculpt(1f, new[] { new TerrainSculptTile(0, 0, kept) });

            TerrainSculpt grown = original.With(new[] { new TerrainSculptTile(1, 0, Ramp(3f)) }, remove: null);

            Assert.Equal(2, grown.TileCount);
            Assert.Equal(1, original.TileCount);                       // the original snapshot is untouched
            Assert.Equal(2f * 15f, grown.SampleHeightAtCell(15), 3);

            kept[15] = 999f;                                           // in-place edit of the SHARED array
            Assert.Equal(999f, grown.SampleHeightAtCell(15), 3);
            Assert.Equal(999f, original.SampleHeightAtCell(15), 3);
        }

        [Fact]
        public void With_RemovesTilesAndAppliesRemovalsBeforeAdditions()
        {
            var original = new TerrainSculpt(1f, new[]
            {
                new TerrainSculptTile(0, 0, Ramp(2f)),
                new TerrainSculptTile(1, 0, Ramp(3f)),
            });

            TerrainSculpt shrunk = original.With(add: null, remove: new[] { (1, 0) });
            Assert.Equal(1, shrunk.TileCount);
            Assert.Equal(2, original.TileCount);

            // A coordinate in BOTH lists ends up with the added tile: removals apply first.
            TerrainSculpt replaced = original.With(new[] { new TerrainSculptTile(0, 0, Ramp(5f)) }, new[] { (0, 0) });
            Assert.Equal(2, replaced.TileCount);
            Assert.Equal(5f * 15f, replaced.SampleHeightAtCell(15), 3);

            // Removing something that is not stored is a no-op, not an error.
            Assert.Equal(2, original.With(null, new[] { (99, 99) }).TileCount);
            // Both lists null is an identical-content copy.
            Assert.Equal(2, original.With(null, null).TileCount);
        }
    }

    static class SculptProbe
    {
        /// <summary>The delta exactly at a cell centre of tile (0, 0), where bilinear interpolation returns the
        /// stored value itself.</summary>
        internal static float SampleHeightAtCell(this TerrainSculpt sculpt, int cellX) =>
            sculpt.SampleDelta(cellX * sculpt.CellSize, 0f);
    }
}
