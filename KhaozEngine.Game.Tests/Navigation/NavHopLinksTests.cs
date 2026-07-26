using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavHopLinksTests
{
    const float StepHeight = 0.5f;
    const float JumpHeight = 1.2f;

    static bool IsMesa(int x, int z) => x >= 4 && x <= 8 && z >= 4 && z <= 8;

    static NavGrid MesaGrid(float mesaHeight)
        => NavGrid.FromSurfaces(12, 12, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(true, IsMesa(x, z) ? mesaHeight : 0f, float.PositiveInfinity),
            StepHeight, agentHeight: 0f);

    [Fact]
    public void IsolatedMesa_EmitsHopAcrossBlockedRim_BothDirections()
    {
        NavGrid grid = MesaGrid(1.0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        // Ground cell (3, 5) is two cells outside the mesa rim. Mesa interior cell (5, 5) is the
        // first passable cell reached scanning +X. Both directions must appear.
        Assert.Contains(links, l =>
            l.Kind == NavLinkKind.Hop && l.FromX == 3 && l.FromZ == 5 && l.ToX == 5 && l.ToZ == 5);
        Assert.Contains(links, l =>
            l.Kind == NavLinkKind.Hop && l.FromX == 5 && l.FromZ == 5 && l.ToX == 3 && l.ToZ == 5);

        Assert.NotEmpty(links);
        foreach (NavLink link in links)
        {
            float? fromHeight = grid.SurfaceHeightAt(link.FromX, link.FromZ);
            float? toHeight = grid.SurfaceHeightAt(link.ToX, link.ToZ);
            Assert.NotNull(fromHeight);
            Assert.NotNull(toHeight);
            float rise = MathF.Abs(toHeight!.Value - fromHeight!.Value);
            Assert.True(rise > StepHeight);
            Assert.True(rise <= JumpHeight);
        }
    }

    [Fact]
    public void IsolatedOneCellTop_SurvivesTheStepBake_AndIsLinkedBothWays()
    {
        // A 5x5 terrace: the center cell at 2.0, its ring at 1.0, the outer ring at 0.0. The ring erodes,
        // which leaves the center a one-cell island. The step bake keeps it (its whole neighborhood is
        // blocked, so it gains no walk edge), and that is the only reason a hop can reach it at all.
        NavGrid grid = NavGrid.FromSurfaces(5, 5, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(
                true,
                Math.Max(Math.Abs(x - 2), Math.Abs(z - 2)) switch { 0 => 2f, 1 => 1f, _ => 0f },
                float.PositiveInfinity),
            stepHeight: 0.4f, agentHeight: 0f);

        float? topHeight = grid.SurfaceHeightAt(2, 2);
        Assert.NotNull(topHeight);
        Assert.Equal(2f, topHeight!.Value);
        Assert.Null(grid.SurfaceHeightAt(2, 1)); // the eroded ring around it

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, stepHeight: 0.4f, jumpHeight: 2.5f);

        Assert.Contains(links, l => l.Kind == NavLinkKind.Hop && l.ToX == 2 && l.ToZ == 2);
        Assert.Contains(links, l => l.Kind == NavLinkKind.Hop && l.FromX == 2 && l.FromZ == 2);

        // End to end: the preserved island is routable precisely because a hop targets it. Its own grid
        // neighbors are all blocked, so the link is the only way in.
        var planner = new GridPathPlanner(new NavSpace(new[] { grid }, links));
        NavPath path = planner.FindPath(new Vector3(0.5f, 0f, 2.5f), new Vector3(2.5f, 2f, 2.5f), 0.2f);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Contains(path.Waypoints, w => w.Kind == NavWaypointKind.Hop);
    }

    [Fact]
    public void EveryHop_SpansChebyshevAtLeastTwo()
    {
        NavGrid grid = MesaGrid(1.0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.NotEmpty(links);
        foreach (NavLink link in links)
        {
            int dx = Math.Abs(link.ToX - link.FromX);
            int dz = Math.Abs(link.ToZ - link.FromZ);
            Assert.True(Math.Max(dx, dz) >= 2);
        }
    }

    [Fact]
    public void Ramp_EmitsNoHop()
    {
        float[] heights = { 0f, 0.3f, 0.6f, 0.9f };
        NavGrid grid = NavGrid.FromSurfaces(4, 1, 1f, 0f, 0f,
            (x, z) => new NavSurfaceSample(true, heights[x], float.PositiveInfinity),
            StepHeight, agentHeight: 0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Empty(links);
    }

    [Fact]
    public void TooTallStep_EmitsNoHop()
    {
        NavGrid grid = MesaGrid(10.0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Empty(links);
    }

    [Fact]
    public void FlatField_EmitsNoHop()
    {
        NavGrid grid = NavGrid.FromSurfaces(12, 12, 1f, 0f, 0f,
            (_, _) => new NavSurfaceSample(true, 0f, float.PositiveInfinity),
            StepHeight, agentHeight: 0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Empty(links);
    }

    [Fact]
    public void NoHeightField_Throws()
    {
        NavGrid grid = NavGrid.FromWalkable(12, 12, 1f, 0f, 0f, (_, _) => true);

        Assert.Throws<ArgumentException>(() => NavHopLinks.Generate(grid, StepHeight, JumpHeight));
    }

    [Fact]
    public void JumpHeightNotAboveStep_Throws()
    {
        NavGrid grid = MesaGrid(1.0f);

        Assert.Throws<ArgumentOutOfRangeException>(() => NavHopLinks.Generate(grid, stepHeight: 0.5f, jumpHeight: 0.3f));
    }

    [Fact]
    public void Deterministic_SameGridTwice_IdenticalLinks()
    {
        NavGrid grid = MesaGrid(1.0f);

        IReadOnlyList<NavLink> first = NavHopLinks.Generate(grid, StepHeight, JumpHeight);
        IReadOnlyList<NavLink> second = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first, second);
    }

    // Builds a single-row grid from hand-shaped samples. Null = blocked (non-standable), a value = that
    // surface height. Bakes with a permissive step budget so passability comes only from the standable
    // flags and Generate's own stepHeight / jumpHeight drive the band.
    static NavGrid RowGrid(params float?[] cells)
        => NavGrid.FromSurfaces(cells.Length, 1, 1f, 0f, 0f,
            (x, z) => cells[x] is float h
                ? new NavSurfaceSample(true, h, float.PositiveInfinity)
                : new NavSurfaceSample(false, 0f, 0f),
            stepHeight: 100f, agentHeight: 0f);

    [Fact]
    public void FirstPassableLanding_StopsRay()
    {
        // L at 0, k1 blocked, k2 passable out of band (5.0), k3 passable in band (1.0). The ray must
        // land on k2 and stop, so nothing emits. A farthest-passable or any-intervening bug would
        // walk through k2 and emit L to k3.
        NavGrid grid = RowGrid(0f, null, 5f, 1f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight, maxHopCells: 3);

        Assert.Empty(links);
    }

    [Fact]
    public void ThickRim_HopsAcross()
    {
        // L at 0, k1 and k2 blocked, k3 passable in band. The ray continues through consecutive
        // blocked cells and emits exactly one hop per direction across the two-cell rim.
        NavGrid grid = RowGrid(0f, null, null, 1f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight, maxHopCells: 3);

        Assert.Equal(2, links.Count);
        Assert.Contains(new NavLink(0, 0, 0, 0, 3, 0) { Kind = NavLinkKind.Hop }, links);
        Assert.Contains(new NavLink(0, 3, 0, 0, 0, 0) { Kind = NavLinkKind.Hop }, links);
    }

    [Fact]
    public void RiseExactlyJumpHeight_EmitsHop()
    {
        NavGrid grid = MesaGrid(JumpHeight);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.NotEmpty(links);
    }

    [Fact]
    public void RiseExactlyStepHeight_EmitsNoHop()
    {
        // A rise of exactly stepHeight bakes passable (the step rule only blocks drops strictly above
        // the budget), so no ray ever starts and no hop emits.
        NavGrid grid = MesaGrid(StepHeight);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Empty(links);
    }

    [Fact]
    public void RimLanding_RiseExactlyStepHeight_NoHop()
    {
        // A reachable landing across a blocked rim whose rise is exactly stepHeight sits on the
        // excluded lower band edge, the band is strictly above stepHeight. A rise >= stepHeight
        // weakening would emit here.
        NavGrid grid = RowGrid(0f, null, 0.5f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Empty(links);
    }

    [Fact]
    public void RimLanding_FlatFence_NoHop()
    {
        // Same height on both sides of a blocked rim (rise 0). You do not hop a flat fence.
        NavGrid grid = RowGrid(0f, null, 0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight);

        Assert.Empty(links);
    }

    [Fact]
    public void OutOfRangeArguments_Throw()
    {
        NavGrid grid = MesaGrid(1.0f);

        Assert.Throws<ArgumentOutOfRangeException>(() => NavHopLinks.Generate(grid, StepHeight, JumpHeight, maxHopCells: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavHopLinks.Generate(grid, StepHeight, JumpHeight, maxHopCells: 2, layer: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavHopLinks.Generate(grid, stepHeight: -0.1f, jumpHeight: JumpHeight));
    }

    [Fact]
    public void NonZeroLayer_StampsBothEndpoints()
    {
        NavGrid grid = MesaGrid(1.0f);

        IReadOnlyList<NavLink> links = NavHopLinks.Generate(grid, StepHeight, JumpHeight, maxHopCells: 2, layer: 3);

        Assert.NotEmpty(links);
        foreach (NavLink link in links)
        {
            Assert.Equal(3, link.FromLayer);
            Assert.Equal(3, link.ToLayer);
        }
    }
}
