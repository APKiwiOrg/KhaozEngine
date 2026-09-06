using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public sealed class TerrainSculptSamplingParityTests
{
    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.25f)]
    public void Sampling_is_bit_identical_across_interiors_seams_negative_cells_and_missing_tiles(float cellSize)
    {
        var tiles = new List<TerrainSculptTile>();
        var cells = new Dictionary<(int X, int Z), float>();
        for (int tz = -2; tz <= 2; tz++)
        for (int tx = -2; tx <= 2; tx++)
        {
            if ((tx + tz) % 3 == 0) continue;
            var values = new float[TerrainSculpt.TileSize * TerrainSculpt.TileSize];
            for (int z = 0; z < TerrainSculpt.TileSize; z++)
            for (int x = 0; x < TerrainSculpt.TileSize; x++)
            {
                float value = ((tx * 97 + tz * 71 + x * 13 + z * 7) % 113) * 0.03125f;
                values[z * TerrainSculpt.TileSize + x] = value;
                cells[(tx * TerrainSculpt.TileSize + x, tz * TerrainSculpt.TileSize + z)] = value;
            }
            tiles.Add(new TerrainSculptTile(tx, tz, values));
        }

        var sculpt = new TerrainSculpt(cellSize, tiles);
        for (int z = -66; z <= 98; z++)
        for (int x = -66; x <= 98; x++)
        {
            AssertSample(x, z);
            AssertSample(x + 0.375f, z + 0.6875f);
        }

        void AssertSample(float cellX, float cellZ)
        {
            float worldX = cellX * cellSize, worldZ = cellZ * cellSize;
            float gx = worldX / cellSize, gz = worldZ / cellSize;
            int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
            float a = cells.GetValueOrDefault((x0, z0));
            float b = cells.GetValueOrDefault((x0 + 1, z0));
            float c = cells.GetValueOrDefault((x0, z0 + 1));
            float d = cells.GetValueOrDefault((x0 + 1, z0 + 1));
            float top = a + (b - a) * (gx - x0);
            float bottom = c + (d - c) * (gx - x0);
            float expected = top + (bottom - top) * (gz - z0);
            Assert.Equal(BitConverter.SingleToInt32Bits(expected),
                BitConverter.SingleToInt32Bits(sculpt.SampleDelta(worldX, worldZ)));
        }
    }
}
