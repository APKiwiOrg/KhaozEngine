using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    public class SegmentClearanceTests
    {
        const float Epsilon = 1e-5f;

        [Fact]
        public void Min_CrossingBladeAtAuthoredEndPenetratesForearmCapsule()
        {
            Skeleton skeleton = CreateSkeleton();
            AnimationClip clip = RidingHeightClip();
            Matrix4x4 segmentLocal = Matrix4x4.CreateTranslation(1f, 0f, 0f);

            float clearance = SegmentClearance.Min(
                clip,
                skeleton,
                "weapon_socket",
                in segmentLocal,
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new[] { new Capsule("Forearm.A", "Forearm.B", 0.25f) },
                samples: 2);

            AssertNear(-0.25f, clearance);
        }

        [Fact]
        public void Min_ClearParallelBladeReturnsPositiveClearance()
        {
            Skeleton skeleton = CreateSkeleton();
            AnimationClip clip = EmptyClip();
            Matrix4x4 segmentLocal = Matrix4x4.CreateTranslation(0f, 2f, 0f);

            float clearance = SegmentClearance.Min(
                clip,
                skeleton,
                "weapon_socket",
                in segmentLocal,
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new[] { new Capsule("Forearm.A", "Forearm.B", 0.25f) },
                samples: 2);

            AssertNear(1.75f, clearance);
        }

        [Fact]
        public void Min_ZeroLengthBladeAndCapsuleReturnPointDistance()
        {
            Skeleton skeleton = CreateSkeleton();
            AnimationClip clip = EmptyClip();
            Matrix4x4 segmentLocal = Matrix4x4.CreateTranslation(0f, 2f, 0f);

            float clearance = SegmentClearance.Min(
                clip,
                skeleton,
                "weapon_socket",
                in segmentLocal,
                Vector3.Zero,
                Vector3.Zero,
                new[] { new Capsule("Root", "Root", 0.25f) },
                samples: 2);

            Assert.True(float.IsFinite(clearance));
            AssertNear(1.75f, clearance);
        }

        [Fact]
        public void Min_RejectsFewerThanTwoSamples()
        {
            Skeleton skeleton = CreateSkeleton();
            Matrix4x4 segmentLocal = Matrix4x4.Identity;

            Assert.Throws<System.ArgumentOutOfRangeException>(() => SegmentClearance.Min(
                EmptyClip(),
                skeleton,
                "weapon_socket",
                in segmentLocal,
                Vector3.Zero,
                Vector3.One,
                new[] { new Capsule("Forearm.A", "Forearm.B", 0.25f) },
                samples: 1));
        }

        [Fact]
        public void Min_RejectsEmptyCapsuleList()
        {
            Skeleton skeleton = CreateSkeleton();
            Matrix4x4 segmentLocal = Matrix4x4.Identity;

            Assert.Throws<System.ArgumentException>(() => SegmentClearance.Min(
                EmptyClip(),
                skeleton,
                "weapon_socket",
                in segmentLocal,
                Vector3.Zero,
                Vector3.One,
                System.Array.Empty<Capsule>(),
                samples: 2));
        }

        [Theory]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void Min_RejectsInvalidCapsuleRadius(float radius)
        {
            Skeleton skeleton = CreateSkeleton();
            Matrix4x4 segmentLocal = Matrix4x4.Identity;

            Assert.Throws<System.ArgumentOutOfRangeException>(() => SegmentClearance.Min(
                EmptyClip(),
                skeleton,
                "weapon_socket",
                in segmentLocal,
                Vector3.Zero,
                Vector3.One,
                new[] { new Capsule("Forearm.A", "Forearm.B", radius) },
                samples: 2));
        }

        [Fact]
        public void Min_RejectsNonFiniteSegmentGeometry()
        {
            Skeleton skeleton = CreateSkeleton();
            Matrix4x4 segmentLocal = Matrix4x4.Identity;

            Assert.Throws<System.ArgumentOutOfRangeException>(() => SegmentClearance.Min(
                EmptyClip(),
                skeleton,
                "weapon_socket",
                in segmentLocal,
                new Vector3(float.NaN, 0f, 0f),
                Vector3.One,
                new[] { new Capsule("Forearm.A", "Forearm.B", 0.25f) },
                samples: 2));
        }

        [Fact]
        public void Min_RejectsNonFiniteSampledSegment()
        {
            Skeleton skeleton = CreateSkeleton();
            Matrix4x4 segmentLocal = Matrix4x4.Identity;
            var clip = new AnimationClip(
                "non-finite",
                1f,
                new List<JointTrack>
                {
                    new(1)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f, 1f },
                            new[] { Vector3.Zero, new Vector3(0f, float.NaN, 0f) },
                            InterpolationMode.Linear),
                    },
                });

            Assert.Throws<System.ArgumentException>(() => SegmentClearance.Min(
                clip,
                skeleton,
                "weapon_socket",
                in segmentLocal,
                Vector3.Zero,
                Vector3.One,
                new[] { new Capsule("Forearm.A", "Forearm.B", 0.25f) },
                samples: 2));
        }

        static Skeleton CreateSkeleton() =>
            new(
                new[] { -1, 0, 0, 0 },
                new[]
                {
                    JointPose.Identity,
                    Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, System.MathF.PI / 2f)),
                    Pose(new Vector3(0f, 0f, -1f), Quaternion.Identity),
                    Pose(new Vector3(0f, 0f, 1f), Quaternion.Identity),
                },
                new[] { 0, 1, 2, 3 },
                new[] { 0, 1, 2, 3 },
                new[] { "Root", "weapon_socket", "Forearm.A", "Forearm.B" });

        static AnimationClip RidingHeightClip() =>
            new(
                "crossing-blade",
                1f,
                new List<JointTrack>
                {
                    new(1)
                    {
                        Translation = new Vector3Track(
                            new[] { 0f, 1f },
                            new[] { new Vector3(0f, 2f, 0f), Vector3.Zero },
                            InterpolationMode.Linear),
                    },
                });

        static AnimationClip EmptyClip() => new("clear-blade", 1f, new List<JointTrack>());

        static JointPose Pose(Vector3 translation, Quaternion rotation) =>
            new() { Translation = translation, Rotation = rotation, Scale = Vector3.One };

        static void AssertNear(float expected, float actual) =>
            Assert.InRange(System.MathF.Abs(expected - actual), 0f, Epsilon);
    }
}
