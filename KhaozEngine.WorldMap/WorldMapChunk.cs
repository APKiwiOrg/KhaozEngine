namespace KhaozEngine.WorldMap;

/// <summary>A chunk's raster and feature anchors built against one cache generation.</summary>
/// <param name="Image">The north-up raster stamped with its sampled source revision.</param>
/// <param name="Features">The icon and label anchors owned by the chunk.</param>
/// <param name="Generation">The cache generation captured when this build started.</param>
public sealed record WorldMapChunk(WorldMapChunkImage Image, WorldMapChunkFeatures Features, int Generation);
