using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Containment tests for a background chunk build that throws (KhaozEngine issue #402: a
    /// <see cref="PropHlod"/> weld overflow inside <c>Scene3DChunkSink.BuildCpu</c> escaped
    /// <c>TerrainStreamer.UpdateAsync</c> and terminated the consumer's client mid-play).
    /// <para>The contract these pin down: one failing chunk must never take the frame loop (or the boot prime) with it.
    /// It is logged, retried a bounded number of times, and then abandoned so it stays absent instead of costing a
    /// rebuild and an error every frame forever. A prime that loads nothing at all is still a real boot failure.</para></summary>
    public class TerrainStreamerBuildFailureTests
    {
        // An async sink whose BuildCpu throws for a chosen chunk. ThrowFor(...) picks the victim; FailuresLeft caps how
        // many times it throws, so a test can model "throws once then succeeds" as well as "throws forever".
        sealed class FailingSink : IAsyncChunkSink
        {
            readonly HashSet<ChunkCoord> _bad = new();
            public int FailuresLeft = int.MaxValue;
            public int BuildAttempts;                       // total BuildCpu calls for the failing coord(s)
            public readonly List<ChunkCoord> Applied = new();

            sealed class Handle { public int Lod; }

            public void ThrowFor(ChunkCoord c) => _bad.Add(c);

            public object BuildCpu(ChunkCoord coord, int lod, ChunkRing ring)
            {
                if (_bad.Contains(coord))
                {
                    BuildAttempts++;
                    if (FailuresLeft > 0)
                    {
                        FailuresLeft--;
                        // The real shape of the field crash: an IndexOutOfRangeException out of the mesh build.
                        throw new IndexOutOfRangeException("Index was outside the bounds of the array.");
                    }
                }
                return new Handle { Lod = lod };
            }

            public object Apply(ChunkCoord coord, int lod, ChunkRing ring, object cpuBuild, object? existing)
            {
                Applied.Add(coord);
                if (existing is Handle h) { h.Lod = lod; return h; }
                return (Handle)cpuBuild;
            }

            public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Apply(coord, lod, ring, BuildCpu(coord, lod, ring), null);
            public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => Apply(coord, lod, ring, BuildCpu(coord, lod, ring), handle);
            public void Unload(ChunkCoord coord, object handle) { }
        }

        static StreamerConfig Async(int load, int unload, int budget, int attempts = 3) =>
            new(LoadRadius: load, UnloadRadius: unload, MaxLoadsPerFrame: budget, ChunkSize: 60f)
            { MaxChunkBuildAttempts = attempts };

        // ---- The scheduler seam ----

        [Fact]
        public void Scheduler_still_throws_when_no_failure_handler_is_set()
        {
            // Unchanged default: a tool or test that wants a build bug to surface loudly still gets the throw.
            var manual = new ManualBuildDispatcher();
            var sched = new ChunkBuildScheduler<object>((_, _, _) => throw new InvalidOperationException("boom"), manual);
            sched.Request(new ChunkCoord(0, 0), 0, ChunkRing.Gameplay);
            manual.RunAll();

            ChunkBuildException ex = Assert.Throws<ChunkBuildException>(() => sched.Pump());
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }

        [Fact]
        public void Scheduler_reports_every_fault_to_the_handler_and_keeps_draining()
        {
            // With a handler, one bad chunk must not abandon the other completions queued in the same pump.
            var manual = new ManualBuildDispatcher();
            var bad = new ChunkCoord(1, 1);
            var sched = new ChunkBuildScheduler<object>(
                (c, _, _) => c == bad ? throw new InvalidOperationException("boom") : new object(), manual);
            var faults = new List<ChunkBuildException>();
            sched.BuildFailed = faults.Add;

            sched.Request(bad, 0, ChunkRing.Gameplay);
            sched.Request(new ChunkCoord(2, 2), 0, ChunkRing.Gameplay);
            manual.RunAll();
            sched.Pump();

            Assert.Single(faults);
            Assert.Equal(bad, faults[0].Coord);
            Assert.Equal(1, sched.ReadyCount);   // the healthy chunk still made it through
        }

        // ---- The streaming path: Update must not throw ----

        [Fact]
        public void Update_does_not_throw_when_a_chunk_build_fails()
        {
            var sink = new FailingSink();
            var manual = new ManualBuildDispatcher();
            var s = new TerrainStreamer(Async(2, 4, 100), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            sink.ThrowFor(ChunkGrid.CoordOf(pos.X, pos.Z, 60f));

            s.Update(pos, 0f);      // requests
            manual.RunAll();        // builds run, one throws
            s.Update(pos, 0f);      // pumps the completions: must contain the fault

            Assert.True(s.FailedBuildCount > 0);
            Assert.NotEmpty(s.Loaded);   // the rest of the ring still loaded
        }

        [Fact]
        public void A_chunk_that_fails_once_then_succeeds_appears_on_the_retry()
        {
            var sink = new FailingSink { FailuresLeft = 1 };
            var manual = new ManualBuildDispatcher();
            var s = new TerrainStreamer(Async(2, 4, 100), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            ChunkCoord bad = ChunkGrid.CoordOf(pos.X, pos.Z, 60f);
            sink.ThrowFor(bad);

            for (int i = 0; i < 4; i++) { s.Update(pos, 0f); manual.RunAll(); }
            s.Update(pos, 0f);

            Assert.Equal(1, s.FailedBuildCount);          // failed exactly once
            Assert.Contains(bad, s.Loaded);               // and came back on a later pass
            Assert.Empty(s.AbandonedChunks);
        }

        [Fact]
        public void A_permanently_failing_chunk_is_abandoned_at_the_cap_and_stops_being_rebuilt()
        {
            var sink = new FailingSink();                 // throws forever
            var manual = new ManualBuildDispatcher();
            var s = new TerrainStreamer(Async(2, 4, 100, attempts: 3), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            ChunkCoord bad = ChunkGrid.CoordOf(pos.X, pos.Z, 60f);
            sink.ThrowFor(bad);

            // Drive well past the cap: the retry budget, not the frame count, must be what stops the rebuilds.
            for (int i = 0; i < 20; i++) { s.Update(pos, 0f); manual.RunAll(); }
            s.Update(pos, 0f);

            Assert.Contains(bad, s.AbandonedChunks);
            Assert.DoesNotContain(bad, s.Loaded);         // absent, and the client is still alive to assert it
            Assert.Equal(3, s.FailedBuildCount);          // capped: 3 attempts, not 20
            Assert.Equal(3, sink.BuildAttempts);          // and the sink was genuinely not asked again

            int attemptsAtCap = sink.BuildAttempts;
            for (int i = 0; i < 5; i++) { s.Update(pos, 0f); manual.RunAll(); }
            Assert.Equal(attemptsAtCap, sink.BuildAttempts);   // still not asked again, frames later
        }

        [Fact]
        public void An_abandoned_chunk_gets_a_fresh_start_after_UnloadAll()
        {
            var sink = new FailingSink();
            var manual = new ManualBuildDispatcher();
            var s = new TerrainStreamer(Async(2, 4, 100, attempts: 2), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            ChunkCoord bad = ChunkGrid.CoordOf(pos.X, pos.Z, 60f);
            sink.ThrowFor(bad);

            for (int i = 0; i < 10; i++) { s.Update(pos, 0f); manual.RunAll(); }
            s.Update(pos, 0f);
            Assert.Contains(bad, s.AbandonedChunks);

            s.UnloadAll();   // a world rebuild: the field or the kit may be different now

            Assert.Empty(s.AbandonedChunks);
            sink.FailuresLeft = 0;                        // whatever was wrong is fixed
            for (int i = 0; i < 4; i++) { s.Update(pos, 0f); manual.RunAll(); }
            s.Update(pos, 0f);
            Assert.Contains(bad, s.Loaded);
        }

        // ---- The boot path: degrade, but a world that cannot prime at all is still a failure ----

        [Fact]
        public void PrimeAround_tolerates_an_isolated_chunk_failure_and_still_primes_the_world()
        {
            var sink = new FailingSink();
            var manual = new ManualBuildDispatcher();
            var s = new TerrainStreamer(Async(3, 5, 100), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            ChunkCoord bad = ChunkGrid.CoordOf(pos.X, pos.Z, 60f);
            sink.ThrowFor(bad);

            s.PrimeAround(pos);   // must NOT throw: one hole beats no world

            Assert.NotEmpty(s.Loaded);
            Assert.DoesNotContain(bad, s.Loaded);
            Assert.True(s.FailedBuildCount > 0);   // and the count is there for the boot step to report
        }

        [Fact]
        public void PrimeAround_throws_when_every_chunk_fails()
        {
            // Nothing loaded at all is not one chunk degrading, it is a world that cannot prime.
            var sink = new FailingSink();
            var manual = new ManualBuildDispatcher();
            var s = new TerrainStreamer(Async(2, 4, 100), sink, manual);
            var pos = new Vector3(30f, 0f, 30f);
            for (int dz = -3; dz <= 3; dz++)
                for (int dx = -3; dx <= 3; dx++)
                    sink.ThrowFor(new ChunkCoord(dx, dz));

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => s.PrimeAround(pos));

            Assert.Empty(s.Loaded);
            Assert.IsType<ChunkBuildException>(ex.InnerException);                 // carries the real cause
            Assert.IsType<IndexOutOfRangeException>(ex.InnerException!.InnerException);
        }
    }
}
