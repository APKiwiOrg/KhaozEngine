using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One corner of a triangle fed to <see cref="MeshAssembler"/>: a position, an optional source normal
    /// (null = "compute a smooth normal from face winding"), a base color, a UV, and an optional source tangent
    /// (xyz = model-space direction, w = +/-1 handedness; null = compute from UV+position gradient).
    /// </summary>
    internal readonly struct MeshCorner
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;   // meaningful only when HasNormal
        public readonly bool HasNormal;
        public readonly Vector4 Color;
        public readonly Vector2 Uv;
        public readonly Vector4? Tangent;  // source tangent (xyz dir, w handedness); null => compute from UV+pos

        public MeshCorner(Vector3 position, Vector3? normal, Vector4 color, Vector2 uv, Vector4? tangent = null)
        {
            Position = position;
            HasNormal = normal.HasValue;
            Normal = normal ?? default;
            Color = color;
            Uv = uv;
            Tangent = tangent;
        }
    }

    /// <summary>
    /// Welds a triangle-soup of <see cref="MeshCorner"/>s into an indexed <see cref="GltfMesh"/>. Two corners
    /// merge only when their position, normal, AND uv all match (quantized) - so hard edges (distinct normals)
    /// and UV seams (distinct uvs) are preserved, unlike a position-only weld. When a corner has no source
    /// normal, an area-weighted face normal is accumulated across the faces that share it (a smooth default),
    /// and such corners weld on position+uv only. Also computes per-vertex tangents via the Lengyel UV+position
    /// method, accumulated and Gram-Schmidt orthogonalized against the finalized normal. A supplied source
    /// tangent on the corner wins over the computed one. Degenerate UVs (no UV gradient) yield a zero tangent
    /// (the shader falls back to the geometric normal). Emits 32-bit indices and lets <see cref="GltfMesh"/>
    /// pick the GPU index width, so meshes past the 65,536-vertex ceiling load instead of throwing/truncating.
    /// </summary>
    internal static class MeshAssembler
    {
        // Quantization scales: positions to 1e-4, normals (unit) to 1e-3, uvs to 1e-4.
        static long Q(float v, float scale) => (long)MathF.Round(v * scale);

        public static GltfMesh Build(IReadOnlyList<MeshCorner> corners)
        {
            if (corners == null) throw new ArgumentNullException(nameof(corners));
            if (corners.Count % 3 != 0)
                throw new ArgumentException("corners must be a multiple of 3 (triangle soup).", nameof(corners));

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();   // source normal, or an accumulator for computed ones
            var colors = new List<Vector4>();
            var uvs = new List<Vector2>();
            var computed = new List<bool>();      // true => normals[i] is an accumulator to normalize at the end
            var tan1 = new List<Vector3>();       // accumulated UV-space s-direction per welded vertex
            var tan2 = new List<Vector3>();       // accumulated UV-space t-direction per welded vertex
            var srcTangent = new List<Vector4?>();// source tangent if the corner supplied one
            var weld = new Dictionary<(long, long, long, bool, long, long, long, long, long), int>();
            var indices = new List<int>(corners.Count);

            int Resolve(in MeshCorner c, Vector3 faceN, Vector3 sdir, Vector3 tdir)
            {
                var key = (Q(c.Position.X, 1e4f), Q(c.Position.Y, 1e4f), Q(c.Position.Z, 1e4f),
                           c.HasNormal,
                           c.HasNormal ? Q(c.Normal.X, 1e3f) : 0L,
                           c.HasNormal ? Q(c.Normal.Y, 1e3f) : 0L,
                           c.HasNormal ? Q(c.Normal.Z, 1e3f) : 0L,
                           Q(c.Uv.X, 1e4f), Q(c.Uv.Y, 1e4f));

                if (weld.TryGetValue(key, out int existing))
                {
                    if (!c.HasNormal) normals[existing] += faceN; // keep smoothing across shared faces
                    tan1[existing] += sdir;                       // accumulate tangent dirs across shared faces
                    tan2[existing] += tdir;
                    return existing;
                }

                int idx = positions.Count;
                positions.Add(c.Position);
                colors.Add(c.Color);
                uvs.Add(c.Uv);
                normals.Add(c.HasNormal ? c.Normal : faceN);
                computed.Add(!c.HasNormal);
                tan1.Add(sdir);
                tan2.Add(tdir);
                srcTangent.Add(c.Tangent);
                weld[key] = idx;
                return idx;
            }

            for (int t = 0; t < corners.Count; t += 3)
            {
                MeshCorner c0 = corners[t], c1 = corners[t + 1], c2 = corners[t + 2];
                Vector3 faceN = Vector3.Cross(c1.Position - c0.Position, c2.Position - c0.Position);
                // Lengyel per-face tangent (s) / bitangent (t) directions from the UV gradient (shared math).
                TangentMath.FaceDirections(c0.Position, c1.Position, c2.Position, c0.Uv, c1.Uv, c2.Uv,
                    out Vector3 sdir, out Vector3 tdir);
                indices.Add(Resolve(c0, faceN, sdir, tdir));
                indices.Add(Resolve(c1, faceN, sdir, tdir));
                indices.Add(Resolve(c2, faceN, sdir, tdir));
            }

            // 32-bit indices: no ushort ceiling. GltfMesh picks UInt16 for meshes that still fit (<= 65536 verts)
            // and UInt32 beyond, so large welded meshes load instead of throwing/truncating.
            var verts = new ModelVertex[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                Vector3 n = normals[i].LengthSquared() > 1e-12f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
                verts[i] = new ModelVertex(positions[i], n, colors[i], uvs[i], TangentMath.Resolve(n, tan1[i], tan2[i], srcTangent[i]));
            }

            var outIndices = new uint[indices.Count];
            for (int i = 0; i < indices.Count; i++) outIndices[i] = (uint)indices[i];
            return new GltfMesh(verts, outIndices);
        }
    }
}
