using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class BoneSocketTests
    {
        const float Epsilon = 1e-5f;

        [Fact]
        public void Compose_AppliesLocalThenJointThenModel()
        {
            Matrix4x4 local = Matrix4x4.CreateTranslation(0f, 0f, 1f);
            Matrix4x4 jointModel = Matrix4x4.CreateRotationY(MathF.PI / 2f)
                * Matrix4x4.CreateTranslation(0f, 2f, 0f);
            Matrix4x4 model = Matrix4x4.CreateTranslation(10f, 0f, 0f);

            Matrix4x4 actual = BoneSocket.Compose(local, jointModel, model);

            AssertMatrixNear(local * jointModel * model, actual);
            AssertVectorNear(new Vector3(11f, 2f, 0f), Vector3.Transform(Vector3.Zero, actual));
        }

        [Fact]
        public void Compose_LocalRollThenJointPitchTipsLongAxisForwardNotSideways()
        {
            Matrix4x4 localRoll = Matrix4x4.CreateRotationY(MathF.PI / 2f);
            Matrix4x4 jointPitch = Matrix4x4.CreateRotationX(MathF.PI / 4f);

            Matrix4x4 actual = BoneSocket.Compose(localRoll, jointPitch, Matrix4x4.Identity);
            Vector3 longAxis = Vector3.TransformNormal(Vector3.UnitY, actual);

            Assert.InRange(MathF.Abs(longAxis.X), 0f, Epsilon);
            Assert.True(longAxis.Z > 0.5f, $"the piece tipped {longAxis.Z} along forward +Z");
            Assert.True(longAxis.Y > 0.5f, $"the piece lost its upward component at {longAxis.Y}");
        }

        [Fact]
        public void ComposeRigid_EqualsComposeForRigidJointMatrix()
        {
            Matrix4x4 local = Matrix4x4.CreateRotationZ(0.35f)
                * Matrix4x4.CreateTranslation(0.2f, -0.1f, 0.4f);
            Matrix4x4 jointModel = Matrix4x4.CreateFromYawPitchRoll(0.7f, -0.4f, 1.1f)
                * Matrix4x4.CreateTranslation(2f, 3f, 4f);
            Matrix4x4 model = Matrix4x4.CreateRotationY(-0.2f)
                * Matrix4x4.CreateTranslation(10f, 0f, -3f);

            AssertMatrixNear(
                BoneSocket.Compose(local, jointModel, model),
                BoneSocket.ComposeRigid(local, jointModel, model));
        }

        [Fact]
        public void ComposeRigid_RemovesJointScaleAndShearAndKeepsTranslation()
        {
            var jointModel = new Matrix4x4(
                2f, 0f, 0f, 0f,
                1f, 2f, 0f, 0f,
                0.5f, 0.25f, 2f, 0f,
                3f, 4f, 5f, 1f);

            Matrix4x4 actual = BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, Matrix4x4.Identity);
            Vector3 x = new(actual.M11, actual.M12, actual.M13);
            Vector3 y = new(actual.M21, actual.M22, actual.M23);
            Vector3 z = new(actual.M31, actual.M32, actual.M33);

            Assert.True(MathF.Abs(x.Length() - 1f) <= Epsilon, $"+X length was {x.Length()}");
            Assert.True(MathF.Abs(y.Length() - 1f) <= Epsilon, $"+Y length was {y.Length()}");
            Assert.True(MathF.Abs(z.Length() - 1f) <= Epsilon, $"+Z length was {z.Length()}");
            Assert.True(MathF.Abs(Vector3.Dot(x, y)) <= Epsilon, $"+X/+Y dot was {Vector3.Dot(x, y)}");
            Assert.True(MathF.Abs(Vector3.Dot(y, z)) <= Epsilon, $"+Y/+Z dot was {Vector3.Dot(y, z)}");
            Assert.True(MathF.Abs(Vector3.Dot(z, x)) <= Epsilon, $"+Z/+X dot was {Vector3.Dot(z, x)}");
            AssertVectorNear(jointModel.Translation, actual.Translation);
        }

        [Fact]
        public void ComposeRigid_ThrowsWhenJointXBasisIsZero()
        {
            var jointModel = new Matrix4x4(
                0f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                0f, 0f, 1f, 0f,
                3f, 4f, 5f, 1f);

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, Matrix4x4.Identity));

            Assert.Equal("jointModel", error.ParamName);
        }

        [Fact]
        public void ComposeRigid_ThrowsWhenJointYBasisDependsOnX()
        {
            var jointModel = new Matrix4x4(
                1f, 0f, 0f, 0f,
                2f, 0f, 0f, 0f,
                0f, 0f, 1f, 0f,
                3f, 4f, 5f, 1f);

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, Matrix4x4.Identity));

            Assert.Equal("jointModel", error.ParamName);
        }

        [Fact]
        public void ComposeRigid_ThrowsWhenJointZBasisDependsOnXAndY()
        {
            var jointModel = new Matrix4x4(
                1f, 0f, 0f, 0f,
                0f, 1f, 0f, 0f,
                1f, 1f, 0f, 0f,
                3f, 4f, 5f, 1f);

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, Matrix4x4.Identity));

            Assert.Equal("jointModel", error.ParamName);
        }

        [Fact]
        public void ComposeRigid_ThrowsWhenJointBasisIsNotFinite()
        {
            Matrix4x4 jointModel = Matrix4x4.Identity;
            jointModel.M23 = float.NaN;

            ArgumentException error = Assert.Throws<ArgumentException>(() =>
                BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, Matrix4x4.Identity));

            Assert.Equal("jointModel", error.ParamName);
        }

        [Fact]
        public void ComposeRigid_PreservesValidReflectedJointHandedness()
        {
            Matrix4x4 jointModel = Matrix4x4.CreateScale(-1f, 1f, 1f)
                * Matrix4x4.CreateFromYawPitchRoll(0.4f, -0.3f, 0.2f)
                * Matrix4x4.CreateTranslation(3f, 4f, 5f);

            Matrix4x4 actual = BoneSocket.ComposeRigid(Matrix4x4.Identity, jointModel, Matrix4x4.Identity);

            Assert.True(actual.GetDeterminant() < 0f, $"determinant was {actual.GetDeterminant()}");
            AssertMatrixNear(jointModel, actual);
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

        static void AssertVectorNear(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, Epsilon);
            Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, Epsilon);
        }
    }
}
