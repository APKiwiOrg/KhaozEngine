using System;
using System.Numerics;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    public class CharacterPoseSocketTests
    {
        [Fact]
        public void ComposeSocket_uses_the_selected_bone_then_the_character_world()
        {
            Matrix4x4 local = Matrix4x4.CreateTranslation(0f, 0f, 1f);
            Matrix4x4 joint = Matrix4x4.CreateRotationY(MathF.PI / 2f)
                * Matrix4x4.CreateTranslation(0f, 2f, 0f);
            Matrix4x4 model = Matrix4x4.CreateTranslation(10f, 0f, 0f);
            CharacterPose pose = Pose(model, Matrix4x4.CreateTranslation(99f, 0f, 0f), joint);

            Matrix4x4 actual = pose.ComposeSocket(1, local);

            Assert.Equal(local * joint * model, actual);
            AssertVectorNear(new Vector3(11f, 2f, 0f), Vector3.Transform(Vector3.Zero, actual));
        }

        [Fact]
        public void ComposeRigidSocket_removes_joint_scale_but_keeps_character_model_scale()
        {
            Matrix4x4 joint = Matrix4x4.CreateScale(2f, 3f, 4f)
                * Matrix4x4.CreateTranslation(1f, 0f, 0f);
            Matrix4x4 model = Matrix4x4.CreateScale(2f)
                * Matrix4x4.CreateTranslation(10f, 0f, 0f);
            CharacterPose pose = Pose(model, joint);

            Matrix4x4 rigid = pose.ComposeRigidSocket(0, Matrix4x4.CreateTranslation(0f, 0f, 1f));

            AssertVectorNear(new Vector3(12f, 0f, 2f), rigid.Translation);
            Assert.InRange(Vector3.TransformNormal(Vector3.UnitZ, rigid).Length(), 1.99999f, 2.00001f);
        }

        [Fact]
        public void ComposeSocket_rejects_invalid_bone_indices()
        {
            CharacterPose pose = Pose(Matrix4x4.Identity, Matrix4x4.Identity);

            Assert.Equal("boneIndex", Assert.Throws<ArgumentOutOfRangeException>(() =>
                pose.ComposeSocket(-1, Matrix4x4.Identity)).ParamName);
            Assert.Equal("boneIndex", Assert.Throws<ArgumentOutOfRangeException>(() =>
                pose.ComposeRigidSocket(1, Matrix4x4.Identity)).ParamName);
        }

        [Fact]
        public void Default_pose_reports_the_missing_palette()
        {
            CharacterPose pose = default;

            Assert.Throws<InvalidOperationException>(() => pose.ComposeSocket(0, Matrix4x4.Identity));
            Assert.Throws<InvalidOperationException>(() => pose.ComposeRigidSocket(0, Matrix4x4.Identity));
        }

        [Fact]
        public void ComposeSocket_reads_the_current_transient_palette_value()
        {
            Matrix4x4[] joints = { Matrix4x4.CreateTranslation(1f, 0f, 0f) };
            CharacterPose pose = new(7, Matrix4x4.Identity, 0f, joints, LocomotionState.Idle, true);
            Vector3 first = pose.ComposeSocket(0, Matrix4x4.Identity).Translation;
            joints[0] = Matrix4x4.CreateTranslation(2f, 0f, 0f);

            Assert.Equal(new Vector3(1f, 0f, 0f), first);
            Assert.Equal(new Vector3(2f, 0f, 0f), pose.ComposeSocket(0, Matrix4x4.Identity).Translation);
        }

        static CharacterPose Pose(Matrix4x4 model, params Matrix4x4[] joints) =>
            new(7, model, 0f, joints, LocomotionState.Idle, true);

        static void AssertVectorNear(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-5f);
        }
    }
}
