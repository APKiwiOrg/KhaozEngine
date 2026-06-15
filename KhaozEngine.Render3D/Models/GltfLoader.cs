using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Schema2;

namespace KhaozEngine.Render3D
{
    /// <summary>Loads a glTF/GLB at runtime via SharpGLTF into a flat-shaded <see cref="GltfMesh"/>.</summary>
    public static class GltfLoader
    {
        public static GltfMesh Load(string path)
        {
            ModelRoot root = ModelRoot.Load(path);
            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var colors = new List<Vector4>();
            var indices = new List<ushort>();
            var weld = new Dictionary<(long, long, long), int>();

            int Vertex(Vector3 p, Vector4 color)
            {
                var key = ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));
                if (weld.TryGetValue(key, out int idx)) return idx;
                idx = positions.Count;
                positions.Add(p); normals.Add(Vector3.Zero); colors.Add(color);
                weld[key] = idx;
                return idx;
            }

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue;
                Vector4 baseColor = ReadBaseColor(prim.Material);

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    Vector3 p0 = pos[a], p1 = pos[b], p2 = pos[c];
                    Vector3 faceN = Vector3.Cross(p1 - p0, p2 - p0); // area-weighted (un-normalized)
                    int i0 = Vertex(p0, baseColor), i1 = Vertex(p1, baseColor), i2 = Vertex(p2, baseColor);
                    normals[i0] += faceN; normals[i1] += faceN; normals[i2] += faceN;
                    indices.Add((ushort)i0); indices.Add((ushort)i1); indices.Add((ushort)i2);
                }
            }
            if (positions.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);

            var verts = new ModelVertex[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 n = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
                verts[i] = new ModelVertex(positions[i], n, colors[i]);
            }
            return new GltfMesh(verts, indices.ToArray());
        }

        static Vector4 ReadBaseColor(Material? mat)
        {
            var fallback = new Vector4(0.8f, 0.8f, 0.8f, 1f);
            if (mat == null) return fallback;
            var ch = mat.FindChannel("BaseColor");
            if (ch == null) return fallback;
            return ch.Value.Color;
        }
    }
}
