using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Typed, oriented primitive comparison. Compound flattening preserves each primitive's complete surface.</summary>
internal static class ShippedBuildingGeometry
{
    const double VertexTolerance = 0.00001, NormalTolerance = 0.000001;
    internal readonly record struct Point(double X, double Y, double Z)
    {
        public static implicit operator Point(Vector3 v) => new(v.X, v.Y, v.Z);
        internal double[] Components => new[] { X, Y, Z };
    }
    internal sealed record Face(int[] Vertices, Point Normal);
    internal sealed record Primitive(string Kind, Point[] Points, Face[] Faces, double? Radius = null);

    internal static IEnumerable<Primitive> Capture(PhysicsShape shape, Func<Vector3, Point> point, Func<Vector3, Vector3> normal)
    {
        if (shape is CompoundShape compound)
        {
            foreach (var child in compound.Children)
                foreach (var primitive in Capture(child.Shape,
                    p => point(child.Local.Position + Vector3.Transform(p, child.Local.Orientation)),
                    n => normal(Vector3.Transform(n, child.Local.Orientation)))) yield return primitive;
            yield break;
        }
        if (shape is BoxShape box)
        {
            var points = (from x in new[] { -box.HalfExtents.X, box.HalfExtents.X }
                          from y in new[] { -box.HalfExtents.Y, box.HalfExtents.Y }
                          from z in new[] { -box.HalfExtents.Z, box.HalfExtents.Z }
                          select point(new(x, y, z))).ToArray();
            int[][] indices = { new[] { 0, 1, 3, 2 }, new[] { 4, 6, 7, 5 }, new[] { 0, 4, 5, 1 },
                new[] { 2, 3, 7, 6 }, new[] { 0, 2, 6, 4 }, new[] { 1, 5, 7, 3 } };
            Vector3[] axes = { -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ };
            yield return new("Box", points, indices.Select((face, i) => new Face(face, normal(axes[i]))).ToArray());
            yield break;
        }
        if (shape is TriangleMeshShape mesh)
        {
            var faces = Enumerable.Range(0, mesh.Indices.Length / 3).Select(i =>
            {
                var indices = mesh.Indices.AsSpan(i * 3, 3).ToArray();
                var n = Vector3.Normalize(Vector3.Cross(mesh.Vertices[indices[1]] - mesh.Vertices[indices[0]],
                    mesh.Vertices[indices[2]] - mesh.Vertices[indices[0]]));
                return new Face(indices, normal(n));
            }).ToArray();
            yield return new("TriangleMesh", mesh.Vertices.Select(point).ToArray(), faces);
            yield break;
        }
        if (shape is ConvexHullShape hull)
        {
            // A convex hull is uniquely determined by its point set. The type supplies closed convex occupancy.
            yield return new("ConvexHull", hull.Points.Select(point).ToArray(), []);
            yield break;
        }
        if (shape is CylinderShape cylinder)
        {
            var start = point(Vector3.Zero); var end = point(new(0, cylinder.Length, 0));
            var radial = point(new(cylinder.Radius, 0, 0));
            double radius = Math.Sqrt(Math.Pow(radial.X - start.X, 2) + Math.Pow(radial.Y - start.Y, 2) + Math.Pow(radial.Z - start.Z, 2));
            yield return new("Cylinder", new[] { start, end }, [], radius);
            yield break;
        }
        throw new InvalidDataException("unsupported comparison shape");
    }

    internal static void Compare(string category, Primitive[] expected, Primitive[] actual, object owner,
        Action<string, bool, object> check, Action<string, double[], double[], double, object> near)
    {
        check(category + "-count", expected.Length == actual.Length, new { owner, expected = expected.Length, actual = actual.Length });
        int[] assigned = Match(expected, actual, Equivalent);
        var matched = assigned.Where(i => i >= 0).ToHashSet();
        for (int i = 0; i < expected.Length; i++)
        {
            if (matched.Contains(i)) continue;
            check(category + "-primitive", false, new { owner, index = i, expected = expected[i], actual });
            // Diagnostics never establish equivalence. Retain exceeded values beside the oriented structures.
            foreach (var point in expected[i].Points)
            {
                var candidates = actual.SelectMany(p => p.Points).OrderBy(p => Error(point, p)).ToArray();
                if (candidates.Length != 0) near(category + "-vertex", point.Components, candidates[0].Components, VertexTolerance, owner);
            }
            foreach (var face in expected[i].Faces)
            {
                var candidates = actual.SelectMany(p => p.Faces).OrderBy(f => Error(face.Normal, f.Normal)).ToArray();
                if (candidates.Length != 0) near(category + "-normal", face.Normal.Components, candidates[0].Normal.Components, NormalTolerance, owner);
            }
        }
        for (int i = 0; i < actual.Length; i++)
            if (assigned[i] < 0) check(category + "-unexpected", false, new { owner, index = i, actual = actual[i] });
    }

    static bool Equivalent(Primitive a, Primitive b)
    {
        if (a.Kind != b.Kind || a.Points.Length != b.Points.Length || a.Faces.Length != b.Faces.Length) return false;
        if (a.Kind == "Cylinder") return a.Radius is { } ar && b.Radius is { } br && Math.Abs(ar - br) <= VertexTolerance &&
            a.Points.Zip(b.Points).All(p => Near(p.First, p.Second, VertexTolerance));
        if (a.Kind == "ConvexHull") return Complete(Match(a.Points, b.Points, (x, y) => Near(x, y, VertexTolerance)), a.Points.Length);
        if (a.Kind == "Box") return Complete(Match(a.Faces, b.Faces, (x, y) => FaceNear(a, x, b, y)), a.Faces.Length);
        if (a.Kind != "TriangleMesh") return false;
        int[] vertexMap = Match(a.Points, b.Points, (x, y) => Near(x, y, VertexTolerance));
        if (!Complete(vertexMap, a.Points.Length)) return false;
        var sourceToActual = new int[a.Points.Length];
        for (int i = 0; i < vertexMap.Length; i++) sourceToActual[vertexMap[i]] = i;
        // The vertex bijection is shared by every triangle, preserving connectivity and winding together.
        return Complete(Match(a.Faces, b.Faces, (x, y) => Near(x.Normal, y.Normal, NormalTolerance) &&
            Cyclic(x.Vertices.Select(i => sourceToActual[i]).ToArray(), y.Vertices, (i, j) => i == j)), a.Faces.Length);
    }

    static bool FaceNear(Primitive a, Face x, Primitive b, Face y) => Near(x.Normal, y.Normal, NormalTolerance) &&
        Cyclic(x.Vertices, y.Vertices, (i, j) => Near(a.Points[i], b.Points[j], VertexTolerance));

    static bool Cyclic<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> same)
    {
        if (a.Count != b.Count) return false;
        for (int shift = 0; shift < b.Count; shift++)
            if (Enumerable.Range(0, a.Count).All(i => same(a[i], b[(i + shift) % b.Count]))) return true;
        return false;
    }
    static bool Complete(int[] assigned, int count) => assigned.Length == count && assigned.All(i => i >= 0);
    static double Error(Point a, Point b) => Math.Max(Math.Abs(a.X - b.X), Math.Max(Math.Abs(a.Y - b.Y), Math.Abs(a.Z - b.Z)));
    static bool Near(Point a, Point b, double tolerance) => double.IsFinite(Error(a, b)) && Error(a, b) <= tolerance;

    /// <summary>Occurrence-preserving augmenting matching. Each actual occurrence is assigned at most once.</summary>
    internal static int[] Match<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, Func<T, T, bool> same)
    {
        if (expected.Count == actual.Count && Enumerable.Range(0, expected.Count).All(i => same(expected[i], actual[i])))
            return Enumerable.Range(0, actual.Count).ToArray();
        var assigned = Enumerable.Repeat(-1, actual.Count).ToArray();
        var candidates = expected.Select(x => Enumerable.Range(0, actual.Count).Where(i => same(x, actual[i])).ToArray()).ToArray();
        bool Assign(int index, bool[] visited)
        {
            foreach (int candidate in candidates[index])
            {
                if (visited[candidate]) continue;
                visited[candidate] = true;
                if (assigned[candidate] < 0 || Assign(assigned[candidate], visited))
                { assigned[candidate] = index; return true; }
            }
            return false;
        }
        for (int i = 0; i < expected.Count; i++) Assign(i, new bool[actual.Count]);
        return assigned;
    }
}
