using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.Terrain
{
    // Records chunk operations and resource lifetime for headless streaming tests.
    sealed class FakeChunkSink : IChunkBuildReasonSink, IDisposable
    {
        public readonly List<(ChunkCoord coord, int lod, ChunkRing ring)> Loads = new();
        public readonly List<(ChunkCoord coord, int lod, ChunkRing ring)> ReLods = new();
        public readonly List<ChunkCoord> Unloads = new();
        public readonly List<(ChunkCoord coord, ChunkBuildReason reason)> Reasons = new();
        // Per-Update op counts (load + relod), reset by the test harness between Updates.
        public int OpsThisFrame;
        public bool IsDisposed { get; private set; }

        public object Load(ChunkCoord coord, int lod, ChunkRing ring) { Loads.Add((coord, lod, ring)); OpsThisFrame++; return new Box(coord); }
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) { ReLods.Add((coord, lod, ring)); OpsThisFrame++; }
        public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring, ChunkBuildReason reason)
        {
            Reasons.Add((coord, reason));
            ReLod(coord, handle, lod, ring);
        }
        public void Unload(ChunkCoord coord, object handle) { Unloads.Add(coord); }
        public void Dispose() => IsDisposed = true;

        public void ResetFrame() => OpsThisFrame = 0;
        sealed class Box { public Box(ChunkCoord c) { Coord = c; } public ChunkCoord Coord; }
    }
}
