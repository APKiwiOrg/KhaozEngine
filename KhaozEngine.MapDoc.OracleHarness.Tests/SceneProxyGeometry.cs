using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Oriented proxy triangle multisets, independent of vertex sharing and index storage order.</summary>
[UnsupportedOSPlatform("windows")]
internal static class SceneProxyGeometry
{
    readonly record struct Point(double X, double Y, double Z);
    readonly record struct Vertex(Point Position, Vector3 Normal, Vector4 Colour, Vector2 Uv, Vector4 Tangent);

    internal static bool Equal(GltfMesh a, MapDrawRecord drawA, GltfMesh b, MapDrawRecord drawB)
    {
        if (a.Indices32.Length != b.Indices32.Length || a.Indices32.Length % 3 != 0 ||
            !Vertices(a, drawA, drawA.Anchor, out var left) || !Vertices(b, drawB, drawA.Anchor, out var right)) return false;
        var candidates = new Dictionary<Point, List<(int Triangle, int Corner)>>();
        var consumed = new bool[b.Indices32.Length / 3];
        for (int t = 0; t < b.Indices32.Length; t += 3)
            for (int c = 0; c < 3; c++)
            {
                uint index = b.Indices32[t + c];
                if (index >= right.Length) return false;
                var key = Cell(right[index].Position);
                if (!candidates.TryGetValue(key, out var rows)) candidates.Add(key, rows = []);
                rows.Add((t / 3, c));
            }
        for (int t = 0; t < a.Indices32.Length; t += 3)
        {
            if (a.Indices32[t] >= left.Length || a.Indices32[t + 1] >= left.Length || a.Indices32[t + 2] >= left.Length) return false;
            var cell = Cell(left[a.Indices32[t]].Position);
            bool found = false;
            for (int x = -1; x <= 1 && !found; x++)
                for (int y = -1; y <= 1 && !found; y++)
                    for (int z = -1; z <= 1 && !found; z++)
                        if (candidates.TryGetValue(new(cell.X + x, cell.Y + y, cell.Z + z), out var rows))
                            foreach (var row in rows)
                            {
                                if (consumed[row.Triangle]) continue;
                                bool equal = true;
                                for (int c = 0; c < 3 && equal; c++)
                                    equal = Equal(left[a.Indices32[t + c]], right[b.Indices32[row.Triangle * 3 + (row.Corner + c) % 3]]);
                                if (!equal) continue;
                                consumed[row.Triangle] = found = true;
                                break;
                            }
            if (!found) return false;
        }
        return true;
    }

    static Point Cell(Point p) => new(Math.Floor(p.X / GeometryTolerance), Math.Floor(p.Y / GeometryTolerance), Math.Floor(p.Z / GeometryTolerance));

    static bool Vertices(GltfMesh mesh, MapDrawRecord draw, MapSubmissionAnchor common, out Vertex[] result)
    {
        result = new Vertex[mesh.Vertices.Length];
        var m = draw.LocalTransform;
        if (!Matrix4x4.Invert(m, out var inverse)) return false;
        var normalMatrix = Matrix4x4.Transpose(inverse);
        double x = SceneSubmissionGeometry.Offset(draw.Anchor.X, common.X) + m.M41;
        double y = SceneSubmissionGeometry.Offset(draw.Anchor.Y, common.Y) + m.M42;
        double z = SceneSubmissionGeometry.Offset(draw.Anchor.Z, common.Z) + m.M43;
        for (int i = 0; i < result.Length; i++)
        {
            var v = mesh.Vertices[i]; var p = v.Position;
            var position = new Point(x + (double)p.X * m.M11 + (double)p.Y * m.M21 + (double)p.Z * m.M31,
                y + (double)p.X * m.M12 + (double)p.Y * m.M22 + (double)p.Z * m.M32,
                z + (double)p.X * m.M13 + (double)p.Y * m.M23 + (double)p.Z * m.M33);
            var normal = Vector3.TransformNormal(v.Normal, normalMatrix);
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z) ||
                !float.IsFinite(normal.LengthSquared()) || normal.LengthSquared() == 0) return false;
            result[i] = new(position, Vector3.Normalize(normal), v.Color, v.Uv, v.Tangent);
        }
        return true;
    }

    static bool Equal(Vertex a, Vertex b) => Math.Abs(a.Position.X - b.Position.X) <= GeometryTolerance &&
        Math.Abs(a.Position.Y - b.Position.Y) <= GeometryTolerance && Math.Abs(a.Position.Z - b.Position.Z) <= GeometryTolerance &&
        Math.Abs(a.Normal.X - b.Normal.X) <= NormalTolerance && Math.Abs(a.Normal.Y - b.Normal.Y) <= NormalTolerance &&
        Math.Abs(a.Normal.Z - b.Normal.Z) <= NormalTolerance && Same(a.Colour, b.Colour) && Same(a.Uv, b.Uv) && Same(a.Tangent, b.Tangent);
    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool Same(Vector2 a, Vector2 b) => Same(a.X, b.X) && Same(a.Y, b.Y);
    static bool Same(Vector4 a, Vector4 b) => Same(a.X, b.X) && Same(a.Y, b.Y) && Same(a.Z, b.Z) && Same(a.W, b.W);
}
