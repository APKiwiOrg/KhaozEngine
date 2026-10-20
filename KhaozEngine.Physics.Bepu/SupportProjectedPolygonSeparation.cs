using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Orientation-independent separation of the entire convex XZ projection from a closed disc.
/// Midpoints propose lines only. Every exclusion checks all vertex enclosures or their installed sources.</summary>
internal static class SupportProjectedPolygonSeparation
{
    internal static GeometrySign Certify(SupportPolygon polygon, FeatureNumber axisX, FeatureNumber axisZ,
        FeatureNumber radius)
    {
        if (!axisX.IsResolved || !axisZ.IsResolved || !radius.IsResolved ||
            radius.Sign is GeometrySign.Negative or GeometrySign.Unresolved || polygon.Vertices.Length == 0)
            return GeometrySign.Unresolved;
        bool touches = false;
        FeatureNumber squaredRadius = radius.Multiply(radius, exactZeroProducts: true);
        for (int i = 0; i < polygon.Vertices.Length; i++)
        {
            FeaturePoint vertex = polygon.Vertices[i], next = polygon.Vertices[(i + 1) % polygon.Vertices.Length];
            FeatureNumber dx = vertex.X.Subtract(axisX), dz = vertex.Z.Subtract(axisZ);
            GeometrySign distance = Distance(dx, dz, squaredRadius);
            if (distance == GeometrySign.Negative) return distance;
            touches |= distance == GeometrySign.Zero;
            FeatureNumber ex = next.X.Subtract(vertex.X), ez = next.Z.Subtract(vertex.Z);
            double x = Middle(dx), z = Middle(dz), edgeX = Middle(ex), edgeZ = Middle(ez);
            double length = edgeX * edgeX + edgeZ * edgeZ;
            double t = length > 0 ? Math.Clamp(-(x * edgeX + z * edgeZ) / length, 0, 1) : 0;
            if (!double.IsFinite(t)) continue;
            // A represented parameter in [0,1] is an attained point on every source segment. Its outward
            // projection may prove intersection, never separation of the rest of the face.
            FeatureNumber parameter = FeatureNumber.Exact(t);
            GeometrySign segment = Distance(dx.Add(ex.Multiply(parameter, true)),
                dz.Add(ez.Multiply(parameter, true)), squaredRadius);
            if (segment == GeometrySign.Negative) return segment;
            touches |= segment == GeometrySign.Zero;
            GeometrySign line = Halfplane(polygon, x + t * edgeX, z + t * edgeZ, axisX, axisZ, radius);
            if (line == GeometrySign.Positive || (touches && line == GeometrySign.Zero)) return line;
            line = Halfplane(polygon, edgeZ, -edgeX, axisX, axisZ, radius);
            if (line == GeometrySign.Positive || (touches && line == GeometrySign.Zero)) return line;
            line = Halfplane(polygon, -edgeZ, edgeX, axisX, axisZ, radius);
            if (line == GeometrySign.Positive || (touches && line == GeometrySign.Zero)) return line;
        }
        // Failure of a proposed separator says nothing about intersection or orientation.
        return GeometrySign.Unresolved;
    }

    static double Middle(FeatureNumber number) => number.IsResolved
        ? number.Bounds.Lower * 0.5 + number.Bounds.Upper * 0.5 : double.NaN;

    static GeometrySign Distance(FeatureNumber x, FeatureNumber z, FeatureNumber squaredRadius) =>
        x.Multiply(x, true).Add(z.Multiply(z, true)).Compare(squaredRadius);

    static GeometrySign Halfplane(SupportPolygon polygon, double nx, double nz,
        FeatureNumber axisX, FeatureNumber axisZ, FeatureNumber radius)
    {
        if (!double.IsFinite(nx) || !double.IsFinite(nz) || (nx == 0 && nz == 0))
            return GeometrySign.Unresolved;
        FeatureNumber x = FeatureNumber.Exact(nx), z = FeatureNumber.Exact(nz);
        FeatureNumber supportSquared = radius.Multiply(radius, true)
            .Multiply(x.Multiply(x, true).Add(z.Multiply(z, true)), true);
        bool boundary = false;
        foreach (FeaturePoint vertex in polygon.Vertices)
        {
            FeatureNumber projection = vertex.X.Subtract(axisX).Multiply(x, true)
                .Add(vertex.Z.Subtract(axisZ).Multiply(z, true));
            GeometrySign forward = projection.Sign;
            GeometrySign comparison = projection.Multiply(projection, true).Compare(supportSquared);
            if (forward == GeometrySign.Negative || comparison == GeometrySign.Negative) return GeometrySign.Negative;
            if (forward == GeometrySign.Unresolved || comparison == GeometrySign.Unresolved)
                return polygon.HasSource
                    ? polygon.SourcePredicates.ProjectedSeparation(polygon, nx, nz, axisX, axisZ, radius)
                    : GeometrySign.Unresolved;
            boundary |= comparison == GeometrySign.Zero;
        }
        return boundary ? GeometrySign.Zero : GeometrySign.Positive;
    }
}
