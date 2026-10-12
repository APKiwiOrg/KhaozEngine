using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap.Raster;

namespace KhaozEngine.WorldMap;

/// <summary>Builds deterministic, GPU-free world-map imagery from an authored surface.</summary>
public static class WorldMapRasterizer
{
    /// <summary>Samples and shades one north-up chunk, including water, shoreline and footprint symbols.</summary>
    /// <param name="source">The authored surface to sample.</param>
    /// <param name="style">The map raster resolution and appearance.</param>
    /// <param name="chunk">The map-space chunk address.</param>
    /// <returns>A square RGBA8 image stamped with the revision captured before sampling.</returns>
    public static WorldMapChunkImage Rasterize(IWorldMapSource source, WorldMapStyle style, WorldMapChunkCoord chunk)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(style);
        if (!float.IsFinite(style.PixelsPerMetre) || style.PixelsPerMetre <= 0f)
            throw new ArgumentOutOfRangeException(nameof(style), "Pixels per metre must be finite and positive.");

        MapRect rect = WorldMapChunking.ChunkRect(chunk, style.ChunkMetres);
        int size = style.ChunkPixels;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        long revision = source.Revision;
        float margin = WorldMapRasterReach.SurfaceMetres(style, source.SurfaceCellSize);
        var sampler = new SurfaceSampler(source.SampleSurface(rect.Expand(margin)));
        var canvas = new RasterCanvas(size, rect, style.PixelsPerMetre);
        var relief = new ReliefShader(style.Relief);
        var water = new WaterPainter(sampler, canvas, style.Water);

        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
            {
                Vector2 point = canvas.MapPoint(column, row);
                SurfaceSample sample = sampler.Sample(point);
                Color colour = !sample.HasGround ? style.VoidColor : sample.IsWet
                    ? water.Paint(column, row, sample)
                    : relief.Shade(sampler, point, sample);
                canvas.Set(column, row, colour);
            }

        var footprints = new List<WorldMapFootprint>();
        float footprintMargin = WorldMapRasterReach.FootprintMetres(style);
        source.CollectFootprints(rect.Expand(footprintMargin), footprints);
        new FootprintPainter(canvas, style).Paint(footprints);

        return new WorldMapChunkImage(chunk, size, canvas.ToBytes(), revision);
    }
}
