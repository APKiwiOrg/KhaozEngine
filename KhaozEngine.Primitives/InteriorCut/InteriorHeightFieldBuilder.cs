using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Primitives;

public static class InteriorHeightFieldBuilder
{
    /// <summary>Rasterize whole cells and merge quantized spans without retaining a floating-point grid.</summary>
    public static InteriorHeightField Build(IReadOnlyList<InteriorVolume> volumes, InteriorWindow window, InteriorEncoding encoding)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        window.Validate();
        encoding.Validate();
        var texels = new byte[window.Cells * window.Cells * 4];
        var occupied = new BitArray(window.Cells * window.Cells);
        var merged = new BitArray(window.Cells * window.Cells);
        var outOfRange = new BitArray(window.Cells * window.Cells);
        const int sliceSize = 64;
        float[]? roofScratch = null;
        int mergedCells = 0, outOfRangeCells = 0;
        double originX = RasterOrigin(window.Min.X, window.CellSize), originZ = RasterOrigin(window.Min.Y, window.CellSize);
        foreach (InteriorVolume volume in volumes)
        {
            ArgumentNullException.ThrowIfNull(volume);
            InteriorFieldRasterizer.Validate(volume.Outline);
            if (!float.IsFinite(volume.FloorY) || !float.IsFinite(volume.CeilingY) || volume.CeilingY < volume.FloorY)
                throw new ArgumentException("Interior heights must be finite and ordered.", nameof(volumes));
            if (volume.CeilingY == volume.FloorY) continue;
            int floor = Pack(volume.FloorY, encoding), ceiling = Pack(volume.CeilingY, encoding);
            Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (Vector2 point in volume.Outline)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            int x0 = CellBound(min.X, originX, window), x1 = CellBound(max.X, originX, window);
            int z0 = CellBound(min.Y, originZ, window), z1 = CellBound(max.Y, originZ, window);
            for (int firstZ = z0; firstZ < z1; firstZ += sliceSize)
                for (int firstX = x0; firstX < x1; firstX += sliceSize)
                {
                    int width = Math.Min(sliceSize, x1 - firstX), height = Math.Min(sliceSize, z1 - firstZ);
                    Span<float> lowestSamples = default;
                    if (volume.Roofs.Count > 0)
                    {
                        roofScratch ??= new float[sliceSize * sliceSize];
                        lowestSamples = roofScratch.AsSpan(0, width * height);
                        lowestSamples.Fill(float.PositiveInfinity);
                        foreach (InteriorRoof roof in volume.Roofs)
                            InteriorRoofRasterizer.Lower(roof, window, firstX, firstZ, width, height, lowestSamples);
                    }
                    for (int z = firstZ; z < firstZ + height; z++)
                        for (int x = firstX; x < firstX + width; x++)
                        {
                            var cellMin = new Vector2((float)(originX + x * (double)window.CellSize), (float)(originZ + z * (double)window.CellSize));
                            if (!InteriorFieldRasterizer.Covers(volume.Outline, cellMin, window.CellSize)) continue;
                            float sample = lowestSamples.IsEmpty ? float.PositiveInfinity : lowestSamples[(z - firstZ) * width + x - firstX];
                            float topY = float.IsPositiveInfinity(sample) ? volume.CeilingY : sample;
                            if (topY <= volume.FloorY) continue;
                            int top = float.IsPositiveInfinity(sample) ? ceiling : Pack(topY, encoding);
                            bool clamped = volume.FloorY < encoding.BaseY || topY > encoding.BaseY + 65535f * encoding.StepY;
                            int cell = z * window.Cells + x, offset = cell * 4;
                            if (clamped && !outOfRange[cell])
                            {
                                outOfRange[cell] = true;
                                outOfRangeCells++;
                            }
                            int oldFloor = (texels[offset] << 8) | texels[offset + 1];
                            int oldTop = (texels[offset + 2] << 8) | texels[offset + 3];
                            int mergedFloor = floor, mergedTop = top;
                            // Collapsed endpoints still contribute to a later union. Span lookup applies the
                            // empty-span rule only after all positive-height contributions have been merged.
                            if (occupied[cell])
                            {
                                mergedFloor = Math.Min(floor, oldFloor);
                                mergedTop = Math.Max(top, oldTop);
                                if (!merged[cell])
                                {
                                    merged[cell] = true;
                                    mergedCells++;
                                }
                            }
                            occupied[cell] = true;
                            texels[offset] = (byte)(mergedFloor >> 8);
                            texels[offset + 1] = (byte)mergedFloor;
                            texels[offset + 2] = (byte)(mergedTop >> 8);
                            texels[offset + 3] = (byte)mergedTop;
                        }
                }
        }
        return new InteriorHeightField(window, encoding, texels, mergedCells, outOfRangeCells);
    }

    static int CellBound(float value, double min, InteriorWindow window) =>
        (int)Math.Clamp(Math.Ceiling(((double)value - min) / window.CellSize), 0d, window.Cells);

    static double RasterOrigin(float min, float cellSize)
    {
        // Recover the world-origin lattice before adding local indices. Reusing a rounded float minimum
        // otherwise changes the first filled cell after a re-centre at non-binary pitches such as 0.24 m.
        double snapped = Math.Round((double)min / cellSize) * cellSize;
        return (float)snapped == min ? snapped : min;
    }

    static int Pack(float height, InteriorEncoding encoding)
    {
        // Round down against the shader's decoded heights, including the rounded last centimetre step.
        int low = 0, high = 65535;
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (encoding.BaseY + encoding.StepY * middle <= height) low = middle;
            else high = middle - 1;
        }
        return low;
    }
}
