using System;
using System.Collections.Generic;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A planar convex polygon of installed world geometry, as enclosures. Edge i runs from vertex i toward
/// vertex i + 1. The outward normal has the winding of the source face, so a point of the plane lies inside when
/// Cross(edge i, point - vertex i) has a positive dot with the normal for every edge.</summary>
internal sealed class SupportPolygon
{
    internal SupportPolygon(StaticHandle owner, int elementId, FeaturePoint[] vertices, FeaturePoint[] edges,
        FeaturePoint normal)
    {
        Static = owner;
        ElementId = elementId;
        Vertices = vertices;
        Edges = edges;
        Normal = normal;
        NormalLength = SupportGeometry.Length(normal.Bounds);
        Low = new double[3];
        High = new double[3];
        bool resolved = NormalLength.IsResolved && NormalLength.Lower > 0 && vertices.Length >= 3 &&
            edges.Length == vertices.Length;
        for (int axis = 0; axis < 3; axis++)
        {
            Low[axis] = double.PositiveInfinity;
            High[axis] = double.NegativeInfinity;
        }
        foreach (FeaturePoint vertex in vertices)
        {
            resolved &= vertex.IsResolved;
            for (int axis = 0; axis < 3; axis++)
            {
                GeometryInterval component = SupportGeometry.Component(vertex, axis);
                Low[axis] = Math.Min(Low[axis], component.Lower);
                High[axis] = Math.Max(High[axis], component.Upper);
            }
        }
        foreach (FeaturePoint edge in edges) resolved &= edge.IsResolved;
        IsResolved = resolved;
    }

    internal StaticHandle Static { get; }
    internal int ElementId { get; }
    internal FeaturePoint[] Vertices { get; }
    internal FeaturePoint[] Edges { get; }
    internal FeaturePoint Normal { get; }
    internal GeometryInterval NormalLength { get; }
    internal double[] Low { get; }
    internal double[] High { get; }
    internal bool IsResolved { get; }

    /// <summary>The signed height of <paramref name="point"/> above this polygon's plane, in metres.</summary>
    internal GeometryInterval Height(FeaturePoint point) =>
        FeaturePoint.Dot(Normal, FeaturePoint.Subtract(point, Vertices[0])).Bounds.Divide(NormalLength);

    /// <summary>Positive strictly inside, zero on the boundary, negative strictly outside. A point is supplied in
    /// the polygon's plane.</summary>
    internal GeometrySign Inside(FeaturePoint point)
    {
        bool boundary = false, unknown = false;
        for (int i = 0; i < Edges.Length; i++)
        {
            GeometrySign side = EdgeSide(point, i);
            if (side == GeometrySign.Negative) return side;
            unknown |= side == GeometrySign.Unresolved;
            boundary |= side == GeometrySign.Zero;
        }
        return unknown ? GeometrySign.Unresolved : boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }

    internal GeometrySign EdgeSide(FeaturePoint point, int edge) => FeaturePoint.Dot(FeaturePoint.Cross(Edges[edge],
        FeaturePoint.Subtract(point, Vertices[edge])), Normal).Sign;
}

/// <summary>A segment and polygon pair whose points may realize the minimum distance. A valid pair certainly
/// lies on the segment and the polygon, so its distance bounds the minimum from above. Every pair that may be
/// the minimizer is present, so the least lower bound bounds the minimum from below.</summary>
internal readonly record struct SupportCandidate(FeaturePoint Axis, FeaturePoint Geometry, bool Valid,
    GeometryInterval Distance);

/// <summary>Bounds on the distance between a segment and a closed polygon, an enclosure of the polygon's closest
/// points, and whether a closest segment point may lie on the polygon's front side within the band.</summary>
internal readonly record struct SupportDistance(bool IsResolved, double Lower, double Upper, GeometryVector Witness,
    bool Front);

/// <summary>Complete enumeration of the closest pair strata of a segment and a closed convex polygon: vertex
/// against segment, edge against segment endpoint, edge against segment interior, segment endpoint against
/// face interior, and the segment crossing the face. An undecided stratum condition keeps every pair it may
/// produce, so ties and boundary positions widen the bounds and never refuse.</summary>
internal sealed class SupportSegmentPolygon
{
    static readonly FeatureNumber One = FeatureNumber.Exact(1);
    static readonly FeatureNumber UnitRange = FeatureNumber.Enclosed(GeometryInterval.Enclose(0, 1));

    readonly List<SupportCandidate> _candidates = [];
    FeaturePoint _start, _end, _direction;
    FeatureNumber _lengthSquared;
    bool _point, _failed;

    /// <summary>Measures the segment from <paramref name="start"/> along <paramref name="direction"/> against
    /// <paramref name="polygon"/>. An unresolved result means the bounded arithmetic could not enclose it.</summary>
    internal SupportDistance Measure(FeaturePoint start, FeaturePoint direction, SupportPolygon polygon,
        double frontBand)
    {
        _candidates.Clear();
        _failed = false;
        _start = start;
        _direction = direction;
        _end = FeaturePoint.Add(start, direction);
        _lengthSquared = FeaturePoint.Dot(direction, direction);
        GeometrySign length = _lengthSquared.Sign;
        if (!polygon.IsResolved || !_end.IsResolved || length is GeometrySign.Negative or GeometrySign.Unresolved)
            return default;
        _point = length == GeometrySign.Zero;
        for (int i = 0; i < polygon.Vertices.Length && !_failed; i++) Vertex(polygon.Vertices[i]);
        for (int i = 0; i < polygon.Edges.Length && !_failed; i++) Edge(polygon, i);
        if (!_failed) Face(polygon);
        if (_failed) return default;

        double lower = double.PositiveInfinity, upper = double.PositiveInfinity;
        foreach (SupportCandidate candidate in _candidates)
        {
            lower = Math.Min(lower, candidate.Distance.Lower);
            if (candidate.Valid) upper = Math.Min(upper, candidate.Distance.Upper);
        }
        if (!double.IsFinite(upper)) return default;
        Span<double> low = [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
        Span<double> high = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
        bool front = false;
        foreach (SupportCandidate candidate in _candidates)
        {
            // Every pair that may realize the minimum encloses the closest points between them.
            if (candidate.Distance.Lower > upper) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                GeometryInterval component = SupportGeometry.Component(candidate.Geometry, axis);
                low[axis] = Math.Min(low[axis], component.Lower);
                high[axis] = Math.Max(high[axis], component.Upper);
            }
            GeometryInterval height = polygon.Height(candidate.Axis);
            if (!height.IsResolved) return default;
            front |= height.Upper >= -frontBand;
        }
        var witness = new GeometryVector(GeometryInterval.Enclose(low[0], high[0]),
            GeometryInterval.Enclose(low[1], high[1]), GeometryInterval.Enclose(low[2], high[2]));
        return witness.IsResolved ? new(true, lower, upper, witness, front) : default;
    }

    void Vertex(FeaturePoint vertex)
    {
        if (_point)
        {
            Add(_start, vertex, true);
            return;
        }
        FeatureNumber t = FeaturePoint.Dot(FeaturePoint.Subtract(vertex, _start), _direction).Divide(_lengthSquared);
        if (!t.IsResolved) { _failed = true; return; }
        GeometrySign low = t.Sign, high = t.Compare(One);
        if (low is not GeometrySign.Positive) Add(_start, vertex, true);
        if (high is not GeometrySign.Negative) Add(_end, vertex, true);
        if (Open(low, high))
            Add(Along(_start, _direction, t), vertex, low == GeometrySign.Positive && high == GeometrySign.Negative);
    }

    void Edge(SupportPolygon polygon, int index)
    {
        FeaturePoint origin = polygon.Vertices[index], edge = polygon.Edges[index];
        FeatureNumber squared = FeaturePoint.Dot(edge, edge);
        if (squared.Sign != GeometrySign.Positive) { _failed = true; return; }
        EdgeEndpoint(origin, edge, squared, _start);
        if (!_point) EdgeEndpoint(origin, edge, squared, _end);
        if (_failed || _point) return;
        FeaturePoint offset = FeaturePoint.Subtract(_start, origin);
        FeatureNumber b = FeaturePoint.Dot(_direction, edge), d = FeaturePoint.Dot(_direction, offset);
        FeatureNumber f = FeaturePoint.Dot(edge, offset);
        FeatureNumber determinant = _lengthSquared.Multiply(squared).Subtract(b.Multiply(b));
        GeometrySign sign = determinant.Sign;
        if (sign == GeometrySign.Zero) return; // Parallel. The endpoint and vertex strata hold the minimum.
        if (sign == GeometrySign.Positive)
        {
            FeatureNumber t = b.Multiply(f).Subtract(squared.Multiply(d)).Divide(determinant);
            FeatureNumber u = _lengthSquared.Multiply(f).Subtract(b.Multiply(d)).Divide(determinant);
            if (!t.IsResolved || !u.IsResolved) { _failed = true; return; }
            GeometrySign tLow = t.Sign, tHigh = t.Compare(One), uLow = u.Sign, uHigh = u.Compare(One);
            if (!Open(tLow, tHigh) || !Open(uLow, uHigh)) return;
            bool valid = tLow == GeometrySign.Positive && tHigh == GeometrySign.Negative &&
                uLow == GeometrySign.Positive && uHigh == GeometrySign.Negative;
            Add(Along(_start, _direction, t), Along(origin, edge, u), valid);
            return;
        }
        // Nearly parallel and undecided. An interior minimizer's edge parameter is the projection of a segment
        // point, so it lies between the projections of the two segment endpoints. Keep that whole range.
        FeatureNumber first = FeaturePoint.Dot(FeaturePoint.Subtract(_start, origin), edge).Divide(squared);
        FeatureNumber second = FeaturePoint.Dot(FeaturePoint.Subtract(_end, origin), edge).Divide(squared);
        if (!first.IsResolved || !second.IsResolved) { _failed = true; return; }
        double low = Math.Max(0, Math.Min(first.Bounds.Lower, second.Bounds.Lower));
        double high = Math.Min(1, Math.Max(first.Bounds.Upper, second.Bounds.Upper));
        if (low > high) return;
        Add(Along(_start, _direction, UnitRange),
            Along(origin, edge, FeatureNumber.Enclosed(GeometryInterval.Enclose(low, high))), false);
    }

    void EdgeEndpoint(FeaturePoint origin, FeaturePoint edge, FeatureNumber squared, FeaturePoint axis)
    {
        FeatureNumber u = FeaturePoint.Dot(FeaturePoint.Subtract(axis, origin), edge).Divide(squared);
        if (!u.IsResolved) { _failed = true; return; }
        GeometrySign low = u.Sign, high = u.Compare(One);
        if (Open(low, high))
            Add(axis, Along(origin, edge, u), low == GeometrySign.Positive && high == GeometrySign.Negative);
    }

    void Face(SupportPolygon polygon)
    {
        FeaturePoint normal = polygon.Normal, origin = polygon.Vertices[0];
        FeatureNumber squared = FeaturePoint.Dot(normal, normal);
        if (squared.Sign != GeometrySign.Positive) { _failed = true; return; }
        FeaturePoint first = FacePoint(polygon, squared, _start);
        FeaturePoint second = _point ? first : FacePoint(polygon, squared, _end);
        if (_failed || _point) return;
        FeatureNumber rate = FeaturePoint.Dot(normal, _direction);
        GeometrySign sign = rate.Sign;
        if (sign == GeometrySign.Zero) return; // Parallel. A crossing segment lies in the plane, held above.
        if (sign is GeometrySign.Positive or GeometrySign.Negative)
        {
            FeatureNumber t = FeaturePoint.Dot(normal, FeaturePoint.Subtract(origin, _start)).Divide(rate);
            if (!t.IsResolved) { _failed = true; return; }
            GeometrySign low = t.Sign, high = t.Compare(One);
            if (low == GeometrySign.Negative || high == GeometrySign.Positive) return;
            FeaturePoint crossing = Along(_start, _direction, t);
            GeometrySign inside = polygon.Inside(crossing);
            if (inside == GeometrySign.Negative) return;
            bool valid = (low is GeometrySign.Positive or GeometrySign.Zero) &&
                (high is GeometrySign.Negative or GeometrySign.Zero) && inside != GeometrySign.Unresolved;
            Touch(crossing, valid);
            return;
        }
        // Nearly parallel and undecided. A crossing needs the endpoints on both sides of the plane, and it
        // projects between the endpoint projections, so one edge excluding both projections excludes it.
        GeometrySign below = FeaturePoint.Dot(normal, FeaturePoint.Subtract(_start, origin)).Sign;
        GeometrySign above = FeaturePoint.Dot(normal, FeaturePoint.Subtract(_end, origin)).Sign;
        if (below == above && (below is GeometrySign.Positive or GeometrySign.Negative)) return;
        for (int i = 0; i < polygon.Edges.Length; i++)
            if (polygon.EdgeSide(first, i) == GeometrySign.Negative && polygon.EdgeSide(second, i) == GeometrySign.Negative)
                return;
        Touch(Along(_start, _direction, UnitRange), false);
    }

    FeaturePoint FacePoint(SupportPolygon polygon, FeatureNumber squared, FeaturePoint axis)
    {
        FeatureNumber scale = FeaturePoint.Dot(FeaturePoint.Subtract(axis, polygon.Vertices[0]), polygon.Normal)
            .Divide(squared);
        FeaturePoint projected = FeaturePoint.Subtract(axis, FeaturePoint.Scale(polygon.Normal, scale));
        if (!projected.IsResolved) { _failed = true; return projected; }
        GeometrySign inside = polygon.Inside(projected);
        // A boundary projection belongs to the edge and vertex strata.
        if (inside is GeometrySign.Positive or GeometrySign.Unresolved)
            Add(axis, projected, inside == GeometrySign.Positive);
        return projected;
    }

    void Add(FeaturePoint axis, FeaturePoint geometry, bool valid)
    {
        GeometryInterval distance = SupportGeometry.Length(FeaturePoint.Subtract(axis, geometry).Bounds);
        if (!axis.IsResolved || !geometry.IsResolved || !distance.IsResolved) { _failed = true; return; }
        _candidates.Add(new(axis, geometry, valid, distance));
    }

    void Touch(FeaturePoint point, bool valid)
    {
        if (!point.IsResolved) { _failed = true; return; }
        _candidates.Add(new(point, point, valid, GeometryInterval.Exact(0)));
    }

    // Excludes the parameter only when one side is decided outside the open unit interval.
    static bool Open(GeometrySign low, GeometrySign high) =>
        (low is GeometrySign.Positive or GeometrySign.Unresolved) &&
        (high is GeometrySign.Negative or GeometrySign.Unresolved);

    static FeaturePoint Along(FeaturePoint origin, FeaturePoint direction, FeatureNumber parameter) =>
        FeaturePoint.Add(origin, FeaturePoint.Scale(direction, parameter));
}

/// <summary>Shared enclosure helpers for the support neighborhood.</summary>
internal static class SupportGeometry
{
    internal static GeometryInterval Component(FeaturePoint point, int axis) =>
        axis == 0 ? point.X.Bounds : axis == 1 ? point.Y.Bounds : point.Z.Bounds;

    /// <summary>A lower bound on the distance between two axis-aligned boxes. Infinity marks a refusal to bound.</summary>
    internal static double BoxGap(ReadOnlySpan<double> lowA, ReadOnlySpan<double> highA, ReadOnlySpan<double> lowB,
        ReadOnlySpan<double> highB)
    {
        GeometryInterval squared = GeometryInterval.Exact(0);
        for (int axis = 0; axis < 3; axis++)
        {
            GeometryInterval gap = highA[axis] < lowB[axis]
                ? GeometryInterval.Exact(lowB[axis]).Subtract(GeometryInterval.Exact(highA[axis]))
                : highB[axis] < lowA[axis]
                    ? GeometryInterval.Exact(lowA[axis]).Subtract(GeometryInterval.Exact(highB[axis]))
                    : GeometryInterval.Exact(0);
            squared = squared.Add(gap.Square());
        }
        GeometryInterval distance = NonnegativeSqrt(squared);
        // An unresolved bound never excludes anything.
        return distance.IsResolved ? distance.Lower : 0;
    }

    /// <summary>The Euclidean length of a vector enclosure. Square keeps each component's self dependency.</summary>
    internal static GeometryInterval Length(GeometryVector value) => !value.IsResolved ? default
        : NonnegativeSqrt(value.X.Square().Add(value.Y.Square()).Add(value.Z.Square()));

    // A sum of squares is nonnegative even when outward rounding puts its lower bound below zero.
    static GeometryInterval NonnegativeSqrt(GeometryInterval squared) => !squared.IsResolved ? default
        : GeometryInterval.Enclose(Math.Max(0, squared.Lower), Math.Max(0, squared.Upper)).Sqrt();
}
