using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SkinnedMeshTypesTests
    {
        [Fact]
        public void SkinnedVertex_LayoutIs96Bytes_WithTangent()
        {
            Assert.Equal(96u, SkinnedVertex.SizeInBytes);
            Assert.Equal(96, System.Runtime.InteropServices.Marshal.SizeOf<SkinnedVertex>());
        }

        [Fact]
        public void SkinnedVertex_DefaultTangentIsZero()
        {
            // The tangent field defaults to Vector4.Zero, so every existing object-initializer call site that
            // does not set Tangent gets the no-TBN fallback (geometric-normal lighting) unchanged.
            var v = new SkinnedVertex { Position = Vector3.UnitX, Normal = Vector3.UnitY };
            Assert.Equal(Vector4.Zero, v.Tangent);
        }

        [Fact]
        public void SkinnedGltfMesh_HoldsVertsIndicesBonesAndRestPose()
        {
            var v = new SkinnedVertex
            {
                Position = new Vector3(1, 2, 3), Normal = Vector3.UnitY,
                Color = new Vector4(1, 1, 1, 1), Uv = Vector2.Zero,
                BoneIndices = new Vector4(0, 1, 0, 0), BoneWeights = new Vector4(0.5f, 0.5f, 0, 0),
            };
            var mesh = new SkinnedGltfMesh(
                new[] { v }, new ushort[] { 0 },
                new[] { Matrix4x4.Identity, Matrix4x4.Identity },
                new[] { Matrix4x4.Identity, Matrix4x4.CreateTranslation(0, 1, 0) });

            Assert.Single(mesh.Vertices);
            Assert.Equal(2, mesh.BoneCount);
            Assert.Equal(2, mesh.InverseBind.Length);
            Assert.Equal(2, mesh.RestPose.Length);
        }

        [Fact]
        public void SkinnedGltfMesh_ThrowsWhenRestPoseAndInverseBindLengthMismatch()
        {
            var v = new SkinnedVertex();
            Assert.Throws<ArgumentException>(() =>
                new SkinnedGltfMesh(
                    new[] { v }, new ushort[] { 0 },
                    new[] { Matrix4x4.Identity },          // 1 bone
                    new[] { Matrix4x4.Identity, Matrix4x4.Identity })); // 2 bones
        }

        [Fact]
        public void SkinnedGltfMesh_ThrowsWhenVerticesIsNull()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new SkinnedGltfMesh(
                    null!, new ushort[] { 0 },
                    new[] { Matrix4x4.Identity },
                    new[] { Matrix4x4.Identity }));
        }

        [Fact]
        public void SkinnedMeshHandle_DefaultIsGenerationZero()
        {
            Assert.Equal(0, default(SkinnedMeshHandle).Generation);
            Assert.Equal(1, new SkinnedMeshHandle(3).Generation);
        }
    }
}
