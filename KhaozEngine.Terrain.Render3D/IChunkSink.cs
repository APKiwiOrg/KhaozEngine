namespace KhaozEngine.Terrain
{
    /// <summary>The load/unload callback seam the <see cref="TerrainStreamer"/> drives. The streamer owns only the
    /// bookkeeping (which chunks are loaded, at what LOD); all GPU work (mesh build + prop scatter + draw) lives
    /// behind this interface, so the streamer is headless-testable with a fake sink. <see cref="Load"/> returns an
    /// opaque handle the streamer hands back to <see cref="ReLod"/> and <see cref="Unload"/>; the production sink
    /// uses a mutable holder it rebuilds in place on <see cref="ReLod"/> (ReLod returns void by design).</summary>
    public interface IChunkSink
    {
        /// <summary>Build the chunk at this LOD (mesh + props) and return an opaque handle for it.</summary>
        object Load(ChunkCoord coord, int lod);

        /// <summary>Rebuild an already-loaded chunk at a new LOD tier (the mesh resolution changed). The handle is
        /// the one returned by <see cref="Load"/>; the sink may mutate it in place.</summary>
        void ReLod(ChunkCoord coord, object handle, int lod);

        /// <summary>Free a chunk that has left the ring.</summary>
        void Unload(ChunkCoord coord, object handle);
    }
}
