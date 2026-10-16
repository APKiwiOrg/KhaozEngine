using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

internal static class CoverageSupportValidation
{
    internal static Matrix4x4 InvertFrame(CoverageSourceTopology source, Matrix4x4 frame)
    {
        float[] values = { frame.M11, frame.M12, frame.M13, frame.M14, frame.M21, frame.M22, frame.M23, frame.M24,
            frame.M31, frame.M32, frame.M33, frame.M34, frame.M41, frame.M42, frame.M43, frame.M44 };
        if (values.Any(value => !float.IsFinite(value)) || frame.M14 != 0 || frame.M24 != 0 || frame.M34 != 0 || frame.M44 != 1
            || frame.GetDeterminant() <= 0 || !Matrix4x4.Invert(frame, out Matrix4x4 inverse))
            throw source.Error("Unsupported coverage transform.");
        double norm = LinearNorm(frame), inverseNorm = LinearNorm(inverse);
        if (!double.IsFinite(norm * inverseNorm) || norm * inverseNorm > 1e6) throw source.Error("Ill-conditioned coverage transform.");
        return inverse;
    }

    static double LinearNorm(Matrix4x4 m) => Math.Sqrt((double)m.M11 * m.M11 + (double)m.M12 * m.M12 + (double)m.M13 * m.M13
        + (double)m.M21 * m.M21 + (double)m.M22 * m.M22 + (double)m.M23 * m.M23
        + (double)m.M31 * m.M31 + (double)m.M32 * m.M32 + (double)m.M33 * m.M33);

    internal static double Cross(Vector3 a, Vector3 b, Vector3 c) =>
        ((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)b.Y - a.Y) * ((double)c.X - a.X);

    internal static CoverageBoundaryEdge[] Validate(CoverageSourceTopology source, Vector3[] positions, int[] selected)
    {
        var points = new List<Vector3>();
        var welded = new Dictionary<Vector2, int>();
        var faces = new List<(int A, int B, int C)>();
        var edges = new Dictionary<(int A, int B), List<(int A, int B)>>();
        double scale = 0;
        foreach (int index in selected)
        {
            CoverageSourceTriangle triangle = source.Triangles[index];
            int a = Weld(positions[triangle.A]), b = Weld(positions[triangle.B]), c = Weld(positions[triangle.C]);
            faces.Add((a, b, c));
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        Vector3 min = points[0], max = min;
        foreach (Vector3 p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        scale = Math.Max((double)max.X - min.X, (double)max.Y - min.Y);
        double tolerance = scale * scale * 1e-12;
        if (!(tolerance > 0) || !double.IsFinite(tolerance)) throw source.Error("Uncertifiable support scale.");
        int sign = 0;
        foreach (var face in faces)
        {
            double area = Cross(points[face.A], points[face.B], points[face.C]);
            if (Math.Abs(area) <= tolerance) throw source.Error("Projected support triangle is degenerate.");
            if (sign != 0 && Math.Sign(area) != sign) throw source.Error("Support winding is inconsistent.");
            sign = Math.Sign(area);
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
            loop.Add(current);
            current = end;
        } while (current != start);
        if (loop.Count != boundary.Length) throw source.Error("Support sheet has a hole or disconnected occurrence.");
        for (int i = 0; i < loop.Count; i++)
            if (Cross(points[loop[i]], points[loop[(i + 1) % loop.Count]], points[loop[(i + 2) % loop.Count]]) * sign < 0)
                throw source.Error("Support boundary is not convex.");
        for (int i = 0; i < faces.Count; i++)
            for (int j = i + 1; j < faces.Count; j++)
            {
                var a = faces[i]; var b = faces[j];
                if (Overlap(points[a.A], points[a.B], points[a.C], points[b.A], points[b.B], points[b.C], sign) > 0)
                    throw source.Error("Projected support triangles overlap.");
                foreach (int vertex in new[] { a.A, a.B, a.C })
                    CheckJunction(vertex, b);
                foreach (int vertex in new[] { b.A, b.B, b.C })
                    CheckJunction(vertex, a);
            }
        double sumArea = faces.Sum(f => Cross(points[f.A], points[f.B], points[f.C]));
        double boundaryArea = 0;
        for (int i = 1; i + 1 < loop.Count; i++) boundaryArea += Cross(points[loop[0]], points[loop[i]], points[loop[i + 1]]);
        if (Math.Abs(sumArea - boundaryArea) > tolerance) throw source.Error("Support sheet does not cover its boundary.");
        return loop.Select((vertex, i) => new CoverageBoundaryEdge(points[vertex], points[loop[(i + 1) % loop.Count]])).ToArray();

        int Weld(Vector3 point)
        {
            var xy = new Vector2(point.X, point.Y);
            if (welded.TryGetValue(xy, out int index))
            {
                if (points[index].Z != point.Z) throw source.Error("Support has inconsistent shared heights or multiple depths.");
                return index;
            }
            int added = points.Count;
            points.Add(point); welded.Add(xy, added); return added;
        }
        void Edge(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!edges.TryGetValue(key, out var list)) edges.Add(key, list = new());
            list.Add((a, b));
        }
        void CheckJunction(int vertex, (int A, int B, int C) face)
        {
            if (vertex == face.A || vertex == face.B || vertex == face.C) return;
            foreach (var edge in new[] { (face.A, face.B), (face.B, face.C), (face.C, face.A) })
            {
                Vector3 p = points[vertex], a = points[edge.Item1], b = points[edge.Item2];
                double dot = ((double)p.X - a.X) * ((double)p.X - b.X) + ((double)p.Y - a.Y) * ((double)p.Y - b.Y);
                if (Math.Abs(Cross(a, b, p)) <= tolerance && dot < 0) throw source.Error("Support has an unmatched edge vertex.");
            }
        }
    }

    // Double-precision convex clipping certifies nonadjacent projected interiors without sampling.
    static double Overlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 e, Vector3 f, int sign)
    {
        var polygon = new List<(double X, double Y)> { (a.X, a.Y), (b.X, b.Y), (c.X, c.Y) };
        foreach (var edge in new[] { (d, e), (e, f), (f, d) })
        {
            var output = new List<(double X, double Y)>();
            for (int i = 0; i < polygon.Count; i++)
            {
                var p = polygon[i]; var q = polygon[(i + 1) % polygon.Count];
                double cp = Side(p), cq = Side(q);
                if (cp >= 0) output.Add(p);
                if ((cp < 0) != (cq < 0))
                {
                    double t = cp / (cp - cq);
                    output.Add((p.X + t * (q.X - p.X), p.Y + t * (q.Y - p.Y)));
                }
            }
            polygon = output;
            double Side((double X, double Y) p) => sign * (((double)edge.Item2.X - edge.Item1.X) * (p.Y - edge.Item1.Y)
                - ((double)edge.Item2.Y - edge.Item1.Y) * (p.X - edge.Item1.X));
        }
        double area = 0;
        for (int i = 1; i + 1 < polygon.Count; i++)
            area += (polygon[i].X - polygon[0].X) * (polygon[i + 1].Y - polygon[0].Y)
                - (polygon[i].Y - polygon[0].Y) * (polygon[i + 1].X - polygon[0].X);
        return Math.Abs(area);
    }
}
