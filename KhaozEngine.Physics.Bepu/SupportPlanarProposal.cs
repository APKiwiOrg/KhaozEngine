using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Sufficient certificates for attained interior contacts on bounded horizontal polygons.
/// All other cases keep the complete finite-stratum evaluator, including uncertain source maps.</summary>
internal static class SupportPlanarProposal
{
    internal static bool Contact(SupportPolygon polygon, FeaturePoint start, FeaturePoint direction,
        double radius, double band)
    {
        if (band != SupportJoinIncidence.ContactBand || !Domain(polygon) ||
            !start.IsExact || !direction.IsExact || direction.X.Value != 0 || direction.Z.Value != 0 ||
            direction.Y.Value < 1d / 65536 || !Coordinate(direction.Y.Value) ||
            !Coordinate(start.X.Value) || !Lattice(start.Y.Value) || !Coordinate(start.Z.Value) ||
            !Coordinate(radius) || radius < 0.01f) return false;
        FeaturePoint end = FeaturePoint.Add(start, direction);
        double height = polygon.Vertices[0].Y.Value;
        if (!end.IsExact || !Lattice(end.Y.Value) || start.Y.Value < height ||
            !Inside(polygon, start.X.Value, start.Z.Value)) return false;
        GeometryInterval separation = start.Y.Subtract(FeatureNumber.Exact(height)).Bounds;
        GeometryInterval reach = FeatureNumber.Exact(radius).Add(FeatureNumber.Exact(band)).Bounds;
        // Use a proved upper bound, not a new tolerance. The attained finite point therefore
        // forces the exhaustive minimum's lower bound inside the same inclusive contact band.
        return separation.IsResolved && reach.IsResolved && separation.Upper <= reach.Lower;
    }

    internal static bool Lowest(SupportPolygon polygon, in SupportFootprintProposal proposal,
        bool sourceHeight, out GeometryInterval height)
    {
        height = default;
        if (!sourceHeight || !Domain(polygon) || !IdentitySource(polygon) ||
            !Coordinate(proposal.Probe.Radius) || proposal.Probe.Radius < 0.01f ||
            !Coordinate(proposal.Start.Position.X) || !Coordinate(proposal.Start.Position.Z) ||
            !Inside(polygon, proposal.Start.Position.X, proposal.Start.Position.Z)) return false;
        // The source-certified horizontal plane bounds every sphere-contact stratum from
        // above. Its closed interior point attains that bound, including edge and vertex ties.
        height = GeometryInterval.Exact(polygon.Vertices[0].Y.Value);
        return true;
    }

    static bool Domain(SupportPolygon polygon)
    {
        if (!polygon.HasJoinIncidenceCertificate || polygon.Normal.Y.Sign != GeometrySign.Positive) return false;
        foreach (FeaturePoint edge in polygon.Edges)
            if (!edge.IsExact) return false;
        return true;
    }

    static bool IdentitySource(SupportPolygon polygon)
    {
        if (!polygon.HasSource) return false;
        SupportPolygonSourceMap map = polygon.SourceMap;
        if (!Unit(map.X, 0) || !Unit(map.Y, 1) || !Unit(map.Z, 2)) return false;
        for (int i = 0; i < polygon.SourceVertices.Length; i++)
        {
            Vector3 source = polygon.SourceVertices[i];
            if (!Coordinate(source.X) || !Coordinate(source.Y) || !Coordinate(source.Z)) return false;
            FeaturePoint world = FeaturePoint.Add(FeaturePoint.Exact(source), FeaturePoint.Exact(map.Translation));
            if (!world.SameExact(polygon.Vertices[i])) return false;
        }
        return true;
    }

    static bool Unit(GeometryVector vector, int axis) =>
        Exact(vector.X, axis == 0 ? 1 : 0) && Exact(vector.Y, axis == 1 ? 1 : 0) &&
        Exact(vector.Z, axis == 2 ? 1 : 0);

    static bool Exact(GeometryInterval value, double expected) =>
        value.IsResolved && value.Lower == expected && value.Upper == expected;

    // Exact signs on the represented coordinates. A rounded cast could move a vertex of a hull installed at an
    // offset centre across the axis. The certificate and the axis lattice keep every operand inside the exact
    // expansion domain.
    static bool Inside(SupportPolygon polygon, double x, double z)
    {
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint a = polygon.Vertices[i], b = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            GeometrySign side = BoundedGeometryArithmetic.Orient2D(a.X.Value, a.Z.Value, b.X.Value, b.Z.Value, x, z);
            if (side is GeometrySign.Positive or GeometrySign.Unresolved) return false;
        }
        return true;
    }

    // Together with the polygon certificate, this lattice keeps the exhaustive evaluator
    // finite. Horizontal edge denominators are at least 2^-32. Positive vertical segments
    // have the same bound. Source signs over the singleton identity map have degree at most
    // eight and quantum at least 2^-640, inside the exact expansion domain. Thus skipped
    // vertex, edge, face and proposal strata cannot hide an arithmetic refusal.
    static bool Coordinate(double value)
    {
        return (double)(float)value == value && Lattice(value);
    }

    static bool Lattice(double value)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > 256) return false;
        double scaled = Math.ScaleB(value, 80);
        return Math.Truncate(scaled) == scaled;
    }
}
