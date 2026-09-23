using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    [Collection("AllocSensitive")]
    public class PoseBlendTests
    {
        const float Epsilon = 1e-5f;

        [Fact]
        public void BlendInto_ZeroWeightLeavesDestinationExactlyUnchanged()
        {
            JointPose[] into =
            [
                Pose(new Vector3(1f, 2f, 3f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), new Vector3(2f, 3f, 4f)),
                Pose(new Vector3(-3f, 4f, 5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.7f), new Vector3(5f, 6f, 7f)),
            ];
            JointPose[] expected = (JointPose[])into.Clone();
            JointPose[] from =
            [
                JointPose.Identity,
                Pose(new Vector3(9f, 8f, 7f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f), Vector3.One),
            ];

            PoseBlend.BlendInto(into, from, 0f, null);

            Assert.Equal(expected, into);
        }

        [Fact]
        public void BlendInto_UnitWeightCopiesSourceExactly()
        {
            JointPose[] into = [JointPose.Identity, JointPose.Identity];
            JointPose[] from =
            [
                Pose(new Vector3(1f, 2f, 3f), new Quaternion(0.2f, 0.3f, 0.4f, 0.5f), new Vector3(2f, 3f, 4f)),
                Pose(new Vector3(-4f, 5f, -6f), new Quaternion(-0.6f, 0.7f, -0.8f, 0.9f), new Vector3(5f, 6f, 7f)),
            ];

            PoseBlend.BlendInto(into, from, 1f, null);

            Assert.Equal(from, into);
        }

        [Fact]
        public void BlendInto_FractionalWeightInterpolatesEveryChannelAndUsesShortestRotationArc()
        {
            JointPose[] into =
            [
                Pose(new Vector3(2f, 4f, 6f), Quaternion.Identity, new Vector3(1f, 2f, 3f)),
            ];
            JointPose[] from =
            [
                Pose(
                    new Vector3(6f, 8f, 10f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 1.5f),
                    new Vector3(5f, 6f, 7f)),
            ];

            PoseBlend.BlendInto(into, from, 0.5f, null);

            AssertVectorNear(new Vector3(4f, 6f, 8f), into[0].Translation);
            AssertVectorNear(new Vector3(3f, 4f, 5f), into[0].Scale);
            AssertRotationNear(Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 4f), into[0].Rotation);
            AssertNear(1f, into[0].Rotation.Length());
        }

        [Fact]
        public void BlendInto_NegativeWeightClampsToZero()
        {
            JointPose[] into =
            [
                Pose(new Vector3(1f, 2f, 3f), new Quaternion(0.2f, 0.3f, 0.4f, 0.5f), new Vector3(2f, 3f, 4f)),
            ];
            JointPose[] expected = (JointPose[])into.Clone();
            JointPose[] from = [JointPose.Identity];

            PoseBlend.BlendInto(into, from, -2f, null);

            Assert.Equal(expected, into);
        }

        [Fact]
        public void BlendInto_WeightAboveOneClampsToUnit()
        {
            JointPose[] into = [JointPose.Identity];
            JointPose[] from =
            [
                Pose(new Vector3(1f, 2f, 3f), new Quaternion(0.2f, 0.3f, 0.4f, 0.5f), new Vector3(2f, 3f, 4f)),
            ];

            PoseBlend.BlendInto(into, from, 2f, null);

            Assert.Equal(from, into);
        }

        [Fact]
        public void BlendInto_MultipliesGlobalAndMaskWeightsBeforeClamping()
        {
            JointPose[] into = [JointPose.Identity];
            JointPose[] from =
            [
                Pose(new Vector3(8f, 4f, 2f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f), new Vector3(3f)),
            ];
            var mask = new BoneMask(new[] { 0.25f });

            PoseBlend.BlendInto(into, from, 2f, mask);

            AssertVectorNear(new Vector3(4f, 2f, 1f), into[0].Translation);
            AssertVectorNear(new Vector3(2f), into[0].Scale);
            AssertRotationNear(Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f), into[0].Rotation);
        }

        [Fact]
        public void BlendInto_SubtreeMaskChangesOnlySelectedNodes()
        {
            var fixture = new InspectionFixture();
            JointPose[] into = new JointPose[fixture.Skeleton.NodeCount];
            Array.Fill(into, JointPose.Identity);
            JointPose[] expected = (JointPose[])into.Clone();
            JointPose[] from = new JointPose[fixture.Skeleton.NodeCount];
            for (int node = 0; node < from.Length; node++)
            {
                from[node] = Pose(
                    new Vector3(node + 1f, node + 2f, node + 3f),
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (node + 1f) * 0.1f),
                    new Vector3(node + 2f));
            }
            BoneMask leftLeg = BoneMask.Subtree(fixture.Skeleton, InspectionFixture.LeftThighNode, 1f);
            expected[InspectionFixture.LeftThighNode] = from[InspectionFixture.LeftThighNode];
            expected[InspectionFixture.LeftFootNode] = from[InspectionFixture.LeftFootNode];

            PoseBlend.BlendInto(into, from, 1f, leftLeg);

            Assert.Equal(expected, into);
        }

        [Fact]
        public void BlendInto_RejectsPoseLengthMismatch()
        {
            JointPose[] into = [JointPose.Identity];
            JointPose[] from = [JointPose.Identity, JointPose.Identity];

            ArgumentException error = Assert.Throws<ArgumentException>(() => PoseBlend.BlendInto(into, from, 1f, null));

            Assert.Equal("from", error.ParamName);
            Assert.Contains("must equal destination length 1", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BlendInto_RejectsMaskLengthMismatch()
        {
            JointPose[] into = [JointPose.Identity, JointPose.Identity];
            JointPose[] from = [JointPose.Identity, JointPose.Identity];
            var mask = new BoneMask(new[] { 1f });

            ArgumentException error = Assert.Throws<ArgumentException>(() => PoseBlend.BlendInto(into, from, 1f, mask));

            Assert.Equal("mask", error.ParamName);
            Assert.Contains("must equal pose length 2", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void BlendInto_RejectsNonFiniteWeight(float weight)
        {
            JointPose[] into = [JointPose.Identity];
            JointPose[] from = [JointPose.Identity];

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => PoseBlend.BlendInto(into, from, weight, null));

            Assert.Equal("weight", error.ParamName);
            Assert.StartsWith("Blend weight must be finite.", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BlendInto_WarmedSteadyStateAllocatesNoManagedMemory()
        {
            JointPose[] into = new JointPose[64];
            JointPose[] from = new JointPose[64];
            Array.Fill(into, JointPose.Identity);
            Array.Fill(from, Pose(Vector3.One, Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), new Vector3(2f)));
            float[] weights = new float[64];
            Array.Fill(weights, 0.75f);
            var mask = new BoneMask(weights);

            PoseBlend.BlendInto(into, from, 0.5f, mask);

            AllocAssert.NoPerCallAllocation("1000 warmed PoseBlend.BlendInto calls", () =>
            {
                for (int i = 0; i < 1000; i++)
                    PoseBlend.BlendInto(into, from, 0.5f, mask);
            });
        }

        static JointPose Pose(Vector3 translation, Quaternion rotation, Vector3 scale) =>
            new() { Translation = translation, Rotation = rotation, Scale = scale };

        static void AssertVectorNear(Vector3 expected, Vector3 actual) =>
            Assert.True(Vector3.Distance(expected, actual) <= Epsilon, $"Expected {expected}, got {actual}.");

        static void AssertRotationNear(Quaternion expected, Quaternion actual) =>
            Assert.True(MathF.Abs(Quaternion.Dot(expected, actual)) >= 1f - Epsilon, $"Expected {expected}, got {actual}.");

        static void AssertNear(float expected, float actual) =>
            Assert.True(MathF.Abs(expected - actual) <= Epsilon, $"Expected {expected}, got {actual}.");
    }
}
