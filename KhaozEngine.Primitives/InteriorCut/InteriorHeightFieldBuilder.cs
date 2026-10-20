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
        foreach (InteriorVolume volume in volumes)
        {
            ArgumentNullException.ThrowIfNull(volume);
            InteriorFieldRasterizer.Validate(volume.Outline);
            if (!float.IsFinite(volume.FloorY) || !float.IsFinite(volume.CeilingY) || volume.CeilingY < volume.FloorY)
                throw new ArgumentException("Interior heights must be finite and ordered.", nameof(volumes));
            if (volume.CeilingY == volume.FloorY && volume.Roofs.Count == 0) continue;
            int floor = Pack(volume.FloorY, encoding), ceiling = Pack(volume.CeilingY, encoding);
            Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
            foreach (Vector2 point in volume.Outline)
            {
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            int x0 = InteriorLattice.Bound(min.X, window.Min.X, window.CellSize, window.Cells);
            int x1 = InteriorLattice.Bound(max.X, window.Min.X, window.CellSize, window.Cells);
            int z0 = InteriorLattice.Bound(min.Y, window.Min.Y, window.CellSize, window.Cells);
            int z1 = InteriorLattice.Bound(max.Y, window.Min.Y, window.CellSize, window.Cells);
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
                            if (!InteriorFieldRasterizer.Covers(volume.Outline, InteriorLattice.Corner(window, x, z),
                                InteriorLattice.Corner(window, x + 1, z + 1))) continue;
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
