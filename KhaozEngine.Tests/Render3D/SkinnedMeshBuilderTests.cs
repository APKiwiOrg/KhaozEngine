using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SkinnedMeshBuilderTests
    {
        // CPU mirror of the shader: skin each vertex by the rest/posed bones and return its deformed position.
        static Vector3 Skin(SkinnedVertex v, Matrix4x4[] joints, Matrix4x4[] inverseBind)
        {
            Span<Matrix4x4> composed = stackalloc Matrix4x4[joints.Length];
            for (int i = 0; i < joints.Length; i++) composed[i] = SkinningMath.Compose(joints[i], inverseBind[i]);
            var skin = SkinningMath.BlendSkinMatrix(composed, v.BoneIndices, v.BoneWeights);
            return Vector3.Transform(v.Position, skin);
        }

        [Fact]
        public void BuildTube_HasRequestedBonesAndNormalizedWeights()
        {
            var tube = SkinnedMeshBuilder.BuildTube(radius: 0.5f, length: 4f, ringSegments: 4, radialSegments: 6, boneCount: 5);
            Assert.Equal(5, tube.BoneCount);
            Assert.Equal(5, tube.RestPose.Length);
            Assert.True(tube.Vertices.Length > 0);
            foreach (var v in tube.Vertices)
            {
                float sum = v.BoneWeights.X + v.BoneWeights.Y + v.BoneWeights.Z + v.BoneWeights.W;
                Assert.True(MathF.Abs(sum - 1f) < 1e-3f, $"weights must sum to 1, got {sum}");
            }
        }

        [Fact]
        public void BuildTube_RestPose_LeavesGeometryUnmoved()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 4, 6, 5);
            foreach (var v in tube.Vertices)
            {
                var moved = Skin(v, tube.RestPose, tube.InverseBind);
                Assert.True(Vector3.Distance(moved, v.Position) < 1e-3f,
                    $"rest pose must not deform: {v.Position} -> {moved}");
            }
        }

        [Fact]
        public void BuildTube_BendingTipBone_CurvesTheFarEnd()
        {
            // Tube along +Z. Rotate only the last bone; the tip vertices should swing in X, the base must not move.
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 8, 6, 6, Axis.Z);
            var posed = (Matrix4x4[])tube.RestPose.Clone();
            int last = tube.BoneCount - 1;
            // Rotate the last bone 90 deg about Y around its own rest origin.
            var origin = tube.RestPose[last].Translation;
            posed[last] = Matrix4x4.CreateTranslation(-origin)
                          * Matrix4x4.CreateRotationY(MathF.PI / 2f)
                          * Matrix4x4.CreateTranslation(origin)
                          * tube.RestPose[last];

            float maxBaseShift = 0f, maxTipShift = 0f;
            foreach (var v in tube.Vertices)
            {
                float shift = Vector3.Distance(Skin(v, posed, tube.InverseBind), v.Position);
                if (v.Position.Z < 0.5f) maxBaseShift = MathF.Max(maxBaseShift, shift);
                if (v.Position.Z > 3.5f) maxTipShift = MathF.Max(maxTipShift, shift);
            }
            Assert.True(maxBaseShift < 1e-2f, $"base should stay put, shifted {maxBaseShift}");
            Assert.True(maxTipShift > 0.5f, $"tip should swing, shifted only {maxTipShift}");
        }
    }
}
