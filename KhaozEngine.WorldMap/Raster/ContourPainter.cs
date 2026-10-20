using System;
using System.Numerics;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Antialiases local height isolines with a bounded raster-space neighbourhood.</summary>
internal sealed class ContourPainter(SurfaceSampler sampler, RasterCanvas canvas, WorldMapContours settings)
{
    // Includes half the stroke, its antialias fringe and a finite-difference sample.
    internal static float ReachPixels(WorldMapContours settings) => settings.WidthPixels * 0.5f + 1f;

    internal void Paint()
    {
        float pixelMetres = 1f / canvas.EffectivePixelsPerMetre;
        float derivativeStep = pixelMetres * 0.5f;
        float reach = (settings.WidthPixels * 0.5f + 0.5f) * pixelMetres;
        for (int row = 0; row < canvas.Size; row++)
            for (int column = 0; column < canvas.Size; column++)
            {
                Vector2 point = canvas.MapPoint(column, row);
                SurfaceSample sample = sampler.Sample(point);
                if (!sample.HasGround || sample.IsWet) continue;
                double contour = Math.Round(sample.Height / (double)settings.IntervalMetres) * settings.IntervalMetres;
                double east = Height(point + new Vector2(derivativeStep, 0f), sample.Height);
                double west = Height(point - new Vector2(derivativeStep, 0f), sample.Height);
                double north = Height(point + new Vector2(0f, derivativeStep), sample.Height);
                double south = Height(point - new Vector2(0f, derivativeStep), sample.Height);
                double dx = east - west, dy = north - south;
                double gradientPerPixel = Math.Sqrt(dx * dx + dy * dy);
                if (gradientPerPixel <= 0d) continue;

                double min = Math.Min(Math.Min(east, west), Math.Min(north, south));
                double max = Math.Max(Math.Max(east, west), Math.Max(north, south));
                Include(Height(point + new Vector2(reach, 0f), sample.Height), ref min, ref max);
                Include(Height(point - new Vector2(reach, 0f), sample.Height), ref min, ref max);
                Include(Height(point + new Vector2(0f, reach), sample.Height), ref min, ref max);
                Include(Height(point - new Vector2(0f, reach), sample.Height), ref min, ref max);
                for (int y = -1; y <= 1; y += 2)
                    for (int x = -1; x <= 1; x += 2)
                        Include(Height(point + new Vector2(x * reach, y * reach), sample.Height), ref min, ref max);
                // A level plateau has no crossing, even when neighbouring slopes produce a gradient.
                if (min >= contour || max <= contour) continue;

                double distancePixels = Math.Abs(sample.Height - contour) / gradientPerPixel;
                float coverage = (float)Math.Clamp(settings.WidthPixels * 0.5d + 0.5d - distancePixels, 0d, 1d);
                if (coverage > 0f) canvas.Blend(column, row, settings.Color, coverage);
            }
    }

    double Height(Vector2 point, float fallback)
    {
        SurfaceSample sample = sampler.Sample(point);
        return sample.HasGround ? sample.Height : fallback;
    }

    static void Include(double height, ref double min, ref double max)
    {
        min = Math.Min(min, height);
        max = Math.Max(max, height);
    }
}
