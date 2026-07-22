using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavLayerLinksTests
{
    const float StepHeight = 0.5f;
    const float JumpHeight = 1.2f;
    const int Width = 10;
    const int Height = 10;

    // Layer A is standable only at three isolated cells, each surrounded by blocked cells of its own,
    // so every cross-layer relationship below comes from exactly the pairing under test and nothing
    // else: (2,2) for the stair pairing, (6,2) for the hop pairing, (2,6) for the same-cell overlap
    // that must produce no link at all.
    static NavGrid LayerA() => NavGrid.FromSurfaces(Width, Height, 1f, 0f, 0f,
        (cx, cz) =>
        {
            bool standable = (cx, cz) is (2, 2) or (6, 2) or (2, 6);
            return new NavSurfaceSample(standable, 0f, float.PositiveInfinity);
        },
        StepHeight, agentHeight: 0f);

    // Layer B: (3,2) at 0.3 is Chebyshev-1 east of A's (2,2) -> within step, a Stair pair. (7,2) at 1.0
    // is Chebyshev-1 east of A's (6,2) -> above step, within jump, a Hop pair. (2,6) at 5.0 sits at the
    // SAME cell as A's (2,6) (Chebyshev 0) with every one of its own neighbors blocked in both grids,
    // so the only relationship there is the overlap itself.
    static NavGrid LayerB() => NavGrid.FromSurfaces(Width, Height, 1f, 0f, 0f,
        (cx, cz) =>
        {
            if ((cx, cz) == (3, 2)) return new NavSurfaceSample(true, 0.3f, float.PositiveInfinity);
            if ((cx, cz) == (7, 2)) return new NavSurfaceSample(true, 1.0f, float.PositiveInfinity);
            if ((cx, cz) == (2, 6)) return new NavSurfaceSample(true, 5.0f, float.PositiveInfinity);
            return new NavSurfaceSample(false, 0f, 0f);
        },
        StepHeight, agentHeight: 0f);

    [Fact]
    public void StairPair_BothDirections_CorrectCoordinates()
    {
        IReadOnlyList<NavLink> links = NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, StepHeight, JumpHeight);

        Assert.Contains(links, l =>
            l.Kind == NavLinkKind.Stair && l.FromLayer == 0 && l.FromX == 2 && l.FromZ == 2 &&
            l.ToLayer == 1 && l.ToX == 3 && l.ToZ == 2);
        Assert.Contains(links, l =>
            l.Kind == NavLinkKind.Stair && l.FromLayer == 1 && l.FromX == 3 && l.FromZ == 2 &&
            l.ToLayer == 0 && l.ToX == 2 && l.ToZ == 2);
    }

    [Fact]
    public void HopPair_BothDirections_CorrectCoordinates()
    {
        IReadOnlyList<NavLink> links = NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, StepHeight, JumpHeight);

        Assert.Contains(links, l =>
            l.Kind == NavLinkKind.Hop && l.FromLayer == 0 && l.FromX == 6 && l.FromZ == 2 &&
            l.ToLayer == 1 && l.ToX == 7 && l.ToZ == 2);
        Assert.Contains(links, l =>
            l.Kind == NavLinkKind.Hop && l.FromLayer == 1 && l.FromX == 7 && l.FromZ == 2 &&
            l.ToLayer == 0 && l.ToX == 6 && l.ToZ == 2);
    }

    [Fact]
    public void SameCellOverlap_Alone_ProducesNoLink()
    {
        IReadOnlyList<NavLink> links = NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, StepHeight, JumpHeight);

        Assert.DoesNotContain(links, l =>
            (l.FromX == 2 && l.FromZ == 6) || (l.ToX == 2 && l.ToZ == 6));
    }

    [Fact]
    public void ExactlyExpectedLinkCount()
    {
        // One Stair pair (2 links) plus one Hop pair (2 links); the overlap cell contributes nothing.
        IReadOnlyList<NavLink> links = NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, StepHeight, JumpHeight);
        Assert.Equal(4, links.Count);
    }

    [Fact]
    public void NullLayers_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => NavLayerLinks.Generate(null!, StepHeight, JumpHeight));
    }

    [Fact]
    public void EmptyLayers_Throws()
    {
        Assert.Throws<ArgumentException>(() => NavLayerLinks.Generate(Array.Empty<NavGrid>(), StepHeight, JumpHeight));
    }

    [Fact]
    public void FromWalkableGrid_NoHeights_Throws()
    {
        NavGrid noHeights = NavGrid.FromWalkable(Width, Height, 1f, 0f, 0f, (_, _) => true);

        Assert.Throws<ArgumentException>(() => NavLayerLinks.Generate(new[] { LayerA(), noHeights }, StepHeight, JumpHeight));
    }

    [Fact]
    public void MismatchedWidth_Throws()
    {
        NavGrid mismatched = NavGrid.FromSurfaces(Width + 1, Height, 1f, 0f, 0f,
            (_, _) => new NavSurfaceSample(false, 0f, 0f), StepHeight, agentHeight: 0f);

        Assert.Throws<ArgumentException>(() => NavLayerLinks.Generate(new[] { LayerA(), mismatched }, StepHeight, JumpHeight));
    }

    [Fact]
    public void MismatchedCellSizeOrOrigin_Throws()
    {
        NavGrid differentCellSize = NavGrid.FromSurfaces(Width, Height, 2f, 0f, 0f,
            (_, _) => new NavSurfaceSample(false, 0f, 0f), StepHeight, agentHeight: 0f);
        Assert.Throws<ArgumentException>(() => NavLayerLinks.Generate(new[] { LayerA(), differentCellSize }, StepHeight, JumpHeight));

        NavGrid differentOrigin = NavGrid.FromSurfaces(Width, Height, 1f, 5f, 0f,
            (_, _) => new NavSurfaceSample(false, 0f, 0f), StepHeight, agentHeight: 0f);
        Assert.Throws<ArgumentException>(() => NavLayerLinks.Generate(new[] { LayerA(), differentOrigin }, StepHeight, JumpHeight));
    }

    [Fact]
    public void SingleLayer_ReturnsEmpty()
    {
        IReadOnlyList<NavLink> links = NavLayerLinks.Generate(new[] { LayerA() }, StepHeight, JumpHeight);
        Assert.Empty(links);
    }

    [Fact]
    public void NegativeStepHeight_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, -0.1f, JumpHeight));
    }

    [Fact]
    public void JumpHeightNotAboveStep_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, 0.5f, 0.3f));
    }

    [Fact]
    public void Deterministic_SameLayersTwice_IdenticalLinks()
    {
        IReadOnlyList<NavLink> first = NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, StepHeight, JumpHeight);
        IReadOnlyList<NavLink> second = NavLayerLinks.Generate(new[] { LayerA(), LayerB() }, StepHeight, JumpHeight);

        Assert.Equal(first, second);
    }
}
