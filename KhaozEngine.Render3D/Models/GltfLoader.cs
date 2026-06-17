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
            var corners = new List<MeshCorner>();

            foreach (var mesh in root.LogicalMeshes)
            foreach (var prim in mesh.Primitives)
            {
                var pos = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (pos == null) continue;
                // NORMAL / TEXCOORD_0 if present (SharpGLTF exposes the standard glTF attributes by name, same
                // accessor pattern as POSITION). Source normals are honoured so the artist's hard edges survive;
                // when absent, MeshAssembler computes a smooth normal from winding.
                var srcNormals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var texcoords = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                Vector4 baseColor = ReadBaseColor(prim.Material);

                Vector3? Norm(int i) => srcNormals != null && i < srcNormals.Count ? srcNormals[i] : (Vector3?)null;
                Vector2 Uv(int i) => texcoords != null && i < texcoords.Count ? texcoords[i] : Vector2.Zero;

                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    corners.Add(new MeshCorner(pos[a], Norm(a), baseColor, Uv(a)));
                    corners.Add(new MeshCorner(pos[b], Norm(b), baseColor, Uv(b)));
                    corners.Add(new MeshCorner(pos[c], Norm(c), baseColor, Uv(c)));
                }
            }
            if (corners.Count == 0) throw new InvalidOperationException("glTF has no triangles: " + path);

            return MeshAssembler.Build(corners);
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
