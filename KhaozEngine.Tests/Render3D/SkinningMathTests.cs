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
