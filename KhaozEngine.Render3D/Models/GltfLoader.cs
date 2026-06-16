using System;
using System.Collections.Generic;
using System.Numerics;
using SharpGLTF.Schema2;
// KhaozEngine.Render3D now defines its own Material struct; alias the glTF one to disambiguate.
using GltfMaterial = SharpGLTF.Schema2.Material;

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
            var uvs = new List<Vector2>();
            var indices = new List<ushort>();
            var weld = new Dictionary<(long, long, long), int>();

            int Vertex(Vector3 p, Vector4 color, Vector2 uv)
            {
                var key = ((long)MathF.Round(p.X * 1e4f), (long)MathF.Round(p.Y * 1e4f), (long)MathF.Round(p.Z * 1e4f));
                if (weld.TryGetValue(key, out int idx)) return idx;
                idx = positions.Count;
                positions.Add(p); normals.Add(Vector3.Zero); colors.Add(color); uvs.Add(uv);
                weld[key] = idx;
                return idx;
            }

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue;
                // TEXCOORD_0 if present; otherwise UVs default to Vector2.Zero (SharpGLTF exposes the
                // standard glTF attribute by name, same accessor pattern as POSITION).
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                Vector2 Uv(int i) => texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero;

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    Vector3 p0 = pos[a], p1 = pos[b], p2 = pos[c];
                    Vector3 faceN = Vector3.Cross(p1 - p0, p2 - p0); // area-weighted (un-normalized)
                    int i0 = Vertex(p0, baseColor, Uv(a)), i1 = Vertex(p1, baseColor, Uv(b)), i2 = Vertex(p2, baseColor, Uv(c));
                    normals[i0] += faceN; normals[i1] += faceN; normals[i2] += faceN;
                    indices.Add((ushort)i0); indices.Add((ushort)i1); indices.Add((ushort)i2);
                }
            }
            if (positions.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);

            var verts = new ModelVertex[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 n = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
                verts[i] = new ModelVertex(positions[i], n, colors[i], uvs[i]);
            }
            return new GltfMesh(verts, indices.ToArray());
        }

        static Vector4 ReadBaseColor(GltfMaterial? mat)
        {
            var fallback = new Vector4(0.8f, 0.8f, 0.8f, 1f);
            if (mat == null) return fallback;
            var ch = mat.FindChannel("BaseColor");
            if (ch == null) return fallback;
            return ch.Value.Color;
        }
    }
}
