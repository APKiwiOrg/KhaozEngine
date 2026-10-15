using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Movement.Research;

public sealed class ProofObstacleAcceptanceTests
{
    [Fact]
    public void IsolatedRoofSurfaceDoesNotCountAsGroundInsideTheBox()
    {
        NavTraversalGraph graph = Graph();
        Assert.True(graph.IsNodePassable(0, 5, 5));
        Assert.Equal(2f, graph.Space.Layers[0].SurfaceHeightAt(5, 5));
        Assert.True(ProofGraphInspection.Inspect(graph).CentralObstacleRejected);
    }

    [Fact]
    public void AcceptedFloorBeneathRoofIsRejectedAcrossLayers()
    {
        NavTraversalGraph graph = Graph(extraFloor: true);
        Assert.True(graph.IsNodePassable(1, 5, 5));
        Assert.Equal(0f, graph.Space.Layers[1].SurfaceHeightAt(5, 5));
        Assert.False(ProofGraphInspection.Inspect(graph).CentralObstacleRejected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RoofNeighborConnectionKeepsItsDirection(bool reverse, bool accepted)
    {
        NavTraversalGraph graph = Graph(connection: reverse ? "exit-back" : "exit");
        Assert.True(reverse ? graph.CanTraverse(0, 3, 4, 0, 2, 4) : graph.CanTraverse(0, 2, 4, 0, 3, 4));
        Assert.Equal(accepted, ProofGraphInspection.Inspect(graph).CentralObstacleRejected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void RoofLinkAcrossLayersKeepsItsDirection(bool reverse, bool accepted)
    {
        NavTraversalGraph graph = Graph(connection: reverse ? "link-back" : "link");
        Assert.Single(graph.Links);
        GraphAcceptance validation = ProofGraphInspection.Inspect(graph);
        GraphProbeTraversal edge = Assert.Single(validation.ObstacleProbe!.Links!);
        Assert.Equal(-1.125f, (reverse ? edge.To : edge.From).WorldX);
        Assert.Equal(accepted, validation.CentralObstacleRejected);
    }

    [Fact]
    public void GroundLinkThroughTheBoxIsRejectedEvenWithEndpointsOutsideIt()
    {
        NavTraversalGraph graph = Graph(connection: "through");
        Assert.True(graph.CanTraverse(0, 1, 4, 0, 8, 4));
        Assert.False(ProofGraphInspection.Inspect(graph).CentralObstacleRejected);
    }

    [Fact]
    public void RoofTraversalRemainsAccepted()
    {
        NavTraversalGraph graph = Graph(connection: "roof");
        Assert.True(graph.CanTraverse(0, 4, 4, 0, 5, 4));
        Assert.True(ProofGraphInspection.Inspect(graph).CentralObstacleRejected);
    }

    [Fact]
    public void ProbeObservesClearGroundOutsideTheCapsuleRadius()
    {
        ObstacleProbe probe = ProofGraphInspection.Inspect(Graph()).ObstacleProbe!;
        GraphProbeNode ground = Assert.Single(probe.Nodes!, node => node.WorldX == -0.875f && node.WorldZ == 0.125f);
        Assert.True(ground.Passable);
        Assert.Equal(0f, ground.RestingHeight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProbeRetainsIncomingAndOutgoingNeighborEdgesAcrossItsBoundary(bool reverse)
    {
        ObstacleProbe probe = ProofGraphInspection.Inspect(Graph()).ObstacleProbe!;
        GraphProbeTraversal edge = Assert.Single(probe.Neighbors!, edge => edge.Accepted &&
            edge.From.WorldX == (reverse ? -0.875f : -1.125f) && edge.From.WorldZ == 0.125f &&
            edge.To.WorldX == (reverse ? -1.125f : -0.875f) && edge.To.WorldZ == 0.125f);
        Assert.Equal(0f, edge.From.RestingHeight);
        Assert.Equal(0f, edge.To.RestingHeight);
    }

    [Fact]
    public void RefusedGroundBesideTheBoxCannotSatisfyAcceptance()
    {
        Assert.False(ProofGraphInspection.Inspect(Graph(connection: "no-clear-ground")).CentralObstacleRejected);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("empty")]
    public void MissingObstacleProbeCannotPassTheEnvelopeGate(string corruption)
    {
        GraphAcceptance validation = AcceptedEnvelope();
        JsonObject json = JsonSerializer.SerializeToNode(validation, ProofJson.Options)!.AsObject();
        if (corruption == "missing") json.Remove("ObstacleProbe");
        else json["ObstacleProbe"] = corruption == "null" ? null : new JsonObject();
        GraphAcceptance retained = json.Deserialize<GraphAcceptance>(ProofJson.Options)!;
        Assert.False(retained.Passed);
        Assert.Throws<InvalidOperationException>(retained.RequireAccepted);
    }

    [Theory]
    [InlineData("Nodes")]
    [InlineData("Neighbors")]
    [InlineData("Links")]
    [InlineData("SurfaceHeight")]
    [InlineData("RestingHeight")]
    [InlineData("duplicate-node")]
    [InlineData("missing-edge")]
    public void MalformedProbeCannotPassTheEnvelopeGate(string corruption)
    {
        JsonObject json = JsonSerializer.SerializeToNode(AcceptedEnvelope(), ProofJson.Options)!.AsObject();
        JsonObject probe = json["ObstacleProbe"]!.AsObject();
        if (corruption is "Nodes" or "Neighbors" or "Links") probe[corruption] = null;
        else if (corruption == "duplicate-node") probe["Nodes"]![1] = probe["Nodes"]![0]!.DeepClone();
        else if (corruption == "missing-edge") probe["Neighbors"]!.AsArray().RemoveAt(0);
        else probe["Nodes"]![14]![corruption] = null;
        GraphAcceptance retained = json.Deserialize<GraphAcceptance>(ProofJson.Options)!;
        Assert.False(retained.Passed);
        Assert.Throws<InvalidOperationException>(retained.RequireAccepted);
    }

    [Fact]
    public void LinkEvidenceCannotBeOmittedFromAnOtherwiseValidProbe()
    {
        GraphAcceptance validation = ProofGraphInspection.Inspect(Graph(connection: "link-back"));
        Assert.True(validation.CentralObstacleRejected);
        Assert.False((validation with { ObstacleProbe = validation.ObstacleProbe! with { Links = [] } })
            .CentralObstacleRejected);
    }

    [Fact]
    public void RejectedAscentRetainsTheActualHeightsAndDirection()
    {
        ObstacleProbe probe = ProofGraphInspection.Inspect(Graph(connection: "exit")).ObstacleProbe!;
        Assert.True(probe.DataComplete);
        Assert.True(probe.GroundNodesRejected);
        Assert.False(probe.IllegalTraversalRejected);
        GraphProbeTraversal edge = Assert.Single(probe.Neighbors!, edge => edge.Accepted && edge.To.InsideBox);
        Assert.Equal(0f, edge.From.SurfaceHeight);
        Assert.Equal(2f, edge.To.RestingHeight);
        Assert.True(edge.From.Passable && edge.To.Passable);
        Assert.False(edge.From.InsideBox);
        Assert.True(edge.To.InsideBox);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-layer")]
    [InlineData("duplicate-layer")]
    [InlineData("wrong-size")]
    public void IncompleteOrIncorrectDimensionsCannotPass(string corruption)
    {
        GraphAcceptance validation = AcceptedEnvelope();
        GraphLayerDimensions[] dimensions = corruption switch
        {
            "empty" => [],
            "missing-layer" => [new(1, 164, 204)],
            "duplicate-layer" => [new(0, 164, 204), new(0, 164, 204)],
            _ => [new(0, 163, 204)],
        };
        Assert.False((validation with { LayerDimensions = dimensions }).DimensionsMatch);
    }

    internal static GraphAcceptance AcceptedEnvelope() => ProofGraphInspection.Inspect(Graph()) with
    {
        Counts = new GraphCounts(1, 10000, 1, 50000, 0, 0, true, true),
        LayerDimensions = [new(0, 164, 204)],
    };

    private static NavTraversalGraph Graph(bool extraFloor = false, string connection = "")
    {
        const int width = 10, height = 10;
        int layers = extraFloor || connection.StartsWith("link", StringComparison.Ordinal) ? 2 : 1;
        var grids = new NavGrid[layers];
        var decisions = new NavTraversalLayer[layers];
        for (int layer = 0; layer < layers; layer++)
        {
            var heights = new float[width * height];
            var nodes = new bool[width * height];
            var exits = new byte[width * height];
            for (int z = 0; z < height; z++)
                for (int x = 0; x < width; x++)
                {
                    bool inside = x is >= 3 and <= 6 && z is >= 3 and <= 6;
                    heights[z * width + x] = inside && layer == 0 ? 2 : 0;
                    nodes[z * width + x] = layer == 0 || (extraFloor ? inside : !inside);
                }
            if (layer == 0)
            {
                if (connection != "no-clear-ground")
                {
                    exits[5 * width] = 1;
                    exits[5 * width + 1] = 2;
                    exits[5 * width + 8] = 1;
                    exits[5 * width + 9] = 2;
                    exits[5] = 4;
                    exits[width + 5] = 8;
                    exits[8 * width + 5] = 4;
                    exits[9 * width + 5] = 8;
                }
                if (connection == "exit") exits[4 * width + 2] = 1;
                if (connection == "exit-back") exits[4 * width + 3] = 2;
                if (connection == "roof") exits[4 * width + 4] = 1;
            }
            grids[layer] = NavGrid.FromBlockedSurfaces(width, height, 0.25f, -1.25f, -1.25f,
                nodes.Select(node => !node).ToArray(), heights);
            decisions[layer] = new(width, height, nodes, exits);
        }
        NavLink[] links = connection switch
        {
            "link" => [new(1, 0, 4, 0, 4, 4)],
            "link-back" => [new(0, 4, 4, 1, 0, 4)],
            "through" => [new(0, 1, 4, 0, 8, 4)],
            _ => [],
        };
        return new NavTraversalGraph(new NavSpace(grids, links), 0.2f, 1.5f, decisions, links);
    }
}
