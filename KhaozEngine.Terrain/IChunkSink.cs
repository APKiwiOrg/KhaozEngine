namespace KhaozEngine.Terrain
{
    /// <summary>The load/unload callback seam the <see cref="TerrainStreamer"/> drives. The streamer owns only the
    /// bookkeeping (which chunks are loaded, at what LOD and in which <see cref="ChunkRing"/>); all GPU work (mesh
    /// build + prop scatter + draw) lives behind this interface, so the streamer is headless-testable with a fake
    /// sink. <see cref="Load"/> returns an opaque handle the streamer hands back to <see cref="ReLod"/> and
    /// <see cref="Unload"/>; the production sink uses a mutable holder it rebuilds in place on <see cref="ReLod"/>
    /// (ReLod returns void by design). The <see cref="ChunkRing"/> tells the sink how much of the chunk to build:
    /// a <see cref="ChunkRing.Gameplay"/> chunk gets scatter + colliders, a <see cref="ChunkRing.Decor"/> chunk is
    /// render-only. The ring is also part of a re-LOD (a chunk can change ring, not just tier, as the player moves).</summary>
    public interface IChunkSink
    {
        /// <summary>Build the chunk at this LOD and ring (mesh + props for a gameplay chunk, mesh only for decor) and
        /// return an opaque handle for it.</summary>
        object Load(ChunkCoord coord, int lod, ChunkRing ring);

        /// <summary>Rebuild an already-loaded chunk at a new LOD tier and/or ring. The handle is the one returned by
        /// <see cref="Load"/>; the sink may mutate it in place. A ring change (gameplay &lt;-&gt; decor) adds or drops
        /// the chunk's scatter and colliders; a pure tier change keeps them.</summary>
        void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring);

        /// <summary>Free a chunk that has left the ring.</summary>
        void Unload(ChunkCoord coord, object handle);
    }

    /// <summary>An <see cref="IChunkSink"/> that also splits <see cref="IChunkSink.Load"/> / <see cref="IChunkSink.ReLod"/>
    /// into a background CPU step and a frame-thread GPU step, so <see cref="TerrainStreamer"/> can build chunk meshes
    /// off the frame thread and only pay the GPU upload (bounded by the per-frame apply budget) on the frame thread.
    /// When the streamer is in async mode and its sink implements this, it uses <see cref="BuildCpu"/> +
    /// <see cref="Apply"/>. The synchronous <see cref="IChunkSink"/> members stay valid (sync mode and back-compat).
    /// The GPU device must be touched ONLY from <see cref="Apply"/> (and <see cref="IChunkSink.Unload"/>), never from
    /// <see cref="BuildCpu"/> - the engine has no threaded-GPU contract.</summary>
    public interface IAsyncChunkSink : IChunkSink
    {
        /// <summary>Build the chunk's CPU-side data at <paramref name="lod"/> for <paramref name="ring"/> (mesh +
        /// scatter for a gameplay chunk, mesh only for a decor chunk). Pure CPU: no GPU device, no shared mutable
        /// state, so the streamer runs it on a worker thread. Returns an opaque payload handed back to
        /// <see cref="Apply"/> on the frame thread. Must be safe to call concurrently for different chunks.</summary>
        object BuildCpu(ChunkCoord coord, int lod, ChunkRing ring);

        /// <summary>Apply a completed CPU build on the frame thread: create/replace the GPU buffers and register any
        /// physics for <paramref name="ring"/>. <paramref name="cpuBuild"/> is the payload from <see cref="BuildCpu"/>.
        /// For a fresh load <paramref name="existing"/> is null. For a re-LOD it is the handle a prior
        /// <see cref="Apply"/> / <see cref="IChunkSink.Load"/> returned, which this may mutate in place. Returns the
        /// new or mutated handle.</summary>
        object Apply(ChunkCoord coord, int lod, ChunkRing ring, object cpuBuild, object? existing);
    }
}
