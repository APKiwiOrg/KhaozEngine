using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class JointPoseTests
    {
        [Fact]
        public void Identity_IsIdentityMatrix()
        {
            Assert.Equal(Matrix4x4.Identity, JointPose.Identity.ToMatrix());
        }

        [Fact]
        public void ToMatrix_TranslatesByT()
        {
            var p = new JointPose
            {
                Translation = new Vector3(1, 2, 3),
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            };
            Assert.Equal(new Vector3(1, 2, 3), p.ToMatrix().Translation);
        }

        [Fact]
        public void ToMatrix_ScaleThenRotateThenTranslate()
        {
            // 90deg about Y, scale 2, translate +X. A local +X point (1,0,0) -> scale 2 -> (2,0,0)
            // -> rotate 90 about Y -> (0,0,-2) -> translate (5,0,0) -> (5,0,-2).
            var p = new JointPose
            {
                Translation = new Vector3(5, 0, 0),
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f),
                Scale = new Vector3(2, 2, 2),
            };
            Vector3 v = Vector3.Transform(new Vector3(1, 0, 0), p.ToMatrix());
            Assert.True(Vector3.Distance(v, new Vector3(5, 0, -2)) < 1e-4f, v.ToString());
        }

        [Fact]
        public void Lerp_AtEndpoints_ReturnsEndpoints()
        {
            var a = new JointPose { Translation = new Vector3(0, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One };
            var b = new JointPose { Translation = new Vector3(2, 4, 6), Rotation = Quaternion.Identity, Scale = new Vector3(3, 3, 3) };
            Assert.True(Vector3.Distance(JointPose.Lerp(a, b, 0f).Translation, a.Translation) < 1e-5f);
            Assert.True(Vector3.Distance(JointPose.Lerp(a, b, 1f).Translation, b.Translation) < 1e-5f);
        }

        [Fact]
        public void Lerp_Midpoint_AveragesTranslationAndScale()
        {
            var a = new JointPose { Translation = new Vector3(0, 0, 0), Rotation = Quaternion.Identity, Scale = new Vector3(1, 1, 1) };
            var b = new JointPose { Translation = new Vector3(2, 0, 0), Rotation = Quaternion.Identity, Scale = new Vector3(3, 3, 3) };
            JointPose m = JointPose.Lerp(a, b, 0.5f);
            Assert.True(Vector3.Distance(m.Translation, new Vector3(1, 0, 0)) < 1e-5f);
            Assert.True(Vector3.Distance(m.Scale, new Vector3(2, 2, 2)) < 1e-5f);
        }

        [Fact]
        public void Lerp_Rotation_StaysUnitLength()
        {
            var a = new JointPose { Translation = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
            var b = new JointPose
            {
                Translation = Vector3.Zero,
                Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.8f),
                Scale = Vector3.One,
            };
            Quaternion q = JointPose.Lerp(a, b, 0.5f).Rotation;
            Assert.True(MathF.Abs(q.Length() - 1f) < 1e-4f, q.Length().ToString());
        }
    }
}
