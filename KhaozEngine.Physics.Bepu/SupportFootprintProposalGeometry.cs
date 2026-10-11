using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Installed-geometry bounds on the lowest point of a downward upright probe at first contact.
/// The lower sphere contacts at max(q.Y + sqrt(r*r - horizontalDistanceSquared) - r). Convex polygon
/// strata are its vertices, clipped edges and plane interior. Sweep distance only selects the neighborhood.</summary>
internal static class SupportFootprintProposalGeometry
{
    static readonly FeatureNumber Zero = FeatureNumber.Exact(0), One = FeatureNumber.Exact(1);

    internal static GeometrySign Lowest(SupportPolygon polygon, in SupportFootprintProposal proposal,
        out GeometryInterval lowest)
    {
        lowest = default;
        GeometrySign upward = polygon.Normal.Y.Sign;
        if (upward is GeometrySign.Negative or GeometrySign.Zero) return GeometrySign.Negative;
        if (upward != GeometrySign.Positive) return GeometrySign.Unresolved;
        FeatureNumber radius = FeatureNumber.Exact(proposal.Probe.Radius);
        FeaturePoint axis = new(FeatureNumber.Exact(proposal.Start.Position.X), Zero,
            FeatureNumber.Exact(proposal.Start.Position.Z));
        var range = new Maximum();
        foreach (FeaturePoint vertex in polygon.Vertices)
            if (!range.Add(vertex, axis, radius, true)) return GeometrySign.Unresolved;
        for (int i = 0; i < polygon.Edges.Length; i++)
            if (!Edge(polygon.Vertices[i], polygon.Edges[i], axis, radius, ref range)) return GeometrySign.Unresolved;

        FeatureNumber length = Root(FeaturePoint.Dot(polygon.Normal, polygon.Normal));
        FeatureNumber scale = radius.Divide(length);
        FeatureNumber x = axis.X.Subtract(polygon.Normal.X.Multiply(scale, exactZeroProducts: true));
        FeatureNumber z = axis.Z.Subtract(polygon.Normal.Z.Multiply(scale, exactZeroProducts: true));
        FeaturePoint first = polygon.Vertices[0];
        FeatureNumber y = first.Y.Add(polygon.Normal.X.Multiply(first.X.Subtract(x), exactZeroProducts: true)
            .Add(polygon.Normal.Z.Multiply(first.Z.Subtract(z), exactZeroProducts: true)).Divide(polygon.Normal.Y));
        var point = new FeaturePoint(x, y, z);
        GeometrySign inside = polygon.Inside(point);
        if (inside != GeometrySign.Negative)
        {
            // Evaluate the plane stratum without subtracting nearly equal horizontal squares at steep slopes.
            FeatureNumber plane = first.Y.Add(polygon.Normal.X.Multiply(first.X.Subtract(axis.X), exactZeroProducts: true)
                .Add(polygon.Normal.Z.Multiply(first.Z.Subtract(axis.Z), exactZeroProducts: true)).Divide(polygon.Normal.Y));
            FeatureNumber height = plane.Add(radius.Multiply(length.Divide(polygon.Normal.Y).Subtract(One), exactZeroProducts: true));
            if (!height.IsResolved) return GeometrySign.Unresolved;
            range.Add(height.Bounds, inside != GeometrySign.Unresolved);
        }
        if (!range.HasUpper) return GeometrySign.Negative;
        if (!range.HasLower) return GeometrySign.Unresolved;
        lowest = GeometryInterval.Enclose(range.Lower, range.Upper);
        return lowest.IsResolved ? GeometrySign.Positive : GeometrySign.Unresolved;
    }

    struct Maximum
    {
        internal double Lower, Upper;
        internal bool HasLower, HasUpper;

        internal void Add(GeometryInterval height, bool attained)
        {
            if (attained) { Lower = HasLower ? Math.Max(Lower, height.Lower) : height.Lower; HasLower = true; }
            Upper = HasUpper ? Math.Max(Upper, height.Upper) : height.Upper;
            HasUpper = true;
        }

        internal bool Add(FeaturePoint point, FeaturePoint axis, FeatureNumber radius, bool attained)
        {
            FeatureNumber dx = point.X.Subtract(axis.X), dz = point.Z.Subtract(axis.Z);
            FeatureNumber radicand = radius.Multiply(radius, exactZeroProducts: true).Subtract(dx.Multiply(dx, exactZeroProducts: true).Add(dz.Multiply(dz, exactZeroProducts: true)));
            GeometrySign sign = radicand.Sign;
            if (sign == GeometrySign.Negative) return true;
            if (sign == GeometrySign.Unresolved) return false;
            FeatureNumber height = point.Y.Add(Root(radicand)).Subtract(radius);
            if (!height.IsResolved) return false;
            Add(height.Bounds, attained);
            return true;
        }
    }

    static bool Edge(FeaturePoint vertex, FeaturePoint edge, FeaturePoint axis, FeatureNumber radius, ref Maximum range)
    {
        FeatureNumber a = edge.X.Multiply(edge.X, exactZeroProducts: true).Add(edge.Z.Multiply(edge.Z, exactZeroProducts: true));
        GeometrySign length = a.Sign;
        if (length == GeometrySign.Zero) return true;
        if (length != GeometrySign.Positive) return false;
        FeatureNumber dx = vertex.X.Subtract(axis.X), dz = vertex.Z.Subtract(axis.Z);
        FeatureNumber b = dx.Multiply(edge.X, exactZeroProducts: true).Add(dz.Multiply(edge.Z, exactZeroProducts: true));
        FeatureNumber cross = dx.Multiply(edge.Z, exactZeroProducts: true).Subtract(dz.Multiply(edge.X, exactZeroProducts: true));
        // Parallel projected edges retain closed tangency under an enclosed installed transform.
        FeatureNumber k = edge.Z.IsExact && edge.Z.Value == 0
            ? radius.Multiply(radius, exactZeroProducts: true).Subtract(dz.Multiply(dz, exactZeroProducts: true))
            : edge.X.IsExact && edge.X.Value == 0
                ? radius.Multiply(radius, exactZeroProducts: true).Subtract(dx.Multiply(dx, exactZeroProducts: true))
                : radius.Multiply(radius, exactZeroProducts: true).Subtract(cross.Multiply(cross, exactZeroProducts: true).Divide(a));
        GeometrySign sign = k.Sign;
        if (sign == GeometrySign.Negative) return true;
        if (sign == GeometrySign.Unresolved) return false;
        FeatureNumber centre = b.Negate().Divide(a), root = Root(k.Divide(a));
        // Closed circle endpoints attain height q.Y - r, including a tangent edge.
        if (!Parameter(vertex, edge, centre.Subtract(root), radius, ref range) ||
            !Parameter(vertex, edge, centre.Add(root), radius, ref range)) return false;
        FeatureNumber t = centre.Add(edge.Y.Multiply(Root(k.Divide(a.Multiply(a.Add(edge.Y.Multiply(edge.Y, exactZeroProducts: true)), exactZeroProducts: true))), exactZeroProducts: true));
        GeometrySign low = t.Sign, high = One.Subtract(t).Sign;
        if (low == GeometrySign.Negative || high == GeometrySign.Negative) return true;
        if (!t.IsResolved) return false;
        // At the stationary point, combine the axial rise and spherical height symbolically.
        // Evaluating horizontal coordinates first would lose their shared root and obscure tangency.
        FeatureNumber height = vertex.Y.Add(edge.Y.Multiply(centre, exactZeroProducts: true)).Add(
            Root(k.Multiply(a.Add(edge.Y.Multiply(edge.Y, exactZeroProducts: true)), exactZeroProducts: true).Divide(a))).Subtract(radius);
        if (!height.IsResolved) return false;
        range.Add(height.Bounds, low != GeometrySign.Unresolved && high != GeometrySign.Unresolved);
        return true;
    }

    static bool Parameter(FeaturePoint vertex, FeaturePoint edge, FeatureNumber t,
        FeatureNumber radius, ref Maximum range)
    {
        GeometrySign low = t.Sign, high = One.Subtract(t).Sign;
        if (low == GeometrySign.Negative || high == GeometrySign.Negative) return true;
        if (!t.IsResolved) return false;
        bool attained = low != GeometrySign.Unresolved && high != GeometrySign.Unresolved;
        FeaturePoint point = FeaturePoint.Add(vertex, FeaturePoint.Scale(edge, t, exactZeroProducts: true));
        if (!point.IsResolved) return false;
        FeatureNumber height = point.Y.Subtract(radius);
        if (!height.IsResolved) return false;
        range.Add(height.Bounds, attained);
        return true;
    }

    static FeatureNumber Root(FeatureNumber value)
    {
        if (!value.IsResolved || value.Bounds.Upper < 0) return default;
        if (value.IsExact)
        {
            double root = Math.Sqrt(value.Value);
            if (BoundedGeometryArithmetic.CompareProducts(root, root, value.Value, 1) == GeometrySign.Zero)
                return FeatureNumber.Exact(root);
        }
        return FeatureNumber.Enclosed(GeometryInterval.Enclose(Math.Max(0, value.Bounds.Lower), value.Bounds.Upper).Sqrt());
    }
}
