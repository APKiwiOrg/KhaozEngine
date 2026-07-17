using System;
using System.Collections.Generic;
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

        Assert.Throws<ArgumentException>(() => NavHopLinks.Generate(grid, stepHeight: 0.5f, jumpHeight: 0.3f));
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
}
