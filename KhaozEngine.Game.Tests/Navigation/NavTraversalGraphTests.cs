using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavTraversalGraphTests
{
    static NavSpace OpenSpace(int width = 3, int height = 3)
        => NavSpace.Single(NavGrid.FromWalkable(width, height, 1f, 0f, 0f, (_, _) => true));

    [Theory]
    [InlineData(0, 2, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 1, 0)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 2, 0)]
    [InlineData(6, 0, 2)]
    [InlineData(7, 0, 0)]
    public void ExitBitsAuthorizeOnlyTheirDirectedNeighbor(int bit, int targetX, int targetZ)
    {
        var nodes = new bool[9];
        nodes[4] = true;
        nodes[targetZ * 3 + targetX] = true;
        var exits = new byte[9];
        exits[4] = (byte)(1 << bit);
        var graph = new NavTraversalGraph(OpenSpace(), 0.3f, 1.8f,
            new[] { new NavTraversalLayer(3, 3, nodes, exits) }, Array.Empty<NavLink>());

        Assert.True(graph.CanTraverse(0, 1, 1, 0, targetX, targetZ));
        Assert.False(graph.CanTraverse(0, targetX, targetZ, 0, 1, 1));
        Assert.False(graph.CanTraverse(0, 1, 1, 0, 1, 1));
    }

    [Fact]
    public void PassableNodesDoNotAuthorizeARefusedEdge()
    {
        var graph = new NavTraversalGraph(OpenSpace(2, 1), 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { true, true }, new byte[2]) },
            Array.Empty<NavLink>());

        Assert.True(graph.IsNodePassable(0, 0, 0));
        Assert.True(graph.IsNodePassable(0, 1, 0));
        Assert.False(graph.CanTraverse(0, 0, 0, 0, 1, 0));
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 3, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 3)]
    public void QueriesOutsideTheGraphReturnFalse(int layer, int x, int z)
    {
        var graph = new NavTraversalGraph(OpenSpace(), 0f, 1f,
            new[] { new NavTraversalLayer(3, 3, new bool[9], new byte[9]) }, Array.Empty<NavLink>());

        Assert.False(graph.IsNodePassable(layer, x, z));
        Assert.False(graph.CanTraverse(layer, x, z, 0, 1, 1));
        Assert.False(graph.CanTraverse(0, 1, 1, layer, x, z));
    }

    [Fact]
    public void LayerOwnsItsNodeAndExitArrays()
    {
        bool[] nodes = { true, true };
        byte[] exits = { 1, 0 };
        var layer = new NavTraversalLayer(2, 1, nodes, exits);
        nodes[0] = false;
        exits[0] = 0;

        Assert.True(layer.IsAccepted(0, 0));
        Assert.Equal(1, layer.ExitMask(0, 0));
        Assert.False(layer.IsAccepted(-1, 0));
        Assert.False(layer.IsAccepted(2, 0));
        Assert.Equal(0, layer.ExitMask(0, -1));
        Assert.Equal(0, layer.ExitMask(0, 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 2)]
    public void LayerRejectsInvalidOrOverflowingDimensions(int width, int height)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NavTraversalLayer(width, height, Array.Empty<bool>(), Array.Empty<byte>()));

    [Fact]
    public void LayerRejectsMismatchedStorage()
    {
        Assert.Throws<ArgumentException>(() => new NavTraversalLayer(2, 1, new bool[1], new byte[2]));
        Assert.Throws<ArgumentException>(() => new NavTraversalLayer(2, 1, new bool[2], new byte[1]));
    }

    [Theory]
    [InlineData(-1f, 1f)]
    [InlineData(float.NaN, 1f)]
    [InlineData(float.PositiveInfinity, 1f)]
    [InlineData(0f, 0f)]
    [InlineData(0f, -1f)]
    [InlineData(0f, float.NaN)]
    [InlineData(0f, float.PositiveInfinity)]
    public void GraphRejectsInvalidProfile(float radius, float height)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new NavTraversalGraph(OpenSpace(1, 1),
            radius, height, new[] { new NavTraversalLayer(1, 1, new[] { true }, new byte[1]) },
            Array.Empty<NavLink>()));

    [Fact]
    public void GraphRejectsMissingOrMismatchedLayers()
    {
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(OpenSpace(), 0f, 1f,
            Array.Empty<NavTraversalLayer>(), Array.Empty<NavLink>()));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(OpenSpace(), 0f, 1f,
            new[] { new NavTraversalLayer(1, 1, new[] { true }, new byte[1]) }, Array.Empty<NavLink>()));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(OpenSpace(), 0f, 1f,
            new NavTraversalLayer[] { null! }, Array.Empty<NavLink>()));
    }

    [Theory]
    [InlineData(true, true, 0, 1)]
    [InlineData(false, true, 1, 0)]
    [InlineData(true, false, 1, 0)]
    public void GraphRejectsExitsOutsideBoundsOrRejectedNodes(bool first, bool second, byte firstExit, byte secondExit)
        => Assert.Throws<ArgumentException>(() => new NavTraversalGraph(OpenSpace(2, 1), 0f, 1f,
            new[] { new NavTraversalLayer(2, 1, new[] { first, second }, new[] { firstExit, secondExit }) },
            Array.Empty<NavLink>()));

    [Fact]
    public void GraphOwnsEveryContainerAndOnlyAdmitsAcceptedLinks()
    {
        NavGrid lower = NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true, 0f, 1f);
        NavGrid upper = NavGrid.FromWalkable(1, 1, 1f, 0f, 0f, (_, _) => true, 2f, 3f);
        var sourceLayers = new List<NavGrid> { lower, upper };
        var up = new NavLink(0, 0, 0, 1, 0, 0);
        var down = new NavLink(1, 0, 0, 0, 0, 0);
        var sourceLinks = new List<NavLink> { up, down };
        var source = new NavSpace(sourceLayers, sourceLinks);
        var acceptedLayers = new List<NavTraversalLayer>
        {
            new(1, 1, new[] { true }, new byte[1]),
            new(1, 1, new[] { true }, new byte[1]),
        };
        var acceptedLinks = new List<NavLink> { up };
        var graph = new NavTraversalGraph(source, 0.4f, 1.7f, acceptedLayers, acceptedLinks);
        sourceLayers.Clear();
        sourceLinks.Clear();
        acceptedLayers.Clear();
        acceptedLinks.Clear();

        Assert.Equal(0.4f, graph.AgentRadius);
        Assert.Equal(1.7f, graph.AgentHeight);
        Assert.Equal(new[] { lower, upper }, graph.Space.Layers);
        Assert.Equal(new[] { up, down }, graph.Space.Links);
        Assert.Equal(new[] { up }, graph.Links);
        Assert.Equal(2, graph.Layers.Count);
        Assert.True(graph.CanTraverse(0, 0, 0, 1, 0, 0));
        Assert.False(graph.CanTraverse(1, 0, 0, 0, 0, 0));
        Assert.Throws<NotSupportedException>(() => ((IList<NavGrid>)graph.Space.Layers)[0] = upper);
        Assert.Throws<NotSupportedException>(() => ((IList<NavLink>)graph.Space.Links).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NavTraversalLayer>)graph.Layers).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<NavLink>)graph.Links).Clear());
    }

    [Fact]
    public void GraphRejectsLinksWithoutAcceptedEndpointsOrSourceMembership()
    {
        NavGrid grid = OpenSpace(2, 1).Layers[0];
        var link = new NavLink(0, 0, 0, 0, 1, 0);
        var space = new NavSpace(new[] { grid }, new[] { link });
        var accepted = new NavTraversalLayer(2, 1, new[] { true, true }, new byte[2]);
        var rejectedTarget = new NavTraversalLayer(2, 1, new[] { true, false }, new byte[2]);
        var rejectedSource = new NavTraversalLayer(2, 1, new[] { false, true }, new byte[2]);

        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(space, 0f, 1f,
            new[] { rejectedTarget }, new[] { link }));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(space, 0f, 1f,
            new[] { rejectedSource }, new[] { link }));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(space, 0f, 1f,
            new[] { accepted }, new[] { new NavLink(0, 0, 0, 1, 0, 0) }));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(space, 0f, 1f,
            new[] { accepted }, new[] { new NavLink(0, 0, 0, 0, 2, 0) }));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(NavSpace.Single(grid), 0f, 1f,
            new[] { accepted }, new[] { link }));
    }

    [Fact]
    public void GraphRejectsNullInputsAndInvalidSourceLayers()
    {
        NavSpace space = OpenSpace(1, 1);
        var layers = new[] { new NavTraversalLayer(1, 1, new[] { true }, new byte[1]) };
        Assert.Throws<ArgumentNullException>(() => new NavTraversalGraph(null!, 0f, 1f, layers, Array.Empty<NavLink>()));
        Assert.Throws<ArgumentNullException>(() => new NavTraversalGraph(space, 0f, 1f, null!, Array.Empty<NavLink>()));
        Assert.Throws<ArgumentNullException>(() => new NavTraversalGraph(space, 0f, 1f, layers, null!));
        Assert.Throws<ArgumentException>(() => new NavTraversalGraph(
            new NavSpace(new NavGrid[] { null! }), 0f, 1f, layers, Array.Empty<NavLink>()));
    }
}
