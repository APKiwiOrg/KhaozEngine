using System.Collections.Generic;

namespace KhaozEngine.Terrain
{
    /// <summary>Decides whether a chunk build has to merge its HLOD cluster at all (issue #393). Owned by
    /// <see cref="Scene3DChunkSink"/>, one instance per sink, and only allocated when some layer actually bakes an
    /// HLOD mesh.
    /// <para><b>The rule.</b> A merged cluster is consumed by exactly two applies: a FRESH LOAD, which uploads it,
    /// and a FIELD REBUILD, which replaces what is already uploaded. Every other apply (a tier re-LOD, a ring
    /// change) deliberately keeps the mesh already on the GPU, because the merge is a function of the placements
    /// and those are tier- and ring-independent. So the merge is needed when the chunk has no applied build yet,
    /// or when the incoming build targets exactly the tier and ring the applied one already has, which is the only
    /// shape a rebuild-in-place takes (<c>ReLod</c> at the current tier, what
    /// <c>TerrainStreamer.Invalidate</c> issues). That covers a live placement source's arrival as well as an
    /// editor field swap, since both arrive as the same same-tier same-ring rebuild.</para>
    /// <para><b>Why it is not just a lookup in the sink's own loaded map.</b> The decision is made in
    /// <c>BuildCpu</c>, which the streamer runs on a background build thread, while the sink's loaded map is
    /// mutated by <c>Apply</c> and <c>Unload</c> on the frame thread. Reading an unsynchronized
    /// <see cref="Dictionary{TKey,TValue}"/> across that boundary is corruption, so this keeps its own copy of the
    /// applied (tier, ring) per chunk behind a lock. The lock is held for a dictionary probe against a build that
    /// costs milliseconds, so contention is not a consideration.</para>
    /// <para>The applied state is recorded on EVERY apply, whether or not that apply rebuilt the merge, so a later
    /// rebuild-in-place at the new tier is still recognized as one.</para></summary>
    internal sealed class HlodBuildGate
    {
        readonly object _sync = new();
        readonly Dictionary<ChunkCoord, Applied> _applied = new();

        /// <summary>Whether a build of <paramref name="coord"/> at (<paramref name="lod"/>, <paramref name="ring"/>)
        /// has to merge the cluster: true for a chunk with no applied build (a fresh load) and for a build that
        /// targets the applied tier and ring (a rebuild in place), false for a tier or ring transition.</summary>
        public bool NeedsMerge(ChunkCoord coord, int lod, ChunkRing ring)
        {
            lock (_sync)
                return !_applied.TryGetValue(coord, out Applied a) || (a.Lod == lod && a.Ring == ring);
        }

        /// <summary>Record the tier and ring a chunk is now applied at. Called from every apply.</summary>
        public void MarkApplied(ChunkCoord coord, int lod, ChunkRing ring)
        {
            lock (_sync) _applied[coord] = new Applied(lod, ring);
        }

        /// <summary>Drop a chunk's applied state on unload, so a later reload merges again.</summary>
        public void Forget(ChunkCoord coord)
        {
            lock (_sync) _applied.Remove(coord);
        }

        /// <summary>Drop every chunk's applied state (sink teardown).</summary>
        public void Clear()
        {
            lock (_sync) _applied.Clear();
        }

        readonly struct Applied
        {
            public readonly int Lod;
            public readonly ChunkRing Ring;
            public Applied(int lod, ChunkRing ring) { Lod = lod; Ring = ring; }
        }
    }
}
