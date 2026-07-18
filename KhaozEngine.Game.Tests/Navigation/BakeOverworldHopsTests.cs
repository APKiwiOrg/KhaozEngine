using System;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class BakeOverworldHopsTests
{
    const float StepHeight = 0.5f;
    const float AgentHeight = 0f;
    const float JumpHeight = 1.2f;

    // A 3x3 raised mesa (world x, z in [4, 7)) at MesaTop, flat ground elsewhere at height 0, matching the
    // NavHopLinksTests mesa shape but expressed as a world-space surface provider so BakeOverworldSteps
    // samples it through cell centers.
    const float MesaTop = 1.0f;

    static bool IsMesa(float x, float z) => x >= 4f && x < 7f && z >= 4f && z < 7f;

    static DelegateSurfaceProvider MesaProvider()
        => new((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            h = IsMesa(x, z) ? MesaTop : 0f;
            return true;
        });

    static DelegateSurfaceProvider FlatProvider(float height = 0f)
        => new((float x, float z, out float h, out float hr) => { h = height; hr = float.PositiveInfinity; return true; });

    [Fact]
    public void IsolatedMesa_SpaceCarriesHopLinks()
    {
        NavSpace space = NavGridBaker.BakeOverworldHops(
            MesaProvider(),
            minX: 0f, minZ: 0f, maxX: 12f, maxZ: 12f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

        Assert.Single(space.Layers);
        Assert.NotEmpty(space.Links);
        foreach (NavLink link in space.Links)
            Assert.Equal(NavLinkKind.Hop, link.Kind);
    }

    [Fact]
    public void FlatProvider_NoHopLinks()
    {
        NavSpace space = NavGridBaker.BakeOverworldHops(
            FlatProvider(),
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

        Assert.Empty(space.Links);

        NavGrid expected = NavGridBaker.BakeOverworldSteps(
            FlatProvider(),
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight);

        NavGrid actual = space.Layers[0];
        for (int cz = 0; cz < expected.Height; cz += 3)
            for (int cx = 0; cx < expected.Width; cx += 3)
                Assert.Equal(expected.ClearanceAt(cx, cz), actual.ClearanceAt(cx, cz));
    }

    [Fact]
    public void GridMatchesBakeOverworldSteps()
    {
        NavSpace space = NavGridBaker.BakeOverworldHops(
            MesaProvider(),
            minX: 0f, minZ: 0f, maxX: 12f, maxZ: 12f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

        NavGrid expected = NavGridBaker.BakeOverworldSteps(
            MesaProvider(),
            minX: 0f, minZ: 0f, maxX: 12f, maxZ: 12f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight);

        NavGrid actual = space.Layers[0];
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.CellSize, actual.CellSize);
        for (int cz = 0; cz < expected.Height; cz++)
            for (int cx = 0; cx < expected.Width; cx++)
                Assert.Equal(expected.ClearanceAt(cx, cz), actual.ClearanceAt(cx, cz));
    }

    [Fact]
    public void ExtraBlocked_StillBlocks()
    {
        NavSpace space = NavGridBaker.BakeOverworldHops(
            FlatProvider(),
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            extraBlocked: (x, z) => x > 5f);

        NavGrid grid = space.Layers[0];
        var (blockedCx, blockedCz) = grid.CellOf(7f, 2f);
        Assert.Equal(0, grid.ClearanceAt(blockedCx, blockedCz));

        var (openCx, openCz) = grid.CellOf(2f, 2f);
        Assert.True(grid.IsPassable(openCx, openCz, 0f));
    }

    [Fact]
    public void JumpHeightNotAboveStep_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldHops(
            FlatProvider(),
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: StepHeight));
    }

    [Fact]
    public void MaxHopCellsBelowTwo_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavGridBaker.BakeOverworldHops(
            FlatProvider(),
            minX: 0f, minZ: 0f, maxX: 10f, maxZ: 10f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            maxHopCells: 1));
    }

    // A single-row world with a two-cell-thick unsampled rim: ground at height 0 for x < 1, unsampled
    // (blocked) for x in [1, 3), a raised landing at 1.0 for x >= 3. Cell centers land at 0.5, 1.5, 2.5,
    // 3.5, so the bake yields standable, blocked, blocked, standable with a rise inside the jump band.
    static DelegateSurfaceProvider ThickRimProvider()
        => new((float x, float z, out float h, out float hr) =>
        {
            hr = float.PositiveInfinity;
            if (x >= 1f && x < 3f) { h = 0f; return false; }
            h = x < 1f ? 0f : MesaTop;
            return true;
        });

    [Fact]
    public void MaxHopCells_PassesThroughToGeneration()
    {
        // Default maxHopCells 2 cannot cross the two-cell rim (the ray ends before a landing), while 3
        // reaches it. A hardcoded 2 inside BakeOverworldHops would leave both spaces link-free.
        NavSpace defaultReach = NavGridBaker.BakeOverworldHops(
            ThickRimProvider(),
            minX: 0f, minZ: 0f, maxX: 4f, maxZ: 1f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight);

        NavSpace extendedReach = NavGridBaker.BakeOverworldHops(
            ThickRimProvider(),
            minX: 0f, minZ: 0f, maxX: 4f, maxZ: 1f,
            cellSize: 1f, stepHeight: StepHeight, agentHeight: AgentHeight, jumpHeight: JumpHeight,
            maxHopCells: 3);

        Assert.Empty(defaultReach.Links);
        Assert.NotEmpty(extendedReach.Links);
        Assert.Contains(new NavLink(0, 0, 0, 0, 3, 0) { Kind = NavLinkKind.Hop }, extendedReach.Links);
        Assert.Contains(new NavLink(0, 3, 0, 0, 0, 0) { Kind = NavLinkKind.Hop }, extendedReach.Links);
    }
}
