using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GroundedNavLayerBakerTests
{
    const float StepHeight = 0.5f;
    const float AgentHeight = 0.25f;

    static DelegateColumnProvider FlatProvider() => new((x, z, surfaces) =>
    {
        surfaces[0] = new NavSurfaceSample(true, 0f, float.PositiveInfinity);
        return 1;
    });

    static DelegateColumnProvider DeckProvider() => new((x, z, surfaces) =>
    {
        surfaces[0] = new NavSurfaceSample(true, 0f, 3f);
        surfaces[1] = new NavSurfaceSample(true, 3f, float.PositiveInfinity);
        return 2;
    });

    [Fact]
    public void FlatGroundHasOneWalkableLayerWithoutLinks()
    {
        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            FlatProvider(), 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight);

        NavGrid grid = Assert.Single(grounded.Layers);
        Assert.Empty(grounded.Links);
        Assert.Equal(3, grid.Width);
        Assert.Equal(2, grid.Height);
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 3; x++)
                Assert.Equal(0f, grid.SurfaceHeightAt(x, z));
        NavPath path = new GridPathPlanner(grounded).FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(2.5f, 0f, 1.5f), 0f);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.All(path.Waypoints, waypoint => Assert.Equal(NavWaypointKind.Walk, waypoint.Kind));
    }

    [Fact]
    public void FractionalBoundsUseTheLegacyGridDimensions()
    {
        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            FlatProvider(), 0f, 0f, 0.3f, 0.3f, 0.1f, StepHeight, AgentHeight);
        NavSpace legacy = NavLayerBaker.BakeOverworldLayered(
            FlatProvider(), 0f, 0f, 0.3f, 0.3f, 0.1f, StepHeight, AgentHeight, jumpHeight: 1.2f);

        NavGrid grid = Assert.Single(grounded.Layers);
        Assert.Equal(3, grid.Width);
        Assert.Equal(3, grid.Height);
        Assert.Equal(legacy.Layers[0].Width, grid.Width);
        Assert.Equal(legacy.Layers[0].Height, grid.Height);
    }

    [Fact]
    public void LowStepSeamIsWalkableInBothDirectionsWithoutHops()
    {
        var columns = new DelegateColumnProvider((x, z, surfaces) =>
        {
            if (x < 1f)
            {
                surfaces[0] = new NavSurfaceSample(true, 0f, 0.5f);
                surfaces[1] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity);
                return 2;
            }
            surfaces[0] = new NavSurfaceSample(true, 0.5f, float.PositiveInfinity);
            return 1;
        });
        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, 2f, 1f, 1f, StepHeight, AgentHeight);

        Assert.Contains(grounded.Links, link => link.Kind == NavLinkKind.Stair);
        Assert.DoesNotContain(grounded.Links, link => link.Kind == NavLinkKind.Hop);
        Assert.Equal(new[] { new NavLink(0, 1, 0, 1, 0, 0), new NavLink(1, 0, 0, 0, 1, 0) }, grounded.Links);
        var planner = new GridPathPlanner(grounded);
        var lower = new Vector3(0.5f, 0f, 0.5f);
        var upper = new Vector3(0.5f, 0.5f, 0.5f);
        Assert.Equal(NavPathStatus.Complete, planner.FindPath(lower, upper, 0f).Status);
        Assert.Equal(NavPathStatus.Complete, planner.FindPath(upper, lower, 0f).Status);
    }

    [Fact]
    public void SeparatedDeckAndGroundRemainWalkableWithoutVerticalLinks()
    {
        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            DeckProvider(), 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight);

        Assert.Equal(2, grounded.Layers.Count);
        Assert.Empty(grounded.Links);
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 3; x++)
            {
                Assert.Equal(0f, grounded.Layers[0].SurfaceHeightAt(x, z));
                Assert.Equal(3f, grounded.Layers[1].SurfaceHeightAt(x, z));
            }
        var planner = new GridPathPlanner(grounded);
        Assert.Equal(NavPathStatus.Complete, planner.FindPath(
            new Vector3(0.5f, 3f, 0.5f), new Vector3(2.5f, 3f, 1.5f), 0f).Status);
        Assert.NotEqual(NavPathStatus.Complete, planner.FindPath(
            new Vector3(0.5f, 0f, 0.5f), new Vector3(0.5f, 3f, 0.5f), 0f).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroundedBakeRejectsHopOnlyRoutesAndPreservesLegacyHopOutput(bool sameLayerGap)
    {
        var columns = new DelegateColumnProvider((x, z, surfaces) =>
        {
            if (sameLayerGap && x is > 1f and < 2f) return 0;
            surfaces[0] = new NavSurfaceSample(true, x < 1f ? 0f : 1f, float.PositiveInfinity);
            return 1;
        });
        float maxX = sameLayerGap ? 3f : 2f;
        int landingX = sameLayerGap ? 2 : 1;
        int landingLayer = sameLayerGap ? 0 : 1;
        var start = new Vector3(0.5f, 0f, 0.5f);
        var goal = new Vector3(landingX + 0.5f, 1f, 0.5f);
        var oldHopWaypoints = new[]
        {
            new NavWaypoint(new Vector2(landingX + 0.5f, 0.5f), landingLayer) { Kind = NavWaypointKind.Hop },
        };

        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, maxX, 1f, 1f, StepHeight, AgentHeight);
        Assert.Empty(grounded.Links);
        Assert.NotEqual(NavPathStatus.Complete, new GridPathPlanner(grounded).FindPath(start, goal, 0f).Status);

        NavSpace legacy = NavLayerBaker.BakeOverworldLayered(
            columns, 0f, 0f, maxX, 1f, 1f, StepHeight, AgentHeight, jumpHeight: 1.2f);
        Assert.Equal(new[]
        {
            new NavLink(0, 0, 0, landingLayer, landingX, 0) { Kind = NavLinkKind.Hop },
            new NavLink(landingLayer, landingX, 0, 0, 0, 0) { Kind = NavLinkKind.Hop },
        }, legacy.Links);
        NavPath unchangedHopPath = new GridPathPlanner(legacy).FindPath(start, goal, 0f);
        Assert.Equal(NavPathStatus.Complete, unchangedHopPath.Status);
        Assert.Equal(oldHopWaypoints, unchangedHopPath.Waypoints);
    }

    [Fact]
    public void EmptyWorldHasOneBlockedLayerAndCountsAgainstTheBudget()
    {
        var columns = new DelegateColumnProvider((x, z, surfaces) => 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight, maxLayerCells: 5));
        NavSpace empty = NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight, maxLayerCells: 6);

        NavGrid grid = Assert.Single(empty.Layers);
        Assert.Empty(empty.Links);
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 3; x++)
                Assert.False(grid.IsPassable(x, z, 0f));
    }

    [Fact]
    public void LayerCellBudgetFailsBeforeGridMaterialization()
    {
        NavSpace bakeWithTooSmallBudget() => NavLayerBaker.BakeGroundedLayered(
            DeckProvider(), 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight, maxLayerCells: 11);

        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => bakeWithTooSmallBudget());
        Assert.Equal("maxLayerCells", error.ParamName);
        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            DeckProvider(), 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight, maxLayerCells: 12);
        Assert.Equal(2, grounded.Layers.Count);
        Assert.Equal(3f, grounded.Layers[1].SurfaceHeightAt(2, 1));
    }

    [Fact]
    public void HeadroomAndExtraBlockedFilterEveryLayer()
    {
        NavSpace grounded = NavLayerBaker.BakeGroundedLayered(
            DeckProvider(), 0f, 0f, 3f, 2f, 1f, StepHeight, agentHeight: 3.5f,
            extraBlocked: (x, z) => x > 2f);

        NavGrid grid = Assert.Single(grounded.Layers);
        Assert.Equal(3f, grid.SurfaceHeightAt(1, 1));
        Assert.Null(grid.SurfaceHeightAt(2, 1));

        NavSpace both = NavLayerBaker.BakeGroundedLayered(
            DeckProvider(), 0f, 0f, 3f, 2f, 1f, StepHeight, AgentHeight,
            extraBlocked: (x, z) => x > 2f);
        Assert.Equal(2, both.Layers.Count);
        Assert.All(both.Layers, layer => Assert.Null(layer.SurfaceHeightAt(2, 1)));
    }

    [Fact]
    public void NullProviderThrows()
    {
        Assert.Throws<ArgumentNullException>(() => NavLayerBaker.BakeGroundedLayered(
            null!, 0f, 0f, 1f, 1f, 1f, StepHeight, AgentHeight));
    }

    public static IEnumerable<object[]> NonFiniteArguments()
    {
        foreach (string argument in new[] { "minX", "minZ", "maxX", "maxZ", "cellSize", "stepHeight", "agentHeight" })
            foreach (float value in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
                yield return new object[] { argument, value };
    }

    [Theory]
    [MemberData(nameof(NonFiniteArguments))]
    [InlineData("maxX", 0f)]
    [InlineData("maxX", -1f)]
    [InlineData("maxZ", 0f)]
    [InlineData("maxZ", -1f)]
    [InlineData("cellSize", 0f)]
    [InlineData("cellSize", -1f)]
    [InlineData("stepHeight", -1f)]
    [InlineData("agentHeight", -1f)]
    public void InvalidBoundsAndSizesThrow(string argument, float value)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeGroundedLayered(
            FlatProvider(), argument == "minX" ? value : 0f, argument == "minZ" ? value : 0f,
            argument == "maxX" ? value : 1f, argument == "maxZ" ? value : 1f,
            argument == "cellSize" ? value : 1f, argument == "stepHeight" ? value : StepHeight,
            argument == "agentHeight" ? value : AgentHeight));
        Assert.Equal(argument, error.ParamName);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(4, 0)]
    [InlineData(4, -1)]
    public void InvalidSurfaceAndLayerBudgetsThrow(int maxSurfaces, int maxCells)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeGroundedLayered(
            FlatProvider(), 0f, 0f, 1f, 1f, 1f, StepHeight, AgentHeight,
            maxSurfacesPerColumn: maxSurfaces, maxLayerCells: maxCells));
    }

    [Theory]
    [InlineData(float.MaxValue, 1f, 1f)]
    [InlineData(1f, float.MaxValue, 1f)]
    [InlineData(65536f, 65536f, 1f)]
    [InlineData(1f, 1f, float.Epsilon)]
    public void DimensionOverflowThrowsBeforeSampling(float maxX, float maxZ, float cellSize)
    {
        var columns = new DelegateColumnProvider((x, z, surfaces) =>
            throw new Xunit.Sdk.XunitException("Invalid dimensions reached the provider."));

        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, maxX, maxZ, cellSize, StepHeight, AgentHeight));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public void ProviderCountOutsideBufferThrows(int count)
    {
        var columns = new DelegateColumnProvider((x, z, surfaces) => count);
        Assert.Throws<InvalidOperationException>(() => NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, 1f, 1f, 1f, StepHeight, AgentHeight));
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void ProviderNonAscendingStandableSurfacesThrow(float secondHeight)
    {
        var columns = new DelegateColumnProvider((x, z, surfaces) =>
        {
            surfaces[0] = new NavSurfaceSample(true, 1f, float.PositiveInfinity);
            surfaces[1] = new NavSurfaceSample(true, secondHeight, float.PositiveInfinity);
            return 2;
        });
        Assert.Throws<InvalidOperationException>(() => NavLayerBaker.BakeGroundedLayered(
            columns, 0f, 0f, 1f, 1f, 1f, StepHeight, AgentHeight));
    }
}
