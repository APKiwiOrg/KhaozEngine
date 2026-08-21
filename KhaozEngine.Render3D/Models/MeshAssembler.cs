using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// One corner of a triangle fed to <see cref="MeshAssembler"/>: a position, an optional source normal
    /// (null = "compute a smooth normal from face winding"), a base color, a UV, and an optional source tangent
    /// (xyz = model-space direction, w = +/-1 handedness; null = compute from UV+position gradient).
    /// <para><c>hasVertexColor</c> marks a corner whose color came from a PER-VERTEX source (a glTF COLOR_0
    /// accessor) rather than from the flat per-material factor. It is what puts the color in the weld key, so an
    /// authored color seam survives. Leave it false for a flat color, which is what every non-COLOR_0 caller
    /// does, and the weld is exactly the position/normal/uv one it has always been.</para>
    /// </summary>
    internal readonly struct MeshCorner
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;   // meaningful only when HasNormal
        public readonly bool HasNormal;
        public readonly Vector4 Color;
        public readonly bool HasVertexColor;   // color is per-vertex (COLOR_0), so it belongs in the weld key
        public readonly Vector2 Uv;
        public readonly Vector4? Tangent;  // source tangent (xyz dir, w handedness); null => compute from UV+pos

        public MeshCorner(Vector3 position, Vector3? normal, Vector4 color, Vector2 uv, Vector4? tangent = null,
                          bool hasVertexColor = false)
        {
            Position = position;
            HasNormal = normal.HasValue;
            Normal = normal ?? default;
            Color = color;
            HasVertexColor = hasVertexColor;
            Uv = uv;
            Tangent = tangent;
        }
    }

    /// <summary>
    /// The weld identity of a <see cref="MeshCorner"/>: the quantized lanes two corners must agree on to merge
    /// into one vertex. A hand-written struct rather than a <c>ValueTuple</c> because a tuple past seven
    /// elements hashes only its seventh element and its <c>Rest</c>, which left the three position lanes out
    /// of the hash entirely and drove the weld toward O(n squared) on a mesh whose surviving lanes are
    /// constant (an unmapped, palette-painted kit piece). Equality here is over every lane, position first,
    /// and so is the hash.
    /// </summary>
    internal readonly struct MeshWeldKey : IEquatable<MeshWeldKey>
    {
        // Quantization scales: positions to 1e-4, normals (unit) to 1e-3, uvs to 1e-4, vertex colors to 1e-3
        // (a 0..1 channel, so the same resolution a unit normal gets).
        const float PosScale = 1e4f, NormalScale = 1e3f, UvScale = 1e4f, ColorScale = 1e3f;

        readonly long _px, _py, _pz;
        readonly long _nx, _ny, _nz;
        readonly long _ux, _uy;
        readonly long _cx, _cy, _cz, _cw;
        readonly bool _hasNormal, _hasVertexColor;

        static long Q(float v, float scale) => (long)MathF.Round(v * scale);

        MeshWeldKey(in MeshCorner c)
        {
            _px = Q(c.Position.X, PosScale);
            _py = Q(c.Position.Y, PosScale);
            _pz = Q(c.Position.Z, PosScale);
            _hasNormal = c.HasNormal;
            _nx = c.HasNormal ? Q(c.Normal.X, NormalScale) : 0L;
            _ny = c.HasNormal ? Q(c.Normal.Y, NormalScale) : 0L;
            _nz = c.HasNormal ? Q(c.Normal.Z, NormalScale) : 0L;
            _ux = Q(c.Uv.X, UvScale);
            _uy = Q(c.Uv.Y, UvScale);
            // Zeroed when the color is flat, so a non-COLOR_0 asset welds byte-for-byte as before.
            _hasVertexColor = c.HasVertexColor;
            _cx = c.HasVertexColor ? Q(c.Color.X, ColorScale) : 0L;
            _cy = c.HasVertexColor ? Q(c.Color.Y, ColorScale) : 0L;
            _cz = c.HasVertexColor ? Q(c.Color.Z, ColorScale) : 0L;
            _cw = c.HasVertexColor ? Q(c.Color.W, ColorScale) : 0L;
        }

        public static MeshWeldKey From(in MeshCorner c) => new MeshWeldKey(c);

        public bool Equals(MeshWeldKey other) =>
            _px == other._px && _py == other._py && _pz == other._pz &&
            _hasNormal == other._hasNormal &&
            _nx == other._nx && _ny == other._ny && _nz == other._nz &&
            _ux == other._ux && _uy == other._uy &&
            _hasVertexColor == other._hasVertexColor &&
            _cx == other._cx && _cy == other._cy && _cz == other._cz && _cw == other._cw;

        public override bool Equals(object? obj) => obj is MeshWeldKey other && Equals(other);

        public static bool operator ==(MeshWeldKey a, MeshWeldKey b) => a.Equals(b);

        public static bool operator !=(MeshWeldKey a, MeshWeldKey b) => !a.Equals(b);

        /// <summary>Mixes EVERY lane, position included. A key shape that drops a lane here still welds
        /// correctly (equality is unchanged) but collapses buckets, which is a load-time cliff, not a
        /// visible one, so <c>MeshAssemblerTests</c> pins the spread deterministically.</summary>
        public override int GetHashCode()
        {
            var h = new HashCode();
            h.Add(_px); h.Add(_py); h.Add(_pz);
            h.Add(_hasNormal);
            h.Add(_nx); h.Add(_ny); h.Add(_nz);
            h.Add(_ux); h.Add(_uy);
            h.Add(_hasVertexColor);
            h.Add(_cx); h.Add(_cy); h.Add(_cz); h.Add(_cw);
            return h.ToHashCode();
        }
    }

    /// <summary>
    /// Welds a triangle-soup of <see cref="MeshCorner"/>s into an indexed <see cref="GltfMesh"/>. Two corners
    /// merge only when their position, normal, uv AND per-vertex color all match (quantized) - so hard edges
    /// (distinct normals), UV seams (distinct uvs) and authored COLOR_0 seams (distinct vertex colors) are all
    /// preserved, unlike a position-only weld. The color only enters the key for corners flagged
    /// <see cref="MeshCorner.HasVertexColor"/>, because a FLAT per-material color is uniform over a primitive
    /// and adding it unconditionally would split coincident corners across materials in the whole-scene weld.
    /// <para>The flag is itself part of the key, so a flagged corner never merges with an unflagged one even
    /// when every other lane matches. A COLOR_0 primitive sharing a coincident corner with a non-COLOR_0
    /// primitive inside the same weld (the whole-scene weld, or one material's weld in
    /// <c>LoadPartsWithMaterials</c>) therefore keeps both corners instead of the first one's color winning.
    /// That is deliberate: the two corners carry colors from different sources, and keeping both is the answer
    /// that loses no authored data.</para>
    /// When a corner has no source
    /// normal, an area-weighted face normal is accumulated across the faces that share it (a smooth default),
    /// and such corners weld on position+uv only. Also computes per-vertex tangents via the Lengyel UV+position
    /// method, accumulated and Gram-Schmidt orthogonalized against the finalized normal. A supplied source
    /// tangent on the corner wins over the computed one. Degenerate UVs (no UV gradient) yield a zero tangent
    /// (the shader falls back to the geometric normal). Emits 32-bit indices and lets <see cref="GltfMesh"/>
    /// pick the GPU index width, so meshes past the 65,536-vertex ceiling load instead of throwing/truncating.
    /// </summary>
    internal static class MeshAssembler
    {
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
            var weld = new Dictionary<MeshWeldKey, int>();
            var indices = new List<int>(corners.Count);

            int Resolve(in MeshCorner c, Vector3 faceN, Vector3 sdir, Vector3 tdir)
            {
                var key = MeshWeldKey.From(c);

                // Corners that do NOT share a key do not share the accumulators below, so a color seam splits
                // the smooth normal and the tangent the same way a UV seam always has.
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
