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
        out GeometryInterval lowest, bool sourceHeight = false)
    {
        if (SupportPlanarProposal.Lowest(polygon, proposal, sourceHeight, out lowest))
        {
            using var phase = ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportPlanarHeight);
            return GeometrySign.Positive;
        }
        return LowestExhaustive(polygon, proposal, out lowest, sourceHeight);
    }

    internal static GeometrySign LowestExhaustive(SupportPolygon polygon, in SupportFootprintProposal proposal,
        out GeometryInterval lowest, bool sourceHeight = false)
    {
        lowest = default;
        GeometrySign upward = SupportFootprintPolygon.Orientation(polygon);
        if (upward is GeometrySign.Negative or GeometrySign.Zero) return GeometrySign.Negative;
        if (upward != GeometrySign.Positive) return GeometrySign.Unresolved;
        FeatureNumber radius = FeatureNumber.Exact(proposal.Probe.Radius);
        FeaturePoint axis = new(FeatureNumber.Exact(proposal.Start.Position.X), Zero,
            FeatureNumber.Exact(proposal.Start.Position.Z));
        bool share = polygon.SharesFootprintOperands(proposal);
        var range = new Maximum();
        for (int i = 0; i < polygon.Vertices.Length; i++)
            if (!range.Add(polygon, i, axis, radius, share)) return GeometrySign.Unresolved;
        for (int i = 0; i < polygon.Edges.Length; i++)
            if (!Edge(polygon, i, axis, radius, share, ref range)) return GeometrySign.Unresolved;

        FeatureNumber length = Root(FeaturePoint.Dot(polygon.Normal, polygon.Normal));
        FeatureNumber scale = radius.Divide(length);
        FeatureNumber x = axis.X.Subtract(polygon.Normal.X.Multiply(scale, exactZeroProducts: true));
        FeatureNumber z = axis.Z.Subtract(polygon.Normal.Z.Multiply(scale, exactZeroProducts: true));
        FeaturePoint first = polygon.Vertices[0];
        FeatureNumber y = first.Y.Add(polygon.Normal.X.Multiply(first.X.Subtract(x), exactZeroProducts: true)
            .Add(polygon.Normal.Z.Multiply(first.Z.Subtract(z), exactZeroProducts: true)).Divide(polygon.Normal.Y));
        var point = new FeaturePoint(x, y, z);
        GeometrySign inside = polygon.Inside(point);
        if (inside == GeometrySign.Unresolved && polygon.HasSource)
            inside = polygon.SourcePredicates.ProposalInterior(polygon, point, axis, radius);
        if (inside != GeometrySign.Negative)
        {
            // Evaluate the plane stratum without subtracting nearly equal horizontal squares at steep slopes.
            FeatureNumber plane = first.Y.Add(polygon.Normal.X.Multiply(first.X.Subtract(axis.X), exactZeroProducts: true)
                .Add(polygon.Normal.Z.Multiply(first.Z.Subtract(axis.Z), exactZeroProducts: true)).Divide(polygon.Normal.Y));
            FeatureNumber height = plane.Add(radius.Multiply(length.Divide(polygon.Normal.Y).Subtract(One), exactZeroProducts: true));
            if (!height.IsResolved) return GeometrySign.Unresolved;
            range.Add(height.Bounds, inside != GeometrySign.Unresolved);
        }
        return Finish(polygon, range, inside, sourceHeight, out lowest);
    }

    /// <summary>The complete evaluator for a proposal whose axis is certified outside the polygon's closed interior,
    /// where the interior stratum resolves without adding a height. Only the vertex and edge strata whose bits are set
    /// run, in the complete order. The caller certifies an upward polygon whose omitted strata each resolve without
    /// adding a height.</summary>
    internal static GeometrySign LowestOutside(SupportPolygon polygon, in SupportFootprintProposal proposal,
        int vertices, int edges, out GeometryInterval lowest)
    {
        lowest = default;
        FeatureNumber radius = FeatureNumber.Exact(proposal.Probe.Radius);
        FeaturePoint axis = new(FeatureNumber.Exact(proposal.Start.Position.X), Zero,
            FeatureNumber.Exact(proposal.Start.Position.Z));
        bool share = polygon.SharesFootprintOperands(proposal);
        var range = new Maximum();
        for (int i = 0; i < polygon.Vertices.Length; i++)
            if ((vertices & (1 << i)) != 0 && !range.Add(polygon, i, axis, radius, share)) return GeometrySign.Unresolved;
        for (int i = 0; i < polygon.Edges.Length; i++)
            if ((edges & (1 << i)) != 0 && !Edge(polygon, i, axis, radius, share, ref range)) return GeometrySign.Unresolved;
        return Finish(polygon, range, GeometrySign.Negative, sourceHeight: true, out lowest);
    }

    static GeometrySign Finish(SupportPolygon polygon, Maximum range, GeometrySign inside, bool sourceHeight,
        out GeometryInterval lowest)
    {
        lowest = default;
        if (!range.HasUpper) return GeometrySign.Negative;
        if (!range.HasLower) return GeometrySign.Unresolved;
        if (sourceHeight && polygon.HasSource && polygon.SourcePredicates.HorizontalPlaneHeight(polygon, out var horizontal))
        {
            // Every sphere-contact stratum is at most the horizontal plane height. The interior
            // attains it only when its membership is certified, including a closed boundary.
            range.Upper = Math.Min(range.Upper, horizontal.Upper);
            if (inside is GeometrySign.Positive or GeometrySign.Zero)
                range.Lower = Math.Max(range.Lower, horizontal.Lower);
        }
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

        internal bool Add(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber radius, bool share)
        {
            FeaturePoint point = polygon.Vertices[index];
            FeatureNumber squared;
            if (share) squared = polygon.FootprintOperands(index, axis).ProjectedDistanceSquared;
            else
            {
                FeatureNumber dx = point.X.Subtract(axis.X), dz = point.Z.Subtract(axis.Z);
                squared = dx.Multiply(dx, exactZeroProducts: true).Add(dz.Multiply(dz, exactZeroProducts: true));
            }
            FeatureNumber radicand = radius.Multiply(radius, exactZeroProducts: true).Subtract(squared);
            GeometrySign sign = radicand.Sign;
            if (sign == GeometrySign.Unresolved && polygon.HasSource)
            {
                GeometrySign disc = polygon.SourcePredicates.VertexDisc(polygon, index, axis, radius);
                sign = disc == GeometrySign.Positive ? GeometrySign.Negative :
                    disc == GeometrySign.Negative ? GeometrySign.Positive : disc;
                if (sign == GeometrySign.Zero) radicand = Zero;
            }
            if (sign == GeometrySign.Negative) return true;
            if (sign == GeometrySign.Unresolved) return false;
            FeatureNumber height = point.Y.Add(Root(radicand)).Subtract(radius);
            if (!height.IsResolved) return false;
            Add(height.Bounds, true);
            return true;
        }
    }

    static bool Edge(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber radius, bool share, ref Maximum range)
    {
        FeaturePoint vertex = polygon.Vertices[index], edge = polygon.Edges[index];
        SupportFootprintEdgeOperands operands = polygon.FootprintOperands(index, axis, share);
        FeatureNumber a = operands.LengthSquared;
        GeometrySign length = a.Sign;
        if (length == GeometrySign.Unresolved && polygon.HasSource)
            length = polygon.SourcePredicates.EdgeLength(polygon, index);
        if (length == GeometrySign.Zero) return true;
        if (length != GeometrySign.Positive) return false;
        FeatureNumber dx = operands.DeltaX, dz = operands.DeltaZ;
        FeatureNumber b = operands.Dot, cross = operands.Cross;
        // Parallel projected edges retain closed tangency under an enclosed installed transform.
        FeatureNumber k = edge.Z.IsExact && edge.Z.Value == 0
            ? radius.Multiply(radius, exactZeroProducts: true).Subtract(dz.Multiply(dz, exactZeroProducts: true))
            : edge.X.IsExact && edge.X.Value == 0
                ? radius.Multiply(radius, exactZeroProducts: true).Subtract(dx.Multiply(dx, exactZeroProducts: true))
                : radius.Multiply(radius, exactZeroProducts: true).Subtract(cross.Multiply(cross, exactZeroProducts: true).Divide(a));
        GeometrySign sign = k.Sign;
        if (sign == GeometrySign.Unresolved && polygon.HasSource)
        {
            sign = polygon.SourcePredicates.EdgeDiscriminant(polygon, index, axis, radius);
            if (sign == GeometrySign.Zero) k = Zero;
            else if (sign == GeometrySign.Positive && k.IsResolved)
                k = FeatureNumber.Enclosed(GeometryInterval.Enclose(Math.Max(0, k.Bounds.Lower), k.Bounds.Upper));
        }
        if (sign == GeometrySign.Negative) return true;
        if (sign == GeometrySign.Unresolved) return false;
        FeatureNumber centre = b.Negate().Divide(a), root = Root(k.Divide(a));
        // Closed circle endpoints attain height q.Y - r, including a tangent edge.
        if (!Parameter(polygon, index, axis, centre.Subtract(root), radius, -1, ref range) ||
            !Parameter(polygon, index, axis, centre.Add(root), radius, 1, ref range)) return false;
        FeatureNumber t = centre.Add(edge.Y.Multiply(Root(k.Divide(a.Multiply(a.Add(edge.Y.Multiply(edge.Y, exactZeroProducts: true)), exactZeroProducts: true))), exactZeroProducts: true));
        GeometrySign low = t.Sign, high = One.Subtract(t).Sign;
        if (polygon.HasSource)
        {
            if (low == GeometrySign.Unresolved)
                low = polygon.SourcePredicates.EdgeStationary(polygon, index, axis, radius, high: false);
            if (high == GeometrySign.Unresolved)
                high = polygon.SourcePredicates.EdgeStationary(polygon, index, axis, radius, high: true);
        }
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

    static bool Parameter(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber t,
        FeatureNumber radius, int direction, ref Maximum range)
    {
        FeaturePoint vertex = polygon.Vertices[index], edge = polygon.Edges[index];
        GeometrySign low = t.Sign, high = One.Subtract(t).Sign;
        if (polygon.HasSource)
        {
            if (low == GeometrySign.Unresolved)
                low = polygon.SourcePredicates.EdgeRoot(polygon, index, axis, radius, direction, high: false);
            if (high == GeometrySign.Unresolved)
                high = polygon.SourcePredicates.EdgeRoot(polygon, index, axis, radius, direction, high: true);
        }
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
