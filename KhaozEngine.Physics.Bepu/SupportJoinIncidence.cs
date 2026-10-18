using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>A sufficient arithmetic certificate for the horizontal common-incidence shortcut.
/// Other geometry keeps the released distance predicate, including its refusal behavior.</summary>
internal static class SupportJoinIncidence
{
    internal const double ContactBand = 0.0001f;
    const double MinimumLength = 1d / 65536;
    static readonly double CoordinateScale = Math.ScaleB(1, 80);

    internal static bool Certify(SupportPolygon polygon)
    {
        FeaturePoint normal = polygon.Normal;
        if (!polygon.IsResolved || polygon.Vertices.Length is < 3 or > 4 ||
            !normal.X.IsExact || !normal.Z.IsExact || normal.X.Value != 0 || normal.Z.Value != 0 ||
            Math.Max(Math.Abs(normal.Y.Bounds.Lower), Math.Abs(normal.Y.Bounds.Upper)) > 524288 ||
            polygon.NormalLength.Lower < MinimumLength)
            return false;
        double height = polygon.Vertices[0].Y.Value;
        bool binary32 = SupportReferenceEvaluation.Active;
        foreach (FeaturePoint vertex in polygon.Vertices)
            if (!vertex.IsExact || vertex.Y.Value != height || !Coordinate(vertex.X.Value, binary32) ||
                !Coordinate(vertex.Y.Value, binary32) || !Coordinate(vertex.Z.Value, binary32)) return false;
        for (int i = 0; i < polygon.Edges.Length; i++)
        {
            FeaturePoint edge = polygon.Edges[i];
            FeaturePoint expected = FeaturePoint.Subtract(polygon.Vertices[(i + 1) % polygon.Vertices.Length],
                polygon.Vertices[i]);
            if (!Same(edge.X, expected.X) || !Same(edge.Y, expected.Y) || !Same(edge.Z, expected.Z) ||
                !edge.Y.IsExact || edge.Y.Value != 0 ||
                Math.Max(MagnitudeLower(edge.X.Bounds), MagnitudeLower(edge.Z.Bounds)) < MinimumLength) return false;
        }
        return true;
    }

    static bool Same(FeatureNumber a, FeatureNumber b) => a.IsResolved && b.IsResolved &&
        a.IsExact == b.IsExact && a.Value == b.Value && a.Bounds.Lower == b.Bounds.Lower && a.Bounds.Upper == b.Bounds.Upper;

    static double MagnitudeLower(GeometryInterval value) =>
        value.Lower > 0 ? value.Lower : value.Upper < 0 ? -value.Upper : 0;

    // Represented coordinates bounded by 2^8 and on the 2^-80 lattice give edge products on the
    // 2^-160 lattice and determinants on the 2^-320 lattice. Outward operations and division
    // stay finite, including uncertain near-parallel strata. Edge and normal denominators
    // are strictly positive. Face rates are zero or enclose zero, so the face-crossing
    // branch never divides by an uncertain rate. The released Measure therefore
    // resolves every edge of these polygons. At a common vertex its minimum lower bound is
    // zero, so incidence proves the same positive verdict without hiding an earlier refusal.
    // None of this needs binary32 coordinates, so a hull installed at an offset centre keeps
    // the certificate. The reference evaluator keeps the binary32 domain, so it still measures
    // such faces with the released kernel.
    static bool Coordinate(double value, bool binary32)
    {
        if (Math.Abs(value) > 256 || (binary32 && (double)(float)value != value)) return false;
        double scaled = value * CoordinateScale;
        return Math.Truncate(scaled) == scaled;
    }

    internal static bool SamePlane(SupportPolygon first, SupportPolygon second) =>
        first.HasJoinIncidenceCertificate && second.HasJoinIncidenceCertificate &&
        first.Vertices[0].Y.Value == second.Vertices[0].Y.Value;

    internal static bool CommonVertex(SupportPolygon first, SupportPolygon second)
    {
        if (!SamePlane(first, second)) return false;
        foreach (FeaturePoint a in first.Vertices)
            foreach (FeaturePoint b in second.Vertices)
                if (a.SameExact(b)) return true;
        return false;
    }
}
