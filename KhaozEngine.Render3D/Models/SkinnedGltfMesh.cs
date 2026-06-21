using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace KhaozEngine.Render3D
{
    /// <summary>Interleaved skinned vertex: position, normal, base color (RGBA), UV, then 4 bone indices
    /// (float-encoded, portable across GL/Metal/Vulkan) and 4 bone weights (normalized at load). 80 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SkinnedVertex
    {
        public Vector3 Position;     // 0
        public Vector3 Normal;       // 12
        public Vector4 Color;        // 24
        public Vector2 Uv;           // 40
        public Vector4 BoneIndices;  // 48 (up to 4 bone indices, float-encoded)
        public Vector4 BoneWeights;  // 64 (sum to 1; all-zero falls back to identity in the shader)
        public const uint SizeInBytes = 80; // 12 + 12 + 16 + 8 + 16 + 16
    }

    /// <summary>CPU-side skinned mesh: skinned vertices + indices + the skin's per-bone inverse-bind matrices and
    /// the rest-pose joint world transforms. Produced by <see cref="GltfLoader"/> or
    /// <see cref="SkinnedMeshBuilder"/>. GPU buffers are created internally by the renderer.</summary>
    public sealed class SkinnedGltfMesh
    {
        public SkinnedVertex[] Vertices { get; }
        public ushort[] Indices { get; }
        /// <summary>One inverse-bind matrix per bone: maps a model-space vertex into bone-local space at rest.</summary>
        public Matrix4x4[] InverseBind { get; }
        /// <summary>One rest (bind-pose) joint world transform per bone. Passing these to
        /// Scene3D.DrawSkinned yields the identity deform (the mesh does not move).</summary>
        public Matrix4x4[] RestPose { get; }
        public int BoneCount => InverseBind.Length;
        public int TriangleCount => Indices.Length / 3;

        public SkinnedGltfMesh(SkinnedVertex[] vertices, ushort[] indices, Matrix4x4[] inverseBind, Matrix4x4[] restPose)
        {
            Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
            Indices = indices ?? throw new ArgumentNullException(nameof(indices));
            InverseBind = inverseBind ?? throw new ArgumentNullException(nameof(inverseBind));
            RestPose = restPose ?? throw new ArgumentNullException(nameof(restPose));
            if (RestPose.Length != InverseBind.Length)
                throw new ArgumentException("RestPose and InverseBind must have one entry per bone.");
        }
    }
}
