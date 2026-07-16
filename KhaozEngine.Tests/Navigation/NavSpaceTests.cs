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

    [Fact]
    public void LayerOf_TwoStackedFiniteBands_ResolvesByContainment()
    {
        NavGrid ground = MakeGrid(yMin: 0f, yMax: 3f);
        NavGrid upper = MakeGrid(yMin: 3f, yMax: 6f);
        var space = new NavSpace(new[] { ground, upper });

        Assert.Equal(0, space.LayerOf(1.5f));
        Assert.Equal(1, space.LayerOf(4.5f));
    }

    [Fact]
    public void LayerOf_BetweenBands_ResolvesToNearestBandCenter()
    {
        NavGrid ground = MakeGrid(yMin: 0f, yMax: 2f);
        NavGrid upper = MakeGrid(yMin: 10f, yMax: 12f);
        var space = new NavSpace(new[] { ground, upper });

        // ground center = 1, upper center = 11. y = 4 is closer to ground center.
        Assert.Equal(0, space.LayerOf(4f));
        // y = 8 is closer to upper center.
        Assert.Equal(1, space.LayerOf(8f));
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
    public void LayerOf_AllBandsInfinite_ReturnsZero()
    {
        NavGrid a = MakeGrid();
        NavGrid b = MakeGrid();
        var space = new NavSpace(new[] { a, b });

        Assert.Equal(0, space.LayerOf(42f));
    }

    [Fact]
    public void Constructor_LinkEndpointOutOfBoundsInFromLayer_Throws()
    {
        NavGrid ground = MakeGrid();
        var links = new[] { new NavLink(0, 10, 10, 0, 1, 1) };
        Assert.Throws<ArgumentException>(() => new NavSpace(new[] { ground }, links));
    }

    [Fact]
    public void Constructor_LinkEndpointOutOfBoundsInToLayer_Throws()
    {
        NavGrid ground = MakeGrid();
        var links = new[] { new NavLink(0, 1, 1, 0, 10, 10) };
        Assert.Throws<ArgumentException>(() => new NavSpace(new[] { ground }, links));
    }

    [Fact]
    public void Constructor_LinkReferencesUnknownLayer_Throws()
    {
        NavGrid ground = MakeGrid();
        var links = new[] { new NavLink(0, 1, 1, 5, 1, 1) };
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
}
