using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Gpu;

// The checker consumes arbitrary finite axes as witnesses. Their source and approximate normalization do not
// affect soundness. Every projection, norm and inequality below uses outward real interval arithmetic.
internal static class CoverageCarryProofGeometry
{
    internal static CoverageBoundedVector[] Triangle(CoverageRefinement t, CoverageCarryBox[] surface, int triangle) =>
        [surface[(int)t.Indices[triangle * 3]].Real, surface[(int)t.Indices[triangle * 3 + 1]].Real,
            surface[(int)t.Indices[triangle * 3 + 2]].Real];

    internal static CoverageBoundedVector[][] Equipment(CoverageCommandPose command)
    {
        if (!CoverageAffine.TryFrom(command.EquipmentWorld, out var a)) throw new ArgumentException("Nonfinite equipment.");
        var affine = CoverageBoundedAffine.From(a);
        var s = command.Support.Source;
        return s.Triangles.Select(t => new[] { affine.Apply(s.Positions[t.A]), affine.Apply(s.Positions[t.B]),
            affine.Apply(s.Positions[t.C]) }).ToArray();
    }

    internal static double Gap(CoverageBoundedVector[] a, CoverageBoundedVector[] b, Vector3 axis)
    {
        if (!float.IsFinite(axis.X) || !float.IsFinite(axis.Y) || !float.IsFinite(axis.Z)) return double.NegativeInfinity;
        var n = CoverageBoundedVector.From(axis);
        var length = n.Length;
        if (!(length.Min > 0)) return double.NegativeInfinity;
        double amin = double.PositiveInfinity, amax = double.NegativeInfinity,
            bmin = double.PositiveInfinity, bmax = double.NegativeInfinity;
        foreach (var v in a) { var p = CoverageBoundedVector.Dot(v, n); amin = Math.Min(amin, p.Min); amax = Math.Max(amax, p.Max); }
        foreach (var v in b) { var p = CoverageBoundedVector.Dot(v, n); bmin = Math.Min(bmin, p.Min); bmax = Math.Max(bmax, p.Max); }
        double gap = Math.Max(Subtract(new(bmin, bmin), new(amax, amax)).Min,
            Subtract(new(amin, amin), new(bmax, bmax)).Min);
        return Divide(new(gap, gap), length).Min;
    }

    internal static double CoordinateGap(CoverageBoundedVector[] a, CoverageBoundedVector[] b, bool xy = false)
    {
        static double Axis(CoverageScalarBounds x0, CoverageScalarBounds x1, CoverageScalarBounds x2,
            CoverageScalarBounds y0, CoverageScalarBounds y1, CoverageScalarBounds y2)
        {
            double xmin = Math.Min(x0.Min, Math.Min(x1.Min, x2.Min)), xmax = Math.Max(x0.Max, Math.Max(x1.Max, x2.Max));
            double ymin = Math.Min(y0.Min, Math.Min(y1.Min, y2.Min)), ymax = Math.Max(y0.Max, Math.Max(y1.Max, y2.Max));
            return Math.Max(Math.BitDecrement(xmin - ymax), Math.BitDecrement(ymin - xmax));
        }
        double g = Math.Max(Axis(a[0].X, a[1].X, a[2].X, b[0].X, b[1].X, b[2].X),
            Axis(a[0].Y, a[1].Y, a[2].Y, b[0].Y, b[1].Y, b[2].Y));
        return xy ? g : Math.Max(g, Axis(a[0].Z, a[1].Z, a[2].Z, b[0].Z, b[1].Z, b[2].Z));
    }

    internal static double Orient(CoverageBoundedVector a, CoverageBoundedVector b, CoverageBoundedVector c) =>
        CoverageBoundedVector.Cross(b - a, c - a).Z.Min;

    internal static int[] Groups(CoverageRefinement t, int triangle) =>
        [t.Vertices[(int)t.Indices[triangle * 3]].PositionGroup, t.Vertices[(int)t.Indices[triangle * 3 + 1]].PositionGroup,
            t.Vertices[(int)t.Indices[triangle * 3 + 2]].PositionGroup];

    internal static double HostMargin(CoverageBoundedVector[] a, CoverageBoundedVector[] b,
        int[] ag, int[] bg, Vector3 axis)
    {
        int[] common = ag.Intersect(bg).ToArray();
        if (common.Length == 0) return Gap(a, b, new(axis.X, axis.Y, 0));
        if (common.Length == 2)
        {
            var p = a[Array.IndexOf(ag, common[0])]; var q = a[Array.IndexOf(ag, common[1])];
            var u = a[Array.FindIndex(ag, g => !common.Contains(g))];
            var v = b[Array.FindIndex(bg, g => !common.Contains(g))];
            var x = CoverageBoundedVector.Cross(q - p, u - p).Z;
            var y = CoverageBoundedVector.Cross(q - p, v - p).Z;
            double gap = Math.Max(Math.Min(x.Min, -y.Max), Math.Min(-x.Max, y.Min));
            var edge = q - p;
            return Divide(new(gap, gap), Sqrt(Add(Square(edge.X), Square(edge.Y)))).Min;
        }
        if (common.Length != 1) return double.NegativeInfinity;
        var origin = a[Array.IndexOf(ag, common[0])];
        var n = CoverageBoundedVector.From(new Vector3(axis.X, axis.Y, 0));
        if (!(n.Length.Min > 0)) return double.NegativeInfinity;
        double amin = double.PositiveInfinity, amax = double.NegativeInfinity,
            bmin = double.PositiveInfinity, bmax = double.NegativeInfinity;
        for (int k = 0; k < 3; k++)
        {
            if (ag[k] != common[0]) { var p = CoverageBoundedVector.Dot(a[k] - origin, n); amin = Math.Min(amin, p.Min); amax = Math.Max(amax, p.Max); }
            if (bg[k] != common[0]) { var p = CoverageBoundedVector.Dot(b[k] - origin, n); bmin = Math.Min(bmin, p.Min); bmax = Math.Max(bmax, p.Max); }
        }
        return Divide(new(Math.Max(Math.Min(amin, -bmax), Math.Min(-amax, bmin)),
            Math.Max(Math.Min(amin, -bmax), Math.Min(-amax, bmin))), n.Length).Min;
    }

    internal static IEnumerable<Vector3> Axes(CoverageBoundedVector[] a, CoverageBoundedVector[] b, bool xy)
    {
        static Vector3 Mid(CoverageBoundedVector p) => new((float)p.X.Midpoint, (float)p.Y.Midpoint, (float)p.Z.Midpoint);
        var x = a.Select(Mid).ToArray(); var y = b.Select(Mid).ToArray();
        yield return Vector3.UnitX; yield return Vector3.UnitY;
        if (!xy) yield return Vector3.UnitZ;
        yield return (x[0] + x[1] + x[2]) - (y[0] + y[1] + y[2]);
        var normals = new List<Vector3>();
        for (int k = 0; k < 3; k++)
        {
            var e = x[(k + 1) % 3] - x[k]; var f = y[(k + 1) % 3] - y[k];
            if (xy) { normals.Add(new(-e.Y, e.X, 0)); normals.Add(new(-f.Y, f.X, 0)); }
            else
                for (int j = 0; j < 3; j++) yield return Vector3.Cross(e, y[(j + 1) % 3] - y[j]);
        }
        if (!xy)
        {
            yield return Vector3.Cross(x[1] - x[0], x[2] - x[0]);
            yield return Vector3.Cross(y[1] - y[0], y[2] - y[0]);
            foreach (var p in x)
                foreach (var q in y) yield return p - q;
            for (int k = 0; k < 3; k++)
                for (int j = 0; j < 3; j++)
                {
                    yield return Residual(x[k], y[j], y[(j + 1) % 3]);
                    yield return Residual(y[k], x[j], x[(j + 1) % 3]);
                }
        }
        foreach (var n in normals) yield return n;
        // Bisectors allow a strict separator through a shared vertex, including opposite collinear rays.
        foreach (var n in normals)
            foreach (var m in normals)
            {
                if (n.LengthSquared() == 0 || m.LengthSquared() == 0) continue;
                yield return Vector3.Normalize(n) + Vector3.Normalize(m);
                yield return Vector3.Normalize(n) - Vector3.Normalize(m);
            }
    }

    static Vector3 Residual(Vector3 p, Vector3 a, Vector3 b)
    {
        var e = b - a;
        float length = e.LengthSquared();
        float t = length > 0 ? Math.Clamp(Vector3.Dot(p - a, e) / length, 0, 1) : 0;
        return p - (a + t * e);
    }

    internal static double SlopeUpper(CoverageSupportSurface support)
    {
        double maximum = 0;
        foreach (var t in support.InwardTriangles)
        {
            var a = CoverageBoundedVector.From(support.ProjectedPositions[t.A]);
            var b = CoverageBoundedVector.From(support.ProjectedPositions[t.B]);
            var c = CoverageBoundedVector.From(support.ProjectedPositions[t.C]);
            var n = CoverageBoundedVector.Cross(b - a, c - a);
            var z = n.Z.Min > 0 ? n.Z : Negate(n.Z);
            if (!(z.Min > 0)) return double.PositiveInfinity;
            maximum = Math.Max(maximum, Divide(Sqrt(Add(Square(n.X), Square(n.Y))), z).Max);
        }
        return maximum;
    }
}
