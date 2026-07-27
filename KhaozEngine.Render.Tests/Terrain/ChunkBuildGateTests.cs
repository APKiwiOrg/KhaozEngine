using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers <see cref="TerrainStreamer.BuildGate"/>: a refused chunk is DEFERRED (not requested, not
    /// marked loaded, reconsidered next update) rather than dropped, a null gate is byte-for-byte the pre-gate
    /// behaviour, and neither unloads nor <see cref="TerrainStreamer.Invalidate(RectArea)"/> are gated.</summary>
    public class ChunkBuildGateTests
    {
        sealed class PredicateGate : IChunkBuildGate
        {
            readonly HashSet<ChunkCoord> _blocked = new();
            public int Calls;
            public bool BlockEverything;
            public void Block(ChunkCoord c) => _blocked.Add(c);
            public void Allow(ChunkCoord c) => _blocked.Remove(c);
            public bool CanBuild(ChunkCoord coord) { Calls++; return !BlockEverything && !_blocked.Contains(coord); }
        }

        static StreamerConfig Cfg(bool async) =>
            new(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerFrame: 32, ChunkSize: 10f, Async: async);

        [Fact]
        public void NullGate_IsPreGateBehaviour()
        {
            var sink = new FakeChunkSink();
            using var streamer = new TerrainStreamer(Cfg(async: false), sink);

            streamer.Update(Vector3.Zero, 0f);

            Assert.Null(streamer.BuildGate);
            Assert.Equal(5, sink.Loads.Count);   // the Euclidean radius-1 disk: the plus shape, no diagonals
        }

        [Fact]
        public void BuildGate_DefersAChunkAndReconsidersItNextUpdate()
        {
            var sink = new FakeChunkSink();
            var gate = new PredicateGate();
            var blocked = new ChunkCoord(1, 0);
            gate.Block(blocked);
            using var streamer = new TerrainStreamer(Cfg(async: false), sink) { BuildGate = gate };

            streamer.Update(Vector3.Zero, 0f);

            Assert.Equal(4, sink.Loads.Count);
            Assert.DoesNotContain(blocked, streamer.Loaded);
            Assert.DoesNotContain(sink.Loads, l => l.coord == blocked);
            Assert.True(gate.Calls >= 5);

            // Deferred, not dropped: the moment the gate relents the chunk arrives, with no other change.
            gate.Allow(blocked);
            streamer.Update(Vector3.Zero, 0f);

            Assert.Equal(5, sink.Loads.Count);
            Assert.Contains(blocked, streamer.Loaded);
        }

        [Fact]
        public void BuildGate_DefersInAsyncModeToo()
        {
            var sink = new FakeAsyncChunkSink();
            var dispatcher = new ManualBuildDispatcher();
            var gate = new PredicateGate();
            var blocked = new ChunkCoord(0, 1);
            gate.Block(blocked);
            using var streamer = new TerrainStreamer(Cfg(async: true), sink, dispatcher) { BuildGate = gate };

            streamer.Update(Vector3.Zero, 0f);
            dispatcher.RunAll();
            streamer.Update(Vector3.Zero, 0f);

            Assert.DoesNotContain(blocked, streamer.Loaded);
            Assert.DoesNotContain(sink.Builds, b => b.coord == blocked);   // never even requested off-thread
            Assert.Equal(4, streamer.Loaded.Count);

            gate.Allow(blocked);
            streamer.Update(Vector3.Zero, 0f);
            dispatcher.RunAll();
            streamer.Update(Vector3.Zero, 0f);

            Assert.Contains(blocked, streamer.Loaded);
        }

        [Fact]
        public void BuildGate_DoesNotBlockUnloadsOrInvalidate()
        {
            var sink = new FakeChunkSink();
            var gate = new PredicateGate();
            using var streamer = new TerrainStreamer(Cfg(async: false), sink) { BuildGate = gate };

            streamer.Update(Vector3.Zero, 0f);
            Assert.Equal(5, sink.Loads.Count);

            // Everything is refused from here on. Invalidate is the "this data just arrived, rebuild it" call a
            // residency layer makes ON ARRIVAL, so gating it would refuse the very rebuild the arrival triggers.
            gate.BlockEverything = true;
            streamer.Invalidate(new RectArea(0f, 0f, 10f, 10f));
            Assert.NotEmpty(sink.ReLods);

            // And a chunk that leaves the ring still unloads, gate or no gate: with every chunk refused, the far
            // ring loads nothing and the near ring still drains to empty.
            streamer.Update(new Vector3(1000f, 0f, 1000f), 0f);
            Assert.Equal(5, sink.Unloads.Count);
            Assert.Empty(streamer.Loaded);
        }
    }
}
