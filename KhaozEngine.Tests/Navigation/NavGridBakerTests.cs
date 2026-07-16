using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavGridBakerTests
{
    static TerrainField FlatField()
        => new(new TerrainConfig
        {
            Seed = 1,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
            },
        });

    static TerrainField BumpyField()
        => new(new TerrainConfig
        {
            Seed = 7, BiomeBlend = 24f,
            GentleFrequency = 0.03f, GentleAmplitude = 2f,
            DetailFrequency = 0.15f, DetailOctaves = 4,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Mountains, BaseHeight = 0f, HillAmplitude = 6f },
            },
        });

    [Fact]
    public void BakeOverworld_ColliderRasterization_BlocksFootprintLeavesFarCellsPassable()
    {
        var terrain = new TerrainCollision(FlatField());
        var colliders = new WorldColliders(new[] { WorldCollider.Cylinder(new Vector2(5f, 5f), 1.5f) });

        NavGrid grid = NavGridBaker.BakeOverworld(
            terrain, colliders,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 0.5f, maxSlopeRadians: MathF.PI / 2f);

        var (centerCx, centerCz) = grid.CellOf(5f, 5f);
        Assert.Equal(0, grid.ClearanceAt(centerCx, centerCz));

        var (edgeCx, edgeCz) = grid.CellOf(5f, 6.6f);
        Assert.Equal(0, grid.ClearanceAt(edgeCx, edgeCz));

        var (farCx, farCz) = grid.CellOf(9f, 9f);
        Assert.True(grid.IsPassable(farCx, farCz, 0.4f));
    }

    [Fact]
    public void BakeOverworld_SlopeGate_MatchesTerrainPredicateAtCellCenter()
    {
        var terrain = new TerrainCollision(BumpyField());
        var colliders = new WorldColliders(Array.Empty<WorldCollider>());
        const float maxSlope = 0.35f;

        NavGrid grid = NavGridBaker.BakeOverworld(
            terrain, colliders,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, maxSlopeRadians: maxSlope);

        for (int cz = 0; cz < grid.Height; cz++)
        {
            for (int cx = 0; cx < grid.Width; cx++)
            {
                Vector2 center = grid.CellCenter(cx, cz);
                bool blocked = grid.ClearanceAt(cx, cz) == 0;
                bool terrainBlocked = !terrain.IsWalkable(center.X, center.Y, maxSlope);
                Assert.Equal(terrainBlocked, blocked);
            }
        }
    }

    [Fact]
    public void BakeOverworld_ExtraBlocked_OverridesAtCellCenter()
    {
        var terrain = new TerrainCollision(FlatField());
        var colliders = new WorldColliders(Array.Empty<WorldCollider>());

        NavGrid grid = NavGridBaker.BakeOverworld(
            terrain, colliders,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, maxSlopeRadians: MathF.PI / 2f,
            extraBlocked: (x, z) => x > 5f);

        var (blockedCx, blockedCz) = grid.CellOf(7f, 2f);
        Assert.Equal(0, grid.ClearanceAt(blockedCx, blockedCz));

        var (openCx, openCz) = grid.CellOf(2f, 2f);
        Assert.True(grid.IsPassable(openCx, openCz, 0f));
    }
}
