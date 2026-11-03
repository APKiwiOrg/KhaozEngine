using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavSpaceTests
{
    static NavGrid MakeGrid(float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity)
        => NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true, yMin, yMax);

    [Fact]
    public void LayerOf_SingleLayer_AlwaysZero()
    {
        NavSpace space = NavSpace.Single(MakeGrid());
        Assert.Equal(0, space.LayerOf(-1000f));
        Assert.Equal(0, space.LayerOf(0f));
        Assert.Equal(0, space.LayerOf(1000f));
    }

    [Theory]
    [InlineData(3f, 3f, 6f, 1.5f, 4.5f)] // Inside adjoining bands.
    [InlineData(2f, 10f, 12f, 4f, 8f)] // Between separated bands, nearest centre.
    public void LayerOf_FiniteBands_ResolvesContainmentOrNearestCenter(float groundMax,
        float upperMin, float upperMax, float groundQueryY, float upperQueryY)
    {
        NavGrid ground = MakeGrid(yMin: 0f, yMax: groundMax);
        NavGrid upper = MakeGrid(yMin: upperMin, yMax: upperMax);
        var space = new NavSpace(new[] { ground, upper });
        Assert.Equal(0, space.LayerOf(groundQueryY));
        Assert.Equal(1, space.LayerOf(upperQueryY));
    }

    [Fact]
    public void LayerOf_EquidistantBetweenBands_TiesToLowestIndex()
    {
        NavGrid ground = MakeGrid(yMin: 0f, yMax: 2f);
        NavGrid upper = MakeGrid(yMin: 10f, yMax: 12f);
        var space = new NavSpace(new[] { ground, upper });

        // ground center = 1, upper center = 11, midpoint = 6, equidistant.
        Assert.Equal(0, space.LayerOf(6f));
    }

    [Fact]
    public void LayerOf_AllBandsInfinite_ContainmentWins()
    {
        NavGrid a = MakeGrid();
        NavGrid b = MakeGrid();
        var space = new NavSpace(new[] { a, b });

        Assert.Equal(0, space.LayerOf(42f));
    }

    [Fact]
    public void LayerOf_NoContainmentAndNoFiniteBand_FallsBackToZero()
    {
        NavGrid layer0 = MakeGrid(yMin: 5f, yMax: float.PositiveInfinity);
        NavGrid layer1 = MakeGrid(yMin: float.NegativeInfinity, yMax: -5f);
        var space = new NavSpace(new[] { layer0, layer1 });

        // Query y=0f is not contained by either layer.
        // Layer 0 has half-infinite band (yMin finite, yMax infinite), so skipped.
        // Layer 1 has half-infinite band (yMin infinite, yMax finite), so skipped.
        // No finite band exists, bestIndex stays -1, fallback returns 0.
        Assert.Equal(0, space.LayerOf(0f));
    }

    [Theory]
    [InlineData(0, 10, 10, 0, 1, 1)] // From cell outside the layer.
    [InlineData(0, 1, 1, 0, 10, 10)] // To cell outside the layer.
    [InlineData(0, 1, 1, 5, 1, 1)] // Unknown destination layer.
    public void Constructor_InvalidLinkEndpoint_Throws(int fromLayer, int fromX, int fromZ,
        int toLayer, int toX, int toZ)
    {
        NavGrid ground = MakeGrid();
        var links = new[] { new NavLink(fromLayer, fromX, fromZ, toLayer, toX, toZ) };
        Assert.Throws<ArgumentException>(() => new NavSpace(new[] { ground }, links));
    }

    [Fact]
    public void Constructor_NoLayers_Throws()
    {
        Assert.Throws<ArgumentException>(() => new NavSpace(Array.Empty<NavGrid>()));
    }

    [Fact]
    public void Constructor_NullLinks_DefaultsToEmpty()
    {
        NavGrid ground = MakeGrid();
        var space = new NavSpace(new[] { ground });
        Assert.Empty(space.Links);
    }

    [Fact]
    public void Constructor_ValidLink_IsStoredInOrder()
    {
        NavGrid ground = MakeGrid(yMin: 0f, yMax: 3f);
        NavGrid upper = MakeGrid(yMin: 3f, yMax: 6f);
        var link = new NavLink(0, 1, 1, 1, 2, 2);
        var space = new NavSpace(new[] { ground, upper }, new[] { link });

        Assert.Single(space.Links);
        Assert.Equal(link, space.Links[0]);
    }

    [Fact]
    public void Layers_ExposesConstructorLayersInOrder()
    {
        NavGrid a = MakeGrid();
        NavGrid b = MakeGrid();
        var space = new NavSpace(new[] { a, b });

        Assert.Equal(2, space.Layers.Count);
        Assert.Same(a, space.Layers[0]);
        Assert.Same(b, space.Layers[1]);
    }

    [Fact]
    public void NavLink_DefaultKind_IsStair()
    {
        Assert.Equal(NavLinkKind.Stair, new NavLink(0, 1, 1, 0, 2, 2).Kind);
    }

    [Fact]
    public void NavLink_HopKind_RoundTrips()
    {
        var link = new NavLink(0, 1, 1, 0, 2, 2) { Kind = NavLinkKind.Hop };
        Assert.Equal(NavLinkKind.Hop, link.Kind);
    }

    [Fact]
    public void NavLink_EqualityIncludesKind()
    {
        var stairLink = new NavLink(0, 1, 1, 0, 2, 2);
        var hopLink = new NavLink(0, 1, 1, 0, 2, 2) { Kind = NavLinkKind.Hop };
        Assert.NotEqual(stairLink, hopLink);
    }
}
