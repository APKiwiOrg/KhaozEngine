using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact disc-to-polygon contact outside a horizontal face's interior. Unproved domains and ambiguous
/// kernel enclosures keep the complete finite-stratum evaluator. No contact threshold is widened.</summary>
internal static class SupportPlanarNeighbour
{
    internal static GeometrySign Contact(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction,
        double radius, double band)
    {
        if (SupportReferenceEvaluation.Active || band != SupportJoinIncidence.ContactBand ||
            !polygon.HasJoinIncidenceCertificate || polygon.Normal.Y.Sign != GeometrySign.Positive ||
            !start.IsExact || !direction.IsExact || direction.X.Value != 0 || direction.Z.Value != 0 ||
            direction.Y.Value < 1d / 65536 || radius < 0.01f ||
            !BoundedGeometryArithmetic.DiscCoordinate(direction.Y.Value)) return GeometrySign.Unresolved;
        FeaturePoint end = FeaturePoint.Add(start, direction);
        double height = polygon.Vertices[0].Y.Value;
        if (!end.IsExact || !BoundedGeometryArithmetic.DiscCoordinate(end.Y.Value) || start.Y.Value < height)
            return GeometrySign.Unresolved;
        bool outside = false;
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint a = polygon.Vertices[i], b = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            GeometrySign side = BoundedGeometryArithmetic.Orient2D(a.X.Value, a.Z.Value, b.X.Value, b.Z.Value,
                start.X.Value, start.Z.Value);
            if (side == GeometrySign.Unresolved) return side;
            outside |= side == GeometrySign.Positive;
        }
        if (!outside) return GeometrySign.Unresolved;
        bool contact = false;
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint a = polygon.Vertices[i], b = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            GeometrySign distance = BoundedGeometryArithmetic.CompareDiscEdge(start.X.Value, start.Z.Value,
                start.Y.Value, height, a.X.Value, a.Z.Value, b.X.Value, b.Z.Value, radius, band);
            if (distance == GeometrySign.Unresolved) return distance;
            contact |= distance is GeometrySign.Negative or GeometrySign.Zero;
        }
        // The same bounded horizontal domain as the interior planar certificate makes every exhaustive stratum
        // finite. An attained point inside the disc forces its least lower bound inside the inclusive band.
        if (contact) return GeometrySign.Positive;
        return NegativeEnclosures(polygon, start, end, radius, band)
            ? GeometrySign.Negative : GeometrySign.Unresolved;
    }

    // Exact exclusion alone cannot replace the kernel's interval verdict at tangency. Check the enclosures it
    // actually uses for its endpoint strata. The horizontal edges and strictly positive plane gap put every
    // interior segment/edge parameter and face crossing strictly before the segment. Face projections must be
    // excluded by the kernel's own inside predicate. Thus no omitted or uncertain candidate can lower its minimum.
    static bool NegativeEnclosures(SupportPolygon polygon, FeaturePoint start, FeaturePoint end,
        double radius, double band)
    {
        if (start.Y.Value <= polygon.Vertices[0].Y.Value || !Horizontal(polygon.Edges)) return false;
        FeatureNumber normalSquared = FeaturePoint.Dot(polygon.Normal, polygon.Normal);
        if (normalSquared.Sign != GeometrySign.Positive || !ExcludedProjection(polygon, start, normalSquared) ||
            !ExcludedProjection(polygon, end, normalSquared)) return false;
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint a = polygon.Vertices[i], edge = polygon.Edges[i];
            if (!Separated(start, a, radius, band)) return false;
            if (!EdgeEndpointSeparated(a, edge, start, radius, band) ||
                !EdgeEndpointSeparated(a, edge, end, radius, band)) return false;
        }
        return true;
    }

    static bool EdgeEndpointSeparated(FeaturePoint origin, FeaturePoint edge, FeaturePoint axis,
        double radius, double band)
    {
        FeatureNumber length = FeaturePoint.Dot(edge, edge);
        FeatureNumber u = FeaturePoint.Dot(FeaturePoint.Subtract(axis, origin), edge).Divide(length);
        GeometrySign low = u.Sign, high = u.Compare(FeatureNumber.Exact(1));
        if (low == GeometrySign.Unresolved || high == GeometrySign.Unresolved) return false;
        if (low != GeometrySign.Positive || high != GeometrySign.Negative) return true;
        FeaturePoint point = FeaturePoint.Add(origin, FeaturePoint.Scale(edge, u));
        return Separated(axis, point, radius, band);
    }

    static bool ExcludedProjection(SupportPolygon polygon, FeaturePoint axis, FeatureNumber squared)
    {
        FeatureNumber scale = FeaturePoint.Dot(FeaturePoint.Subtract(axis, polygon.Vertices[0]), polygon.Normal)
            .Divide(squared);
        FeaturePoint projected = FeaturePoint.Subtract(axis, FeaturePoint.Scale(polygon.Normal, scale));
        return projected.IsResolved && polygon.Inside(projected) == GeometrySign.Negative;
    }

    static bool Separated(FeaturePoint a, FeaturePoint b, double radius, double band)
    {
        GeometryInterval distance = SupportGeometry.Length(FeaturePoint.Subtract(a, b).Bounds);
        GeometryInterval separation = distance.Subtract(GeometryInterval.Exact(radius));
        return separation.IsResolved && separation.Lower > band;
    }

    static bool Horizontal(ReadOnlySpan<FeaturePoint> edges)
    {
        foreach (FeaturePoint edge in edges)
            if (!edge.IsExact || edge.Y.Value != 0) return false;
        return true;
    }
}
