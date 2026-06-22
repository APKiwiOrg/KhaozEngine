using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Composes transformed (and optionally re-coloured) <see cref="GltfMesh"/> primitives into a single
    /// <see cref="GltfMesh"/>, so a game can build a multi-part, multi-colour silhouette (a turret, a drone)
    /// in code and draw it as one instance. Positions are transformed by the given matrix; normals by the
    /// inverse-transpose of its upper-3×3 (so non-uniform scale stays correct) and re-normalized. Indices are
    /// offset by the running vertex count as each part is appended. Fluent.
    /// </summary>
    public sealed class MeshBuilder
    {
        readonly List<ModelVertex> _vertices = new();
        readonly List<uint> _indices = new();

        /// <summary>Total vertices accumulated so far.</summary>
        public int VertexCount => _vertices.Count;

        /// <summary>Total indices accumulated so far.</summary>
        public int IndexCount => _indices.Count;

        /// <summary>Appends <paramref name="part"/> transformed by <paramref name="transform"/>, keeping its own vertex colours.</summary>
        public MeshBuilder Add(GltfMesh part, Matrix4x4 transform) => AddInternal(part, transform, null);

        /// <summary>Appends <paramref name="part"/> transformed by <paramref name="transform"/>, baking <paramref name="color"/> onto every appended vertex.</summary>
        public MeshBuilder Add(GltfMesh part, Matrix4x4 transform, Color color) => AddInternal(part, transform, color);

        MeshBuilder AddInternal(GltfMesh part, Matrix4x4 transform, Vector4? color)
        {
            if (part is null) throw new ArgumentNullException(nameof(part));

            Matrix4x4 normalMatrix = BuildNormalMatrix(transform);
            int offset = _vertices.Count;

            foreach (var v in part.Vertices)
            {
                Vector3 pos = Vector3.Transform(v.Position, transform);
                Vector3 nrm = Vector3.TransformNormal(v.Normal, normalMatrix);
                float len = nrm.Length();
                nrm = len > 1e-12f ? nrm / len : v.Normal;
                _vertices.Add(new ModelVertex(pos, nrm, color ?? v.Color, v.Uv));
            }

            foreach (var idx in part.Indices32)
                _indices.Add(idx + (uint)offset);

            return this;
        }

        /// <summary>
        /// Normal matrix = transpose(inverse(upper-3×3)). Falls back to the rotation/upper-3×3 of the transform
        /// if it is not invertible (degenerate scale).
        /// </summary>
        static Matrix4x4 BuildNormalMatrix(Matrix4x4 transform)
        {
            // strip translation; only the linear part affects directions.
            Matrix4x4 linear = transform;
            linear.M14 = linear.M24 = linear.M34 = 0f;
            linear.M41 = linear.M42 = linear.M43 = 0f;
            linear.M44 = 1f;

            if (Matrix4x4.Invert(linear, out var inv))
                return Matrix4x4.Transpose(inv);

            return linear; // degenerate: best-effort fall back to the raw linear part.
        }

        /// <summary>
        /// Returns the accumulated mesh. Indices are 32-bit, so parts fuse freely across the 65,536-vertex
        /// boundary; <see cref="GltfMesh"/> picks the GPU index width (UInt16 while it still fits, UInt32 beyond),
        /// so a fused mesh that stays small keeps its byte-identical 16-bit index buffer.
        /// </summary>
        public GltfMesh Build() => new GltfMesh(_vertices.ToArray(), _indices.ToArray());
    }
}
