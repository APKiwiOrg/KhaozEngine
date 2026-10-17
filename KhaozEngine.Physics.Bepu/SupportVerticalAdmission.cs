using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The query-independent part of the vertical admission certificate for one polygon.</summary>
internal enum SupportVerticalDomain : byte
{
    /// <summary>The certificate never decides this polygon.</summary>
    None,
    /// <summary>A sloped edge would give a met feature an enclosed height, so only a disc that meets no edge is
    /// decided.</summary>
    Separation,
    /// <summary>Every edge is vertical or horizontal, so every met feature has the exact height of a vertex and the
    /// column verdict is decided too.</summary>
    Complete,
}

/// <summary>A certified admission before the extrema kernel for a vertical polygon, whose admission reads only where
/// its projected boundary meets the expanded disc. It decides the kernel's admission and column verdict where that
/// kernel resolves, and otherwise leaves the polygon to the kernel.
///
/// Domain. The polygon is resolved with orientation Zero, so the kernel adds no centre or uphill point. Its vertices
/// are exact within 2^40, and each edge is the exact difference of consecutive vertices, with each horizontal
/// component zero or of magnitude in [2^-60, 2^41]. Per query the expanded radius R is exact.
///
/// The kernel then adds the vertices inside the closed disc and the points where each edge of positive projected
/// length crosses the circle, and refuses only when one of its signs does not resolve. Every operation it makes is an
/// exact result or one outward interval step, which moves an endpoint at most 3 2^-53 of its magnitude, and the
/// component bounds keep subnormal terms far below the margins here. Let D bound every horizontal component of a
/// vertex less the axis, and R, and let a be an edge's squared projected length, so its projection is at most 2D. Then
/// the kernel's enclosure of a squared vertex distance less R^2 is narrower than 2^-40 D^2, of a discriminant
/// (a R^2 less the squared cross product, or its factored axis-aligned form) narrower than 2^-45 a D^2, of its root
/// narrower than 2^-22.4 sqrt(a) D, since the root of an interval is no wider than the root of its width, and of each
/// root numerator (-dot plus or minus the root, a + dot less or plus it) narrower than 2^-22 sqrt(a) D.
///
/// A root numerator equals sqrt(a) times the distance from an edge end V to the root's circle point P, and
/// |P - V| is at least |g| / (|V - A| + R) for g = |V - A|^2 - R^2, which is above g / (3D). The certificate requires
/// |g| of at least 2^-14 D^2 at every vertex and a discriminant of magnitude at least 2^-14 a D^2 at every edge. Each
/// root numerator is then at least 2^-16 sqrt(a) D from zero, 64 times its enclosure width, and each discriminant
/// and vertex distance lies more than 2^25 times its enclosure width from zero. No vertex lies on the circle, so the endpoint root
/// factoring never runs. Every kernel sign therefore resolves without a fallback and is the true sign, as is every
/// sign the certificate takes from its own outward intervals. Both find the same met features, and in the complete
/// domain the same exact heights, so both reach the same admission and column verdict.</summary>
internal static class SupportVerticalAdmission
{
    const int MaximumVertices = 64;
    static readonly double MaximumCoordinate = Math.ScaleB(1, 40), MinimumComponent = Math.ScaleB(1, -60),
        MaximumComponent = Math.ScaleB(1, 41);

    /// <summary>The certificate's query-independent domain for one finished polygon. Exact differences only, so
    /// deriving it records no support counter.</summary>
    internal static SupportVerticalDomain Certify(SupportPolygon polygon)
    {
        ReadOnlySpan<FeaturePoint> vertices = polygon.Vertices, edges = polygon.Edges;
        int count = vertices.Length;
        if (!polygon.IsResolved || count < 3 || count > MaximumVertices || polygon.Orientation != GeometrySign.Zero)
            return SupportVerticalDomain.None;
        bool projected = false, complete = true;
        for (int i = 0; i < count; i++)
        {
            FeaturePoint vertex = vertices[i], edge = edges[i];
            if (!Coordinate(vertex) || !edge.IsExact ||
                !SupportContactPrefilter.Chord(vertex, vertices[(i + 1) % count], edge)) return SupportVerticalDomain.None;
            if (edge.X.Value == 0 && edge.Z.Value == 0) continue;
            if (!Component(edge.X.Value) || !Component(edge.Z.Value)) return SupportVerticalDomain.None;
            projected = true;
            complete &= edge.Y.Value == 0;
        }
        return !projected ? SupportVerticalDomain.None
            : complete ? SupportVerticalDomain.Complete : SupportVerticalDomain.Separation;
    }

    /// <summary>The kernel's admission and column verdict for <paramref name="polygon"/> over the disc of exact
    /// <paramref name="radius"/> about the request axis: Negative for a non-member, Positive for a polygon the column
    /// admits, or Unresolved when the certificate does not decide and the kernel must run.</summary>
    internal static GeometrySign Admit(SupportPolygon polygon, in SupportFootprintRequest request, FeatureNumber radius,
        double bottom, double top)
    {
        SupportVerticalDomain domain = polygon.VerticalDomain;
        if (SupportReferenceEvaluation.Active || domain == SupportVerticalDomain.None || !radius.IsExact)
            return GeometrySign.Unresolved;
        ReadOnlySpan<FeaturePoint> vertices = polygon.Vertices, edges = polygon.Edges;
        int count = vertices.Length;
        GeometryInterval axisX = GeometryInterval.Exact(request.Axis.X), axisZ = GeometryInterval.Exact(request.Axis.Y);
        Span<GeometryInterval> dx = stackalloc GeometryInterval[count], dz = stackalloc GeometryInterval[count];
        double scale = radius.Value;
        for (int i = 0; i < count; i++)
        {
            dx[i] = GeometryInterval.Exact(vertices[i].X.Value).Subtract(axisX);
            dz[i] = GeometryInterval.Exact(vertices[i].Z.Value).Subtract(axisZ);
            if (!dx[i].IsResolved || !dz[i].IsResolved) return GeometrySign.Unresolved;
            scale = Math.Max(scale, Math.Max(Magnitude(dx[i]), Magnitude(dz[i])));
        }
        GeometryInterval squaredRadius = GeometryInterval.Exact(radius.Value).Square();
        double margin = Math.ScaleB(GeometryInterval.Exact(scale).Square().Upper, -14);
        bool met = false;
        double lowest = double.PositiveInfinity, highest = double.NegativeInfinity;
        ulong inside = 0;
        for (int i = 0; i < count; i++)
        {
            GeometryInterval g = dx[i].Square().Add(dz[i].Square()).Subtract(squaredRadius);
            if (!g.IsResolved) return GeometrySign.Unresolved;
            if (g.Lower >= margin) continue;
            if (g.Upper > -margin || domain != SupportVerticalDomain.Complete) return GeometrySign.Unresolved;
            inside |= 1UL << i;
            Meet(vertices[i].Y.Value, ref met, ref lowest, ref highest);
        }
        for (int i = 0; i < count; i++)
        {
            FeaturePoint edge = edges[i];
            if (edge.X.Value == 0 && edge.Z.Value == 0) continue;
            GeometryInterval ex = GeometryInterval.Exact(edge.X.Value), ez = GeometryInterval.Exact(edge.Z.Value);
            GeometryInterval a = ex.Square().Add(ez.Square());
            GeometryInterval cross = dx[i].Multiply(ez).Subtract(dz[i].Multiply(ex));
            GeometryInterval discriminant = a.Multiply(squaredRadius).Subtract(cross.Square());
            if (!discriminant.IsResolved) return GeometrySign.Unresolved;
            double clearance = Math.BitIncrement(a.Upper * margin);
            if (discriminant.Lower < clearance && discriminant.Upper > -clearance) return GeometrySign.Unresolved;
            if (discriminant.Upper < 0) continue;
            bool ends = (inside & (1UL << i | 1UL << (i + 1) % count)) != 0;
            if (!ends)
            {
                // Both ends lie outside, so the line's chord meets the edge exactly when the foot of the axis lies
                // strictly between them.
                GeometryInterval dot = dx[i].Multiply(ex).Add(dz[i].Multiply(ez)), far = a.Add(dot);
                if (dot.Lower > 0 || far.Upper < 0) continue;
                if (dot.Upper >= 0 || far.Lower <= 0) return GeometrySign.Unresolved;
                if (domain != SupportVerticalDomain.Complete) return GeometrySign.Unresolved;
            }
            Meet(vertices[i].Y.Value, ref met, ref lowest, ref highest);
        }
        SupportQueryDiagnostics.Record(SupportQueryCounter.AdmissionCertificates);
        return !met || lowest > top || highest < bottom ? GeometrySign.Negative : GeometrySign.Positive;
    }

    static void Meet(double height, ref bool met, ref double lowest, ref double highest)
    {
        met = true;
        lowest = Math.Min(lowest, height);
        highest = Math.Max(highest, height);
    }

    static double Magnitude(GeometryInterval value) => Math.Max(Math.Abs(value.Lower), Math.Abs(value.Upper));

    static bool Coordinate(FeaturePoint point) => point.IsExact && Math.Abs(point.X.Value) <= MaximumCoordinate &&
        Math.Abs(point.Y.Value) <= MaximumCoordinate && Math.Abs(point.Z.Value) <= MaximumCoordinate;

    static bool Component(double value) =>
        value == 0 || (Math.Abs(value) >= MinimumComponent && Math.Abs(value) <= MaximumComponent);
}
