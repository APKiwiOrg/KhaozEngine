using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class AnimationSamplerTests
    {
        // ---- Track sampling (Task 3) ----

        [Fact]
        public void Vector3Track_Linear_InterpolatesMidpoint()
        {
            var t = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0) }, InterpolationMode.Linear);
            Assert.True(Vector3.Distance(t.Sample(0.5f), new Vector3(1, 0, 0)) < 1e-5f);
        }

        [Fact]
        public void Vector3Track_Step_HoldsLeftKey()
        {
            var t = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0) }, InterpolationMode.Step);
            Assert.True(Vector3.Distance(t.Sample(0.5f), new Vector3(0, 0, 0)) < 1e-5f);
            Assert.True(Vector3.Distance(t.Sample(1f), new Vector3(2, 0, 0)) < 1e-5f);
        }

        [Fact]
        public void Vector3Track_ClampsOutsideRange()
        {
            var t = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(0, 0, 0), new Vector3(2, 0, 0) }, InterpolationMode.Linear);
            Assert.True(Vector3.Distance(t.Sample(-1f), new Vector3(0, 0, 0)) < 1e-5f);
            Assert.True(Vector3.Distance(t.Sample(5f), new Vector3(2, 0, 0)) < 1e-5f);
        }

        [Fact]
        public void QuaternionTrack_Linear_StaysUnitLength()
        {
            var q0 = Quaternion.Identity;
            var q1 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.9f);
            var t = new QuaternionTrack(new[] { 0f, 1f }, new[] { q0, q1 }, InterpolationMode.Linear);
            Quaternion s = t.Sample(0.5f);
            Assert.True(MathF.Abs(s.Length() - 1f) < 1e-4f);
        }

        [Fact]
        public void JointTrack_SampleLocal_OverridesOnlyPresentChannels()
        {
            var rest = new JointPose { Translation = new Vector3(9, 9, 9), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f), Scale = new Vector3(2, 2, 2) };
            var jt = new JointTrack(targetNode: 7)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(0, 0, 0), new Vector3(4, 0, 0) }, InterpolationMode.Linear),
            };
            JointPose p = jt.SampleLocal(rest, 0.5f);
            Assert.True(Vector3.Distance(p.Translation, new Vector3(2, 0, 0)) < 1e-5f);   // from track
            Assert.Equal(rest.Rotation, p.Rotation);                                       // kept from rest
            Assert.Equal(rest.Scale, p.Scale);                                             // kept from rest
        }

        // ---- Sampler (Task 4) ----

        static Skeleton Chain2(out int node0Logical, out int node1Logical)
        {
            node0Logical = 100; node1Logical = 101;
            var parents = new[] { -1, 0 };
            var rest = new[]
            {
                JointPose.Identity,
                new JointPose { Translation = new Vector3(0, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            return new Skeleton(parents, rest, new[] { 100, 101 }, new[] { 0, 1 });
        }

        [Fact]
        public void SamplePose_EmptyClip_ReturnsRestLocals()
        {
            Skeleton s = Chain2(out _, out _);
            var clip = new AnimationClip("empty", 1f, new List<JointTrack>());
            JointPose[] poses = AnimationSampler.SamplePose(clip, s, 0.5f);
            Assert.Equal(2, poses.Length);
            Assert.True(Vector3.Distance(poses[1].Translation, new Vector3(0, 1, 0)) < 1e-5f);
        }

        [Fact]
        public void Compose_OfEmptyClip_EqualsRestPose()
        {
            Skeleton s = Chain2(out _, out _);
            var clip = new AnimationClip("empty", 1f, new List<JointTrack>());
            Matrix4x4[] palette = AnimationSampler.SampleToBonePalette(clip, s, 0.5f);
            Matrix4x4[] rest = s.ComposeRestPose();
            for (int i = 0; i < rest.Length; i++)
                Assert.True(Vector3.Distance(palette[i].Translation, rest[i].Translation) < 1e-5f);
        }

        [Fact]
        public void SampleToBonePalette_AnimatesChildWorldTranslation()
        {
            Skeleton s = Chain2(out _, out int node1Logical);
            // node1 translation goes (0,1,0) -> (0,3,0) over 1s; at 0.5 it is (0,2,0). Parent at origin -> bone1 world (0,2,0).
            var jt = new JointTrack(targetNode: node1Logical)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(0, 1, 0), new Vector3(0, 3, 0) }, InterpolationMode.Linear),
            };
            var clip = new AnimationClip("walk", 1f, new List<JointTrack> { jt });
            Matrix4x4[] palette = AnimationSampler.SampleToBonePalette(clip, s, 0.5f);
            Assert.True(Vector3.Distance(palette[1].Translation, new Vector3(0, 2, 0)) < 1e-5f, palette[1].Translation.ToString());
        }

        [Fact]
        public void BlendPoses_HalfwayAveragesTranslation()
        {
            var a = new[] { new JointPose { Translation = new Vector3(0, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } };
            var b = new[] { new JointPose { Translation = new Vector3(2, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One } };
            JointPose[] blended = AnimationSampler.BlendPoses(a, b, 0.5f);
            Assert.True(Vector3.Distance(blended[0].Translation, new Vector3(1, 0, 0)) < 1e-5f);
        }

        [Fact]
        public void Wrap_LoopsAndHandlesZeroDuration()
        {
            Assert.True(MathF.Abs(AnimationSampler.Wrap(1.2f, 1f) - 0.2f) < 1e-5f);
            Assert.True(MathF.Abs(AnimationSampler.Wrap(-0.1f, 1f) - 0.9f) < 1e-5f);
            Assert.Equal(0f, AnimationSampler.Wrap(3f, 0f));
        }
    }
}
