using System;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Finite feature pairs. Endpoint/interior axis regimes use Gram elimination without division.
/// Singular pairs reduce to endpoint or boundary strata. An invertible three-column pair is an
/// axis/face intersection. Solid endpoint occupancy covers axes wholly inside the solid.</summary>
internal static class CapsuleSweepFeatureDistance
{
    internal readonly record struct Point(int X, int Y, int Z);
    static Point Add(Point a, Point b, SweepExactWorkspace w) => new(w.Add(a.X, b.X), w.Add(a.Y, b.Y), w.Add(a.Z, b.Z));
    static Point Sub(Point a, Point b, SweepExactWorkspace w) => new(w.Subtract(a.X, b.X), w.Subtract(a.Y, b.Y), w.Subtract(a.Z, b.Z));
    static Point Scale(Point a, int b, SweepExactWorkspace w) => new(w.Multiply(a.X, b), w.Multiply(a.Y, b), w.Multiply(a.Z, b));
    static int Dot(Point a, Point b, SweepExactWorkspace w) => w.Add(w.Add(w.Multiply(a.X, b.X), w.Multiply(a.Y, b.Y)), w.Multiply(a.Z, b.Z));
    static Point Cross(Point a, Point b, SweepExactWorkspace w) => new(
        w.Subtract(w.Multiply(a.Y, b.Z), w.Multiply(a.Z, b.Y)),
        w.Subtract(w.Multiply(a.Z, b.X), w.Multiply(a.X, b.Z)),
        w.Subtract(w.Multiply(a.X, b.Y), w.Multiply(a.Y, b.X)));
    static Point Literal(Vector3 v, SweepExactWorkspace w) => new(w.Constant(v.X), w.Constant(v.Y), w.Constant(v.Z));
    static Point Source(FeaturePoint v, SweepExactWorkspace w) => new(w.Source(v.X), w.Source(v.Y), w.Source(v.Z));

    internal static bool TryBuild(in InstalledSweepLeaf leaf, int featureIndex, int axisRegime,
        Vector3 centre, float radius, float halfCylinderLength, Vector3 displacement,
        CapsuleSweepScratch scratch, out SweepFeatureRegime regime)
    {
        regime = default;
        SweepExactWorkspace w = scratch.Exact;
        w.Reset();
        InstalledSweepGeometry geometry = leaf.Geometry;
        if ((uint)featureIndex >= geometry.FeatureCount || (uint)axisRegime > 2) return false;
        int zero = w.Constant(0), one = w.Constant(1), radiusSquared = w.Square(w.Constant(radius));
        if (halfCylinderLength == 0 && axisRegime != 0)
        { regime = new(zero, zero, zero, scratch.Eligibility, 0, true); return w.IsResolved; }
        Point c = Literal(centre, w), d = Literal(displacement, w), axis = Literal(Vector3.UnitY, w);
        Point x = Source(leaf.Map.X, w), y = Source(leaf.Map.Y, w), z = Source(leaf.Map.Z, w);
        Point translation = Literal(leaf.Pose.Position, w);
        if (leaf.Operational)
        {
            // Pinned local-map arithmetic is a transpose, never an inverse. The offset error
            // below encloses the complete rounded subtract/dot graph over the original path.
            Point rx = Source(leaf.Map.RepresentedX, w), ry = Source(leaf.Map.RepresentedY, w), rz = Source(leaf.Map.RepresentedZ, w);
            // Selecting a matrix row is the exact unit-axis dot. Preserve source-proved zero
            // products instead of adding an artificial rounding enclosure to those identities.
            axis = new(rx.Y, ry.Y, rz.Y);
            Point relative = Sub(c, translation, w);
            c = new(Dot(relative, rx, w), Dot(relative, ry, w), Dot(relative, rz, w));
            d = new(Dot(d, rx, w), Dot(d, ry, w), Dot(d, rz, w));
            if (!InstalledSweepBounds.TryOperationalError(leaf, centre, displacement, halfCylinderLength, out double error)) return false;
            int uncertainty = w.Enclosed(GeometryInterval.Enclose(-error, error));
            c = new(w.Add(c.X, uncertainty), w.Add(c.Y, uncertainty), w.Add(c.Z, uncertainty));
            x = Literal(Vector3.UnitX, w); y = Literal(Vector3.UnitY, w); z = Literal(Vector3.UnitZ, w);
            translation = Literal(Vector3.Zero, w);
        }
        Point Vertex(int index)
        {
            Vector3 local = geometry.Vertex(index);
            return Add(Add(Add(Scale(x, w.Constant(local.X), w), Scale(y, w.Constant(local.Y), w), w),
                Scale(z, w.Constant(local.Z), w), w), translation, w);
        }
        Point query = axisRegime == 2 ? c : Add(c, Scale(axis,
            w.Constant(axisRegime == 0 ? -halfCylinderLength : halfCylinderLength), w), w);
        int count = 0;
        void Bound(int constant, int slope)
        {
            if (count >= scratch.Eligibility.Length) { w.Refuse(); return; }
            scratch.Eligibility[count++] = new(constant, slope);
        }
        if (!geometry.IsTriangle && featureIndex == geometry.FeatureCount - 1)
        {
            if (axisRegime == 2)
            { regime = new(zero, zero, zero, scratch.Eligibility, 0, true); return w.IsResolved; }
            for (int face = 0; face < geometry.FaceCount; face++)
            {
                Point a = Vertex(geometry.InteriorFaceVertex(face, 0));
                Point normal = Cross(Sub(Vertex(geometry.InteriorFaceVertex(face, 1)), a, w),
                    Sub(Vertex(geometry.InteriorFaceVertex(face, geometry.InteriorFaceSize(face) - 1)), a, w), w);
                Bound(w.Negate(Dot(normal, Sub(query, a, w), w)), w.Negate(Dot(normal, d, w)));
            }
            regime = new(zero, zero, w.Negate(radiusSquared), scratch.Eligibility, count);
            return w.IsResolved;
        }
        int edgeStart = geometry.SurfaceCount, vertexStart = edgeStart + geometry.EdgeCount;
        int dimension = featureIndex < edgeStart ? 2 : featureIndex < vertexStart ? 1 : 0;
        int anchor = dimension == 2 ? geometry.FaceVertex(featureIndex, 0) : dimension == 1
            ? geometry.Edge(featureIndex - edgeStart).A : featureIndex - vertexStart;
        Point originPoint = Vertex(anchor), offset = Sub(query, originPoint, w);
        Span<Point> columns = stackalloc Point[3];
        if (dimension == 2)
        {
            columns[0] = Sub(Vertex(geometry.FaceVertex(featureIndex, 1)), originPoint, w);
            columns[1] = Sub(Vertex(geometry.FaceVertex(featureIndex, geometry.InteriorFaceSize(featureIndex) - 1)), originPoint, w);
        }
        else if (dimension == 1) columns[0] = Sub(Vertex(geometry.Edge(featureIndex - edgeStart).B), originPoint, w);
        int rank = dimension;
        if (axisRegime == 2) columns[rank++] = Scale(axis, w.Constant(-1), w);
        Span<int> gram = stackalloc int[9], adjugate = stackalloc int[9];
        for (int row = 0; row < rank; row++)
            for (int column = 0; column < rank; column++) gram[row * 3 + column] = Dot(columns[row], columns[column], w);
        int denominator = one;
        Point planeNormal = default;
        if (rank == 1) { denominator = gram[0]; adjugate[0] = one; }
        else if (rank == 2)
        {
            planeNormal = Cross(columns[0], columns[1], w);
            denominator = Dot(planeNormal, planeNormal, w);
            adjugate[0] = gram[4]; adjugate[1] = w.Negate(gram[1]);
            adjugate[3] = w.Negate(gram[3]); adjugate[4] = gram[0];
        }
        else if (rank == 3)
        {
            int volume = Dot(Cross(columns[0], columns[1], w), columns[2], w);
            if (w.Sign(volume) == GeometrySign.Zero)
            { regime = new(zero, zero, zero, scratch.Eligibility, 0, true); return w.IsResolved; }
            denominator = w.Square(volume);
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                {
                    int r0 = (column + 1) % 3, r1 = (column + 2) % 3;
                    int c0 = (row + 1) % 3, c1 = (row + 2) % 3;
                    adjugate[row * 3 + column] = w.Subtract(w.Multiply(gram[r0 * 3 + c0], gram[r1 * 3 + c1]),
                        w.Multiply(gram[r0 * 3 + c1], gram[r1 * 3 + c0]));
                }
        }
        GeometrySign invertible = w.Sign(denominator);
        if (invertible == GeometrySign.Zero)
        { regime = new(zero, zero, zero, scratch.Eligibility, 0, true); return w.IsResolved; }
        if (invertible != GeometrySign.Positive) return false;
        bool endpointRegion = geometry.IsTriangle && axisRegime != 2 && halfCylinderLength > 0;
        Point residual0 = endpointRegion ? Scale(offset, denominator, w) : default;
        Point residual1 = endpointRegion ? Scale(d, denominator, w) : default;
        Span<int> parameters = stackalloc int[4];
        for (int row = 0; row < rank; row++)
        {
            int p0 = zero, p1 = zero;
            for (int column = 0; column < rank; column++)
            {
                p0 = w.Add(p0, w.Multiply(adjugate[row * 3 + column], Dot(columns[column], offset, w)));
                p1 = w.Add(p1, w.Multiply(adjugate[row * 3 + column], Dot(columns[column], d, w)));
            }
            if (endpointRegion)
            {
                residual0 = Sub(residual0, Scale(columns[row], p0, w), w);
                residual1 = Sub(residual1, Scale(columns[row], p1, w), w);
            }
            if (row < dimension)
            {
                if ((geometry.IsHull || geometry.IsTriangle) && dimension == 2)
                { parameters[row * 2] = p0; parameters[row * 2 + 1] = p1; }
                else
                {
                    Bound(p0, p1);
                    Bound(w.Subtract(denominator, p0), w.Negate(p1));
                }
            }
            else
            {
                int h = w.Multiply(w.Constant(halfCylinderLength), denominator);
                Bound(w.Add(h, p0), p1);
                Bound(w.Subtract(h, p0), w.Negate(p1));
            }
        }
        if (endpointRegion)
        {
            // At the lower axis endpoint the distance derivative into the segment is
            // nonnegative. At the upper endpoint it is nonpositive. The eliminated
            // target parameters leave this derivative linear in the original fraction.
            // Every global closest pair satisfies one of these closed endpoint regions
            // or the existing interior-axis regime. This avoids proving redundant cap
            // events without changing the distance, path or any numerical/work ceiling.
            int side0 = Dot(residual0, axis, w), side1 = Dot(residual1, axis, w);
            Bound(axisRegime == 0 ? side0 : w.Negate(side0), axisRegime == 0 ? side1 : w.Negate(side1));
        }
        if ((geometry.IsHull || geometry.IsTriangle) && dimension == 2)
        {
            // Test the original polygon, without artificial fan diagonals. Gram parameters
            // describe its projected closest point with a proved positive denominator.
            // Each wound finite edge contributes one closed linear inequality on that point.
            Point projected0 = Add(Scale(columns[0], parameters[0], w), Scale(columns[1], parameters[2], w), w);
            Point projected1 = Add(Scale(columns[0], parameters[1], w), Scale(columns[1], parameters[3], w), w);
            Point normal = Cross(columns[0], columns[1], w);
            int vertices = geometry.InteriorFaceSize(featureIndex);
            for (int i = 0; i < vertices; i++)
            {
                Point a = Vertex(geometry.FaceVertex(featureIndex, i));
                Point edge = Sub(Vertex(geometry.FaceVertex(featureIndex, (i + 1) % vertices)), a, w);
                Point offset0 = Sub(projected0, Scale(Sub(a, originPoint, w), denominator, w), w);
                Bound(Dot(Cross(edge, offset0, w), normal, w), Dot(Cross(edge, projected1, w), normal, w));
            }
        }
        int coefficientA, coefficientB, coefficientC;
        SweepDistanceTerms? distance = null;
        if (rank == 3)
        {
            // Three independent columns span space, so their exact least-squares residual is
            // identically zero. The original-operand parameter inequalities still carry the map.
            coefficientA = coefficientB = zero; coefficientC = w.Negate(radiusSquared);
        }
        else if (rank == 2)
        {
            int n0 = Dot(offset, planeNormal, w), n1 = Dot(d, planeNormal, w);
            coefficientA = w.Square(n1); coefficientB = w.Multiply(w.Constant(2), w.Multiply(n0, n1));
            coefficientC = w.Subtract(w.Square(n0), w.Multiply(radiusSquared, denominator));
            distance = new(new(n0, n1), default, default, w.Multiply(radiusSquared, denominator), 1);
        }
        else
        {
            Point r0 = rank == 1 ? Cross(offset, columns[0], w) : offset;
            Point r1 = rank == 1 ? Cross(d, columns[0], w) : d;
            coefficientA = Dot(r1, r1, w); coefficientB = w.Multiply(w.Constant(2), Dot(r0, r1, w));
            coefficientC = w.Subtract(Dot(r0, r0, w), w.Multiply(radiusSquared, denominator));
            distance = new(new(r0.X, r1.X), new(r0.Y, r1.Y), new(r0.Z, r1.Z),
                w.Multiply(radiusSquared, denominator), 3);
        }
        regime = new(coefficientA, coefficientB, coefficientC, scratch.Eligibility, count, Distance: distance);
        return w.IsResolved;
    }

}
