using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation.Inspection
{
    public class PoseProbeTests
    {
        const float Epsilon = 1e-5f;

        [Fact]
        public void Constructor_ComposesNamedRestJointAndNonPaletteSocket()
        {
            var fixture = new InspectionFixture();
            var probe = new PoseProbe(fixture.Skeleton);

            AssertVectorNear(new Vector3(-0.25f, 0f, 0.15f), probe.JointPosition("Foot.L"));
            AssertVectorNear(new Vector3(0.6f, 1.45f, 0.5f), probe.JointPosition("weapon_socket"));
            Assert.Equal(-1, fixture.Skeleton.BoneIndexOfNode(InspectionFixture.WeaponSocketNode));
        }

        [Fact]
        public void SampleClip_NormalisedAndSecondsReachTheSameKnownPose()
        {
            var fixture = new InspectionFixture();
            var byPhase = new PoseProbe(fixture.Skeleton);
            var bySeconds = new PoseProbe(fixture.Skeleton);

            byPhase.SampleClip(fixture.WalkClip, 0.5f);
            bySeconds.SampleClipAtSeconds(fixture.WalkClip, 1f);

            Matrix4x4 expected = bySeconds.JointModel("Hand.R");
            AssertMatrixNear(expected, byPhase.JointModel(InspectionFixture.RightHandNode));
            AssertVectorNear(new Vector3(0.5f, 1.45f, 0.3f), byPhase.JointPosition("Hand.R"));
        }

        [Fact]
        public void SampleClip_PhaseOneSamplesAuthoredEndKey()
        {
            var fixture = new InspectionFixture();
            var probe = new PoseProbe(fixture.Skeleton);

            probe.SampleClip(fixture.CreateEndpointClip(), 1f);

            AssertVectorNear(new Vector3(0f, 1f, 2f), probe.JointPosition("Hips"));
        }

        [Fact]
        public void SetLocals_ComposesLocalBeforeParentModel()
        {
            var fixture = new InspectionFixture();
            var probe = new PoseProbe(fixture.Skeleton);
            var locals = new JointPose[fixture.Skeleton.NodeCount];
            Array.Fill(locals, JointPose.Identity);
            locals[InspectionFixture.RootNode] = new JointPose
            {
                Translation = new Vector3(10f, 0f, 0f),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
                Scale = Vector3.One,
            };
            locals[InspectionFixture.HipsNode] = Pose(new Vector3(2f, 0f, 0f));
            locals[InspectionFixture.RightHandNode] = Pose(new Vector3(0f, 3f, 0f));
            locals[InspectionFixture.WeaponSocketNode] = Pose(new Vector3(1f, 0f, 0f));

            probe.SetLocals(locals);

            AssertVectorNear(new Vector3(10f, 2f, 0f), probe.JointModel(InspectionFixture.HipsNode).Translation);
            AssertVectorNear(new Vector3(7f, 3f, 0f), probe.JointPosition("weapon_socket"));
        }

        [Fact]
        public void JointModel_MissingNamePreservesSkeletonDiagnostic()
        {
            var fixture = new InspectionFixture();
            var probe = new PoseProbe(fixture.Skeleton);

            ArgumentException error = Assert.Throws<ArgumentException>(() => probe.JointModel("Head"));

            Assert.Equal("nodeName", error.ParamName);
            Assert.StartsWith("Skeleton joint 'Head' was not found.", error.Message, StringComparison.Ordinal);
            Assert.Contains("weapon_socket", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SampleClip_RejectsNegativeDurationWithPreciseDiagnostic()
        {
            var fixture = new InspectionFixture();
            var probe = new PoseProbe(fixture.Skeleton);
            var broken = new AnimationClip("broken", -1f, new List<JointTrack>());

            ArgumentException error = Assert.Throws<ArgumentException>(() => probe.SampleClip(broken, 0.5f));

            Assert.Equal("clip", error.ParamName);
            Assert.StartsWith("Clip duration must be finite and non-negative.", error.Message, StringComparison.Ordinal);
        }

        static JointPose Pose(Vector3 translation) =>
            new() { Translation = translation, Rotation = Quaternion.Identity, Scale = Vector3.One };

        static void AssertVectorNear(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, Epsilon);
        }

        static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual)
        {
            Assert.InRange(MathF.Abs(expected.M11 - actual.M11), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M12 - actual.M12), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M13 - actual.M13), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M14 - actual.M14), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M21 - actual.M21), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M22 - actual.M22), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M23 - actual.M23), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M24 - actual.M24), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M31 - actual.M31), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M32 - actual.M32), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M33 - actual.M33), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M34 - actual.M34), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M41 - actual.M41), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M42 - actual.M42), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M43 - actual.M43), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.M44 - actual.M44), 0f, Epsilon);
        }
    }
}
