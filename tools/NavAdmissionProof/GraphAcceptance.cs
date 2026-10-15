using System;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed record GraphLayerDimensions(int Layer, int Width, int Height);

internal sealed record GraphAcceptance(GraphCounts Counts, GraphLayerDimensions[] LayerDimensions,
    GroundObstacleEvidence? GroundObstacle = null)
{
    public bool DimensionsMatch => LayerDimensions.All(layer =>
        layer.Width == ProofFixture.Width && layer.Height == ProofFixture.Height);
    public bool EnoughAcceptedNodes => Counts.AcceptedNodes >= 10000;
    public bool EnoughDirectedEdges => Counts.DirectedEdges >= 50000;
    public bool HasRejectedLayerCells => Counts.RejectedLayerCells != 0;
    public bool ClearEdgeAccepted => Counts.ClearEdgeAccepted;
    public bool ClearGroundApproaches => Counts.ClearGroundApproaches;
    public bool AdjacentGroundObstructions => Counts.AdjacentGroundObstructions;
    public bool GroundEntriesRefused => Counts.GroundEntriesRefused;
    public bool Passed => DimensionsMatch && EnoughAcceptedNodes && EnoughDirectedEdges &&
        HasRejectedLayerCells && ClearEdgeAccepted && ClearGroundApproaches &&
        AdjacentGroundObstructions && GroundEntriesRefused;

    internal void RequireAccepted()
    {
        if (!Passed)
            throw new InvalidOperationException("Public fixture graph acceptance failed. " +
                JsonSerializer.Serialize(this, ProofJson.Options));
    }
}
