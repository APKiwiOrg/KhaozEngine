using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D;

// All predicates here are independent double arithmetic over submitted float triangles.
// No production geometry, refinement or certification predicate is used.
internal static class CoverageGeometryOracle
{
    internal sealed record Result(int UnmatchedInteriorEdges, int WeldedPositionChanges,
        int ProtectedPositionChanges, int Folds, int NonadjacentIntersections,
        int ClearanceViolations, double MinimumClearance, double CoreRecession,
        int EquipmentIntersections, double MinimumEquipmentDistance);

    internal static Result Validate(ModelVertex[] before, ModelVertex[] after, CoverageFixture fixture) =>
        Validate(before, after, fixture, fixture.SurfaceIndices, fixture.ProtectedVertices);

    internal static Result Validate(ModelVertex[] before, ModelVertex[] after, CoverageFixture fixture,
        uint[] indices, bool[] protectedVertices)
    {
        if (before.Length != after.Length || protectedVertices.Length != before.Length || indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("The oracle requires complete explicit submitted topology and protection witnesses.");
        int cracks = 0, protectedChanges = 0, folds = 0, intersections = 0, unmatched = 0, violations = 0, equipmentIntersections = 0;
        double minClearance = double.PositiveInfinity, recession = 0, minDistance = double.PositiveInfinity;
        var welds = new Dictionary<Vector3, Vector3>();
        var edgeCounts = new Dictionary<(P, P), int>();
        var originalEdges = new Dictionary<(P, P), int>();
        foreach (var t in fixture.Source.Triangles)
        {
            Add(originalEdges, P.From(fixture.Posed[t.A].Position), P.From(fixture.Posed[t.B].Position));
            Add(originalEdges, P.From(fixture.Posed[t.B].Position), P.From(fixture.Posed[t.C].Position));
            Add(originalEdges, P.From(fixture.Posed[t.C].Position), P.From(fixture.Posed[t.A].Position));
        }
        for (int i = 0; i < before.Length; i++)
        {
            if ((protectedVertices[i] || AuthoredProtected(P.From(before[i].Position), fixture)) && before[i].Position != after[i].Position) protectedChanges++;
            if (welds.TryGetValue(before[i].Position, out var other))
            {
                if (other != after[i].Position) cracks++;
            }
            else welds.Add(before[i].Position, after[i].Position);
        }
        for (int i = 0; i < indices.Length; i += 3)
        {
            P a = P.From(before[indices[i]].Position), b = P.From(before[indices[i + 1]].Position), c = P.From(before[indices[i + 2]].Position);
            P x = P.From(after[indices[i]].Position), y = P.From(after[indices[i + 1]].Position), z = P.From(after[indices[i + 2]].Position);
            Add(edgeCounts, x, y); Add(edgeCounts, y, z); Add(edgeCounts, z, x);
            P normal = Cross(b - a, c - a), finalNormal = Cross(y - x, z - x);
            int owner = FindOwner((a + b + c) / 3, fixture);
            if (Dot(normal, normal) < 1e-24 || Dot(finalNormal, finalNormal) < 1e-24 || Dot(normal, finalNormal) <= 0) folds++;
            else if (owner >= 0)
            {
                var t = fixture.Source.Triangles[owner];
                P originalNormal = Cross(P.From(fixture.Posed[t.B].Position) - P.From(fixture.Posed[t.A].Position),
                    P.From(fixture.Posed[t.C].Position) - P.From(fixture.Posed[t.A].Position));
                if (Dot(originalNormal, normal) <= 0) folds++;
            }
            bool core = false;
            var command = owner >= 0 ? RegionCommand(fixture, owner, out core) : null;
            foreach (var equipment in fixture.Commands)
            {
                double distance = double.PositiveInfinity;
                for (int r = 0; r < fixture.RigidIndices.Length; r += 3)
                {
                    P d = Place(fixture.RigidPositions[fixture.RigidIndices[r]], equipment.CoverageLocal);
                    P e = Place(fixture.RigidPositions[fixture.RigidIndices[r + 1]], equipment.CoverageLocal);
                    P f = Place(fixture.RigidPositions[fixture.RigidIndices[r + 2]], equipment.CoverageLocal);
                    if (Intersect(x, y, z, d, e, f)) equipmentIntersections++;
                    if (command == equipment) distance = Math.Min(distance, TriangleDistance(x, y, z, d, e, f));
                }
                if (command == equipment)
                {
                    minDistance = Math.Min(minDistance, distance);
                    if (distance < equipment.Clearance - 1e-7) violations++;
                }
            }
            if (command is not null && core)
            {
                P lx = Unplace(x, command.CoverageLocal), ly = Unplace(y, command.CoverageLocal), lz = Unplace(z, command.CoverageLocal);
                P la = Unplace(a, command.CoverageLocal), lb = Unplace(b, command.CoverageLocal), lc = Unplace(c, command.CoverageLocal);
                double zScale = Math.Sqrt((double)command.CoverageLocal.M31 * command.CoverageLocal.M31
                    + (double)command.CoverageLocal.M32 * command.CoverageLocal.M32 + (double)command.CoverageLocal.M33 * command.CoverageLocal.M33);
                recession += ((la.Z - lx.Z) + (lb.Z - ly.Z) + (lc.Z - lz.Z)) * zScale / 3 * Math.Sqrt(Dot(normal, normal)) / 2;
                double coveredArea = 0, requiredArea = Math.Abs(Orient(lx, ly, lz)) / 2;
                if (requiredArea < 1e-16) folds++;
                for (int s = 0; s < fixture.SupportIndices.Length; s += 3)
                {
                    P h0 = P.From(fixture.RigidPositions[fixture.SupportIndices[s]]);
                    P h1 = P.From(fixture.RigidPositions[fixture.SupportIndices[s + 1]]);
                    P h2 = P.From(fixture.RigidPositions[fixture.SupportIndices[s + 2]]);
                    var overlay = Clip([lx, ly, lz], h0, h1, h2);
                    if (overlay.Count < 3 || Math.Abs(Area(overlay)) < 1e-16) continue;
                    coveredArea += Math.Abs(Area(overlay));
                    double gap = overlay.Min(p => Height(h0, h1, h2, p.X, p.Y) - Height(lx, ly, lz, p.X, p.Y));
                    minClearance = Math.Min(minClearance, gap);
                    if (gap < command.Clearance / zScale - 1e-7) violations++;
                }
                // An empty overlay is a core escape, never a vacuous clearance success.
                if (coveredArea < requiredArea - Math.Max(1e-14, requiredArea * 1e-9)) violations++;
            }
            for (int j = i + 3; j < indices.Length; j += 3)
            {
                P d = P.From(before[indices[j]].Position), e = P.From(before[indices[j + 1]].Position), f = P.From(before[indices[j + 2]].Position);
                if (new[] { a, b, c }.Any(p => p == d || p == e || p == f)) continue;
                if (Intersect(x, y, z, P.From(after[indices[j]].Position), P.From(after[indices[j + 1]].Position), P.From(after[indices[j + 2]].Position))) intersections++;
            }
        }
        foreach (var edge in edgeCounts)
        {
            if (edge.Value == 2) continue;
            bool boundary = edge.Value == 1 && originalEdges.Any(e => e.Value == 1 && OnSegment(edge.Key.Item1, e.Key.Item1, e.Key.Item2)
                && OnSegment(edge.Key.Item2, e.Key.Item1, e.Key.Item2));
            if (!boundary) unmatched++;
        }
        return new(unmatched, cracks, protectedChanges, folds, intersections, violations, minClearance, recession, equipmentIntersections, minDistance);
    }

    static CoverageFixture.Command? RegionCommand(CoverageFixture fixture, int index, out bool core)
    {
        var t = fixture.Source.Triangles[index];
        foreach (var region in fixture.Map.Regions)
            foreach (var range in region.Triangles)
                if (range.Triangles.Mesh == t.Mesh && range.Triangles.Primitive == t.Primitive && t.Triangle >= range.Triangles.FirstTriangle
                    && t.Triangle < range.Triangles.FirstTriangle + range.Triangles.Count)
                {
                    core = range.Role == CoverageTriangleRole.Core;
                    return fixture.Commands.Single(c => c.Region == region.Key);
                }
        core = false;
        return null;
    }

    static P Place(Vector3 p, Matrix4x4 m) => new((double)p.X * m.M11 + (double)p.Y * m.M21 + (double)p.Z * m.M31 + m.M41,
        (double)p.X * m.M12 + (double)p.Y * m.M22 + (double)p.Z * m.M32 + m.M42,
        (double)p.X * m.M13 + (double)p.Y * m.M23 + (double)p.Z * m.M33 + m.M43);

    static P Unplace(P p, Matrix4x4 m)
    {
        P x = new(m.M11, m.M12, m.M13), y = new(m.M21, m.M22, m.M23), z = new(m.M31, m.M32, m.M33);
        P delta = p - new P(m.M41, m.M42, m.M43); double det = Dot(x, Cross(y, z));
        if (det <= 0 || !double.IsFinite(det)) throw new ArgumentException("Oracle frame must be finite and positive affine.");
        return new(Dot(delta, Cross(y, z)) / det, Dot(x, Cross(delta, z)) / det, Dot(x, Cross(y, delta)) / det);
    }

    static double TriangleDistance(P a, P b, P c, P d, P e, P f)
    {
        if (Intersect(a, b, c, d, e, f)) return 0;
        double squared = Math.Min(Math.Min(PointTriangle(a, d, e, f), PointTriangle(b, d, e, f)), PointTriangle(c, d, e, f));
        squared = Math.Min(squared, Math.Min(Math.Min(PointTriangle(d, a, b, c), PointTriangle(e, a, b, c)), PointTriangle(f, a, b, c)));
        foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
            foreach (var (x, y) in new[] { (d, e), (e, f), (f, d) }) squared = Math.Min(squared, SegmentDistance(u, v, x, y));
        return Math.Sqrt(squared);
    }

    static double PointTriangle(P p, P a, P b, P c)
    {
        P u = b - a, v = c - a, w = p - a, n = Cross(u, v);
        double nn = Dot(n, n);
        if (nn > 0)
        {
            double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), wu = Dot(w, u), wv = Dot(w, v), den = uu * vv - uv * uv;
            double s = (wu * vv - wv * uv) / den, t = (wv * uu - wu * uv) / den;
            if (s >= 0 && t >= 0 && s + t <= 1) return Dot(w, n) * Dot(w, n) / nn;
        }
        return Math.Min(PointSegment(p, a, b), Math.Min(PointSegment(p, b, c), PointSegment(p, c, a)));
    }

    static double PointSegment(P p, P a, P b)
    {
        P v = b - a; double vv = Dot(v, v), t = vv == 0 ? 0 : Math.Clamp(Dot(p - a, v) / vv, 0, 1);
        P delta = p - (a + v * t); return Dot(delta, delta);
    }

    static double SegmentDistance(P a, P b, P c, P d)
    {
        double minimum = Math.Min(Math.Min(PointSegment(a, c, d), PointSegment(b, c, d)), Math.Min(PointSegment(c, a, b), PointSegment(d, a, b)));
        P u = b - a, v = d - c, w = a - c;
        double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), uw = Dot(u, w), vw = Dot(v, w), den = uu * vv - uv * uv;
        if (den > 0)
        {
            double s = (uv * vw - vv * uw) / den, t = (uu * vw - uv * uw) / den;
            if (s >= 0 && s <= 1 && t >= 0 && t <= 1) { P delta = w + u * s - v * t; minimum = Math.Min(minimum, Dot(delta, delta)); }
        }
        return minimum;
    }

    static int FindOwner(P p, CoverageFixture fixture)
    {
        for (int i = 0; i < fixture.Source.Triangles.Count; i++)
        {
            var t = fixture.Source.Triangles[i];
            P a = P.From(fixture.Posed[t.A].Position), b = P.From(fixture.Posed[t.B].Position), c = P.From(fixture.Posed[t.C].Position);
            P u = b - a, v = c - a, w = p - a, n = Cross(u, v);
            double nn = Dot(n, n);
            if (nn == 0 || Math.Abs(Dot(w, n)) > 2e-6 * Math.Sqrt(nn)) continue;
            double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), wu = Dot(w, u), wv = Dot(w, v);
            double den = uu * vv - uv * uv;
            double s = (wu * vv - wv * uv) / den, q = (wv * uu - wu * uv) / den;
            if (s >= -1e-6 && q >= -1e-6 && s + q <= 1 + 1e-6) return i;
        }
        return -1;
    }

    static bool AuthoredProtected(P p, CoverageFixture fixture)
    {
        int owner = FindOwner(p, fixture);
        if (owner < 0) return true;
        var triangle = fixture.Source.Triangles[owner];
        P a = P.From(fixture.Posed[triangle.A].Position), b = P.From(fixture.Posed[triangle.B].Position), c = P.From(fixture.Posed[triangle.C].Position);
        P u = b - a, v = c - a, w = p - a;
        double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), den = uu * vv - uv * uv;
        double wb = (Dot(w, u) * vv - Dot(w, v) * uv) / den, wc = (Dot(w, v) * uu - Dot(w, u) * uv) / den;
        return Math.Abs((1 - wb - wc) * Strength(triangle.A) + wb * Strength(triangle.B) + wc * Strength(triangle.C)) < 1e-6;
        double Strength(int vertex)
        {
            var source = fixture.Source.Vertices[vertex];
            int surface = fixture.Map.Topology.Single(t => t.Mesh == source.Mesh && t.Primitive == source.Primitive && t.Vertex == source.Vertex).SurfaceId;
            return fixture.Map.Regions.SelectMany(r => r.Strengths).Where(s => s.SurfaceId == surface).Sum(s => s.Strength);
        }
    }

    internal static Vector3 ExpectedNormal(uint[] indices, ModelVertex[] before, ModelVertex[] after, int vertex)
    {
        P sum = default;
        for (int i = 0; i < indices.Length; i += 3)
        {
            if (indices[i] != vertex && indices[i + 1] != vertex && indices[i + 2] != vertex) continue;
            P a = P.From(before[indices[i]].Position), u = P.From(before[indices[i + 1]].Position) - a, v = P.From(before[indices[i + 2]].Position) - a;
            P x = P.From(after[indices[i]].Position), s = P.From(after[indices[i + 1]].Position) - x, t = P.From(after[indices[i + 2]].Position) - x;
            P n = Unit(Cross(u, v)), m = Unit(Cross(s, t)), authored = P.From(before[vertex].Normal);
            // Solve F^T * transformedNormal = authored through dot constraints on the before basis.
            double determinant = Dot(s, Cross(t, m));
            P transformed = (Cross(t, m) * Dot(u, authored) + Cross(m, s) * Dot(v, authored) + Cross(s, t) * Dot(n, authored)) / determinant;
            sum += Unit(transformed);
        }
        var result = Unit(sum);
        return new((float)result.X, (float)result.Y, (float)result.Z);
    }

    internal static Vector3 ExpectedTangent(uint[] indices, ModelVertex[] before, ModelVertex[] after, int vertex)
    {
        P sum = default;
        var raw = before[vertex].Tangent; P authored = new(raw.X, raw.Y, raw.Z);
        for (int i = 0; i < indices.Length; i += 3)
        {
            if (indices[i] != vertex && indices[i + 1] != vertex && indices[i + 2] != vertex) continue;
            P a = P.From(before[indices[i]].Position), u = P.From(before[indices[i + 1]].Position) - a, v = P.From(before[indices[i + 2]].Position) - a;
            P x = P.From(after[indices[i]].Position), s = P.From(after[indices[i + 1]].Position) - x, t = P.From(after[indices[i + 2]].Position) - x;
            P n = Unit(Cross(u, v)), m = Unit(Cross(s, t));
            double det = Dot(u, Cross(v, n));
            sum += Unit((s * Dot(authored, Cross(v, n)) + t * Dot(u, Cross(authored, n)) + m * Dot(u, Cross(v, authored))) / det);
        }
        var result = Unit(sum); return new((float)result.X, (float)result.Y, (float)result.Z);
    }

    static void Add(Dictionary<(P, P), int> counts, P a, P b)
    {
        var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
        counts.TryGetValue(key, out int n); counts[key] = n + 1;
    }

    static bool OnSegment(P p, P a, P b)
    {
        P v = b - a, w = p - a;
        double t = Dot(w, v) / Dot(v, v);
        return t >= -1e-6 && t <= 1 + 1e-6 && Dot(w - v * t, w - v * t) < 4e-12;
    }

    static bool Intersect(P a, P b, P c, P d, P e, P f)
    {
        if (Math.Max(a.X, Math.Max(b.X, c.X)) < Math.Min(d.X, Math.Min(e.X, f.X))
            || Math.Max(d.X, Math.Max(e.X, f.X)) < Math.Min(a.X, Math.Min(b.X, c.X))
            || Math.Max(a.Y, Math.Max(b.Y, c.Y)) < Math.Min(d.Y, Math.Min(e.Y, f.Y))
            || Math.Max(d.Y, Math.Max(e.Y, f.Y)) < Math.Min(a.Y, Math.Min(b.Y, c.Y))
            || Math.Max(a.Z, Math.Max(b.Z, c.Z)) < Math.Min(d.Z, Math.Min(e.Z, f.Z))
            || Math.Max(d.Z, Math.Max(e.Z, f.Z)) < Math.Min(a.Z, Math.Min(b.Z, c.Z))) return false;
        P n = Cross(b - a, c - a);
        if (Math.Abs(Dot(n, d - a)) < 1e-12 && Math.Abs(Dot(n, e - a)) < 1e-12 && Math.Abs(Dot(n, f - a)) < 1e-12)
        {
            int axis = Math.Abs(n.X) > Math.Abs(n.Y) ? Math.Abs(n.X) > Math.Abs(n.Z) ? 0 : 2 : Math.Abs(n.Y) > Math.Abs(n.Z) ? 1 : 2;
            P Project(P p) => axis == 0 ? new(p.Y, p.Z, 0) : axis == 1 ? new(p.Z, p.X, 0) : new(p.X, p.Y, 0);
            var polygon = Clip([Project(a), Project(b), Project(c)], Project(d), Project(e), Project(f));
            return polygon.Count >= 3 && Math.Abs(Area(polygon)) > 1e-14;
        }
        return SegmentTriangle(a, b, d, e, f) || SegmentTriangle(b, c, d, e, f) || SegmentTriangle(c, a, d, e, f)
            || SegmentTriangle(d, e, a, b, c) || SegmentTriangle(e, f, a, b, c) || SegmentTriangle(f, d, a, b, c);
    }

    static bool SegmentTriangle(P p, P q, P a, P b, P c)
    {
        P u = b - a, v = c - a, direction = q - p, cross = Cross(direction, v);
        double det = Dot(u, cross);
        if (Math.Abs(det) < 1e-15) return false;
        double s = Dot(p - a, cross) / det, t = Dot(direction, Cross(p - a, u)) / det;
        double along = Dot(v, Cross(p - a, u)) / det;
        return s >= 0 && t >= 0 && s + t <= 1 && along > 1e-10 && along < 1 - 1e-10;
    }

    static List<P> Clip(List<P> polygon, P a, P b, P c)
    {
        double sign = Math.Sign(Orient(a, b, c));
        if (sign == 0) return [];
        foreach (var (u, v) in new[] { (a, b), (b, c), (c, a) })
        {
            var next = new List<P>();
            for (int i = 0; i < polygon.Count; i++)
            {
                P p = polygon[i], q = polygon[(i + 1) % polygon.Count];
                double dp = sign * Orient(u, v, p), dq = sign * Orient(u, v, q);
                if (dp >= 0) next.Add(p);
                if ((dp < 0) != (dq < 0)) next.Add(p + (q - p) * (dp / (dp - dq)));
            }
            polygon = next;
        }
        return polygon;
    }

    static double Height(P a, P b, P c, double x, double y)
    {
        P p = new(x, y, 0); double area = Orient(a, b, c);
        return (a.Z * Orient(b, c, p) + b.Z * Orient(c, a, p) + c.Z * Orient(a, b, p)) / area;
    }

    static double Area(List<P> polygon)
    {
        double sum = 0;
        for (int i = 0; i < polygon.Count; i++) { P a = polygon[i], b = polygon[(i + 1) % polygon.Count]; sum += a.X * b.Y - a.Y * b.X; }
        return sum / 2;
    }

    static double Orient(P a, P b, P c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    static double Dot(P a, P b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static P Cross(P a, P b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static P Unit(P p) => p / Math.Sqrt(Dot(p, p));
    readonly record struct P(double X, double Y, double Z) : IComparable<P>
    {
        public static P From(Vector3 p) => new(p.X, p.Y, p.Z);
        public static P operator +(P a, P b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static P operator -(P a, P b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static P operator *(P p, double s) => new(p.X * s, p.Y * s, p.Z * s);
        public static P operator /(P p, double s) => p * (1 / s);
        public int CompareTo(P other) => (X, Y, Z).CompareTo((other.X, other.Y, other.Z));
    }
}
