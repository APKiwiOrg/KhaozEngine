using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The query-independent part of the contact prefilter's certificate for one polygon: whether it lies in
/// the prefilter's domain, and an upper bound on the distance of any vertex from the plane through the first vertex
/// with the polygon's normal.</summary>
internal readonly record struct SupportContactDomain(bool Admitted, double OffPlane);

/// <summary>A certified negative before <see cref="SupportSegmentPolygon.MeasureFootprintContact"/>. It decides
/// Negative only where that kernel would return a resolved Negative, and otherwise leaves the member to the kernel.
///
/// Domain. The polygon is resolved, its vertices and edges are exact, and each edge is the exact difference of
/// consecutive vertices. It is strictly convex with every vertex inside every other edge, and each turn has a sine
/// of at least 2^-4 against the normal. Every edge is exactly vertical or at least 2^-8 away from vertical. The
/// normal is exactly horizontal and exact, or its upward part is at least 2^-8 of its length, and each of its
/// components is enclosed within 2^-44 of its length. Coordinates stay within 2^40, and every edge, the normal and
/// the segment length lie in [2^-60, 2^90]. The contact segment is exact and exactly vertical.
///
/// Inside this domain every kernel quotient has a denominator of at least 2^-256 and every value stays below 2^460,
/// so the kernel never refuses: edge and normal lengths are positive, the vertex strata always hold a valid pair,
/// a positive determinant is at least 2^-17 of its scale, and the face rate is exactly zero or sign definite. Each
/// interval operation moves a bound by at most 2^-51 of its magnitude, and subnormal steps add absolute terms far
/// below the margin. With D the coordinate extent sum of the segment and polygon boxes and B their largest
/// coordinate magnitude, every axis enclosure the kernel forms lies within 2^-27 D + 2^-40 B of the segment box,
/// since the edge conditioning bounds every parameter width. Every geometry enclosure lies within 2^-27 D + 2^-40 B
/// of the polygon box, except a face projection, which must not be excluded by any edge. Its true point then lies
/// within 2^-30 (B + D) of the projected polygon by the turn bound, and that polygon lies within the off-plane bound
/// of the polygon box. A face crossing would put one point within 2^-22 D + 2^-30 B plus the off-plane bound of both
/// boxes, so none is formed. Each candidate distance is a box gap between its two enclosures, so the kernel's least
/// lower bound exceeds the certified box gap less 2^-25 D + 2^-28 B + 2 OffPlane. The margin below is at least
/// twice that, so a box gap beyond radius, band and margin leaves the kernel's separation certainly beyond the
/// band.</summary>
internal static class SupportContactPrefilter
{
    static readonly double MaximumCoordinate = Math.ScaleB(1, 40), MinimumLength = Math.ScaleB(1, -60),
        MaximumComponent = Math.ScaleB(1, 90), NormalWidth = Math.ScaleB(1, -44), TurnSquared = Math.ScaleB(1, -8),
        ConditionSquared = Math.ScaleB(1, -16), ExtentMargin = Math.ScaleB(1, -12),
        MagnitudeMargin = Math.ScaleB(1, -26);

    /// <summary>Whether the vertical contact segment from <paramref name="start"/> along
    /// <paramref name="direction"/> is certified farther than <paramref name="radius"/> plus
    /// <paramref name="band"/> from <paramref name="polygon"/>, so the contact kernel's member verdict is
    /// Negative.</summary>
    internal static bool Separated(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction,
        double radius, double band)
    {
        if (!TryBound(polygon, start, direction, out double gap, out double margin)) return false;
        GeometryInterval reach = GeometryInterval.Exact(radius).Add(GeometryInterval.Exact(band))
            .Add(GeometryInterval.Exact(margin));
        return reach.IsResolved && gap > reach.Upper;
    }

    /// <summary>The certified box gap between the contact segment and <paramref name="polygon"/>, and the outward
    /// margin M, when both lie in the prefilter's domain. The derivation above proves the kernel's distance lower
    /// bound is at least the gap less M/2 there.</summary>
    internal static bool TryBound(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction,
        out double gap, out double margin)
    {
        gap = margin = 0;
        if (!start.IsExact || !direction.IsExact || direction.X.Value != 0 || direction.Z.Value != 0 ||
            !Coordinate(start)) return false;
        double length = Math.Abs(direction.Y.Value);
        if (length != 0 && (length < MinimumLength || length > MaximumComponent)) return false;
        SupportContactDomain domain = polygon.ContactDomain;
        if (!domain.Admitted) return false;
        // The kernel's segment end is this sum, or exactly its value when that is representable.
        GeometryInterval end = start.Y.Bounds.Add(direction.Y.Bounds);
        if (!end.IsResolved) return false;
        Span<double> low = [start.X.Value, Math.Min(start.Y.Value, end.Lower), start.Z.Value];
        Span<double> high = [start.X.Value, Math.Max(start.Y.Value, end.Upper), start.Z.Value];
        GeometryInterval extent = GeometryInterval.Exact(0);
        double magnitude = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            double lower = Math.Min(low[axis], polygon.Low[axis]), upper = Math.Max(high[axis], polygon.High[axis]);
            extent = extent.Add(GeometryInterval.Exact(upper).Subtract(GeometryInterval.Exact(lower)));
            magnitude = Math.Max(magnitude, Math.Max(Math.Abs(lower), Math.Abs(upper)));
        }
        GeometryInterval bound = extent.Multiply(GeometryInterval.Exact(ExtentMargin))
            .Add(GeometryInterval.Exact(magnitude).Multiply(GeometryInterval.Exact(MagnitudeMargin)))
            .Add(GeometryInterval.Exact(domain.OffPlane).Multiply(GeometryInterval.Exact(32)));
        if (!bound.IsResolved) return false;
        gap = SupportGeometry.BoxGap(low, high, polygon.Low, polygon.High);
        margin = bound.Upper;
        return true;
    }

    /// <summary>The prefilter's query-independent domain certificate for one finished polygon. Interval arithmetic
    /// and exact differences only, so deriving it records no support counter.</summary>
    internal static SupportContactDomain Certify(SupportPolygon polygon)
    {
        ReadOnlySpan<FeaturePoint> vertices = polygon.Vertices, edges = polygon.Edges;
        int count = vertices.Length;
        FeaturePoint normal = polygon.Normal;
        GeometryVector n = normal.Bounds;
        GeometryInterval nn = Squared(n), length = SupportGeometry.Length(n);
        if (!polygon.IsResolved || count < 3 || !Bounded(n) || !length.IsResolved || length.Lower < MinimumLength)
            return default;
        double width = length.Lower * NormalWidth;
        if (n.X.Upper - n.X.Lower > width || n.Y.Upper - n.Y.Lower > width || n.Z.Upper - n.Z.Lower > width)
            return default;
        // An exact horizontal normal makes the kernel's face rate exactly zero. Otherwise its sign is definite.
        if (!(normal.IsExact && normal.Y.Value == 0) && n.Y.Square().Lower < Scaled(nn, ConditionSquared).Upper)
            return default;
        for (int i = 0; i < count; i++)
            if (!Coordinate(vertices[i]) || !edges[i].IsExact || !Bounded(edges[i].Bounds) ||
                Squared(edges[i].Bounds).Lower < MinimumLength * MinimumLength ||
                !Chord(vertices[i], vertices[(i + 1) % count], edges[i])) return default;
        for (int i = 0; i < count; i++)
        {
            GeometryVector edge = edges[i].Bounds, previous = edges[(i + count - 1) % count].Bounds;
            GeometryInterval squared = Squared(edge);
            if ((edges[i].X.Value != 0 || edges[i].Z.Value != 0) &&
                edge.X.Square().Add(edge.Z.Square()).Lower < Scaled(squared, ConditionSquared).Upper) return default;
            GeometryInterval turn = GeometryVectorOperations.Dot(GeometryVectorOperations.Cross(previous, edge), n);
            if (!turn.IsResolved || turn.Lower <= 0 ||
                turn.Square().Lower < Scaled(nn.Multiply(Squared(previous)).Multiply(squared), TurnSquared).Upper)
                return default;
            GeometryVector origin = vertices[i].Bounds;
            for (int k = 0; k < count; k++)
            {
                if (k == i || k == (i + 1) % count) continue;
                GeometryInterval side = GeometryVectorOperations.Dot(GeometryVectorOperations.Cross(edge,
                    Difference(vertices[k].Bounds, origin)), n);
                if (!side.IsResolved || side.Lower <= 0) return default;
            }
        }
        double offPlane = 0;
        for (int i = 1; i < count; i++)
        {
            GeometryInterval height = GeometryVectorOperations.Dot(n, Difference(vertices[i].Bounds,
                vertices[0].Bounds)).Divide(length);
            if (!height.IsResolved) return default;
            offPlane = Math.Max(offPlane, Math.Max(-height.Lower, height.Upper));
        }
        return new(true, offPlane);
    }

    /// <summary>Whether the exact <paramref name="edge"/> is exactly the difference of its exact endpoints. Two-Sum is
    /// error free here.</summary>
    internal static bool Chord(FeaturePoint from, FeaturePoint to, FeaturePoint edge) =>
        Chord(from.X.Value, to.X.Value, edge.X.Value) && Chord(from.Y.Value, to.Y.Value, edge.Y.Value) &&
        Chord(from.Z.Value, to.Z.Value, edge.Z.Value);

    static bool Chord(double from, double to, double edge)
    {
        ExactExpansion.TwoSum(to, -from, out double difference, out double error);
        return error == 0 && difference == edge;
    }

    static bool Coordinate(FeaturePoint point) => point.IsExact && Math.Abs(point.X.Value) <= MaximumCoordinate &&
        Math.Abs(point.Y.Value) <= MaximumCoordinate && Math.Abs(point.Z.Value) <= MaximumCoordinate;

    static bool Bounded(GeometryVector value) => value.IsResolved &&
        Bounded(value.X) && Bounded(value.Y) && Bounded(value.Z);

    static bool Bounded(GeometryInterval value) =>
        value.Lower >= -MaximumComponent && value.Upper <= MaximumComponent;

    static GeometryInterval Squared(GeometryVector value) => value.X.Square().Add(value.Y.Square()).Add(value.Z.Square());

    static GeometryInterval Scaled(GeometryInterval value, double scale) => value.Multiply(GeometryInterval.Exact(scale));

    static GeometryVector Difference(GeometryVector a, GeometryVector b) =>
        new(a.X.Subtract(b.X), a.Y.Subtract(b.Y), a.Z.Subtract(b.Z));
}
