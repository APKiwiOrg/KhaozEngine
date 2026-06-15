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
            var verts = new List<ModelVertex>();
            var indices = new List<ushort>();

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue;

                Vector4 baseColor = ReadBaseColor(prim.Material);

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    Vector3 p0 = pos[a], p1 = pos[b], p2 = pos[c];
                    Vector3 n = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0)); // flat per-triangle
                    AddVertex(verts, indices, p0, n, baseColor);
                    AddVertex(verts, indices, p1, n, baseColor);
                    AddVertex(verts, indices, p2, n, baseColor);
                }
            }
            if (verts.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);
            return new GltfMesh(verts.ToArray(), indices.ToArray());
        }

        static void AddVertex(List<ModelVertex> verts, List<ushort> indices, Vector3 p, Vector3 n, Vector4 c)
        {
            indices.Add((ushort)verts.Count);
            verts.Add(new ModelVertex(p, n, c));
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
