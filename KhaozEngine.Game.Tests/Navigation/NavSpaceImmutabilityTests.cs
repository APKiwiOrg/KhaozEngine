using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavSpaceImmutabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_LayerInputMutationPreservesValidatedEndpoints(bool useList)
    {
        NavGrid ground = MakeGrid(0f, 3f);
        NavGrid upper = MakeGrid(3f, 6f);
        IList<NavGrid> layers = useList ? new List<NavGrid> { ground, upper } : new[] { ground, upper };
        var link = new NavLink(0, 3, 3, 1, 3, 3);
        var space = new NavSpace((IReadOnlyList<NavGrid>)layers, new[] { link });

        NavGrid smaller = NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true);
        layers[1] = smaller;
        if (useList)
        {
            layers.Clear();
            layers.Add(smaller);
        }

        Assert.Equal(2, space.Layers.Count);
        Assert.Same(ground, space.Layers[0]);
        Assert.Same(upper, space.Layers[1]);
        Assert.Equal(1, space.LayerOf(4.5f));
        Assert.True(space.Layers[link.FromLayer].InBounds(link.FromX, link.FromZ));
        Assert.True(space.Layers[link.ToLayer].InBounds(link.ToX, link.ToZ));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_LinkInputMutationPreservesValidatedTopology(bool useList)
    {
        NavGrid ground = MakeGrid(0f, 3f);
        NavGrid upper = MakeGrid(3f, 6f);
        var stair = new NavLink(0, 1, 1, 1, 2, 2);
        var hop = new NavLink(1, 2, 2, 0, 1, 1) { Kind = NavLinkKind.Hop };
        IList<NavLink> links = useList ? new List<NavLink> { stair, hop } : new[] { stair, hop };
        var space = new NavSpace(new[] { ground, upper }, (IReadOnlyList<NavLink>)links);

        var invalid = new NavLink(9, 99, 99, 9, 99, 99);
        links[0] = invalid;
        if (useList)
        {
            links.Clear();
            links.Add(invalid);
        }

        Assert.Equal(new[] { stair, hop }, space.Links);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicViews_RejectContainerMutation(bool useList)
    {
        NavGrid grid = MakeGrid(0f, 3f);
        var link = new NavLink(0, 1, 1, 0, 2, 2);
        IReadOnlyList<NavGrid> layers = useList ? new List<NavGrid> { grid } : new[] { grid };
        IReadOnlyList<NavLink> links = useList ? new List<NavLink> { link } : new[] { link };
        var space = new NavSpace(layers, links);

        AssertReadOnly(space.Layers, grid);
        AssertReadOnly(space.Links, link);
        Assert.Same(grid, Assert.Single(space.Layers));
        Assert.Equal(link, Assert.Single(space.Links));
    }

    [Fact]
    public void Single_PreservesGridIdentityAndReadOnlyViews()
    {
        NavGrid grid = MakeGrid(0f, 3f);
        NavSpace space = NavSpace.Single(grid);

        AssertReadOnly(space.Layers, grid);
        AssertReadOnly(space.Links, new NavLink(0, 1, 1, 0, 2, 2));
        Assert.Same(grid, Assert.Single(space.Layers));
        Assert.Empty(space.Links);
    }

    static NavGrid MakeGrid(float yMin, float yMax)
        => NavGrid.FromWalkable(4, 4, 1f, 0f, 0f, (_, _) => true, yMin, yMax);

    static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.False(values is T[]);
        Assert.False(values is List<T>);
        var list = Assert.IsAssignableFrom<IList<T>>(values);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(replacement));
        Assert.Throws<NotSupportedException>(() => list.Clear());
        if (list.Count > 0)
            Assert.Throws<NotSupportedException>(() => list[0] = replacement);
    }
}
