using System;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>Interleaved skinned vertex: position, normal, base color (RGBA), UV, 4 bone indices
    /// (float-encoded, portable across GL/Metal/Vulkan), 4 bone weights (normalized at load), and a tangent
    /// (xyz = model-space tangent direction, w = +/-1 bitangent handedness) mirroring <see cref="ModelVertex"/>.
    /// 96 bytes. A zero tangent (the default, since the field defaults to <c>Vector4.Zero</c>) signals "no TBN":
    /// the skin transform carries it to the produced <see cref="ModelVertex"/>, and the shared model fragment
    /// shader then lights with the geometric normal - so untangented skinned meshes render exactly as before.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SkinnedVertex
    {
        public Vector3 Position;     // 0
        public Vector3 Normal;       // 12
        public Vector4 Color;        // 24
        public Vector2 Uv;           // 40
        public Vector4 BoneIndices;  // 48 (up to 4 bone indices, float-encoded)
        public Vector4 BoneWeights;  // 64 (sum to 1; all-zero falls back to identity in the shader)
        public Vector4 Tangent;      // 80 (xyz model-space tangent + handedness w; zero => no TBN, geometric normal)
        public const uint SizeInBytes = 96; // 12 + 12 + 16 + 8 + 16 + 16 + 16
    }

    /// <summary>CPU-side skinned mesh: skinned vertices + indices + the skin's per-bone inverse-bind matrices and
    /// the rest-pose joint world transforms. Produced by <see cref="GltfLoader"/> or
    /// <see cref="SkinnedMeshBuilder"/>. GPU buffers are created internally by the renderer.</summary>
    public sealed class SkinnedGltfMesh
    {
        public SkinnedVertex[] Vertices { get; }

        /// <summary>Authoritative index buffer (32-bit). Always valid regardless of mesh size.</summary>
        public uint[] Indices32 { get; }

        /// <summary>The narrowest GPU index-buffer width this mesh needs: <see cref="GpuIndexFormat.UInt16"/> when
        /// the largest index fits in 16 bits, else <see cref="GpuIndexFormat.UInt32"/>.</summary>
        public GpuIndexFormat IndexFormat { get; }

        ushort[]? _indices16;

        /// <summary>Back-compat 16-bit index view. Returns the narrowed indices for meshes that fit; <b>throws</b>
        /// <see cref="InvalidOperationException"/> for a 32-bit mesh - read <see cref="Indices32"/> instead.</summary>
        public ushort[] Indices
        {
            get
            {
                if (IndexFormat == GpuIndexFormat.UInt32)
                    throw new InvalidOperationException(
                        "This skinned mesh uses 32-bit indices (more than 65536 vertices); read Indices32 instead of Indices.");
                return _indices16 ??= MeshIndices.Narrow(Indices32);
            }
        }

        /// <summary>One inverse-bind matrix per bone: maps a model-space vertex into bone-local space at rest.</summary>
        public Matrix4x4[] InverseBind { get; }
        /// <summary>One rest (bind-pose) joint world transform per bone. Passing these to
        /// Scene3D.DrawSkinned yields the identity deform (the mesh does not move).</summary>
        public Matrix4x4[] RestPose { get; }
        public int BoneCount => InverseBind.Length;
        public int TriangleCount => Indices32.Length / 3;

        /// <summary>Construct from 16-bit indices (the common path: <see cref="SkinnedMeshBuilder"/> + small rigs).</summary>
        public SkinnedGltfMesh(SkinnedVertex[] vertices, ushort[] indices, Matrix4x4[] inverseBind, Matrix4x4[] restPose)
        {
            Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
            if (indices is null) throw new ArgumentNullException(nameof(indices));
            _indices16 = indices;
            Indices32 = MeshIndices.Widen(indices);
            IndexFormat = GpuIndexFormat.UInt16;
            InverseBind = inverseBind ?? throw new ArgumentNullException(nameof(inverseBind));
            RestPose = restPose ?? throw new ArgumentNullException(nameof(restPose));
            if (RestPose.Length != InverseBind.Length)
                throw new ArgumentException("RestPose and InverseBind must have one entry per bone.");
        }

        /// <summary>Construct from 32-bit indices. <see cref="IndexFormat"/> is chosen from the largest index value,
        /// enabling skinned meshes past the 65,536-vertex ceiling.</summary>
        public SkinnedGltfMesh(SkinnedVertex[] vertices, uint[] indices, Matrix4x4[] inverseBind, Matrix4x4[] restPose)
        {
            Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
            Indices32 = indices ?? throw new ArgumentNullException(nameof(indices));
            IndexFormat = MeshIndices.ChooseFormat(indices);
            InverseBind = inverseBind ?? throw new ArgumentNullException(nameof(inverseBind));
            RestPose = restPose ?? throw new ArgumentNullException(nameof(restPose));
            if (RestPose.Length != InverseBind.Length)
                throw new ArgumentException("RestPose and InverseBind must have one entry per bone.");
        }
    }
}
