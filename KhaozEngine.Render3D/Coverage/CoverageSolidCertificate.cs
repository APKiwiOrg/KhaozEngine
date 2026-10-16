using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Cold proof of an embedded, consistently oriented closed manifold. Unsupported topology and geometric
/// ambiguity retain query-only compatibility. An interval volume sign chooses the orientation of its embedded shell.</summary>
internal static class CoverageSolidCertificate
{
    internal static bool Inward(CoverageSourceTopology source, CoverageAffinePoint[] projected, int[] selected)
    {
        var ids = new Dictionary<Vector3, int>();
        var weld = new int[source.Positions.Length];
        for (int i = 0; i < weld.Length; i++)
        {
            if (!ids.TryGetValue(source.Positions[i], out int id)) ids.Add(source.Positions[i], id = ids.Count);
            weld[i] = id;
        }
        var edges = new Dictionary<(int, int), List<(int Face, int Direction)>>();
        var links = new Dictionary<int, List<(int, int)>>();
        for (int t = 0; t < source.Triangles.Count; t++)
        {
            CoverageSourceTriangle f = source.Triangles[t];
            int a = weld[f.A], b = weld[f.B], c = weld[f.C];
            if (a == b || b == c || c == a) return false;
            var pa = CoverageBoundedVector.From(source.Positions[f.A]);
            var pb = CoverageBoundedVector.From(source.Positions[f.B]);
            var pc = CoverageBoundedVector.From(source.Positions[f.C]);
            if (!(CoverageBoundedVector.Cross(pb - pa, pc - pa).Length.Min > 0)) return false;
            Edge(a, b, t); Edge(b, c, t); Edge(c, a, t);
            Link(a, b, c); Link(b, c, a); Link(c, a, b);
        }
        if (edges.Values.Any(e => e.Count != 2 || e[0].Direction == e[1].Direction) || links.Values.Any(link => !Cycle(link))) return false;
        Span<CoverageVector> first = stackalloc CoverageVector[3], second = stackalloc CoverageVector[3];
        Span<int> firstIds = stackalloc int[3], secondIds = stackalloc int[3];
        for (int t = 0; t < source.Triangles.Count; t++)
        {
            Fill(t, first, firstIds);
            for (int u = t + 1; u < source.Triangles.Count; u++)
            {
                Fill(u, second, secondIds);
                if (BoxesApart(first, second)) continue;
                if (!CoverageContact.OnlyCommonTopology(first, second, firstIds, secondIds)) return false;
            }
        }
        var component = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(selected[0]);
        while (pending.TryDequeue(out int t))
        {
            if (!component.Add(t)) continue;
            CoverageSourceTriangle f = source.Triangles[t];
            foreach (var key in new[] { Key(weld[f.A], weld[f.B]), Key(weld[f.B], weld[f.C]), Key(weld[f.C], weld[f.A]) })
                foreach (var neighbour in edges[key]) pending.Enqueue(neighbour.Face);
        }
        if (selected.Any(t => !component.Contains(t))) return false;
        CoverageSourceTriangle originFace = source.Triangles[selected[0]];
        CoverageBoundedVector origin = CoverageBoundedVector.From(source.Positions[originFace.A]);
        CoverageScalarBounds volume = CoverageIntervals.Zero;
        foreach (int t in component)
        {
            CoverageSourceTriangle f = source.Triangles[t];
            var a = CoverageBoundedVector.From(source.Positions[f.A]) - origin;
            var b = CoverageBoundedVector.From(source.Positions[f.B]) - origin;
            var c = CoverageBoundedVector.From(source.Positions[f.C]) - origin;
            volume = CoverageIntervals.Add(volume, CoverageBoundedVector.Dot(a, CoverageBoundedVector.Cross(b, c)));
        }
        if (!volume.IsFinite || volume.Min <= 0 && volume.Max >= 0) return false;
        int sign = volume.Min > 0 ? 1 : -1;
        int faceSign = CoverageGeometry.Edge(projected[originFace.A], projected[originFace.B], projected[originFace.C]).Sign;
        if (sign * faceSign >= 0) throw source.Error("Support is the outer side of its displayed solid, not its inward side.");
        return true;

        void Edge(int a, int b, int face)
        {
            var key = Key(a, b);
            if (!edges.TryGetValue(key, out var list)) edges.Add(key, list = new());
            list.Add((face, a < b ? 1 : -1));
        }
        void Link(int vertex, int a, int b)
        {
            if (!links.TryGetValue(vertex, out var list)) links.Add(vertex, list = new());
            list.Add((a, b));
        }
        void Fill(int t, Span<CoverageVector> points, Span<int> groups)
        {
            CoverageSourceTriangle f = source.Triangles[t];
            points[0] = CoverageVector.From(source.Positions[f.A]); points[1] = CoverageVector.From(source.Positions[f.B]); points[2] = CoverageVector.From(source.Positions[f.C]);
            groups[0] = weld[f.A]; groups[1] = weld[f.B]; groups[2] = weld[f.C];
        }
    }

    static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    static bool Cycle(List<(int A, int B)> link)
    {
        var graph = new Dictionary<int, List<int>>();
        foreach (var (a, b) in link) { Add(a, b); Add(b, a); }
        if (graph.Values.Any(adjacent => adjacent.Count != 2)) return false;
        var reached = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(graph.Keys.First());
        while (pending.TryPop(out int v))
            if (reached.Add(v))
                foreach (int next in graph[v]) pending.Push(next);
        return reached.Count == graph.Count;

        void Add(int a, int b)
        {
            if (!graph.TryGetValue(a, out var list)) graph.Add(a, list = new());
            list.Add(b);
        }
    }

    static bool BoxesApart(ReadOnlySpan<CoverageVector> a, ReadOnlySpan<CoverageVector> b)
    {
        for (int k = 0; k < 3; k++)
        {
            double amin = double.PositiveInfinity, amax = double.NegativeInfinity, bmin = amin, bmax = amax;
            for (int i = 0; i < 3; i++)
            {
                double av = Coordinate(a[i], k), bv = Coordinate(b[i], k);
                amin = Math.Min(amin, av); amax = Math.Max(amax, av); bmin = Math.Min(bmin, bv); bmax = Math.Max(bmax, bv);
            }
            if (amin > bmax || bmin > amax) return true;
        }
        return false;
        static double Coordinate(CoverageVector p, int i) => i switch { 0 => p.X, 1 => p.Y, _ => p.Z };
    }
}
