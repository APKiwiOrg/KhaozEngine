using System;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tests.Movement.Research;

internal sealed record GraphLayerDimensions(int Layer, int Width, int Height);

internal sealed record GraphAcceptance(GraphCounts Counts, GraphLayerDimensions[] LayerDimensions)
{
    public ObstacleProbe? ObstacleProbe { get; init; }
    public bool DimensionsMatch => LayerDimensions is not null && Counts.Layers > 0 &&
        LayerDimensions.Length == Counts.Layers && LayerDimensions.Select(layer => layer?.Layer)
            .SequenceEqual(Enumerable.Range(0, Counts.Layers).Select(layer => (int?)layer)) &&
        LayerDimensions.All(layer => layer.Width == ProofFixture.Width && layer.Height == ProofFixture.Height);
    public bool EnoughAcceptedNodes => Counts.AcceptedNodes >= 10000;
    public bool EnoughDirectedEdges => Counts.DirectedEdges >= 50000;
    public bool HasRejectedLayerCells => Counts.RejectedLayerCells != 0;
    public bool ClearEdgeAccepted => Counts.ClearEdgeAccepted;
    public bool CentralObstacleRejected => Counts.CentralObstacleRejected && ObstacleProbe?.Passed == true &&
        ObstacleProbe.Layers == Counts.Layers && ObstacleProbe.Links is { } links &&
        links.Length == Counts.CandidateLinks && links.Count(link => link.Accepted) == Counts.AcceptedLinks;
    public bool Passed => DimensionsMatch && EnoughAcceptedNodes && EnoughDirectedEdges &&
        HasRejectedLayerCells && ClearEdgeAccepted && CentralObstacleRejected;

    internal void RequireAccepted()
    {
        if (!Passed)
            throw new InvalidOperationException("Public fixture graph acceptance failed. " +
                JsonSerializer.Serialize(this, ProofJson.Options));
    }
}
