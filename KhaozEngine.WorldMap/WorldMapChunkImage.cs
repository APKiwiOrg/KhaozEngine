namespace KhaozEngine.WorldMap;

/// <summary>A square RGBA8 world-map raster with row zero at the north edge.</summary>
/// <param name="Coord">The map-space chunk address.</param>
/// <param name="Size">The edge length in pixels.</param>
/// <param name="Rgba">Row-major RGBA bytes, with columns running west to east.</param>
/// <param name="Revision">The source revision captured before sampling began.</param>
public sealed record WorldMapChunkImage(WorldMapChunkCoord Coord, int Size, byte[] Rgba, long Revision);
