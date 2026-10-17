using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Complete convex polygon/disc extrema. Admission uses the connected height interval over the expanded
/// disc, then support measures the original disc on the whole polygon. No clipping or sweep witness is used.</summary>
internal static class SupportFootprintPolygon
{
    static readonly FeatureNumber Zero = FeatureNumber.Exact(0), One = FeatureNumber.Exact(1);

    internal static GeometrySign Measure(SupportPolygon polygon, in SupportFootprintRequest request,
        out SupportFootprintMember member)
    {
        SupportFootprintBounds bounds;
        double bottom, top;
        using (ContactAllocationDiagnostics.Begin(ContactAllocationPhase.SupportColumnSetup))
            if (!SupportFootprintBounds.TryCreate(request, out bounds) ||
                !SupportFootprintBounds.TryColumn(request, out bottom, out top))
            {
                member = default;
                SupportQueryDiagnostics.Record(SupportQueryCounter.Polygons);
                return GeometrySign.Unresolved;
            }
        return Measure(polygon, request, bounds, bottom, top, out member);
    }

    // The collector certifies these identical request operands once, before traversing any static. The certified
    // bounds and column reject runs before any per-query operand is begun, reading the polygon's derived normal
    // signs. The area and resolution checks keep their place ahead of it, so every verdict is unchanged.
    internal static GeometrySign Measure(SupportPolygon polygon, in SupportFootprintRequest request,
        in SupportFootprintBounds bounds, double bottom, double top, out SupportFootprintMember member)
    {
        member = default;
        bool reference = SupportReferenceEvaluation.Active;
        if (reference) BeginOperands(polygon, request);
        SupportQueryDiagnostics.Record(SupportQueryCounter.Polygons);
        GeometrySign area = reference ? DeriveNormalArea(polygon) : polygon.NormalArea;
        if (area == GeometrySign.Zero) return GeometrySign.Negative;
        if (!polygon.IsResolved || area != GeometrySign.Positive) return GeometrySign.Unresolved;
        if (polygon.High[0] < bounds.Minimum.X || polygon.Low[0] > bounds.Maximum.X ||
            polygon.High[1] < bottom || polygon.Low[1] > top ||
            polygon.High[2] < bounds.Minimum.Z || polygon.Low[2] > bounds.Maximum.Z) return GeometrySign.Negative;
        if (!reference) BeginOperands(polygon, request);
        FeatureNumber expanded = FeatureNumber.Exact(request.Radius).Add(FeatureNumber.Exact(request.ContactBand));
        GeometrySign admission = Extrema(polygon, request, expanded, out Range column);
        if (admission != GeometrySign.Positive) return admission;
        if (column.Minimum.Lower > top || column.Maximum.Upper < bottom) return GeometrySign.Negative;
        if (column.Minimum.Upper > top || column.Maximum.Lower < bottom) return GeometrySign.Unresolved;
        GeometryVector normalBounds = GeometryVectorOperations.Normalize(polygon.Normal.Bounds);
        if (normalBounds.IsResolved && normalBounds.Y.Lower < request.CosMaxSlope &&
            normalBounds.Y.Upper >= request.CosMaxSlope && Orientation(polygon) == GeometrySign.Positive &&
            polygon.HasSource && polygon.SourcePredicates.NormalSlope(polygon, request.CosMaxSlope) == GeometrySign.Zero)
            normalBounds = new(normalBounds.X, GeometryInterval.Exact(request.CosMaxSlope), normalBounds.Z);
        GeometryVectorOutput normal = GeometryVectorOperations.Publish(normalBounds);
        GeometryVectorOutput plane = GeometryVectorOperations.Publish(polygon.Vertices[0].Bounds);
        if (!normal.IsResolved || !plane.IsResolved) return GeometrySign.Unresolved;
        Range disc = default;
        GeometrySign orientation = Orientation(polygon);
        if (orientation == GeometrySign.Unresolved) return GeometrySign.Unresolved;
        bool supports = orientation == GeometrySign.Positive;
        if (supports)
        {
            GeometrySign footprint = Extrema(polygon, request, FeatureNumber.Exact(request.Radius), out disc);
            if (footprint == GeometrySign.Unresolved) return footprint;
            supports = footprint == GeometrySign.Positive;
        }
        GeometryVectorOutput witness = supports ? GeometryVectorOperations.Publish(disc.Witness) : default;
        if (supports && !witness.IsResolved) return GeometrySign.Unresolved;
        member = new(polygon.Static, SupportElementKind.Polygon, polygon.ElementId, plane.Value, plane.Error,
            normal.Value, normal.Error, normalBounds.Y.Lower, supports, supports ? disc.Maximum.Lower : 0,
            supports ? disc.Maximum.Upper : 0, 0, supports ? (byte)1 : (byte)0,
            supports ? new(SupportWitnessRole.Footprint, witness.Value, witness.Error, normal.Value, normal.Error,
                normalBounds.Y.Lower) : default,
            default);
        return GeometrySign.Positive;
    }

    static void BeginOperands(SupportPolygon polygon, in SupportFootprintRequest request)
    {
        polygon.BeginFootprintOperands(request);
        SupportQueryDiagnostics.Record(SupportQueryCounter.PolygonOperands);
    }

    struct Range
    {
        internal GeometryInterval Minimum, Maximum;
        internal GeometryVector Witness;
        internal bool HasPoint;

        internal void Add(FeaturePoint point)
        {
            SupportQueryDiagnostics.Record(SupportQueryCounter.Extrema);
            GeometryInterval y = point.Y.Bounds;
            if (!HasPoint)
            {
                Minimum = Maximum = y;
                Witness = point.Bounds;
                HasPoint = true;
                return;
            }
            Minimum = GeometryInterval.Enclose(Math.Min(Minimum.Lower, y.Lower), Math.Min(Minimum.Upper, y.Upper));
            if (y.Lower > Maximum.Upper) Witness = point.Bounds;
            else if (y.Upper >= Maximum.Lower && !(y.Lower == y.Upper &&
                Maximum.Lower == Maximum.Upper && y.Lower == Maximum.Lower))
                Witness = Union(Witness, point.Bounds);
            Maximum = GeometryInterval.Enclose(Math.Max(Maximum.Lower, y.Lower), Math.Max(Maximum.Upper, y.Upper));
        }
    }

    static GeometryVector Union(GeometryVector a, GeometryVector b) => new(
        GeometryInterval.Enclose(Math.Min(a.X.Lower, b.X.Lower), Math.Max(a.X.Upper, b.X.Upper)),
        GeometryInterval.Enclose(Math.Min(a.Y.Lower, b.Y.Lower), Math.Max(a.Y.Upper, b.Y.Upper)),
        GeometryInterval.Enclose(Math.Min(a.Z.Lower, b.Z.Lower), Math.Max(a.Z.Upper, b.Z.Upper)));

    static GeometrySign Extrema(SupportPolygon polygon, in SupportFootprintRequest request, FeatureNumber radius,
        out Range range)
    {
        range = default;
        FeaturePoint axis = new(FeatureNumber.Exact(request.Axis.X), Zero, FeatureNumber.Exact(request.Axis.Y));
        GeometrySign orientation = Orientation(polygon);
        if (orientation == GeometrySign.Unresolved) return orientation;
        if (orientation != GeometrySign.Zero)
        {
            FeaturePoint centre = OnPlane(polygon, axis);
            GeometrySign inside = Interior(polygon, axis, new(Zero, Zero, Zero), radius, One, One, orientation);
            if (inside == GeometrySign.Unresolved) return inside;
            if (inside != GeometrySign.Negative) range.Add(centre);
        }
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint vertex = polygon.Vertices[i];
            GeometrySign sign = InDisc(polygon, i, vertex, axis, radius, originalVertex: true);
            if (sign == GeometrySign.Unresolved) return sign;
            if (sign != GeometrySign.Positive) range.Add(vertex);
        }
        for (int i = 0; i < polygon.Edges.Length; i++)
            if (!Edge(polygon, i, axis, radius, ref range)) return GeometrySign.Unresolved;
        if (orientation != GeometrySign.Zero)
        {
            int sourceDirection = orientation == GeometrySign.Positive ? -1 : 1;
            FeatureNumber direction = FeatureNumber.Exact(sourceDirection);
            FeaturePoint uphill = new(polygon.Normal.X.Multiply(direction, exactZeroProducts: true), Zero, polygon.Normal.Z.Multiply(direction, exactZeroProducts: true));
            // For an axis-aligned gradient the normalized direction is exact even when the
            // installed coefficient is enclosed. Retain that identity before forming a radical.
            if (uphill.Z.IsExact && uphill.Z.Value == 0)
            {
                GeometrySign xSign = uphill.X.Sign;
                if (xSign == GeometrySign.Unresolved && polygon.HasSource)
                    xSign = polygon.SourcePredicates.NormalComponent(polygon, 0, sourceDirection);
                if (xSign is GeometrySign.Positive or GeometrySign.Negative)
                    uphill = new(FeatureNumber.Exact(xSign == GeometrySign.Positive ? 1 : -1), Zero, Zero);
            }
            else if (uphill.X.IsExact && uphill.X.Value == 0)
            {
                GeometrySign zSign = uphill.Z.Sign;
                if (zSign == GeometrySign.Unresolved && polygon.HasSource)
                    zSign = polygon.SourcePredicates.NormalComponent(polygon, 2, sourceDirection);
                if (zSign is GeometrySign.Positive or GeometrySign.Negative)
                    uphill = new(Zero, Zero, FeatureNumber.Exact(zSign == GeometrySign.Positive ? 1 : -1));
            }
            FeatureNumber squared = DotXZ(uphill, uphill);
            GeometrySign gradient = squared.Sign;
            if (gradient == GeometrySign.Unresolved && polygon.HasSource)
                gradient = polygon.SourcePredicates.NormalSquared(polygon, projected: true);
            if (gradient == GeometrySign.Unresolved) return gradient;
            if (gradient == GeometrySign.Positive)
                for (int sign = 1; sign >= -1; sign -= 2)
                {
                    FeaturePoint directionPoint = FeaturePoint.Scale(uphill, FeatureNumber.Exact(sign), exactZeroProducts: true);
                    FeatureNumber root = Sqrt(squared);
                    FeaturePoint point = OnPlane(polygon, FeaturePoint.Add(axis,
                        FeaturePoint.Scale(directionPoint, radius.Divide(root), exactZeroProducts: true)));
                    if (!point.IsResolved) return GeometrySign.Unresolved;
                    GeometrySign inside = Interior(polygon, axis, directionPoint, radius, squared, root, orientation,
                        sign * (orientation == GeometrySign.Positive ? -1 : 1));
                    if (inside == GeometrySign.Unresolved) return inside;
                    if (inside != GeometrySign.Negative) range.Add(point);
                }
        }
        return range.HasPoint ? GeometrySign.Positive : GeometrySign.Negative;
    }

    static FeaturePoint OnPlane(SupportPolygon polygon, FeaturePoint point)
    {
        FeaturePoint first = polygon.Vertices[0];
        if (polygon.Normal.X.IsExact && polygon.Normal.X.Value == 0 &&
            polygon.Normal.Z.IsExact && polygon.Normal.Z.Value == 0) return new(point.X, first.Y, point.Z);
        FeatureNumber height = first.Y.Add(polygon.Normal.X.Multiply(first.X.Subtract(point.X), exactZeroProducts: true)
            .Add(polygon.Normal.Z.Multiply(first.Z.Subtract(point.Z), exactZeroProducts: true)).Divide(polygon.Normal.Y));
        return new(point.X, height, point.Z);
    }

    static FeatureNumber DotXZ(FeaturePoint a, FeaturePoint b) => a.X.Multiply(b.X, exactZeroProducts: true).Add(a.Z.Multiply(b.Z, exactZeroProducts: true));

    internal static GeometrySign Orientation(SupportPolygon polygon) =>
        SupportReferenceEvaluation.Active ? DeriveOrientation(polygon) : polygon.Orientation;

    internal static GeometrySign DeriveOrientation(SupportPolygon polygon)
    {
        GeometrySign sign = polygon.Normal.Y.Sign;
        return sign == GeometrySign.Unresolved && polygon.HasSource ? polygon.SourcePredicates.NormalComponent(polygon, 1) : sign;
    }

    internal static GeometrySign DeriveNormalArea(SupportPolygon polygon)
    {
        GeometrySign area = FeaturePoint.Dot(polygon.Normal, polygon.Normal).Sign;
        return area == GeometrySign.Unresolved && polygon.HasSource ? polygon.SourcePredicates.NormalSquared(polygon) : area;
    }

    static GeometrySign InDisc(SupportPolygon polygon, int index, FeaturePoint point, FeaturePoint axis,
        FeatureNumber radius, bool originalVertex = false)
    {
        GeometrySign sign = originalVertex
            ? InDisc(point, axis, radius, polygon.FootprintOperands(index, axis).ProjectedDistanceSquared)
            : InDisc(point, axis, radius);
        return sign == GeometrySign.Unresolved && polygon.HasSource ? polygon.SourcePredicates.VertexDisc(polygon, index, axis, radius) : sign;
    }

    static GeometrySign InDisc(FeaturePoint point, FeaturePoint axis, FeatureNumber radius)
    {
        FeaturePoint delta = FeaturePoint.Subtract(point, axis);
        FeatureNumber squared = delta.X.Multiply(delta.X, exactZeroProducts: true).Add(delta.Z.Multiply(delta.Z, exactZeroProducts: true));
        return InDisc(point, axis, radius, squared);
    }

    static GeometrySign InDisc(FeaturePoint point, FeaturePoint axis, FeatureNumber radius, FeatureNumber squared)
    {
        GeometrySign sign = squared.Compare(radius.Multiply(radius, exactZeroProducts: true));
        if (sign != GeometrySign.Unresolved || !point.X.IsExact || !point.Z.IsExact || !radius.IsExact) return sign;
        SupportQueryDiagnostics.Record(SupportQueryCounter.ExactFallbacks);
        return BoundedGeometryArithmetic.CompareSquaredDistances([point.X.Value, point.Z.Value],
            [axis.X.Value, axis.Z.Value], [radius.Value], [0d]);
    }

    static bool Edge(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber radius, ref Range range)
    {
        FeaturePoint vertex = polygon.Vertices[index], edge = polygon.Edges[index];
        SupportFootprintEdgeOperands operands = polygon.FootprintOperands(index, axis);
        FeatureNumber a = operands.LengthSquared;
        GeometrySign length = a.Sign;
        if (length == GeometrySign.Unresolved && polygon.HasSource)
            length = polygon.SourcePredicates.EdgeLength(polygon, index);
        if (length == GeometrySign.Zero) return true;
        if (length != GeometrySign.Positive) return false;
        FeaturePoint delta = FeaturePoint.Subtract(vertex, axis);
        FeatureNumber dot = operands.Dot;
        // An attained endpoint on the circle makes one root exactly 0 or 1. Factor that root before taking a
        // square root, so independently rounded enclosures cannot obscure a closed endpoint equality.
        if (InDisc(polygon, index, vertex, axis, radius, originalVertex: true) == GeometrySign.Zero)
            return Parameter(polygon, index, axis, dot.Multiply(FeatureNumber.Exact(-2), exactZeroProducts: true).Divide(a), false, ref range);
        if (InDisc(polygon, (index + 1) % polygon.Vertices.Length, FeaturePoint.Add(vertex, edge), axis, radius) == GeometrySign.Zero)
            return Parameter(polygon, index, axis, dot.Multiply(FeatureNumber.Exact(-2), exactZeroProducts: true).Divide(a).Subtract(One), true, ref range);
        FeatureNumber cross = operands.Cross;
        // Parallel projected edges factor the discriminant. This proves closed tangency
        // for every coefficient in the installed operator, without subtracting correlated intervals.
        FeatureNumber discriminant = edge.Z.IsExact && edge.Z.Value == 0
            ? a.Multiply(radius.Multiply(radius, exactZeroProducts: true).Subtract(delta.Z.Multiply(delta.Z, exactZeroProducts: true)), exactZeroProducts: true)
            : edge.X.IsExact && edge.X.Value == 0
                ? a.Multiply(radius.Multiply(radius, exactZeroProducts: true).Subtract(delta.X.Multiply(delta.X, exactZeroProducts: true)), exactZeroProducts: true)
                : a.Multiply(radius.Multiply(radius, exactZeroProducts: true), exactZeroProducts: true).Subtract(cross.Multiply(cross, exactZeroProducts: true));
        GeometrySign sign = discriminant.Sign;
        if (sign == GeometrySign.Unresolved)
        {
            sign = ExactDiscriminant(delta, edge, radius);
            if (sign == GeometrySign.Unresolved && polygon.HasSource)
                sign = polygon.SourcePredicates.EdgeDiscriminant(polygon, index, axis, radius);
            if (sign == GeometrySign.Zero) discriminant = Zero;
            else if (sign == GeometrySign.Positive && discriminant.IsResolved)
                discriminant = FeatureNumber.Enclosed(GeometryInterval.Enclose(Math.Max(0, discriminant.Bounds.Lower),
                    discriminant.Bounds.Upper));
        }
        if (sign == GeometrySign.Negative) return true;
        if (sign == GeometrySign.Unresolved) return false;
        FeatureNumber root = Sqrt(discriminant);
        if (!root.IsResolved) return false;
        for (int direction = -1; direction <= 1; direction += 2)
        {
            FeatureNumber factor = FeatureNumber.Exact(direction);
            GeometrySign low = RadicalSign(dot.Negate(), factor, discriminant, root);
            GeometrySign high = RadicalSign(a.Add(dot), factor.Negate(), discriminant, root);
            if (polygon.HasSource)
            {
                if (low == GeometrySign.Unresolved)
                    low = polygon.SourcePredicates.EdgeRoot(polygon, index, axis, radius, direction, high: false);
                if (high == GeometrySign.Unresolved)
                    high = polygon.SourcePredicates.EdgeRoot(polygon, index, axis, radius, direction, high: true);
            }
            if (low == GeometrySign.Negative || high == GeometrySign.Negative) continue;
            if (low == GeometrySign.Unresolved || high == GeometrySign.Unresolved) return false;
            FeatureNumber t = low == GeometrySign.Zero ? Zero : high == GeometrySign.Zero ? One :
                dot.Negate().Add(root.Multiply(factor, exactZeroProducts: true)).Divide(a);
            FeaturePoint point = EdgePoint(vertex, edge, t);
            if (!point.IsResolved) return false;
            range.Add(point);
            if (sign == GeometrySign.Zero) break;
        }
        return true;
    }

    static bool Parameter(SupportPolygon polygon, int index, FeaturePoint axis, FeatureNumber t, bool endpoint, ref Range range)
    {
        FeaturePoint vertex = polygon.Vertices[index], edge = polygon.Edges[index];
        GeometrySign low = t.Sign, high = One.Subtract(t).Sign;
        if (polygon.HasSource)
        {
            if (low == GeometrySign.Unresolved)
                low = polygon.SourcePredicates.EdgeOtherRoot(polygon, index, axis, endpoint, high: false);
            if (high == GeometrySign.Unresolved)
                high = polygon.SourcePredicates.EdgeOtherRoot(polygon, index, axis, endpoint, high: true);
        }
        if (low == GeometrySign.Negative || high == GeometrySign.Negative) return true;
        if (low == GeometrySign.Unresolved || high == GeometrySign.Unresolved) return false;
        FeaturePoint point = EdgePoint(vertex, edge, t);
        if (!point.IsResolved) return false;
        range.Add(point);
        return true;
    }

    static FeaturePoint EdgePoint(FeaturePoint vertex, FeaturePoint edge, FeatureNumber t)
    {
        FeaturePoint point = FeaturePoint.Add(vertex, FeaturePoint.Scale(edge, t, exactZeroProducts: true));
        // A horizontal edge has constant height even when the circle root is not representable.
        return edge.Y.IsExact && edge.Y.Value == 0 ? new(point.X, vertex.Y, point.Z) : point;
    }

    // Expand (ex*ex + ez*ez)*r*r - (dx*ez - dz*ex)^2 over the original exact operands.
    // Every operand is in the expansion domain, so four factors have a 2^-1008 quantum and stay finite.
    static GeometrySign ExactDiscriminant(FeaturePoint delta, FeaturePoint edge, FeatureNumber radius)
    {
        if (!delta.X.IsExact || !delta.Z.IsExact || !edge.X.IsExact || !edge.Z.IsExact || !radius.IsExact)
            return GeometrySign.Unresolved;
        double dx = delta.X.Value, dz = delta.Z.Value, ex = edge.X.Value, ez = edge.Z.Value, r = radius.Value;
        if (!ExactExpansion.InRange(dx) || !ExactExpansion.InRange(dz) || !ExactExpansion.InRange(ex) ||
            !ExactExpansion.InRange(ez) || !ExactExpansion.InRange(r)) return GeometrySign.Unresolved;
        SupportQueryDiagnostics.Record(SupportQueryCounter.ExactFallbacks);
        var exact = new ExactExpansion(stackalloc double[41]);
        AddFour(ref exact, ex, ex, r, r, 1);
        AddFour(ref exact, ez, ez, r, r, 1);
        AddFour(ref exact, dx, ez, dx, ez, -1);
        AddFour(ref exact, dz, ex, dz, ex, -1);
        AddFour(ref exact, dx, ez, dz, ex, 2);
        return exact.Sign;
    }

    static void AddFour(ref ExactExpansion exact, double a, double b, double c, double d, double sign)
    {
        double left = a * b, leftError = Math.FusedMultiplyAdd(a, b, -left);
        double right = c * d, rightError = Math.FusedMultiplyAdd(c, d, -right);
        exact.AddProduct(sign * left, right);
        exact.AddProduct(sign * left, rightError);
        exact.AddProduct(sign * leftError, right);
        exact.AddProduct(sign * leftError, rightError);
    }

    static GeometrySign Interior(SupportPolygon polygon, FeaturePoint axis, FeaturePoint direction,
        FeatureNumber radius, FeatureNumber squared, FeatureNumber root, GeometrySign orientation, int sourceDirection = 0)
    {
        bool boundary = false;
        for (int i = 0; i < polygon.Edges.Length; i++)
        {
            FeaturePoint edge = polygon.Edges[i], delta = FeaturePoint.Subtract(axis, polygon.Vertices[i]);
            FeatureNumber k = edge.Z.Multiply(delta.X, exactZeroProducts: true).Subtract(edge.X.Multiply(delta.Z, exactZeroProducts: true));
            FeatureNumber l = edge.Z.Multiply(direction.X, exactZeroProducts: true).Subtract(edge.X.Multiply(direction.Z, exactZeroProducts: true)).Multiply(radius, exactZeroProducts: true);
            GeometrySign side = RadicalSign(l, k, squared, root);
            if (side == GeometrySign.Unresolved && polygon.HasSource)
                side = polygon.SourcePredicates.Interior(polygon, i, axis, radius, sourceDirection);
            if (orientation == GeometrySign.Negative)
                side = side == GeometrySign.Positive ? GeometrySign.Negative :
                    side == GeometrySign.Negative ? GeometrySign.Positive : side;
            if (side is GeometrySign.Negative or GeometrySign.Unresolved) return side;
            boundary |= side == GeometrySign.Zero;
        }
        return boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }

    static FeatureNumber Sqrt(FeatureNumber value)
    {
        if (value.IsExact)
        {
            double proposed = Math.Sqrt(value.Value);
            if (BoundedGeometryArithmetic.CompareProducts(proposed, proposed, value.Value, 1) == GeometrySign.Zero)
                return FeatureNumber.Exact(proposed);
        }
        return FeatureNumber.Enclosed(value.Bounds.Sqrt());
    }

    // Exact sign of k + l*sqrt(d). Opposite signs reduce to k*k - l*l*d without a rounded radical.
    static GeometrySign RadicalSign(FeatureNumber k, FeatureNumber l, FeatureNumber d, FeatureNumber root)
    {
        GeometrySign direct = k.Add(l.Multiply(root, exactZeroProducts: true)).Sign;
        if (direct != GeometrySign.Unresolved) return direct;
        GeometrySign ks = k.Sign, ls = l.Sign;
        if (ls == GeometrySign.Zero || d.Sign == GeometrySign.Zero) return ks;
        if (ks == GeometrySign.Zero) return ls;
        if (ks == ls) return ks;
        if (!k.IsExact || !l.IsExact || !d.IsExact || !ExactExpansion.InRange(k.Value) ||
            !ExactExpansion.InRange(l.Value) || !ExactExpansion.InRange(d.Value)) return GeometrySign.Unresolved;
        SupportQueryDiagnostics.Record(SupportQueryCounter.ExactFallbacks);
        double square = l.Value * l.Value, error = Math.FusedMultiplyAdd(l.Value, l.Value, -square);
        if (!ExactExpansion.InRange(square) || !ExactExpansion.InRange(error)) return GeometrySign.Unresolved;
        var exact = new ExactExpansion(stackalloc double[7]);
        exact.AddProduct(k.Value, k.Value);
        exact.AddProduct(-square, d.Value);
        exact.AddProduct(-error, d.Value);
        GeometrySign comparison = exact.Sign;
        return comparison == GeometrySign.Zero ? comparison : comparison == GeometrySign.Positive ? ks : ls;
    }
}
