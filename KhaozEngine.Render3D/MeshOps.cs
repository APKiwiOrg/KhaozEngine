using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Mesh post-processing helpers operating on CPU-side <see cref="GltfMesh"/> data (positions / normals /
    /// colours / UVs / indices). Pure, allocation-returning, deterministic.
    /// </summary>
    public static class MeshOps
    {
        /// <summary>
        /// Returns a copy of <paramref name="mesh"/> whose normals are smoothed: all vertices whose positions
        /// coincide (welded by rounding to <paramref name="positionEpsilon"/>) get the averaged, re-normalized
        /// sum of their normals. Positions, colours, UVs and indices are left intact (only the per-vertex Normal
        /// changes), so this turns a flat-shaded mesh (e.g. a faceted <see cref="MeshPrimitives.Box"/>) smooth at
        /// its shared corners without changing its topology.
        /// </summary>
        public static GltfMesh WithSmoothNormals(GltfMesh mesh, float positionEpsilon = 1e-5f)
        {
            if (mesh is null) throw new ArgumentNullException(nameof(mesh));
            if (positionEpsilon <= 0f) positionEpsilon = 1e-5f;
            float inv = 1f / positionEpsilon;

            var verts = mesh.Vertices;
            var groups = new Dictionary<(long, long, long), Vector3>(verts.Length);

            (long, long, long) Key(Vector3 p) => (
                (long)MathF.Round(p.X * inv),
                (long)MathF.Round(p.Y * inv),
                (long)MathF.Round(p.Z * inv));

            // accumulate the (un-normalized) normal sum per welded position.
            foreach (var v in verts)
            {
                var key = Key(v.Position);
                groups.TryGetValue(key, out var sum);
                groups[key] = sum + v.Normal;
            }

            var outVerts = new ModelVertex[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                var sum = groups[Key(v.Position)];
                Vector3 n = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : v.Normal;
                outVerts[i] = new ModelVertex(v.Position, n, v.Color, v.Uv);
            }

            var outIndices = (uint[])mesh.Indices32.Clone();
            return new GltfMesh(outVerts, outIndices);
        }

        /// <summary>
        /// Returns a copy of <paramref name="mesh"/> with per-triangle (flat) normals: every vertex of a triangle
        /// is given that triangle's geometric face normal. Because most meshes share vertices between triangles,
        /// the last triangle touching a vertex wins; for a faceted look feed an un-welded mesh. Positions,
        /// colours, UVs and indices are left intact.
        /// </summary>
        public static GltfMesh RecomputeFlatNormals(GltfMesh mesh)
        {
            if (mesh is null) throw new ArgumentNullException(nameof(mesh));
            var verts = (ModelVertex[])mesh.Vertices.Clone();
            var idx = mesh.Indices32;

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = (int)idx[t], b = (int)idx[t + 1], c = (int)idx[t + 2];
                Vector3 p0 = verts[a].Position, p1 = verts[b].Position, p2 = verts[c].Position;
                Vector3 face = Vector3.Cross(p1 - p0, p2 - p0);
                Vector3 n = face.LengthSquared() > 1e-12f ? Vector3.Normalize(face) : Vector3.UnitY;
                verts[a].Normal = n; verts[b].Normal = n; verts[c].Normal = n;
            }

            return new GltfMesh(verts, (uint[])idx.Clone());
        }

        /// <summary>
        /// Returns a copy of <paramref name="mesh"/> with a per-vertex tangent computed from its UVs + positions
        /// (Lengyel accumulate then Gram-Schmidt against the normal), so a UV-mapped primitive (e.g.
        /// <see cref="MeshPrimitives.Box"/>) can be normal-mapped. A vertex whose faces have no UV gradient keeps a
        /// zero tangent, which the shader reads as "no TBN" (geometric normal). Positions, normals, colours, UVs and
        /// indices are unchanged.
        /// </summary>
        public static GltfMesh WithTangents(GltfMesh mesh)
        {
            if (mesh is null) throw new ArgumentNullException(nameof(mesh));
            var verts = mesh.Vertices;
            var idx = mesh.Indices32;
            var sdir = new Vector3[verts.Length];
            var tdir = new Vector3[verts.Length];

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int a = (int)idx[t], b = (int)idx[t + 1], c = (int)idx[t + 2];
                TangentMath.FaceDirections(
                    verts[a].Position, verts[b].Position, verts[c].Position,
                    verts[a].Uv, verts[b].Uv, verts[c].Uv, out Vector3 s, out Vector3 td);
                sdir[a] += s; sdir[b] += s; sdir[c] += s;
                tdir[a] += td; tdir[b] += td; tdir[c] += td;
            }

            var outVerts = new ModelVertex[verts.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                ModelVertex v = verts[i];
                Vector4 tan = TangentMath.Resolve(v.Normal, sdir[i], tdir[i], null);
                outVerts[i] = new ModelVertex(v.Position, v.Normal, v.Color, v.Uv, tan);
            }
            return new GltfMesh(outVerts, (uint[])idx.Clone());
        }
    }
}
