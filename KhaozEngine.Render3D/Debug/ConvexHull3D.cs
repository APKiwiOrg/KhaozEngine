// KhaozEngine.Render3D/Debug/ConvexHull3D.cs
using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D.Debug;

/// <summary>Dependency-free 3D convex-hull triangulation for debug rendering.</summary>
public static class ConvexHull3D
{
    const float Eps = 1e-6f;

    public static (Vector3[] Vertices, int[] Indices) Triangulate(IReadOnlyList<Vector3> points)
    {
        Vector3[] pts = Dedupe(points);
        if (pts.Length < 4) return (Array.Empty<Vector3>(), Array.Empty<int>());

        // Build an initial tetrahedron from 4 non-coplanar extreme points.
        if (!InitialTetrahedron(pts, out int[] seed))
            return (Array.Empty<Vector3>(), Array.Empty<int>()); // coplanar/collinear

        var faces = new List<(int A, int B, int C)>();
        AddTetraFaces(pts, seed, faces); // 4 faces, each oriented outward vs the tetra centroid

        for (int p = 0; p < pts.Length; p++)
        {
            if (Array.IndexOf(seed, p) >= 0) continue;
            // Collect faces visible from pts[p] (point in front of face plane).
            var visible = new List<int>();
            for (int f = 0; f < faces.Count; f++)
                if (InFront(pts, faces[f], pts[p])) visible.Add(f);
            if (visible.Count == 0) continue; // interior or on-surface point

            // Horizon = edges bordering exactly one visible face. Remove visible faces,
            // then stitch new outward faces from each horizon edge to pts[p].
            var horizon = HorizonEdges(faces, visible);
            RemoveDescending(faces, visible);
            foreach (var (a, b) in horizon)
                faces.Add(OrientOutward(a, b, p));
        }

        return Emit(pts, faces);
    }

    static Vector3[] Dedupe(IReadOnlyList<Vector3> points)
    {
        var seen = new Dictionary<(int, int, int), Vector3>();
        foreach (var q in points)
        {
            var key = ((int)MathF.Round(q.X * 1e5f), (int)MathF.Round(q.Y * 1e5f), (int)MathF.Round(q.Z * 1e5f));
            seen.TryAdd(key, q);
        }
        return new List<Vector3>(seen.Values).ToArray();
    }

    /// <summary>
    /// Picks 4 non-coplanar points to seed the hull: the two points forming the longest
    /// segment among the extreme axis-aligned points, the point farthest from that segment's
    /// line, and the point farthest from the resulting plane.
    /// </summary>
    static bool InitialTetrahedron(Vector3[] pts, out int[] seed)
    {
        seed = Array.Empty<int>();

        // 1) Find the two points with the maximum pairwise distance among the 6
        // axis-extreme points (cheap heuristic that also works fine on small point sets).
        int a = 0, b = 0;
        float best = -1f;
        for (int i = 0; i < pts.Length; i++)
        {
            for (int j = i + 1; j < pts.Length; j++)
            {
                float distSq = Vector3.DistanceSquared(pts[i], pts[j]);
                if (distSq > best) { best = distSq; a = i; b = j; }
            }
        }
        if (best <= Eps) return false; // all points coincident

        // 2) Find the point farthest from the line through a-b.
        int c = -1;
        float bestDist = Eps;
        Vector3 dir = Vector3.Normalize(pts[b] - pts[a]);
        for (int i = 0; i < pts.Length; i++)
        {
            if (i == a || i == b) continue;
            Vector3 ap = pts[i] - pts[a];
            Vector3 proj = ap - Vector3.Dot(ap, dir) * dir;
            float dist = proj.LengthSquared();
            if (dist > bestDist) { bestDist = dist; c = i; }
        }
        if (c < 0) return false; // all points collinear

        // 3) Find the point farthest from the plane through a-b-c.
        Vector3 normal = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
        float normalLen = normal.Length();
        if (normalLen <= Eps) return false; // degenerate (shouldn't happen given step 2)
        normal /= normalLen;

        int d = -1;
        float bestPlaneDist = Eps;
        for (int i = 0; i < pts.Length; i++)
        {
            if (i == a || i == b || i == c) continue;
            float dist = MathF.Abs(Vector3.Dot(pts[i] - pts[a], normal));
            if (dist > bestPlaneDist) { bestPlaneDist = dist; d = i; }
        }
        if (d < 0) return false; // all points coplanar

        seed = new[] { a, b, c, d };
        return true;
    }

    static void AddTetraFaces(Vector3[] pts, int[] seed, List<(int A, int B, int C)> faces)
    {
        int a = seed[0], b = seed[1], c = seed[2], d = seed[3];
        Vector3 centroid = (pts[a] + pts[b] + pts[c] + pts[d]) / 4f;

        faces.Add(OrientOutwardAbout(pts, a, b, c, centroid));
        faces.Add(OrientOutwardAbout(pts, a, b, d, centroid));
        faces.Add(OrientOutwardAbout(pts, a, c, d, centroid));
        faces.Add(OrientOutwardAbout(pts, b, c, d, centroid));
    }

    /// <summary>Orders (a,b,c) so the triangle's normal points away from a reference interior point.</summary>
    static (int A, int B, int C) OrientOutwardAbout(Vector3[] pts, int a, int b, int c, Vector3 interior)
    {
        Vector3 n = Vector3.Cross(pts[b] - pts[a], pts[c] - pts[a]);
        if (Vector3.Dot(n, pts[a] - interior) < 0f) return (a, c, b);
        return (a, b, c);
    }

    /// <summary>Builds a new face stitching a horizon edge (a,b) to the apex point. Horizon
    /// edges come out of <see cref="HorizonEdges"/> already ordered so that (a,b,apex) is
    /// correctly wound: the visible face being replaced had its own edges in outward CCW
    /// order, and the horizon edge inherits that same direction unchanged (only the far
    /// vertex of the visible triangle is swapped out for the new apex point).</summary>
    static (int A, int B, int C) OrientOutward(int a, int b, int apex) => (a, b, apex);

    static bool InFront(Vector3[] pts, (int A, int B, int C) face, Vector3 point)
    {
        Vector3 n = Vector3.Cross(pts[face.B] - pts[face.A], pts[face.C] - pts[face.A]);
        return Vector3.Dot(n, point - pts[face.A]) > Eps;
    }

    /// <summary>Collects the boundary edges of the visible-face region: edges that appear in
    /// exactly one visible face (the other side belongs to a retained face), oriented so they
    /// walk consistently around the horizon (matching the visible face's own winding).</summary>
    static List<(int A, int B)> HorizonEdges(List<(int A, int B, int C)> faces, List<int> visible)
    {
        var edgeCount = new Dictionary<(int, int), int>();

        foreach (int f in visible)
        {
            var (a, b, c) = faces[f];
            AddEdge(edgeCount, a, b);
            AddEdge(edgeCount, b, c);
            AddEdge(edgeCount, c, a);
        }

        var horizon = new List<(int A, int B)>();
        foreach (int f in visible)
        {
            var (a, b, c) = faces[f];
            TryAddHorizon(edgeCount, a, b, horizon);
            TryAddHorizon(edgeCount, b, c, horizon);
            TryAddHorizon(edgeCount, c, a, horizon);
        }
        return horizon;
    }

    static void AddEdge(Dictionary<(int, int), int> edgeCount, int u, int v)
    {
        var key = u < v ? (u, v) : (v, u);
        edgeCount.TryGetValue(key, out int n);
        edgeCount[key] = n + 1;
    }

    static void TryAddHorizon(Dictionary<(int, int), int> edgeCount, int u, int v, List<(int A, int B)> horizon)
    {
        var key = u < v ? (u, v) : (v, u);
        // An edge shared by two visible faces is interior to the removed cap and is not
        // part of the horizon; an edge that appears once borders a retained face.
        if (edgeCount[key] == 1)
            horizon.Add((u, v));
    }

    static void RemoveDescending(List<(int A, int B, int C)> faces, List<int> visible)
    {
        var sorted = new List<int>(visible);
        sorted.Sort();
        for (int k = sorted.Count - 1; k >= 0; k--)
            faces.RemoveAt(sorted[k]);
    }

    static (Vector3[] Vertices, int[] Indices) Emit(Vector3[] pts, List<(int A, int B, int C)> faces)
    {
        var used = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var indices = new int[faces.Count * 3];

        int Remap(int original)
        {
            if (!used.TryGetValue(original, out int newIndex))
            {
                newIndex = vertices.Count;
                vertices.Add(pts[original]);
                used[original] = newIndex;
            }
            return newIndex;
        }

        for (int f = 0; f < faces.Count; f++)
        {
            var (a, b, c) = faces[f];
            indices[f * 3 + 0] = Remap(a);
            indices[f * 3 + 1] = Remap(b);
            indices[f * 3 + 2] = Remap(c);
        }

        return (vertices.ToArray(), indices);
    }
}
