using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

internal static partial class SupportFootprintPolygon
{
    // These are certificate bounds, not geometric tolerances. Products of certified nonzero edge and axis
    // differences stay normal and finite, so the generic interior's single nonzero product has the same sign.
    static readonly double StairMinimum = Math.ScaleB(1, -100), StairMaximum = Math.ScaleB(1, 100);

    static bool TryStairExtrema(SupportPolygon polygon, in SupportFootprintRequest request, FeatureNumber radius,
        ref Range range, out GeometrySign result)
    {
        result = GeometrySign.Unresolved;
        Span<double> dx = stackalloc double[4], dz = stackalloc double[4];
        if (!StairRectangle(polygon, request.Axis.X, request.Axis.Y, radius, dx, dz)) return false;
        SupportQueryDiagnostics.Record(SupportQueryCounter.StairStratum);
        FeaturePoint axis = new(FeatureNumber.Exact(request.Axis.X), Zero, FeatureNumber.Exact(request.Axis.Y));
        // Opposite rectangle corners bracket the centre exactly. OnPlane copies the first vertex's Y when
        // both horizontal normal components are exact zero, including its signed-zero representation.
        if (BetweenZero(dx[0], dx[2]) && BetweenZero(dz[0], dz[2]))
            range.Add(new(axis.X, polygon.Vertices[0].Y, axis.Z));
        Span<GeometrySign> vertices = stackalloc GeometrySign[4];
        for (int i = 0; i < 4; i++)
        {
            // Keep the generic distance expression and source fallback. No edge workspace is needed to form it.
            GeometrySign sign = InDisc(polygon, i, polygon.Vertices[i], axis, radius);
            if (sign == GeometrySign.Unresolved) return true;
            vertices[i] = sign;
            if (sign != GeometrySign.Positive) range.Add(polygon.Vertices[i]);
        }
        FeatureNumber squaredRadius = radius.Multiply(radius, exactZeroProducts: true);
        for (int i = 0; i < 4; i++)
        {
            FeaturePoint edge = polygon.Edges[i];
            // Endpoint equalities run Parameter before the generic discriminant. Preserve that path even
            // at a closed tangent or an endpoint tie. Otherwise the identical negative discriminant adds none.
            if (vertices[i] != GeometrySign.Zero && vertices[(i + 1) % 4] != GeometrySign.Zero)
            {
                FeatureNumber perpendicular = FeatureNumber.Exact(edge.Z.Value == 0 ? dz[i] : dx[i]);
                FeatureNumber discriminant = DotXZ(edge, edge).Multiply(squaredRadius.Subtract(
                    perpendicular.Multiply(perpendicular, exactZeroProducts: true)), exactZeroProducts: true);
                if (discriminant.Sign == GeometrySign.Negative) continue;
            }
            // All contributing roots retain the generic parameter arithmetic, enclosures and Add order.
            if (!Edge(polygon, i, axis, radius, ref range)) return true;
        }
        // The generic uphill direction and its square are exact zero for this normal, so it adds no candidates.
        result = range.HasPoint ? GeometrySign.Positive : GeometrySign.Negative;
        return true;
    }

    /// <summary>Whether <paramref name="polygon"/> is exactly an upward axis-aligned horizontal rectangle and
    /// <paramref name="radius"/> an exact positive radius, all in the certified operand range. Writes each vertex's
    /// exact offsets from the axis at (<paramref name="axisX"/>, <paramref name="axisZ"/>).</summary>
    internal static bool StairRectangle(SupportPolygon polygon, double axisX, double axisZ, FeatureNumber radius,
        Span<double> dx, Span<double> dz)
    {
        if (!polygon.IsResolved || polygon.Vertices.Length != 4 || polygon.Orientation != GeometrySign.Positive ||
            !polygon.Normal.X.IsExact || polygon.Normal.X.Value != 0 ||
            !polygon.Normal.Z.IsExact || polygon.Normal.Z.Value != 0 || !radius.IsExact ||
            radius.Value <= 0 || !StairOperand(radius.Value)) return false;
        ReadOnlySpan<FeaturePoint> vertices = polygon.Vertices, edges = polygon.Edges;
        bool firstAlongX = edges[0].X.IsExact && edges[0].X.Value != 0;
        long height = BitConverter.DoubleToInt64Bits(vertices[0].Y.Value);
        for (int i = 0; i < 4; i++)
        {
            FeaturePoint vertex = vertices[i], next = vertices[(i + 1) % 4], edge = edges[i];
            if (!vertex.IsExact || BitConverter.DoubleToInt64Bits(vertex.Y.Value) != height || !edge.IsExact ||
                edge.Y.Value != 0 || !StairDifference(next.X.Value, vertex.X.Value, out double ex) ||
                !StairDifference(next.Z.Value, vertex.Z.Value, out double ez) ||
                ex != edge.X.Value || ez != edge.Z.Value ||
                !StairDifference(vertex.X.Value, axisX, out dx[i]) ||
                !StairDifference(vertex.Z.Value, axisZ, out dz[i])) return false;
            bool alongX = (i % 2 == 0) == firstAlongX;
            if (alongX ? ex == 0 || ez != 0 : ez == 0 || ex != 0) return false;
        }
        if (edges[2].X.Value != -edges[0].X.Value || edges[2].Z.Value != -edges[0].Z.Value ||
            edges[3].X.Value != -edges[1].X.Value || edges[3].Z.Value != -edges[1].Z.Value) return false;
        // Certify the winding as well as the published normal, including a malformed face with a contrary normal.
        return firstAlongX ? (edges[0].X.Value > 0) != (edges[1].Z.Value > 0)
            : (edges[0].Z.Value > 0) == (edges[1].X.Value > 0);
    }

    static bool StairDifference(double a, double b, out double difference)
    {
        ExactExpansion.TwoSum(a, -b, out difference, out double error);
        return error == 0 && StairOperand(difference);
    }

    internal static bool StairOperand(double value) => value == 0 ||
        Math.Abs(value) >= StairMinimum && Math.Abs(value) <= StairMaximum;

    internal static bool BetweenZero(double a, double b) => a <= 0 && b >= 0 || b <= 0 && a >= 0;
}
