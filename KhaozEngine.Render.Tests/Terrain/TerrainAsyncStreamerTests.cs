using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    // A dispatcher that queues build bodies instead of running them, so a test can complete them in a controlled
    // order (in-order, reverse, one at a time) to exercise out-of-order async completion. Drain() runs them all
    // (this is what FlushPendingBuilds / PrimeAround rely on for a deterministic drain).
    sealed class ManualBuildDispatcher : IChunkBuildDispatcher
    {
        readonly List<Action> _queued = new();
        public int PendingCount => _queued.Count;
        public void Schedule(Action build) => _queued.Add(build);
        public void RunAt(int index) { Action a = _queued[index]; _queued.RemoveAt(index); a(); }
        public void RunAll() { var copy = new List<Action>(_queued); _queued.Clear(); foreach (Action a in copy) a(); }
        // Run every queued body last-to-first (removing the tail never shifts earlier indices) = reverse of schedule order.
        public void RunReverse() { while (_queued.Count > 0) RunAt(_queued.Count - 1); }
        public void Drain() => RunAll();
    }

    // An async sink that records BuildCpu (background) / Apply (frame) / Unload (frame) so a headless test can assert
    // the async invariants with no GPU. The handle is a mutable holder tracking the live LOD (mirrors the production
    // Scene3DChunkSink.ChunkLoad). BuildCpu is guarded so the real-thread-pool test can call it concurrently.
    sealed class FakeAsyncChunkSink : IAsyncChunkSink, IDisposable
    {
        readonly object _buildsLock = new();
        public readonly List<(ChunkCoord coord, int lod, ChunkRing ring)> Builds = new();       // BuildCpu ran (background)
        public readonly List<(ChunkCoord coord, int lod, ChunkRing ring, bool relod)> Applies = new();  // Apply ran (frame)
        public readonly List<ChunkCoord> Unloads = new();
        public int DisposeCount;

        sealed class Handle { public ChunkCoord Coord; public int Lod; public ChunkRing Ring; }
        sealed class Payload { public ChunkCoord Coord; public int Lod; public ChunkRing Ring; }

        public object BuildCpu(ChunkCoord coord, int lod, ChunkRing ring)
        {
            lock (_buildsLock) Builds.Add((coord, lod, ring));
            return new Payload { Coord = coord, Lod = lod, Ring = ring };
        }

        public object Apply(ChunkCoord coord, int lod, ChunkRing ring, object cpuBuild, object? existing)
        {
            var p = (Payload)cpuBuild;
            if (p.Coord != coord || p.Lod != lod || p.Ring != ring)
                throw new InvalidOperationException("payload did not match the coord/lod/ring it was applied for");
            Applies.Add((coord, lod, ring, existing is not null));
            if (existing is Handle h) { h.Lod = lod; h.Ring = ring; return h; }
            return new Handle { Coord = coord, Lod = lod, Ring = ring };
        }

        // Synchronous IChunkSink members (used only when the streamer runs in synchronous mode).
        public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Apply(coord, lod, ring, BuildCpu(coord, lod, ring), existing: null);
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => Apply(coord, lod, ring, BuildCpu(coord, lod, ring), handle);
        public void Unload(ChunkCoord coord, object handle) => Unloads.Add(coord);
        public void Dispose() => DisposeCount++;

        public bool Applied(ChunkCoord c) { foreach (var a in Applies) if (a.coord == c) return true; return false; }
    }

    public class TerrainAsyncStreamerTests
    {
        static StreamerConfig Async(int load, int unload, int budget) =>
            new(LoadRadius: load, UnloadRadius: unload, MaxLoadsPerFrame: budget, ChunkSize: 60f);

        static HashSet<ChunkCoord> ExpectedDisk(ChunkCoord center, int radius)
        {
            var set = new HashSet<ChunkCoord>();
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    if (dx * dx + dz * dz <= radius * radius)
                        set.Add(new ChunkCoord(center.X + dx, center.Z + dz));
            return set;
        }

        // ---- Scheduler-level token semantics (pure, no streamer) --------------------------------------------------

        [Fact]
        public void Scheduler_last_request_wins_and_supersedes_the_earlier_build()
        {
            var manual = new ManualBuildDispatcher();
            var sched = new ChunkBuildScheduler<int>((_, lod, _) => lod, manual);   // payload = the LOD it built at
            var c = new ChunkCoord(0, 0);

            sched.Request(c, 1, ChunkRing.Gameplay);
            sched.Request(c, 2, ChunkRing.Gameplay);          // supersedes the LOD-1 request
            manual.RunAll();              // BOTH bodies run and enqueue (LOD 1 is now stale)
            sched.Pump();

            IReadOnlyList<ChunkBuild<int>> ready = sched.TakeReady(10, static (_, _) => 0);
            Assert.Single(ready);
            Assert.Equal(2, ready[0].Lod);        // the last-requested LOD
            Assert.Equal(2, ready[0].Payload);
        }

        [Fact]
        public void Scheduler_cancel_discards_an_in_flight_build_result()
        {
            var manual = new ManualBuildDispatcher();
            var sched = new ChunkBuildScheduler<int>((_, lod, _) => lod, manual);
            var c = new ChunkCoord(3, -1);

            sched.Request(c, 0, ChunkRing.Gameplay);
            sched.Cancel(c);              // left the ring before the body ran
            manual.RunAll();              // body runs, enqueues a now-stale completion
            sched.Pump();

            Assert.Equal(0, sched.ReadyCount);
            Assert.Equal(0, sched.InFlightCount);
            Assert.Empty(sched.TakeReady(10, static (_, _) => 0));
        }

        [Fact]
        public void Scheduler_re_request_after_cancel_builds_again()
        {
            var manual = new ManualBuildDispatcher();
            var sched = new ChunkBuildScheduler<int>((_, lod, _) => lod, manual);
            var c = new ChunkCoord(2, 2);

            sched.Request(c, 0, ChunkRing.Gameplay);
            manual.RunAll();
            sched.Cancel(c);
            sched.Pump();
            Assert.Equal(0, sched.ReadyCount);    // cancelled result dropped

            sched.Request(c, 0, ChunkRing.Gameplay);                  // re-enters the ring
            manual.RunAll();
            sched.Pump();
            Assert.Equal(1, sched.ReadyCount);    // rebuilt, not stuck
        }

        [Fact]
        public void Scheduler_surfaces_a_build_fault_on_the_frame_thread()
        {
            var manual = new ManualBuildDispatcher();
            var sched = new ChunkBuildScheduler<int>((_, _, _) => throw new InvalidOperationException("boom"), manual);
            var c = new ChunkCoord(0, 0);

            sched.Request(c, 0, ChunkRing.Gameplay);
            manual.RunAll();
            ChunkBuildException ex = Assert.Throws<ChunkBuildException>(() => sched.Pump());
            Assert.Equal(c, ex.Coord);
            Assert.IsType<InvalidOperationException>(ex.InnerException);
            Assert.Equal(0, sched.InFlightCount);   // cleared so a later request can retry
        }

        // ---- Streamer async orchestration ------------------------------------------------------------------------

        [Fact]
        public void Async_update_requests_builds_off_the_frame_thread_and_defers_the_apply()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(2, 4, 3), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);

            s.Update(pos, 0f);   // requests the disk, applies nothing yet (no build has run)

            Assert.True(manual.PendingCount > 0);       // builds were requested (unbudgeted)
            Assert.Empty(sink.Applies);                  // nothing applied on the frame thread yet
            Assert.Empty(s.Loaded);
        }

        [Fact]
        public void Budget_caps_applies_not_requests()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(4, 6, 3), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            int disk = ExpectedDisk(new ChunkCoord(0, 0), 4).Count;

            s.Update(pos, 0f);
            Assert.Equal(disk, manual.PendingCount);     // the WHOLE disk was requested at once (builds are unbudgeted)
            Assert.Empty(sink.Applies);

            manual.RunAll();                              // every build completes
            s.Update(pos, 0f);
            Assert.Equal(3, sink.Applies.Count);          // but only MaxLoadsPerFrame are APPLIED this frame
            Assert.Equal(3, s.Loaded.Count);

            // Drain the ready backlog: <= budget applied each frame, ring eventually fills.
            for (int i = 0; i < 50 && s.Loaded.Count < disk; i++)
            {
                int before = sink.Applies.Count;
                s.Update(pos, 0f);
                Assert.True(sink.Applies.Count - before <= 3, $"frame {i} applied more than the budget");
            }
            Assert.Equal(disk, s.Loaded.Count);
        }

        [Fact]
        public void Out_of_order_completion_still_applies_every_chunk_at_the_right_lod()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(1, 3, 100), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            var disk = ExpectedDisk(new ChunkCoord(0, 0), 1);

            s.Update(pos, 0f);
            manual.RunReverse();          // complete the builds in the reverse of the request order
            s.Update(pos, 0f);            // apply them (nearest-first, budget 100)

            Assert.Equal(disk, new HashSet<ChunkCoord>(s.Loaded));
            foreach (ChunkCoord c in disk)
            {
                Vector2 center = ChunkGrid.CenterOf(c, 60f);
                float d = Vector2.Distance(new Vector2(pos.X, pos.Z), center);
                Assert.Equal(TerrainLod.PickLod(d), s.LodOf(c));
            }
        }

        [Fact]
        public void Unload_while_a_build_is_in_flight_discards_the_result_and_never_applies_or_leaks()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(1, 2, 100), sink, manual);

            s.Update(new Vector3(30f, 0f, 30f), 0f);        // request the disk around chunk (0,0), nothing run yet
            var origin = new ChunkCoord(0, 0);

            // Teleport far away before any build ran: the origin disk leaves the ring while in flight.
            var far = new Vector3(50 * 60f + 30f, 0f, 30f);
            s.Update(far, 0f);                               // cancels the in-flight origin-disk builds, requests the far disk
            manual.RunAll();                                 // all bodies run (origin ones are now stale)
            s.Update(far, 0f);                               // pump + apply: origin results dropped, far applied

            Assert.False(sink.Applied(origin));              // never applied
            Assert.DoesNotContain(origin, s.Loaded);
            Assert.Empty(sink.Unloads);                       // never loaded, so never unloaded (no leak)
            Assert.Contains(new ChunkCoord(50, 0), s.Loaded); // the destination did load
        }

        [Fact]
        public void ReLod_applies_a_finer_tier_on_the_same_handle()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(4, 10, 100), sink, manual);
            var origin = new ChunkCoord(0, 0);

            // Stand far enough that the origin chunk loads coarse but is still inside the load disk: centre distance
            // 210 m -> LOD 2, chunk distance 3.5 <= LoadRadius 4.
            var farPos = new Vector3(30f, 0f, 30f + 210f);
            s.Update(farPos, 0f); manual.RunAll(); s.FlushPendingBuilds();
            Assert.Equal(2, s.LodOf(origin));

            // Walk onto the origin chunk -> centre distance 0 -> LOD 0. Expect a re-LOD applied on the same handle.
            int appliesBefore = sink.Applies.Count;
            var onIt = new Vector3(30f, 0f, 30f);
            s.Update(onIt, 0f); manual.RunAll(); s.FlushPendingBuilds();

            Assert.Equal(0, s.LodOf(origin));
            Assert.Contains(sink.Applies.GetRange(appliesBefore, sink.Applies.Count - appliesBefore),
                            a => a.coord == origin && a.relod);   // applied as a re-LOD (existing handle), not a fresh load
        }

        [Fact]
        public void ReLod_superseded_by_a_newer_tier_applies_only_the_last_tier()
        {
            // Drive the supersede purely through the streamer: request a re-LOD, then change the target before the
            // build completes, then complete both. Last tier wins, the stale one is discarded.
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(6, 12, 100), sink, manual);
            var origin = new ChunkCoord(0, 0);

            // Load the origin at LOD 0 (stand on it).
            s.Update(new Vector3(30f, 0f, 30f), 0f); manual.RunAll(); s.FlushPendingBuilds();
            Assert.Equal(0, s.LodOf(origin));

            // Step to a distance where origin -> LOD 1 (centre 150 m), request the re-LOD but DO NOT run it.
            s.Update(new Vector3(30f, 0f, 30f + 150f), 0f);
            // Step again to LOD 2 (centre 360 m) before the LOD-1 build ran: supersedes it. Chunk distance 6 <= LoadRadius 6.
            s.Update(new Vector3(30f, 0f, 30f + 360f), 0f);
            manual.RunAll();                 // both the LOD-1 (stale) and LOD-2 builds run
            s.FlushPendingBuilds();          // apply the survivor

            Assert.Equal(2, s.LodOf(origin));   // the last-requested tier won
        }

        [Fact]
        public void FlushPendingBuilds_completes_and_applies_the_whole_backlog()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(3, 5, 3), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            var disk = ExpectedDisk(new ChunkCoord(0, 0), 3);

            s.Update(pos, 0f);          // request the disk
            s.FlushPendingBuilds();     // block on the builds + apply ALL (ignores the budget)

            Assert.Equal(disk, new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void PrimeAround_fills_the_full_ring_with_the_manual_dispatcher()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(3, 5, 3), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);

            s.PrimeAround(pos);

            Assert.Equal(ExpectedDisk(new ChunkCoord(0, 0), 3), new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void PrimeAround_fills_the_ring_on_the_real_thread_pool()
        {
            // No injected dispatcher -> the default TaskChunkBuildDispatcher runs builds on the thread pool. PrimeAround
            // blocks on them, so the ring is fully loaded and deterministic despite the concurrency.
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(3, 5, 3), sink);
            var pos = new Vector3(30f, 0f, 30f);

            s.PrimeAround(pos);

            Assert.Equal(ExpectedDisk(new ChunkCoord(0, 0), 3), new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void Synchronous_mode_builds_and_applies_inline_even_for_an_async_sink()
        {
            // The sync escape hatch: an async-capable sink, but StreamerConfig.Synchronous() forces the old inline
            // build+apply path. No dispatcher is used, the budget caps build+apply ops (as before async).
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(4, 6, 3).Synchronous(), sink);
            var pos = new Vector3(30f, 0f, 30f);

            s.Update(pos, 0f);
            Assert.Equal(3, sink.Applies.Count);   // applied inline this frame, capped at the budget
            Assert.Equal(3, s.Loaded.Count);
        }

        [Fact]
        public void The_unload_budget_applies_on_the_async_path_too()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var cfg = Async(3, 4, 1000) with { MaxUnloadsPerFrame = 2 };
            var s = new TerrainStreamer(cfg, sink, manual);
            var home = new Vector3(30f, 0f, 30f);
            var away = new Vector3(9 * 60f + 30f, 0f, 30f);

            s.Update(home, 0f); manual.RunAll(); s.Update(home, 0f);
            Assert.True(s.Loaded.Count > 4);
            sink.Unloads.Clear();

            s.Update(away, 0f);

            Assert.Equal(2, sink.Unloads.Count);

            // The backlog still drains: pump until the old ring is gone.
            for (int i = 0; i < 40; i++) { s.Update(away, 0f); manual.RunAll(); }
            Assert.Equal(ExpectedDisk(new ChunkCoord(9, 0), 3), new HashSet<ChunkCoord>(s.Loaded));
        }

        [Fact]
        public void UnloadAll_frees_the_ring_and_discards_in_flight_builds()
        {
            var manual = new ManualBuildDispatcher();
            var sink = new FakeAsyncChunkSink();
            var s = new TerrainStreamer(Async(3, 5, 100), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);

            s.Update(pos, 0f); manual.RunAll(); s.Update(pos, 0f);   // load the ring
            int loaded = s.Loaded.Count;
            Assert.True(loaded > 0);

            // Kick off a fresh build backlog (queued, not run), then tear down while it is in flight.
            s.Update(new Vector3(30f, 0f, 30f + 120f), 0f);          // requests new work
            Assert.True(manual.PendingCount > 0);
            s.UnloadAll();

            Assert.Empty(s.Loaded);
            Assert.Equal(loaded, sink.Unloads.Count);                // every loaded chunk was unloaded exactly once
            Assert.Equal(0, manual.PendingCount);                    // UnloadAll drained (and discarded) the in-flight builds
        }
    }
}
