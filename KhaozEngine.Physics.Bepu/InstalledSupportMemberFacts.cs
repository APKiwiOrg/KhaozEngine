using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Face-only arithmetic retained by an installed world face. The expressions and their order match
/// the uncached consumers. No axis, radius, slope threshold, membership or support decision is retained.</summary>
internal sealed class InstalledSupportMemberFacts
{
    readonly FeatureNumber[] _projectedEdgeLengthsSquared;

    internal ReadOnlySpan<FeatureNumber> ProjectedEdgeLengthsSquared => _projectedEdgeLengthsSquared;
    internal GeometryVector NormalBounds { get; }
    internal GeometryVectorOutput PublishedNormal { get; }
    internal GeometryVectorOutput PublishedPlane { get; }
    internal FeatureNumber ProposalNormalLength { get; }

    internal InstalledSupportMemberFacts(SupportPolygon polygon)
    {
        _projectedEdgeLengthsSquared = new FeatureNumber[polygon.Edges.Length];
        for (int i = 0; i < _projectedEdgeLengthsSquared.Length; i++)
        {
            FeaturePoint edge = polygon.Edges[i];
            _projectedEdgeLengthsSquared[i] = edge.X.Multiply(edge.X, exactZeroProducts: true)
                .Add(edge.Z.Multiply(edge.Z, exactZeroProducts: true));
        }
        NormalBounds = GeometryVectorOperations.Normalize(polygon.Normal.Bounds);
        PublishedNormal = GeometryVectorOperations.Publish(NormalBounds);
        PublishedPlane = GeometryVectorOperations.Publish(polygon.Vertices[0].Bounds);
        ProposalNormalLength = SupportFootprintProposalGeometry.Root(FeaturePoint.Dot(polygon.Normal, polygon.Normal));
    }
}
