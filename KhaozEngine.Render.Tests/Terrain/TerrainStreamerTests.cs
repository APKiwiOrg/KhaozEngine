using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // Records every sink op so tests can assert load/unload/relod behaviour with no GPU. Implements IDisposable so
    // the TerrainStreamer.Dispose() "dispose the sink it owns" path is observable headless (DisposeCount).
    sealed class FakeChunkSink : IChunkSink, IDisposable
    {
        public readonly List<(ChunkCoord coord, int lod, ChunkRing ring)> Loads = new();
        public readonly List<(ChunkCoord coord, int lod, ChunkRing ring)> ReLods = new();
        public readonly List<ChunkCoord> Unloads = new();
        // Per-Update op counts (load + relod), reset by the test harness between Updates.
        public int OpsThisFrame;
        public int DisposeCount;

        public object Load(ChunkCoord coord, int lod, ChunkRing ring) { Loads.Add((coord, lod, ring)); OpsThisFrame++; return new Box(coord); }
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) { ReLods.Add((coord, lod, ring)); OpsThisFrame++; }
        public void Unload(ChunkCoord coord, object handle) { Unloads.Add(coord); }
        public void Dispose() => DisposeCount++;

        public void ResetFrame() => OpsThisFrame = 0;
        sealed class Box { public Box(ChunkCoord c) { Coord = c; } public ChunkCoord Coord; }
    }

    // Forwards every op to a real Scene3DChunkSink but remembers each coord's opaque handle, so a test can inspect
    // the production ChunkLoad after streamer ops. TerrainStreamer itself only tracks coord + LOD internally, never
    // the handle, so there is no other way to reach it from outside the sink.
    sealed class HandleTrackingSink : IChunkSink
    {
        readonly Scene3DChunkSink _inner;
        readonly Dictionary<ChunkCoord, object> _handles = new();

        public HandleTrackingSink(Scene3DChunkSink inner) => _inner = inner;

        public object Load(ChunkCoord coord, int lod, ChunkRing ring)
        {
            object handle = _inner.Load(coord, lod, ring);
            _handles[coord] = handle;
            return handle;
        }

        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => _inner.ReLod(coord, handle, lod, ring);

        public void Unload(ChunkCoord coord, object handle)
        {
            _inner.Unload(coord, handle);
            _handles.Remove(coord);
        }

        public Scene3DChunkSink.ChunkLoad HandleFor(ChunkCoord coord) => (Scene3DChunkSink.ChunkLoad)_handles[coord];
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

        // --- Unload budget (a ring shift spread over frames) --------------------------------------------------------
        // Loads were budgeted from the start, unloads were not, so one Update could free the whole outgoing ring.

        static readonly Vector3 UnloadHome = new(30f, 0f, 30f);            // chunk (0,0)
        static readonly Vector3 UnloadAway = new(9 * 60f + 30f, 0f, 30f);  // chunk (9,0), clear of a radius-3 disk

        [Fact]
        public void At_most_MaxUnloadsPerFrame_chunks_unload_per_update_and_the_backlog_drains()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                MaxUnloadsPerFrame: 2);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, UnloadHome, 3);

            // Not vacuous: the outgoing ring needs several frames' worth of budget to clear.
            Assert.True(s.Loaded.Count > 2 * cfg.MaxUnloadsPerFrame, $"only {s.Loaded.Count} chunks loaded");

            for (int i = 0; i < 40; i++)
            {
                int before = sink.Unloads.Count;
                s.Update(UnloadAway, 1f / 60f);
                int landed = sink.Unloads.Count - before;
                Assert.True(landed <= cfg.MaxUnloadsPerFrame, $"frame {i} unloaded {landed}, budget is {cfg.MaxUnloadsPerFrame}");
            }

            // Every out-of-range chunk went eventually, and only the new disk is left.
            Assert.Equal(ExpectedDisk(new ChunkCoord(9, 0), 3), new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void The_unload_budget_frees_the_farthest_chunks_first()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                MaxUnloadsPerFrame: 1);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, UnloadHome, 3);
            sink.Unloads.Clear();

            s.Update(UnloadAway, 1f / 60f);

            // Of the radius-3 disk around (0,0), (-3,0) is the single farthest chunk from (9,0), so it goes first.
            Assert.Single(sink.Unloads);
            Assert.Equal(new ChunkCoord(-3, 0), sink.Unloads[0]);
        }

        [Fact]
        public void A_chunk_that_returns_to_range_before_its_turn_is_never_unloaded_or_reloaded()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                MaxUnloadsPerFrame: 1);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, UnloadHome, 3);

            // (3,0) is the NEAREST of the old disk to the away position, so a budget of 1 never reaches it in
            // two frames: it spends them queued for an unload that has not landed.
            var target = new ChunkCoord(3, 0);
            Assert.Contains(target, s.Loaded);
            Pump(s, sink, UnloadAway, 2);
            Assert.Contains(target, s.Loaded);

            Pump(s, sink, UnloadHome, 3);   // back in range before its turn came up

            Assert.Contains(target, s.Loaded);
            Assert.DoesNotContain(target, sink.Unloads);                    // the queued unload was simply dropped
            Assert.Equal(1, sink.Loads.Count(l => l.coord == target));      // and it was never re-loaded
        }

        [Fact]
        public void UnloadAll_ignores_the_unload_budget()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                MaxUnloadsPerFrame: 1);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, UnloadHome, 3);

            var loaded = new HashSet<ChunkCoord>(s.Loaded);
            Assert.True(loaded.Count > 1);
            int before = sink.Unloads.Count;

            s.UnloadAll();   // the teleport contract: total and immediate, whatever the per-frame budget says

            Assert.Equal(loaded, new HashSet<ChunkCoord>(sink.Unloads.Skip(before)));
            Assert.Empty(s.Loaded);
        }

        [Fact]
        public void A_non_positive_unload_budget_clears_the_ring_in_one_update()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                MaxUnloadsPerFrame: 0);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, UnloadHome, 3);
            var old = new HashSet<ChunkCoord>(s.Loaded);
            sink.Unloads.Clear();

            s.Update(UnloadAway, 1f / 60f);   // opted out: the pre-budget behaviour, everything at once

            Assert.Equal(old, new HashSet<ChunkCoord>(sink.Unloads));
        }

        // --- PrimeAround settles on work done, not on the resident count --------------------------------------------
        // Once unloads are budgeted, a pass whose unloads cancel out its loads leaves the resident count unchanged
        // while the ring still has holes in it and stale chunks resident, so a count-based settle exits right there.

        [Fact]
        public void PrimeAround_over_a_displaced_ring_fills_it_and_leaves_no_stale_chunk()
        {
            // Equal budgets, so the first pass's 8 unloads cancel its 8 loads exactly.
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 8, ChunkSize: 60f,
                MaxUnloadsPerFrame: 8);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            s.PrimeAround(UnloadHome);
            var home = ExpectedDisk(new ChunkCoord(0, 0), 3);
            Assert.Equal(home, new HashSet<ChunkCoord>(s.Loaded));
            // Not vacuous: the away ring is clear of the home ring, so the whole of it goes stale at once and one
            // pass of either budget cannot clear it.
            Assert.True(home.Count > cfg.MaxUnloadsPerFrame, $"only {home.Count} chunks primed");

            s.PrimeAround(UnloadAway);

            Assert.Equal(ExpectedDisk(new ChunkCoord(9, 0), 3), new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void PrimeAround_fills_the_ring_when_the_load_budget_undercuts_the_unload_budget()
        {
            // Unequal budgets settle later instead of never: the counts drift for a few passes, then the pass that
            // frees the last of the stale chunks happens to free exactly as many as it loads.
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 5, ChunkSize: 60f,
                MaxUnloadsPerFrame: 8);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            s.PrimeAround(UnloadHome);
            s.PrimeAround(UnloadAway);

            Assert.Equal(ExpectedDisk(new ChunkCoord(9, 0), 3), new HashSet<ChunkCoord>(s.Loaded));
        }

        // --- LOD hysteresis (a dead zone at every tier boundary) ----------------------------------------------------
        // A chunk parked near 80 m used to re-LOD on every small move, and each re-LOD frees a live GPU mesh.

        // Standing in chunk (1,0), x = 61 and x = 79 put chunk (2,0)'s centre (x = 150) at 89 m and 71 m: either side
        // of the 80 m tier boundary, without the player's own chunk or any residency ring changing.
        static readonly Vector3 NearSideOfBoundary = new(79f, 0f, 30f);
        static readonly Vector3 FarSideOfBoundary = new(61f, 0f, 30f);

        [Fact]
        public void Small_moves_across_a_LOD_boundary_do_not_re_lod()
        {
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, FarSideOfBoundary, 3);
            sink.ReLods.Clear();

            for (int i = 0; i < 20; i++)
            {
                Pump(s, sink, NearSideOfBoundary, 1);
                Pump(s, sink, FarSideOfBoundary, 1);
            }

            Assert.Empty(sink.ReLods);
        }

        [Fact]
        public void Without_hysteresis_the_same_shuffle_churns_re_lods()
        {
            // Not vacuous: the shuffle really does straddle a boundary, so with the dead zone off it churns.
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                LodHysteresis: 0f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, FarSideOfBoundary, 3);
            sink.ReLods.Clear();

            for (int i = 0; i < 20; i++)
            {
                Pump(s, sink, NearSideOfBoundary, 1);
                Pump(s, sink, FarSideOfBoundary, 1);
            }

            Assert.Contains(sink.ReLods, r => r.coord == new ChunkCoord(2, 0));
        }

        [Fact]
        public void A_move_clear_of_the_margin_still_re_lods()
        {
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var target = new ChunkCoord(2, 0);

            Pump(s, sink, FarSideOfBoundary, 3);          // target centre at 89 m -> tier 1
            Assert.Equal(1, s.LodOf(target));
            sink.ReLods.Clear();

            Pump(s, sink, new Vector3(85f, 0f, 30f), 2);  // 65 m: past 80 - 10, so the dead zone yields

            Assert.Equal(0, s.LodOf(target));
            Assert.Contains(sink.ReLods, r => r.coord == target);
        }

        [Fact]
        public void First_load_picks_the_stateless_tier_whatever_the_margin()
        {
            var cfg = new StreamerConfig(LoadRadius: 5, UnloadRadius: 7, MaxLoadsPerFrame: 1000, ChunkSize: 60f,
                LodHysteresis: 40f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var pos = new Vector3(30f, 0f, 30f);

            Pump(s, sink, pos, 3);

            // A viewer that has not moved sees exactly today's tiers: hysteresis only ever damps a CHANGE.
            foreach (ChunkCoord c in s.Loaded)
            {
                Vector2 center = ChunkGrid.CenterOf(c, 60f);
                Assert.Equal(TerrainLod.PickLod(Vector2.Distance(new Vector2(pos.X, pos.Z), center)), s.LodOf(c));
            }
        }

        // --- Decor ring (far/render-only chunks) --------------------------------------------------------------------

        [Fact]
        public void Decor_ring_loads_render_only_chunks_beyond_the_gameplay_radius()
        {
            // Gameplay radius 2, decor radius 5: chunks within 2 are Gameplay, chunks in (2,5] are render-only Decor.
            var cfg = new StreamerConfig(LoadRadius: 2, UnloadRadius: 7, MaxLoadsPerFrame: 10000, ChunkSize: 60f, DecorRadius: 5);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 3);   // player at center of chunk (0,0)

            // Chunks load out to the OUTER (decor) radius, not just the gameplay radius.
            Assert.Contains(new ChunkCoord(4, 0), s.Loaded);       // chunk distance 4: inside decor, outside gameplay
            Assert.Contains(new ChunkCoord(0, 0), s.Loaded);

            foreach (ChunkCoord c in s.Loaded)
            {
                int d2 = c.X * c.X + c.Z * c.Z;
                ChunkRing expected = d2 <= 2 * 2 ? ChunkRing.Gameplay : ChunkRing.Decor;
                Assert.Equal(expected, s.RingOf(c));
            }
            // The far chunk is decor, the origin is gameplay: not a vacuous all-one-ring assertion.
            Assert.Equal(ChunkRing.Decor, s.RingOf(new ChunkCoord(4, 0)));
            Assert.Equal(ChunkRing.Gameplay, s.RingOf(new ChunkCoord(0, 0)));
            // Every recorded load carried the ring the streamer resolved for that chunk.
            foreach (var l in sink.Loads)
            {
                int d2 = (l.coord.X) * (l.coord.X) + (l.coord.Z) * (l.coord.Z);
                Assert.Equal(d2 <= 4 ? ChunkRing.Gameplay : ChunkRing.Decor, l.ring);
            }
        }

        [Fact]
        public void Decor_chunk_upgrades_to_gameplay_on_approach_and_downgrades_on_retreat()
        {
            var cfg = new StreamerConfig(LoadRadius: 2, UnloadRadius: 9, MaxLoadsPerFrame: 10000, ChunkSize: 60f, DecorRadius: 5);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var target = new ChunkCoord(4, 0);

            // Stand at the origin: target chunk-distance 4 -> render-only Decor.
            Pump(s, sink, new Vector3(30f, 0f, 30f), 3);
            Assert.Equal(ChunkRing.Decor, s.RingOf(target));

            // Walk onto the target chunk: distance 0 -> Gameplay. Expect a re-LOD that upgrades its ring.
            sink.ReLods.Clear();
            Pump(s, sink, new Vector3(4 * 60f + 30f, 0f, 30f), 4);
            Assert.Equal(ChunkRing.Gameplay, s.RingOf(target));
            Assert.Contains(sink.ReLods, r => r.coord == target && r.ring == ChunkRing.Gameplay);

            // Retreat to the origin: target back to Decor via a downgrade re-LOD.
            sink.ReLods.Clear();
            Pump(s, sink, new Vector3(30f, 0f, 30f), 4);
            Assert.Equal(ChunkRing.Decor, s.RingOf(target));
            Assert.Contains(sink.ReLods, r => r.coord == target && r.ring == ChunkRing.Decor);
        }

        [Fact]
        public void Far_chunks_select_the_coarse_far_tiers_across_the_extended_table()
        {
            // A big decor radius reaches metre distances that only the default config's far tiers (8, then 4 segments)
            // cover, so LodOf must track PickLod all the way out - not saturate at the old terminal tier 2.
            var cfg = new StreamerConfig(LoadRadius: 4, UnloadRadius: 24, MaxLoadsPerFrame: 100000, ChunkSize: 60f, DecorRadius: 20);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            var pos = new Vector3(30f, 0f, 30f);

            Pump(s, sink, pos, 3);

            TerrainLodConfig lod = TerrainLodConfig.Default;
            bool sawTier3 = false, sawTier4 = false;
            foreach (ChunkCoord c in s.Loaded)
            {
                Vector2 center = ChunkGrid.CenterOf(c, 60f);
                float dist = Vector2.Distance(new Vector2(pos.X, pos.Z), center);
                int expected = lod.PickLod(dist);
                Assert.Equal(expected, s.LodOf(c));
                if (expected == 3) sawTier3 = true;
                if (expected == 4) sawTier4 = true;
            }
            Assert.True(sawTier3, "the 20-chunk decor disk should include tier-3 (8-segment) chunks");
            Assert.True(sawTier4, "the 20-chunk decor disk should include tier-4 (4-segment) chunks");
        }

        [Fact]
        public void Ctor_rejects_a_degenerate_hysteresis_band()
        {
            // UnloadRadius must exceed the OUTER load radius (max of gameplay + decor), else oscillation churns.
            Assert.Throws<ArgumentException>(() =>
                new TerrainStreamer(new StreamerConfig(LoadRadius: 4, UnloadRadius: 4, MaxLoadsPerFrame: 3, ChunkSize: 60f), new FakeChunkSink()));
            // A decor radius past the unload radius is the same failure.
            Assert.Throws<ArgumentException>(() =>
                new TerrainStreamer(new StreamerConfig(LoadRadius: 4, UnloadRadius: 6, MaxLoadsPerFrame: 3, ChunkSize: 60f, DecorRadius: 6), new FakeChunkSink()));
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

        [Fact]
        public void Sink_scatters_props_matching_PropScatter_for_the_chunk_area()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig scatter = ScatterConfig.ForestRing();
            float size = 60f;
            var sink = new Scene3DChunkSink(scene: null!, field, scatter,
                propMeshes: new Dictionary<string, MeshHandle>(), chunkSize: size, propDrawRadius: 90f);

            var coord = new ChunkCoord(-2, -2);   // a meadow chunk with props
            var expected = PropScatter.Generate(field, scatter, ChunkGrid.AreaOf(coord, size));
            IReadOnlyList<PropPlacement> got = sink.ScatterFor(coord);

            Assert.Equal(expected.Count, got.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Id, got[i].Id);
                Assert.Equal(expected[i].X, got[i].X, 3);
                Assert.Equal(expected[i].Z, got[i].Z, 3);
            }
        }

        [Fact]
        public void UnloadAll_unloads_every_loaded_chunk_and_empties_the_ring()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 2);   // fill the load disk

            var loaded = new HashSet<ChunkCoord>(s.Loaded);
            Assert.NotEmpty(loaded);                               // not vacuous
            int unloadsBefore = sink.Unloads.Count;

            s.UnloadAll();

            // Every chunk that was loaded got exactly one Unload, and the ring is now empty.
            var freshUnloads = new HashSet<ChunkCoord>(sink.Unloads.Skip(unloadsBefore));
            Assert.Equal(loaded, freshUnloads);
            Assert.Empty(s.Loaded);
            Assert.Equal(0, sink.DisposeCount);                    // UnloadAll keeps the sink alive (rebuild-same-sink path)
        }

        [Fact]
        public void UnloadAll_then_rebuild_with_the_same_sink_reloads_the_ring()
        {
            var cfg = new StreamerConfig(LoadRadius: 2, UnloadRadius: 4, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var pos = new Vector3(30f, 0f, 30f);

            var first = new TerrainStreamer(cfg, sink);
            Pump(first, sink, pos, 2);
            var loaded = new HashSet<ChunkCoord>(first.Loaded);
            first.UnloadAll();
            Assert.Empty(first.Loaded);

            // Same sink survives, so a fresh streamer can reload the same ring (no GPU leak between rebuilds).
            var second = new TerrainStreamer(cfg, sink);
            Pump(second, sink, pos, 2);
            Assert.Equal(loaded, new HashSet<ChunkCoord>(second.Loaded));
            Assert.Equal(0, sink.DisposeCount);
        }

        [Fact]
        public void Dispose_flushes_the_ring_and_disposes_the_owned_sink_idempotently()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, new Vector3(30f, 0f, 30f), 2);

            var loaded = new HashSet<ChunkCoord>(s.Loaded);
            Assert.NotEmpty(loaded);
            int unloadsBefore = sink.Unloads.Count;

            s.Dispose();

            Assert.Equal(loaded, new HashSet<ChunkCoord>(sink.Unloads.Skip(unloadsBefore)));   // ring flushed
            Assert.Empty(s.Loaded);
            Assert.Equal(1, sink.DisposeCount);                   // owned sink disposed once

            s.Dispose();                                          // second dispose is a no-op
            Assert.Equal(1, sink.DisposeCount);
            Assert.Equal(loaded.Count, sink.Unloads.Count - unloadsBefore);   // no re-unload
        }

        [Fact]
        public void Invalidate_Rect_ReLodsOnlyLoadedChunksIntersecting()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 2);   // fills the disk around chunk (0,0)

            var target = new ChunkCoord(1, 1);
            Assert.Contains(target, s.Loaded);
            var loadedBefore = new HashSet<ChunkCoord>(s.Loaded);
            int lodBefore = s.LodOf(target);

            sink.ReLods.Clear();
            // Rect strictly inside chunk (1,1)'s world bounds [60,120)x[60,120): no border touch, so exactly one chunk.
            s.Invalidate(new RectArea(70f, 70f, 110f, 110f));

            Assert.Single(sink.ReLods);
            Assert.Equal(target, sink.ReLods[0].coord);
            Assert.Equal(lodBefore, sink.ReLods[0].lod);
            Assert.Equal(loadedBefore, new HashSet<ChunkCoord>(s.Loaded));   // ring untouched
        }

        [Fact]
        public void Invalidate_Rect_OnChunkBorder_IncludesBothChunks()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 2);   // fills the disk around chunk (0,0)

            Assert.Contains(new ChunkCoord(0, 0), s.Loaded);
            Assert.Contains(new ChunkCoord(1, 0), s.Loaded);

            sink.ReLods.Clear();
            // X spans exactly [0,60]: the seam between chunk 0 and chunk 1. Z stays inside chunk row 0.
            s.Invalidate(new RectArea(0f, 10f, 60f, 20f));

            var got = new HashSet<ChunkCoord>(sink.ReLods.Select(r => r.coord));
            Assert.Equal(new HashSet<ChunkCoord> { new ChunkCoord(0, 0), new ChunkCoord(1, 0) }, got);
        }

        [Fact]
        public void Invalidate_UnloadedChunk_IsNoOp()
        {
            var cfg = new StreamerConfig(LoadRadius: 3, UnloadRadius: 5, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);
            Pump(s, sink, new Vector3(30f, 0f, 30f), frames: 2);

            var farCoord = new ChunkCoord(500, 500);
            Assert.DoesNotContain(farCoord, s.Loaded);

            int reLodsBefore = sink.ReLods.Count;
            int loadsBefore = sink.Loads.Count;
            int unloadsBefore = sink.Unloads.Count;

            s.Invalidate(farCoord);

            Assert.Equal(reLodsBefore, sink.ReLods.Count);
            Assert.Equal(loadsBefore, sink.Loads.Count);
            Assert.Equal(unloadsBefore, sink.Unloads.Count);
        }

        [Fact]
        public void Invalidate_PreservesLod()
        {
            var cfg = new StreamerConfig(LoadRadius: 6, UnloadRadius: 8, MaxLoadsPerFrame: 1000, ChunkSize: 60f);
            var sink = new FakeChunkSink();
            var s = new TerrainStreamer(cfg, sink);

            // Same setup as Approaching_a_far_chunk_triggers_a_ReLod_to_a_finer_tier: target loads at coarse LOD 2.
            var target = new ChunkCoord(5, 0);
            Pump(s, sink, new Vector3(-30f, 0f, 30f), 4);
            Assert.Equal(2, s.LodOf(target));

            sink.ReLods.Clear();
            s.Invalidate(target);

            Assert.Single(sink.ReLods);
            Assert.Equal(target, sink.ReLods[0].coord);
            Assert.Equal(2, sink.ReLods[0].lod);          // rebuilt at the current tier, not reset to lod 0
            Assert.Equal(2, s.LodOf(target));              // tracked tier unchanged
        }

        // --- Invalidate through the real Scene3DChunkSink (stale-trees regression, streamer level) -----------------
        // GPU-gated: Scene3DChunkSink.Apply always uploads through a real Scene3D (UploadMesh has no GPU-free path).

        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static TerrainField Flat(float height, float waterLevel = 0f) => new TerrainField(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = waterLevel,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
            },
        });

        static ScatterConfig OneKind(string id, int seed, float cell) => new ScatterConfig
        {
            Seed = seed,
            CellSize = cell,
            Jitter = 0.5f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind(id, 1f) } },
            },
        };

        [GpuFact]
        public void Invalidate_AfterFieldSwap_RegeneratesChunkProps() => WithScene(scene =>
        {
            TerrainField fieldA = Flat(5f);                   // WaterLevel 0: the chunk's candidates are kept
            TerrainField fieldB = Flat(5f, waterLevel: 10f);   // simulates dragging a lake over the chunk
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            var realSink = new Scene3DChunkSink(scene, fieldA, scatter, new Dictionary<string, MeshHandle>(),
                chunkSize: 60f, propDrawRadius: 90f);
            var sink = new HandleTrackingSink(realSink);
            var cfg = new StreamerConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerFrame: 100, ChunkSize: 60f);
            var s = new TerrainStreamer(cfg, sink);
            var target = new ChunkCoord(0, 0);

            s.Update(new Vector3(30f, 0f, 30f), 1f / 60f);   // loads the ring around chunk (0,0), including target
            Assert.Contains(target, s.Loaded);
            Assert.True(sink.HandleFor(target).LayerProps[0].Count > 0);   // not vacuous: pre-carve chunk has trees

            realSink.UpdateField(fieldB);
            s.Invalidate(target);

            Assert.Empty(sink.HandleFor(target).LayerProps[0]);   // regenerated: carved chunk has none left
        });
    }
}
