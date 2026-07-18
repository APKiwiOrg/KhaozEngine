using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Navigation;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class BakeOverworldStepsTests
{
    // Mirrors NavGridBakerTests.FlatField: flat-slope terrain (HillAmplitude 0) with the default gentle
    // low-frequency roll still present, so heights are nonzero but slopes stay walkable. Good enough for
    // the parity test (both bakes read the same terrain), not for exact-height assertions.
    static TerrainField FlatField()
        => new(new TerrainConfig
        {
            Seed = 1,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
            },
        });

    // Mirrors TerrainSurfaceProviderTests.FlatField: truly flat (height 0 everywhere), zeroing the gentle
    // roll too, so exact ground/prop heights can be asserted.
    static TerrainField TrulyFlatField()
        => new(new TerrainConfig
        {
            Seed = 1,
            GentleAmplitude = 0f,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = 0f, HillAmplitude = 0f },
            },
        });

    // A 3x3 plus-shaped prop top at world Y = y, its center grid point defined so a query on that exact
    // point returns y (matches TerrainSurfaceProviderTests.FlatTop).
    static PropSurface FlatTop(float y)
    {
        float n = float.NaN;
        return new PropSurface(3, 3, 1f, -1f, -1f, new[] { n, y, n, y, y, y, n, y, n });
    }

    // A flat standable surface at a constant height with open-sky headroom.
    static DelegateSurfaceProvider FlatSurface(float height = 0f)
        => new((float x, float z, out float h, out float hr) => { h = height; hr = float.PositiveInfinity; return true; });

    [Fact]
    public void LowProp_BecomesWalkable_PlannerRoutesOver()
    {
        // Height 0 everywhere except a central vertical band (world x in [4.5, 5.5]) raised to 0.4, a rise
        // within the 0.5 step budget, so the band stays walkable instead of blocking.
        var provider = new DelegateSurfaceProvider((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            h = x >= 4.5f && x <= 5.5f ? 0.4f : 0f;
            return true;
        });

        NavGrid grid = NavGridBaker.BakeOverworldSteps(
            provider,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 0.5f, stepHeight: 0.5f, agentHeight: 0f);

        var (centerCx, centerCz) = grid.CellOf(5f, 5f);
        Assert.True(grid.IsPassable(centerCx, centerCz, 0.3f));

        // Start and goal sit on the horizontal line z = 5, which runs straight through the raised band at
        // x = 5. A walkable band lets the planner keep the straight line-of-sight fast path (one waypoint,
        // the goal), which is the proof the low prop no longer forces a detour.
        var planner = new GridPathPlanner(NavSpace.Single(grid));
        NavPath path = planner.FindPath(new Vector3(1f, 0f, 5f), new Vector3(9f, 0f, 5f), 0f);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Single(path.Waypoints);
        Assert.Equal(9f, path.Waypoints[0].Position.X, 3);
        Assert.Equal(5f, path.Waypoints[0].Position.Y, 3);
    }

    [Fact]
    public void TallProp_StillBlocks_PlannerRoutesAround()
    {
        // Same central band, but raised 10.0, far past the 0.5 step budget, so its cells bake blocked. The
        // wall only spans z <= 6, leaving a gap in the high-z rows so a detour exists (not Unreachable).
        var provider = new DelegateSurfaceProvider((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            h = x >= 4.5f && x <= 5.5f && z <= 6f ? 10f : 0f;
            return true;
        });

        NavGrid grid = NavGridBaker.BakeOverworldSteps(
            provider,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 0.5f, stepHeight: 0.5f, agentHeight: 0f);

        var (blockedCx, blockedCz) = grid.CellOf(5f, 5f);
        Assert.Equal(0, grid.ClearanceAt(blockedCx, blockedCz));
        var (blockedCx2, blockedCz2) = grid.CellOf(5f, 2f);
        Assert.Equal(0, grid.ClearanceAt(blockedCx2, blockedCz2));

        var planner = new GridPathPlanner(NavSpace.Single(grid));
        NavPath path = planner.FindPath(new Vector3(1f, 0f, 5f), new Vector3(9f, 0f, 5f), 0f);

        Assert.Equal(NavPathStatus.Complete, path.Status);

        // The straight z = 5 line is blocked, so a completed path had to detour over the top of the wall.
        // No waypoint may sit inside the blocked band (world x in [4.5, 5.5] and z <= 6), and at least one
        // must clear the wall's z extent, proving the route went around it rather than through it.
        float maxWaypointZ = float.NegativeInfinity;
        foreach (NavWaypoint wp in path.Waypoints)
        {
            bool insideBlockedBand = wp.Position.X >= 4.5f && wp.Position.X <= 5.5f && wp.Position.Y <= 6f;
            Assert.False(insideBlockedBand);
            if (wp.Position.Y > maxWaypointZ) maxWaypointZ = wp.Position.Y;
        }

        Assert.True(maxWaypointZ > 6f);
    }

    [Fact]
    public void ExtraBlocked_BlocksCell()
    {
        NavGrid grid = NavGridBaker.BakeOverworldSteps(
            FlatSurface(),
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: 1f, agentHeight: 0f,
            extraBlocked: (x, z) => x > 5f);

        var (blockedCx, blockedCz) = grid.CellOf(7f, 2f);
        Assert.Equal(0, grid.ClearanceAt(blockedCx, blockedCz));

        var (openCx, openCz) = grid.CellOf(2f, 2f);
        Assert.True(grid.IsPassable(openCx, openCz, 0f));
    }

    [Fact]
    public void TerrainOnlyProvider_MatchesBakeOverworld_OnFlatTerrain()
    {
        var terrain = new TerrainCollision(FlatField());
        var colliders = new WorldColliders(Array.Empty<WorldCollider>());
        const float maxSlope = MathF.PI / 2f;

        // A terrain-only provider with a huge step budget can never erode a flat-slope surface, so it must
        // reproduce BakeOverworld's mask cell-for-cell over the same region.
        var provider = new TerrainSurfaceProvider(terrain, maxSlope);

        NavGrid stepGrid = NavGridBaker.BakeOverworldSteps(
            provider,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: 1000f, agentHeight: 0f);

        NavGrid overworldGrid = NavGridBaker.BakeOverworld(
            terrain, colliders,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, maxSlopeRadians: maxSlope);

        Assert.Equal(overworldGrid.Width, stepGrid.Width);
        Assert.Equal(overworldGrid.Height, stepGrid.Height);
        for (int cz = 0; cz < overworldGrid.Height; cz++)
            for (int cx = 0; cx < overworldGrid.Width; cx++)
                Assert.Equal(overworldGrid.ClearanceAt(cx, cz), stepGrid.ClearanceAt(cx, cz));
    }

    [Fact]
    public void TerrainSurfaceProvider_EndToEnd_LowSurfaceWalkable()
    {
        var terrain = new TerrainCollision(TrulyFlatField());
        // A low prop (top 0.4, within the step budget) centered on a cell center so the query lands on the
        // prop's defined center grid point.
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(FlatTop(0.4f), new Vector2(5.5f, 5.5f), 1f, 0f, 0f) });
        var provider = new TerrainSurfaceProvider(terrain, MathF.PI / 2f, surfaces);

        NavGrid grid = NavGridBaker.BakeOverworldSteps(
            provider,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: 0.5f, agentHeight: 0f);

        var (coveredCx, coveredCz) = grid.CellOf(5.5f, 5.5f);
        Assert.True(grid.IsPassable(coveredCx, coveredCz, 0.3f));

        float? surfaceTop = grid.SurfaceHeightAt(coveredCx, coveredCz);
        Assert.True(surfaceTop.HasValue);
        Assert.Equal(0.4f, surfaceTop!.Value, 3);
    }

    [Fact]
    public void Deterministic()
    {
        var provider = new DelegateSurfaceProvider((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            h = x >= 4.5f && x <= 5.5f ? 0.4f : 0f;
            return true;
        });

        NavGrid first = NavGridBaker.BakeOverworldSteps(
            provider,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 0.5f, stepHeight: 0.5f, agentHeight: 0f);
        NavGrid second = NavGridBaker.BakeOverworldSteps(
            provider,
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 0.5f, stepHeight: 0.5f, agentHeight: 0f);

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);
        for (int cz = 0; cz < first.Height; cz++)
        {
            for (int cx = 0; cx < first.Width; cx++)
            {
                Assert.Equal(first.ClearanceAt(cx, cz), second.ClearanceAt(cx, cz));
                Assert.Equal(first.SurfaceHeightAt(cx, cz), second.SurfaceHeightAt(cx, cz));
            }
        }
    }

    [Fact]
    public void Validation()
    {
        DelegateSurfaceProvider provider = FlatSurface();

        Assert.Throws<ArgumentNullException>(() => NavGridBaker.BakeOverworldSteps(
            null!, 0f, 0f, 10f, 10f, 0.5f, 0.5f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldSteps(
            provider, 0f, 0f, 0f, 10f, 0.5f, 0.5f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldSteps(
            provider, 0f, 0f, 10f, 0f, 0.5f, 0.5f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldSteps(
            provider, 0f, 0f, 10f, 10f, 0f, 0.5f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldSteps(
            provider, 0f, 0f, 10f, 10f, 0.5f, -0.5f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldSteps(
            provider, 0f, 0f, 10f, 10f, 0.5f, 0.5f, -0.5f));
    }
}
