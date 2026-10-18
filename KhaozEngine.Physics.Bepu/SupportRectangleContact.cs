using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Exact certificates for axis-aligned rectangles ahead of the contact kernel and the complete
/// lowest-height evaluator. Each returns only what that path would return, bit for bit, and otherwise leaves the
/// member to it. The reference evaluator never uses them.</summary>
internal static class SupportRectangleContact
{
    // 1 + 2^-20. A certificate bound, not a geometric tolerance: an offset beyond this multiple of the radius keeps
    // every outward enclosure of the squared radius difference strictly negative.
    static readonly double SkipScale = 1 + Math.ScaleB(1, -20);

    /// <summary>The collector's contact member verdict for a vertical segment against <paramref name="polygon"/>, or
    /// Unresolved where this cannot prove the kernel path's own resolved verdict.
    ///
    /// Domain. The prefilter's domain (<see cref="SupportContactPrefilter.TryBound"/>), where the kernel never refuses,
    /// with an upward segment. The polygon's four exact vertices are the four distinct corners of its bounding box,
    /// which is flat along exactly one axis, and its exact normal lies along that axis. The convex polygon is then the
    /// box, so its exact distance from the segment is the gap between their boxes.
    ///
    /// Positive. The kernel's least lower bound is at most the exact distance, so an exact distance within the radius
    /// plus the band puts the separation's rounded-down lower bound within the band. Some candidate encloses a
    /// closest pair, so its distance enclosure meets the minimum and it is approached when that pair's segment point
    /// is at most the band below its rectangle point. Closest pairs are level where the vertical spans overlap and
    /// pair the segment top with the rectangle bottom where the rectangle is higher, so a segment top at most the
    /// band below the rectangle bottom approaches.
    ///
    /// Negative. The prefilter's derivation keeps every axis enclosure within half its margin of the segment box and
    /// every geometry enclosure within half of it of the rectangle, so a rectangle bottom above the segment top by
    /// the band plus twice the margin leaves no candidate approached.</summary>
    internal static GeometrySign Sign(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction,
        double radius, double band)
    {
        if (SupportReferenceEvaluation.Active || !double.IsFinite(radius) || radius < 0 || !double.IsFinite(band) ||
            band < 0 || !direction.IsExact || !(direction.Y.Value > 0) || !Rectangle(polygon) ||
            !SupportContactPrefilter.TryBound(polygon, start, direction, out _, out double margin))
            return GeometrySign.Unresolved;
        double x = start.X.Value, bottom = start.Y.Value, z = start.Z.Value;
        // The kernel's segment top is this sum, or exactly its value when that is representable.
        GeometryInterval top = GeometryInterval.Exact(bottom).Add(GeometryInterval.Exact(direction.Y.Value));
        double[] low = polygon.Low, high = polygon.High;
        GeometryInterval above = GeometryInterval.Exact(low[1]).Subtract(top);
        GeometryInterval reach = GeometryInterval.Exact(band).Add(GeometryInterval.Exact(margin))
            .Add(GeometryInterval.Exact(margin));
        if (above.IsResolved && reach.IsResolved && above.Lower > reach.Upper) return Decided(GeometrySign.Negative);
        GeometryInterval approach = top.Subtract(GeometryInterval.Exact(low[1])).Add(GeometryInterval.Exact(band));
        GeometryInterval squared = Gap(GeometryInterval.Exact(x), GeometryInterval.Exact(x), low[0], high[0])
            .Add(Gap(GeometryInterval.Exact(bottom), top, low[1], high[1]))
            .Add(Gap(GeometryInterval.Exact(z), GeometryInterval.Exact(z), low[2], high[2]));
        GeometryInterval contact = GeometryInterval.Exact(radius).Add(GeometryInterval.Exact(band)).Square();
        return approach.IsResolved && approach.Lower >= 0 && squared.IsResolved && contact.IsResolved &&
            squared.Upper <= contact.Lower ? Decided(GeometrySign.Positive) : GeometrySign.Unresolved;
    }

    /// <summary>Whether this certificate decided <paramref name="polygon"/>'s lowest proposal contact height. When it
    /// does, <paramref name="sign"/> and every bit of <paramref name="bounds"/> are those of
    /// <see cref="SupportFootprintProposalGeometry.Lowest"/> with source heights.
    ///
    /// Domain. The exact upward horizontal rectangle of the stair stratum, with an exact upward normal and height in
    /// its operand range, and the proposal axis strictly outside the closed rectangle. The planar fast path then
    /// declines, so the complete evaluator decides. Its interior point is the axis at the plane height, exactly,
    /// and each edge side of it is a single nonzero product of certified operands, so the interior resolves outside
    /// and adds nothing.
    ///
    /// A vertex or edge whose x or z offset exceeds the radius by the skip scale has a squared offset whose outward
    /// enclosure exceeds the squared radius enclosure, so its radicand or discriminant resolves negative and adds
    /// nothing. Every other vertex and edge runs the complete evaluator's own stratum code in the complete order,
    /// and its source clamp runs unchanged, so every bit and refusal is the complete evaluator's.</summary>
    internal static bool TryLowest(SupportPolygon polygon, in SupportFootprintProposal proposal, out GeometrySign sign,
        out GeometryInterval bounds)
    {
        sign = GeometrySign.Unresolved;
        bounds = default;
        if (SupportReferenceEvaluation.Active) return false;
        FeatureNumber normal = polygon.Normal.Y;
        double radius = proposal.Probe.Radius;
        Span<double> dx = stackalloc double[4], dz = stackalloc double[4];
        if (!normal.IsExact || !SupportFootprintPolygon.StairOperand(normal.Value) ||
            !SupportFootprintPolygon.StairRectangle(polygon, proposal.Start.Position.X, proposal.Start.Position.Z,
                FeatureNumber.Exact(radius), dx, dz) ||
            !SupportFootprintPolygon.StairOperand(polygon.Vertices[0].Y.Value) ||
            (SupportFootprintPolygon.BetweenZero(dx[0], dx[2]) && SupportFootprintPolygon.BetweenZero(dz[0], dz[2])))
            return false;
        double clear = radius * SkipScale;
        int vertices = 0, edges = 0;
        for (int i = 0; i < 4; i++)
        {
            if (!(Math.Abs(dx[i]) > clear) && !(Math.Abs(dz[i]) > clear)) vertices |= 1 << i;
            // An edge along x keeps its vertices' z offset, and an edge along z their x offset.
            double offset = polygon.Edges[i].Z.Value == 0 ? dz[i] : dx[i];
            if (!(Math.Abs(offset) > clear)) edges |= 1 << i;
        }
        SupportQueryDiagnostics.Record(SupportQueryCounter.RectangleContact);
        sign = SupportFootprintProposalGeometry.LowestOutside(polygon, proposal, vertices, edges, out bounds);
        return true;
    }

    static GeometrySign Decided(GeometrySign sign)
    {
        SupportQueryDiagnostics.Record(SupportQueryCounter.RectangleContact);
        return sign;
    }

    // Four exact vertices on the four distinct corners of a bounding box flat along exactly one axis, with an exact
    // normal along that axis.
    static bool Rectangle(SupportPolygon polygon)
    {
        ReadOnlySpan<FeaturePoint> vertices = polygon.Vertices;
        if (!polygon.IsResolved || vertices.Length != 4 || !polygon.Normal.IsExact) return false;
        double[] low = polygon.Low, high = polygon.High;
        int flat = -1;
        for (int axis = 0; axis < 3; axis++)
        {
            double normal = Component(polygon.Normal, axis);
            if (low[axis] == high[axis])
            {
                if (flat >= 0 || normal == 0) return false;
                flat = axis;
            }
            else if (!(low[axis] < high[axis]) || normal != 0) return false;
        }
        if (flat < 0) return false;
        int first = flat == 0 ? 1 : 0, second = flat == 2 ? 1 : 2, corners = 0;
        foreach (FeaturePoint vertex in vertices)
        {
            if (!vertex.IsExact) return false;
            int a = Corner(Component(vertex, first), low[first], high[first]);
            int b = Corner(Component(vertex, second), low[second], high[second]);
            if (a < 0 || b < 0) return false;
            corners |= 1 << (a + 2 * b);
        }
        return corners == 15;
    }

    static int Corner(double value, double low, double high) => value == low ? 0 : value == high ? 1 : -1;

    static double Component(FeaturePoint point, int axis) =>
        axis == 0 ? point.X.Value : axis == 1 ? point.Y.Value : point.Z.Value;

    // An upper bound on the squared gap between a span whose ends lie in [from] and [to] and the span [low, high].
    static GeometryInterval Gap(GeometryInterval from, GeometryInterval to, double low, double high)
    {
        GeometryInterval below = GeometryInterval.Exact(low).Subtract(to);
        GeometryInterval beyond = from.Subtract(GeometryInterval.Exact(high));
        return below.IsResolved && beyond.IsResolved
            ? GeometryInterval.Exact(Math.Max(0, Math.Max(below.Upper, beyond.Upper))).Square() : default;
    }
}
