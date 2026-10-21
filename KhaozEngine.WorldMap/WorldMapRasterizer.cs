using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap.Raster;

namespace KhaozEngine.WorldMap;

/// <summary>Builds deterministic, GPU-free world-map imagery from an authored surface.</summary>
public static class WorldMapRasterizer
{
    /// <summary>Samples and shades one north-up chunk, including water, optional contours and footprint symbols.</summary>
    /// <param name="source">The authored surface to sample.</param>
    /// <param name="style">The map raster resolution and appearance.</param>
    /// <param name="chunk">The map-space chunk address.</param>
    /// <returns>A square RGBA8 image stamped with the revision captured before sampling.</returns>
    /// <exception cref="ArgumentException">A style property is outside its supported finite range.</exception>
    public static WorldMapChunkImage Rasterize(IWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord chunk) =>
        Rasterize(source, style, chunk, border: 0);

    /// <summary>Rasterizes one chunk surrounded by a gutter of neighbouring map pixels.</summary>
    /// <param name="source">The authored surface to sample.</param>
    /// <param name="style">The map raster resolution and appearance.</param>
    /// <param name="chunk">The map-space chunk address.</param>
    /// <param name="border">The non-negative gutter width in pixels on each side of the chunk.</param>
    /// <returns>An image whose interior matches the borderless raster and whose gutter matches the adjacent chunks.</returns>
    /// <exception cref="ArgumentException">A style property is outside its supported finite range.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The border is negative.</exception>
    public static WorldMapChunkImage Rasterize(IWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord chunk, int border)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentOutOfRangeException.ThrowIfNegative(border);
        WorldMapStyleValidation.Validate(style);

        MapRect rect = WorldMapChunking.ChunkRect(chunk, style.ChunkMetres);
        int size = style.ChunkPixels;
        long revision = source.Revision;
        float levelPpm = style.LevelPixelsPerMetre(chunk.Level);
        float maxCellSize = 2f / levelPpm;
        // The resolution contract fixes the returned cell size before allocating the expanded grid.
        float cellSize = WorldMapRasterReach.SampleCellSize(source.SurfaceCellSize, maxCellSize);
        float margin = WorldMapRasterReach.SurfaceMetres(style, cellSize, chunk.Level);
        var canvas = new RasterCanvas(size, border, rect, levelPpm);
        // The gutter samples the neighbouring map area with the same reach as the interior.
        var sampler = new SurfaceSampler(source.SampleSurface(canvas.PaintedRect.Expand(margin), maxCellSize: maxCellSize));
        var relief = new ReliefShader(style.Relief);
        var water = new WaterPainter(sampler, canvas, style.Water);

        for (int row = canvas.First; row < canvas.End; row++)
            for (int column = canvas.First; column < canvas.End; column++)
            {
                Vector2 point = canvas.MapPoint(column, row);
                SurfaceSample sample = sampler.Sample(point);
                Color colour = !sample.HasGround ? style.VoidColor : sample.IsWet
                    ? water.Paint(column, row, sample)
                    : relief.Shade(sampler, point, sample);
                canvas.Set(column, row, colour);
            }

        if (style.Contours is { } contours)
            new ContourPainter(sampler, canvas, contours).Paint();

        var footprints = new List<WorldMapFootprint>();
        float footprintMargin = WorldMapRasterReach.FootprintMetres(style, chunk.Level);
        source.CollectFootprints(new WorldMapFootprintQuery(canvas.PaintedRect.Expand(footprintMargin), 0.5f / levelPpm), footprints);
        new FootprintPainter(canvas, style).Paint(footprints);

        return new WorldMapChunkImage(chunk, size, canvas.ToBytes(), revision) { Border = border };
    }
}
