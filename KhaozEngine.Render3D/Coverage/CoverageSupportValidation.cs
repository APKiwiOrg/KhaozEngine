using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

internal static class CoverageSupportValidation
{
    internal static Matrix4x4 InvertFrame(CoverageSourceTopology source, Matrix4x4 frame)
    {
        if (!IsFiniteAffine(frame) || frame.GetDeterminant() <= 0
            || !Matrix4x4.Invert(frame, out Matrix4x4 inverse) || !IsFiniteAffine(inverse))
            throw source.Error("Unsupported coverage transform.");
        double norm = LinearNorm(frame), inverseNorm = LinearNorm(inverse);
        if (!double.IsFinite(norm * inverseNorm) || norm * inverseNorm > 1e6) throw source.Error("Ill-conditioned coverage transform.");
        return inverse;
    }

    static bool IsFiniteAffine(in Matrix4x4 matrix)
    {
        if (matrix.M14 != 0 || matrix.M24 != 0 || matrix.M34 != 0 || matrix.M44 != 1) return false;
        ReadOnlySpan<float> values = stackalloc float[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24, matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
        foreach (float value in values)
            if (!float.IsFinite(value)) return false;
        return true;
    }

    static double LinearNorm(Matrix4x4 m) => Math.Sqrt((double)m.M11 * m.M11 + (double)m.M12 * m.M12 + (double)m.M13 * m.M13
        + (double)m.M21 * m.M21 + (double)m.M22 * m.M22 + (double)m.M23 * m.M23
        + (double)m.M31 * m.M31 + (double)m.M32 * m.M32 + (double)m.M33 * m.M33);

    internal static double Cross(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c) => CoverageGeometry.Edge(a, b, c).Estimate;

    internal static int[] Validate(CoverageSourceTopology source, CoverageAffinePoint[] positions, int[] selected)
    {
        var points = new List<CoverageAffinePoint>();
        var sourceVertices = new List<int>();
        var faces = new List<(int A, int B, int C)>();
        var edges = new Dictionary<(int A, int B), List<(int A, int B)>>();
        foreach (int index in selected)
        {
            CoverageSourceTriangle triangle = source.Triangles[index];
            int a = Weld(triangle.A), b = Weld(triangle.B), c = Weld(triangle.C);
            faces.Add((a, b, c));
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        CoverageCoordinate minX = points[0].X, maxX = minX, minY = points[0].Y, maxY = minY;
        foreach (CoverageAffinePoint point in points)
        {
            minX = CoverageGeometry.Min(minX, point.X); maxX = CoverageGeometry.Max(maxX, point.X);
            minY = CoverageGeometry.Min(minY, point.Y); maxY = CoverageGeometry.Max(maxY, point.Y);
        }
        double scale = Math.Max(CoverageGeometry.Difference(maxX, minX).Bounds.Max, CoverageGeometry.Difference(maxY, minY).Bounds.Max);
        double tolerance = scale * scale * 1e-12;
        if (!(tolerance > 0) || !double.IsFinite(tolerance)) throw source.Error("Uncertifiable support scale.");
        int sign = 0;
        foreach (var face in faces)
        {
            CoverageScalar area = CoverageGeometry.Edge(points[face.A], points[face.B], points[face.C]);
            double minimumArea = area.Sign >= 0 ? area.Bounds.Min : -area.Bounds.Max;
            if (!area.IsFinite || minimumArea <= tolerance) throw source.Error("Projected support triangle is degenerate.");
            if (sign != 0 && area.Sign != sign) throw source.Error("Support winding is inconsistent.");
            sign = area.Sign;
        }
        foreach (var edge in edges.Values)
            if (edge.Count > 2 || edge.Count == 2 && edge[0] != (edge[1].B, edge[1].A))
                throw source.Error("Support topology is nonmanifold.");
        var boundary = edges.Values.Where(edge => edge.Count == 1).Select(edge => edge[0]).ToArray();
        if (boundary.Length is < 3 or > CoverageSupportSurface.MaxBoundaryEdges) throw source.Error("Unsupported support boundary capacity.");
        var next = new Dictionary<int, int>();
        var incoming = new HashSet<int>();
        foreach (var edge in boundary)
            if (!next.TryAdd(edge.A, edge.B) || !incoming.Add(edge.B)) throw source.Error("Support boundary is not simple.");
        int start = boundary[0].A, current = start;
        var loop = new List<int>();
        do
        {
            if (loop.Count >= boundary.Length || !next.TryGetValue(current, out int end)) throw source.Error("Support boundary has a gap.");
            loop.Add(current); current = end;
        } while (current != start);
        if (loop.Count != boundary.Length) throw source.Error("Support sheet has a hole or disconnected occurrence.");
        for (int i = 0; i < loop.Count; i++)
            if (Sign(points[loop[i]], points[loop[(i + 1) % loop.Count]], points[loop[(i + 2) % loop.Count]]) * sign < 0)
                throw source.Error("Support boundary is not convex.");
        for (int i = 0; i < faces.Count; i++)
            for (int j = i + 1; j < faces.Count; j++)
            {
                var a = faces[i]; var b = faces[j];
                if (!Separated(a, b) && !Separated(b, a)) throw source.Error("Projected support triangles overlap.");
                foreach (int vertex in new[] { a.A, a.B, a.C }) CheckJunction(vertex, b);
                foreach (int vertex in new[] { b.A, b.B, b.C }) CheckJunction(vertex, a);
            }
        double sumArea = faces.Sum(f => Cross(points[f.A], points[f.B], points[f.C]));
        double boundaryArea = 0;
        for (int i = 1; i + 1 < loop.Count; i++) boundaryArea += Cross(points[loop[0]], points[loop[i]], points[loop[i + 1]]);
        if (Math.Abs(sumArea - boundaryArea) > tolerance) throw source.Error("Support sheet does not cover its boundary.");
        return loop.Select(vertex => sourceVertices[vertex]).ToArray();

        int Weld(int sourceVertex)
        {
            CoverageAffinePoint point = positions[sourceVertex];
            for (int i = 0; i < points.Count; i++)
            {
                if (!CoverageGeometry.SameXY(point, points[i])) continue;
                if (CoverageGeometry.Compare(point.Z, points[i].Z) != 0) throw source.Error("Support has inconsistent shared heights or multiple depths.");
                positions[sourceVertex] = points[i];
                return i;
            }
            int added = points.Count;
            points.Add(point); sourceVertices.Add(sourceVertex); return added;
        }
        void Edge(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!edges.TryGetValue(key, out var list)) edges.Add(key, list = new());
            list.Add((a, b));
        }
        int Sign(CoverageAffinePoint a, CoverageAffinePoint b, CoverageAffinePoint c)
        {
            CoverageScalar value = CoverageGeometry.Edge(a, b, c);
            if (!value.IsFinite) throw source.Error("Support predicate is numerically uncertifiable.");
            return value.Sign;
        }
        bool Separated((int A, int B, int C) a, (int A, int B, int C) b)
        {
            foreach (var edge in new[] { (a.A, a.B), (a.B, a.C), (a.C, a.A) })
                if (Sign(points[edge.Item1], points[edge.Item2], points[b.A]) * sign <= 0
                    && Sign(points[edge.Item1], points[edge.Item2], points[b.B]) * sign <= 0
                    && Sign(points[edge.Item1], points[edge.Item2], points[b.C]) * sign <= 0) return true;
            return false;
        }
        void CheckJunction(int vertex, (int A, int B, int C) face)
        {
            if (vertex == face.A || vertex == face.B || vertex == face.C) return;
            foreach (var edge in new[] { (face.A, face.B), (face.B, face.C), (face.C, face.A) })
            {
                CoverageAffinePoint p = points[vertex], a = points[edge.Item1], b = points[edge.Item2];
                if (Sign(a, b, p) == 0 && CoverageGeometry.Dot(a, p, b, p).Sign < 0)
                    throw source.Error("Support has an unmatched edge vertex.");
            }
        }
    }
}
