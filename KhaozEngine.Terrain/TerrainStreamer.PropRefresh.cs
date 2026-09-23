namespace KhaozEngine.Terrain
{
    /// <summary>The generation-config refresh seam, in its own file because the streamer is near the file-size cap.</summary>
    public sealed partial class TerrainStreamer
    {
        /// <summary>Re-serves every prop layer of each loaded chunk overlapping <paramref name="area"/> without
        /// rebuilding its terrain, for a change to the sink's generation config that leaves the field alone (a layer
        /// swap such as <c>Scene3DChunkSink.UpdateLayers</c> that moved an exclusion). A sink implementing
        /// <see cref="IChunkPropRefreshSink"/> republishes only the props. Any other sink falls back to the in-place
        /// rebuild <see cref="Invalidate(RectArea)"/> performs. The rect maps to chunks exactly as
        /// <see cref="Invalidate(RectArea)"/> maps it, and chunks that are not loaded are left alone: they build
        /// from the new config when they stream in. Pending async builds are flushed first, so a build that read
        /// the old config cannot land after the refresh. Returns how many loaded chunks were refreshed.</summary>
        public int RefreshProps(RectArea area)
        {
            FlushPendingBuilds();
            float cs = _config.ChunkSize;
            ChunkCoord min = ChunkGrid.CoordOf(area.MinX, area.MinZ, cs);
            ChunkCoord max = ChunkGrid.CoordOf(area.MaxX, area.MaxZ, cs);
            int refreshed = 0;
            for (int z = min.Z; z <= max.Z; z++)
            for (int x = min.X; x <= max.X; x++)
            {
                var coord = new ChunkCoord(x, z);
                if (!_loaded.TryGetValue(coord, out Entry? e)) continue;
                if (_sink is IChunkPropRefreshSink props) props.RefreshProps(coord, e.Handle);
                else InvalidateLoaded(coord);
                refreshed++;
            }
            return refreshed;
        }
    }
}
