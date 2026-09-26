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

        // AddInto rows. The reference rotation is 90 degrees about X, which does not commute with the rotations
        // about Y the samples add, so extracting the delta on the wrong side is observable (#20).
        static Quaternion Ref90X() => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

        [Theory]
        [InlineData(0f)]
        [InlineData(-2f)]
        public void AddInto_ZeroOrNegativeWeightLeavesDestinationExactlyUnchanged(float weight)
        {
            JointPose[] destination =
            [
                Pose(new Vector3(1f, 2f, 3f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.4f), new Vector3(2f, 3f, 4f)),
                Pose(new Vector3(-3f, 4f, 5f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, -0.7f), new Vector3(5f, 6f, 7f)),
            ];
            JointPose[] expected = (JointPose[])destination.Clone();
            JointPose[] reference = [Pose(Vector3.One, Ref90X(), Vector3.One), JointPose.Identity];
            JointPose[] sample =
            [
                Pose(new Vector3(9f, 8f, 7f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f), new Vector3(3f)),
                Pose(new Vector3(-1f, 0f, 2f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.9f), new Vector3(0.5f)),
            ];

            PoseBlend.AddInto(destination, sample, reference, weight);

            Assert.Equal(expected, destination);
        }

        [Fact]
        public void AddInto_UnitWeightOverTheReferenceReproducesTheSample_NonIdentityReference()
        {
            // The additive invariant: destination == reference and weight 1 gives
            // reference * inverse(reference) * sample == sample on every channel. The parent-frame extraction
            // (sample * inverse(reference)) instead yields the sample conjugated by the reference.
            Quaternion refRot = Ref90X();
            Quaternion sampleRot = Quaternion.Normalize(refRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f));
            JointPose[] reference =
            [
                Pose(new Vector3(2f, -1f, 0.5f), refRot, new Vector3(1.5f, 0.5f, 2f)),
                Pose(new Vector3(0f, 1f, 0f), Quaternion.Identity, Vector3.One),
            ];
            JointPose[] sample =
            [
                Pose(new Vector3(5f, 3f, -2f), sampleRot, new Vector3(0.25f, 3f, 1f)),
                Pose(new Vector3(1f, 1f, 1f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.6f), new Vector3(2f)),
            ];
            JointPose[] destination = (JointPose[])reference.Clone();

            PoseBlend.AddInto(destination, sample, reference, 1f);

            for (int node = 0; node < destination.Length; node++)
            {
                AssertVectorNear(sample[node].Translation, destination[node].Translation);
                AssertRotationNear(sample[node].Rotation, destination[node].Rotation);
                AssertVectorNear(sample[node].Scale, destination[node].Scale);
            }
            Quaternion conjugated = Quaternion.Normalize(refRot * Quaternion.Normalize(sampleRot * Quaternion.Inverse(refRot)));
            Assert.False(
                MathF.Abs(Quaternion.Dot(conjugated, destination[0].Rotation)) >= 1f - 1e-4f,
                "The delta must be extracted in the joint's local frame, not the parent frame.");
        }

        [Fact]
        public void AddInto_FractionalWeightScalesRotationByShortestArcAndTranslationAndScaleLinearly()
        {
            // The full local delta is 270 degrees about Y. Its shortest arc is -90 degrees, so half weight composes
            // -45 degrees about Y on the right of the destination, never the long-way 135.
            Quaternion refRot = Ref90X();
            Quaternion destinationRot = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
            JointPose[] reference = [Pose(new Vector3(1f, 1f, 1f), refRot, new Vector3(1f, 2f, 1f))];
            JointPose[] sample =
            [
                Pose(
                    new Vector3(3f, -1f, 5f),
                    Quaternion.Normalize(refRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 1.5f)),
                    new Vector3(2f, 1f, 3f)),
            ];
            JointPose[] destination = [Pose(new Vector3(10f, 20f, 30f), destinationRot, new Vector3(1f, 1f, 1f))];

            PoseBlend.AddInto(destination, sample, reference, 0.5f);

            AssertVectorNear(new Vector3(11f, 19f, 32f), destination[0].Translation);
            AssertVectorNear(new Vector3(1.5f, 0.5f, 2f), destination[0].Scale);
            AssertRotationNear(
                Quaternion.Normalize(destinationRot * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 4f)),
                destination[0].Rotation);
            AssertNear(1f, destination[0].Rotation.Length());
        }

        [Fact]
        public void AddInto_WeightAboveOneClampsToUnit()
        {
            JointPose[] reference = [JointPose.Identity];
            JointPose[] sample =
            [
                Pose(new Vector3(1f, 2f, 3f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), new Vector3(2f)),
            ];
            JointPose[] destination = [JointPose.Identity];

            PoseBlend.AddInto(destination, sample, reference, 2f);

            AssertVectorNear(new Vector3(1f, 2f, 3f), destination[0].Translation);
            AssertVectorNear(new Vector3(2f), destination[0].Scale);
            AssertRotationNear(Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), destination[0].Rotation);
        }

        [Fact]
        public void AddInto_MultipliesGlobalAndMaskWeightsPerNodeBeforeClamping()
        {
            // Global weight 2 against mask weights 0.25, 1 and 0: half, clamped full, and untouched.
            JointPose delta = Pose(
                new Vector3(4f, 8f, -2f),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
                new Vector3(3f));
            JointPose[] reference = [JointPose.Identity, JointPose.Identity, JointPose.Identity];
            JointPose[] sample = [delta, delta, delta];
            JointPose start = Pose(new Vector3(1f, 1f, 1f), Ref90X(), Vector3.One);
            JointPose[] destination = [start, start, start];
            var mask = new BoneMask(new[] { 0.25f, 1f, 0f });

            PoseBlend.AddInto(destination, sample, reference, 2f, mask);

            AssertVectorNear(new Vector3(3f, 5f, 0f), destination[0].Translation);
            AssertVectorNear(new Vector3(2f), destination[0].Scale);
            AssertRotationNear(
                Quaternion.Normalize(Ref90X() * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f)),
                destination[0].Rotation);

            AssertVectorNear(new Vector3(5f, 9f, -1f), destination[1].Translation);
            AssertVectorNear(new Vector3(3f), destination[1].Scale);
            AssertRotationNear(
                Quaternion.Normalize(Ref90X() * Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)),
                destination[1].Rotation);

            Assert.Equal(start, destination[2]);
        }

        [Fact]
        public void AddInto_RejectsSampleLengthMismatch()
        {
            JointPose[] destination = [JointPose.Identity];
            JointPose[] sample = [JointPose.Identity, JointPose.Identity];
            JointPose[] reference = [JointPose.Identity];

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => PoseBlend.AddInto(destination, sample, reference, 1f));

            Assert.Equal("sample", error.ParamName);
            Assert.Contains("must equal destination length 1", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AddInto_RejectsReferenceLengthMismatch()
        {
            JointPose[] destination = [JointPose.Identity, JointPose.Identity];
            JointPose[] sample = [JointPose.Identity, JointPose.Identity];
            JointPose[] reference = [JointPose.Identity];

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => PoseBlend.AddInto(destination, sample, reference, 1f));

            Assert.Equal("reference", error.ParamName);
            Assert.Contains("must equal destination length 2", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AddInto_RejectsMaskLengthMismatch()
        {
            JointPose[] destination = [JointPose.Identity, JointPose.Identity];
            JointPose[] sample = [JointPose.Identity, JointPose.Identity];
            JointPose[] reference = [JointPose.Identity, JointPose.Identity];
            var mask = new BoneMask(new[] { 1f });

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => PoseBlend.AddInto(destination, sample, reference, 1f, mask));

            Assert.Equal("mask", error.ParamName);
            Assert.Contains("must equal pose length 2", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void AddInto_RejectsNonFiniteWeight(float weight)
        {
            JointPose[] destination = [JointPose.Identity];
            JointPose[] sample = [JointPose.Identity];
            JointPose[] reference = [JointPose.Identity];

            ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
                () => PoseBlend.AddInto(destination, sample, reference, weight));

            Assert.Equal("weight", error.ParamName);
            Assert.StartsWith("Additive weight must be finite.", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AddInto_WarmedSteadyStateAllocatesNoManagedMemory()
        {
            JointPose[] destination = new JointPose[64];
            JointPose[] sample = new JointPose[64];
            JointPose[] reference = new JointPose[64];
            Array.Fill(destination, JointPose.Identity);
            Array.Fill(sample, Pose(Vector3.One, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(2f)));
            Array.Fill(reference, Pose(Vector3.Zero, Ref90X(), Vector3.One));
            float[] weights = new float[64];
            Array.Fill(weights, 0.75f);
            var mask = new BoneMask(weights);

            PoseBlend.AddInto(destination, sample, reference, 0.5f, mask);

            AllocAssert.NoPerCallAllocation("1000 warmed PoseBlend.AddInto calls", () =>
            {
                for (int i = 0; i < 1000; i++)
                    PoseBlend.AddInto(destination, sample, reference, 0.5f, mask);
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
