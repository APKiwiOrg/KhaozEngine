using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>Interleaved vertex: position, normal, base color (RGBA), texture UV, and a tangent
    /// (xyz = model-space tangent direction, w = +/-1 bitangent handedness). 64 bytes. A zero tangent
    /// (the default from the back-compat ctors) signals "no TBN" to the shader, which then lights with the
    /// geometric normal - so untangented meshes (primitives, skinned) render exactly as before.</summary>
    public struct ModelVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Color;
        public Vector2 Uv;
        public Vector4 Tangent;
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv, Vector4 tangent)
        { Position = p; Normal = n; Color = c; Uv = uv; Tangent = tangent; }
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv) : this(p, n, c, uv, Vector4.Zero) { }
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c) : this(p, n, c, Vector2.Zero, Vector4.Zero) { } // back-compat
        public const uint SizeInBytes = 64; // 3*4 + 3*4 + 4*4 + 2*4 + 4*4
    }

    /// <summary>CPU-side loaded mesh. GPU buffers are created internally by the renderer. Indices are stored as
    /// 32-bit (<see cref="Indices32"/>); <see cref="IndexFormat"/> reports the narrowest GPU index width the mesh
    /// needs (16-bit for meshes up to 65,536 vertices, 32-bit beyond), so detailed/sculpted models load while
    /// small meshes keep their byte-identical 16-bit index buffers.</summary>
    public sealed class GltfMesh
    {
        public ModelVertex[] Vertices { get; }

        /// <summary>Authoritative index buffer (32-bit). Always valid regardless of mesh size - use this in new
        /// code that may handle large meshes.</summary>
        public uint[] Indices32 { get; }

        /// <summary>The narrowest GPU index-buffer width this mesh needs: <see cref="GpuIndexFormat.UInt16"/> when
        /// the largest index fits in 16 bits (&lt;= 65535), else <see cref="GpuIndexFormat.UInt32"/>. The renderer
        /// uploads and binds the matching width.</summary>
        public GpuIndexFormat IndexFormat { get; }

        ushort[]? _indices16;

        /// <summary>Back-compat 16-bit index view. Returns the indices narrowed to <see cref="ushort"/> for meshes
        /// that fit (<see cref="IndexFormat"/> == <see cref="GpuIndexFormat.UInt16"/>); <b>throws</b>
        /// <see cref="InvalidOperationException"/> for a 32-bit mesh - read <see cref="Indices32"/> instead.
        /// Existing small-mesh callers are unaffected.</summary>
        public ushort[] Indices
        {
            get
            {
                if (IndexFormat == GpuIndexFormat.UInt32)
                    throw new InvalidOperationException(
                        "This mesh uses 32-bit indices (more than 65536 vertices); read Indices32 instead of Indices.");
                return _indices16 ??= MeshIndices.Narrow(Indices32);
            }
        }

        public int TriangleCount => Indices32.Length / 3;

        /// <summary>Construct from 16-bit indices (the common path: primitives + small assembler output). Always a
        /// <see cref="GpuIndexFormat.UInt16"/> mesh, since a <see cref="ushort"/> index can never exceed 65535.</summary>
        public GltfMesh(ModelVertex[] v, ushort[] i)
        {
            Vertices = v ?? throw new ArgumentNullException(nameof(v));
            if (i is null) throw new ArgumentNullException(nameof(i));
            _indices16 = i;                              // keep the exact array the caller passed
            Indices32 = MeshIndices.Widen(i);
            IndexFormat = GpuIndexFormat.UInt16;
        }

        /// <summary>Construct from 32-bit indices. <see cref="IndexFormat"/> is chosen from the largest index value
        /// (UInt16 when it still fits in 16 bits, else UInt32), enabling meshes past the 65,536-vertex ceiling.</summary>
        public GltfMesh(ModelVertex[] v, uint[] i)
        {
            Vertices = v ?? throw new ArgumentNullException(nameof(v));
            Indices32 = i ?? throw new ArgumentNullException(nameof(i));
            IndexFormat = MeshIndices.ChooseFormat(i);
        }
    }
}
