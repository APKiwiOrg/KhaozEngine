using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Records every sink op so tests can assert load/unload/relod behaviour with no GPU.
    sealed class FakeChunkSink : IChunkSink
    {
        public readonly List<(ChunkCoord coord, int lod)> Loads = new();
        public readonly List<(ChunkCoord coord, int lod)> ReLods = new();
        public readonly List<ChunkCoord> Unloads = new();
        // Per-Update op counts (load + relod), reset by the test harness between Updates.
        public int OpsThisFrame;

        public object Load(ChunkCoord coord, int lod) { Loads.Add((coord, lod)); OpsThisFrame++; return new Box(coord); }
        public void ReLod(ChunkCoord coord, object handle, int lod) { ReLods.Add((coord, lod)); OpsThisFrame++; }
        public void Unload(ChunkCoord coord, object handle) { Unloads.Add(coord); }

        public void ResetFrame() => OpsThisFrame = 0;
        sealed class Box { public Box(ChunkCoord c) { Coord = c; } public ChunkCoord Coord; }
    }

    public class TerrainStreamerTests
    {
        static TerrainStreamer Pump(TerrainStreamer s, FakeChunkSink sink, Vector3 pos, int frames)
        {
            for (int i = 0; i < frames; i++) { sink.ResetFrame(); s.Update(pos, 1f / 60f); }
            return s;
        }

        static HashSet<ChunkCoord> ExpectedDisk(ChunkCoord center, int radius)
        {
            var set = new HashSet<ChunkCoord>();
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dz * dz <= radius * radius)
                        set.Add(new ChunkCoord(center.X + dx, center.Z + dz));
            return set;
        }

        [Fact]
        public void Loaded_fills_the_expected_disk_after_draining()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 2);   // player at center of chunk (0,0)

            var expected = ExpectedDisk(new ChunkCoord(0, 0), 3);
            Assert.Equal(expected, new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void Moving_loads_new_and_unloads_old()
        {
            var cfg = new StreamerConfig(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, new Vector3(30f, 0f, 30f), 2);      // centered on chunk (0,0)

            // Teleport far enough (chunk 20) that the whole old disk is beyond UnloadRadius: clean swap, no
            // hysteresis survivors, so the new loaded set is exactly the fresh load disk.
            Pump(s, sink, new Vector3(20 * 60f + 30f, 0f, 30f), 4);
            var after = new HashSet<ChunkCoord>(s.Loaded);

            Assert.Equal(ExpectedDisk(new ChunkCoord(20, 0), 2), after);
            Assert.Contains(new ChunkCoord(20, 0), after);      // newly in range
            Assert.DoesNotContain(new ChunkCoord(0, 0), after); // old center beyond UnloadRadius -> gone
            Assert.True(sink.Unloads.Count > 0);
        }

        [Fact]
        public void Oscillating_across_a_boundary_does_not_churn()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            // Prime the union of both sides' disks by visiting each extreme until the loaded set is stable
            // (the one-time leading-edge expansion happens here, not during the oscillation we measure).
            Pump(s, sink, new Vector3(59f, 0f, 30f), 3);   // chunk (0,0)
            Pump(s, sink, new Vector3(61f, 0f, 30f), 3);   // chunk (1,0)
            Pump(s, sink, new Vector3(59f, 0f, 30f), 3);   // back to chunk (0,0); union now loaded
            int loadsAfterPrime = sink.Loads.Count;
            int unloadsAfterPrime = sink.Unloads.Count;

            // Oscillate across the x=60 boundary (chunk 0 <-> chunk 1) many times.
            for (int i = 0; i < 20; i++)
            {
                Pump(s, sink, new Vector3(61f, 0f, 30f), 1);   // now in chunk (1,0)
                Pump(s, sink, new Vector3(59f, 0f, 30f), 1);   // back in chunk (0,0)
            }

            // The hysteresis band absorbs the oscillation: no chunk is re-loaded and none is unloaded (no churn).
            Assert.Equal(loadsAfterPrime, sink.Loads.Count);
            Assert.Equal(unloadsAfterPrime, sink.Unloads.Count);
        }

        [Fact]
        public void Requested_lod_matches_PickLod_of_center_distance()
        {
            var cfg = new StreamerConfig(LoadRadius: 5, UnloadRadius: 7, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var pos = new Vector3(30f, 0f, 30f);

            Pump(s, sink, pos, 3);

            foreach (ChunkCoord c in s.Loaded)
            {
                Vector2 center = ChunkGrid.CenterOf(c, 60f);
                float dist = Vector2.Distance(new Vector2(pos.X, pos.Z), center);
                Assert.Equal(TerrainLod.PickLod(dist), s.LodOf(c));
            }
        }

        [Fact]
        public void Approaching_a_far_chunk_triggers_a_ReLod_to_a_finer_tier()
        {
            var cfg = new StreamerConfig(LoadRadius: 6, UnloadRadius: 8, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            // Pick a target chunk and stand far enough that it loads at a coarse tier (LOD 2, dist > 200 m).
            var target = new ChunkCoord(5, 0);
            // Player at chunk (-1,0): target center is at x=330, player near x=-30 -> ~360 m -> LOD 2.
            Pump(s, sink, new Vector3(-30f, 0f, 30f), 4);
            Assert.Equal(2, s.LodOf(target));

            // Walk toward the target so its center distance drops into a finer tier, expect a ReLod for it.
            sink.ReLods.Clear();
            Pump(s, sink, new Vector3(5 * 60f + 30f, 0f, 30f), 6);   // stand on the target chunk -> LOD 0
            Assert.Equal(0, s.LodOf(target));
            Assert.Contains(sink.ReLods, r => r.coord == target);
        }

        [Fact]
        public void At_most_MaxLoadsPerFrame_ops_per_update_and_backlog_drains()
        {
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var pos = new Vector3(30f, 0f, 30f);

            int fullDisk = ExpectedDisk(new ChunkCoord(0, 0), 4).Count;

            // First Update from empty: only MaxLoadsPerFrame ops happen.
            sink.ResetFrame();
            s.Update(pos, 1f / 60f);
            Assert.True(sink.OpsThisFrame <= cfg.MaxLoadsPerFrame);
            Assert.Equal(cfg.MaxLoadsPerFrame, s.Loaded.Count);  // only the budget loaded so far

            // Keep pumping; every frame stays within budget and the disk eventually fills.
            for (int i = 0; i < 50 && s.Loaded.Count < fullDisk; i++)
            {
                sink.ResetFrame();
                s.Update(pos, 1f / 60f);
                Assert.True(sink.OpsThisFrame <= cfg.MaxLoadsPerFrame, $"frame {i} exceeded budget: {sink.OpsThisFrame}");
            }
            Assert.Equal(fullDisk, s.Loaded.Count);              // backlog drained
        }

        [Fact]
        public void Nearest_chunk_loads_first()
        {
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 1, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            sink.ResetFrame();
            s.Update(new Vector3(30f, 0f, 30f), 1f / 60f);   // standing on chunk (0,0)

            Assert.Single(sink.Loads);
            Assert.Equal(new ChunkCoord(0, 0), sink.Loads[0].coord);   // the player's own chunk first
        }
    }
}
