using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SkinningMathTests
    {
        static Vector3 Apply(Matrix4x4 m, Vector3 p) => Vector3.Transform(p, m);

        [Fact]
        public void Compose_InverseBindTimesRestWorld_IsIdentity()
        {
            // A bone sitting at (0,1,0): inverseBind is the inverse of its rest world transform.
            var restWorld = Matrix4x4.CreateTranslation(0, 1, 0);
            Matrix4x4.Invert(restWorld, out var inverseBind);
            var skin = SkinningMath.Compose(restWorld, inverseBind);
            // Identity: a point is unmoved.
            var p = new Vector3(2, 3, 4);
            Assert.True(Vector3.Distance(Apply(skin, p), p) < 1e-4f);
        }

        [Fact]
        public void Blend_SingleBoneFullWeight_MatchesThatBonesSkinMatrix()
        {
            var skin0 = Matrix4x4.CreateTranslation(5, 0, 0);
            var skin1 = Matrix4x4.CreateTranslation(0, 9, 0);
            var bones = new[] { skin0, skin1 };
            var blended = SkinningMath.BlendSkinMatrix(bones,
                indices: new Vector4(1, 0, 0, 0), weights: new Vector4(1, 0, 0, 0));
            var p = new Vector3(1, 0, 0);
            Assert.True(Vector3.Distance(Apply(blended, p), new Vector3(1, 9, 0)) < 1e-4f);
        }

        [Fact]
        public void Blend_TwoBoneHalfHalf_IsAveragedTransform()
        {
            var skin0 = Matrix4x4.CreateTranslation(4, 0, 0);
            var skin1 = Matrix4x4.CreateTranslation(0, 8, 0);
            var bones = new[] { skin0, skin1 };
            var blended = SkinningMath.BlendSkinMatrix(bones,
                indices: new Vector4(0, 1, 0, 0), weights: new Vector4(0.5f, 0.5f, 0, 0));
            Assert.True(Vector3.Distance(Apply(blended, Vector3.Zero), new Vector3(2, 4, 0)) < 1e-4f);
        }

        [Fact]
        public void Blend_ZeroTotalWeight_IsIdentity()
        {
            var bones = new[] { Matrix4x4.CreateTranslation(5, 5, 5) };
            var blended = SkinningMath.BlendSkinMatrix(bones,
                indices: new Vector4(0, 0, 0, 0), weights: Vector4.Zero);
            Assert.True(Vector3.Distance(Apply(blended, new Vector3(1, 1, 1)), new Vector3(1, 1, 1)) < 1e-4f);
        }

        [Fact]
        public void NormalizeWeights_SumsToOne_OrZeroStaysZero()
        {
            var n = SkinningMath.NormalizeWeights(new Vector4(1, 1, 2, 0));
            Assert.Equal(1f, n.X + n.Y + n.Z + n.W, 4);
            Assert.Equal(Vector4.Zero, SkinningMath.NormalizeWeights(Vector4.Zero));
        }

        // ---- Tangent carried through CPU skinning (the skinned PBR-lite path). ----

        // The no-tangent skinned vertex (the default, no normal map) must produce a ModelVertex byte-identical
        // to the pre-tangent path: position/normal/color/uv from the skin deform and a ZERO tangent. This is the
        // regression guard for "the no-maps / zero-tangent skinned path stays byte-identical".
        [Fact]
        public void SkinVertex_ZeroTangent_ProducesZeroTangentModelVertex()
        {
            var bones = new[] { Matrix4x4.CreateRotationZ(0.7f) * Matrix4x4.CreateTranslation(3, 1, 0) };
            var v = new SkinnedVertex
            {
                Position = new Vector3(1, 2, 0), Normal = Vector3.UnitX,
                Color = new Vector4(0.3f, 0.4f, 0.5f, 1f), Uv = new Vector2(0.25f, 0.75f),
                BoneIndices = new Vector4(0, 0, 0, 0), BoneWeights = new Vector4(1, 0, 0, 0),
                Tangent = Vector4.Zero,
            };
            ModelVertex got = SkinningMath.SkinVertex(v, bones);

            // Tangent stays exactly zero (the no-TBN fallback).
            Assert.Equal(Vector4.Zero, got.Tangent);
            // Position/normal match a direct skin transform; color/uv pass through.
            Vector3 expectP = Vector3.Transform(v.Position, bones[0]);
            Vector3 expectN = Vector3.Normalize(Vector3.TransformNormal(v.Normal, bones[0]));
            Assert.True(Vector3.Distance(got.Position, expectP) < 1e-5f);
            Assert.True(Vector3.Distance(got.Normal, expectN) < 1e-5f);
            Assert.Equal(v.Color, got.Color);
            Assert.Equal(v.Uv, got.Uv);
        }

        // A real tangent rides through the same skin rotation as the normal and keeps its handedness w, so the
        // deformed mesh's TBN tracks the pose. With a pure rotation the tangent rotates and stays unit-length.
        [Fact]
        public void SkinVertex_Tangent_RotatesWithSkinAndKeepsHandedness()
        {
            var rot = Matrix4x4.CreateRotationZ(MathF.PI / 2f);   // +X -> +Y
            var bones = new[] { rot };
            var v = new SkinnedVertex
            {
                Position = Vector3.UnitX, Normal = Vector3.UnitZ,
                Color = Vector4.One, Uv = Vector2.Zero,
                BoneIndices = new Vector4(0, 0, 0, 0), BoneWeights = new Vector4(1, 0, 0, 0),
                Tangent = new Vector4(1, 0, 0, -1),               // tangent +X, handedness -1
            };
            ModelVertex got = SkinningMath.SkinVertex(v, bones);

            var tDir = new Vector3(got.Tangent.X, got.Tangent.Y, got.Tangent.Z);
            Assert.True(Vector3.Distance(tDir, Vector3.UnitY) < 1e-5f, $"tangent should rotate +X->+Y, got {tDir}");
            Assert.Equal(-1f, got.Tangent.W);                    // handedness preserved
            Assert.True(MathF.Abs(tDir.Length() - 1f) < 1e-5f);  // re-normalized
        }

        // BlendSkinMatrix reads composedBones[(int)index] for all four bones unconditionally, so an
        // out-of-range JOINTS_0 value from a malicious/malformed glTF would index past the palette
        // (>= the 128-slot window throws mid-frame). AreBoneIndicesValid is the load-time guard.
        [Theory]
        [InlineData(0f, 1f, 2f, 3f, 4, true)]    // all in [0,4)
        [InlineData(3f, 0f, 0f, 0f, 4, true)]    // 3 is the max valid for boneCount 4
        [InlineData(4f, 0f, 0f, 0f, 4, false)]   // 4 == boneCount, out of range
        [InlineData(0f, 0f, 0f, 128f, 128, false)] // 128 == palette window size, would index past it
        [InlineData(-1f, 0f, 0f, 0f, 4, false)]  // negative
        public void AreBoneIndicesValid_RequiresEveryComponentInRange(
            float x, float y, float z, float w, int boneCount, bool expected)
        {
            Assert.Equal(expected, SkinningMath.AreBoneIndicesValid(new Vector4(x, y, z, w), boneCount));
        }
    }
}
