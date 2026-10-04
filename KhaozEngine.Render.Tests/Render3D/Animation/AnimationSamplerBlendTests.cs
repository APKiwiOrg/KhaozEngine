using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    internal static class BlendFixture
    {
        // Two nodes, as AnimationSamplerTests.Chain2 builds them.
        public static Skeleton Chain2()
        {
            var parents = new[] { -1, 0 };
            var rest = new[]
            {
                JointPose.Identity,
                new JointPose { Translation = new Vector3(0, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            return new Skeleton(parents, rest, new[] { 100, 101 }, new[] { 0, 1 });
        }

        // Moves node 1 (logical 101) from (0,1,0) to (0,3,0) over one second.
        public static AnimationClip Lift() => new("lift", 1f, new List<JointTrack>
        {
            new(targetNode: 101)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(0, 1, 0), new Vector3(0, 3, 0) }, InterpolationMode.Linear),
            },
        });

        // Turns node 0 (logical 100) half a revolution about Y over one second.
        public static AnimationClip Turn() => new("turn", 1f, new List<JointTrack>
        {
            new(targetNode: 100)
            {
                Rotation = new QuaternionTrack(new[] { 0f, 1f },
                    new[] { Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.9f) },
                    InterpolationMode.Linear),
            },
        });
    }

    public class AnimationSamplerBlendTests
    {
        static readonly JointPose Marker = new()
        {
            Translation = new Vector3(7, -7, 7),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.25f),
            Scale = new Vector3(3, 3, 3),
        };

        static void AssertBitIdentical(JointPose[] expected, JointPose[] actual)
        {
            Assert.Equal(expected.Length, actual.Length);
            for (int n = 0; n < expected.Length; n++)
            {
                JointPose e = expected[n], a = actual[n];
                Assert.Equal(Bits(e.Translation.X), Bits(a.Translation.X));
                Assert.Equal(Bits(e.Translation.Y), Bits(a.Translation.Y));
                Assert.Equal(Bits(e.Translation.Z), Bits(a.Translation.Z));
                Assert.Equal(Bits(e.Rotation.X), Bits(a.Rotation.X));
                Assert.Equal(Bits(e.Rotation.Y), Bits(a.Rotation.Y));
                Assert.Equal(Bits(e.Rotation.Z), Bits(a.Rotation.Z));
                Assert.Equal(Bits(e.Rotation.W), Bits(a.Rotation.W));
                Assert.Equal(Bits(e.Scale.X), Bits(a.Scale.X));
                Assert.Equal(Bits(e.Scale.Y), Bits(a.Scale.Y));
                Assert.Equal(Bits(e.Scale.Z), Bits(a.Scale.Z));
            }
        }

        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);

        static JointPose[] Sampled(AnimationClip clip, Skeleton skel, float time)
        {
            var poses = new JointPose[skel.NodeCount];
            AnimationSampler.SampleInto(clip, skel, time, poses);
            return poses;
        }

        [Fact]
        public void OneFullWeightSampleIsBitIdenticalToSampleInto()
        {
            Skeleton skel = BlendFixture.Chain2();
            AnimationClip lift = BlendFixture.Lift();
            var into = new JointPose[skel.NodeCount];
            var scratch = new JointPose[skel.NodeCount];

            AnimationSampler.SampleBlendInto(skel, new[] { new ClipSample(lift, 0.37f, 1f) }, into, scratch);

            AssertBitIdentical(Sampled(lift, skel, 0.37f), into);
        }

        [Fact]
        public void TwoSamplesEqualJointPoseLerp()
        {
            Skeleton skel = BlendFixture.Chain2();
            AnimationClip lift = BlendFixture.Lift(), turn = BlendFixture.Turn();
            var into = new JointPose[skel.NodeCount];
            var scratch = new JointPose[skel.NodeCount];

            AnimationSampler.SampleBlendInto(skel,
                new[] { new ClipSample(lift, 0.25f, 0.3f), new ClipSample(turn, 0.6f, 0.9f) }, into, scratch);

            JointPose[] a = Sampled(lift, skel, 0.25f), b = Sampled(turn, skel, 0.6f);
            var expected = new JointPose[skel.NodeCount];
            for (int n = 0; n < expected.Length; n++) expected[n] = JointPose.Lerp(a[n], b[n], 0.9f / 1.2f);
            AssertBitIdentical(expected, into);
        }

        [Fact]
        public void ZeroTotalWeightLeavesTheBase()
        {
            Skeleton skel = BlendFixture.Chain2();
            var into = new[] { Marker, Marker };
            var scratch = new JointPose[skel.NodeCount];

            AnimationSampler.SampleBlendInto(skel,
                new[] { new ClipSample(BlendFixture.Lift(), 0.5f, 0f), new ClipSample(BlendFixture.Turn(), 0.5f, 0f) },
                into, scratch);
            AssertBitIdentical(new[] { Marker, Marker }, into);

            AnimationSampler.SampleBlendInto(skel, ReadOnlySpan<ClipSample>.Empty, into, scratch);
            AssertBitIdentical(new[] { Marker, Marker }, into);
        }

        [Fact]
        public void ZeroWeightSamplesAreSkipped()
        {
            Skeleton skel = BlendFixture.Chain2();
            AnimationClip lift = BlendFixture.Lift(), turn = BlendFixture.Turn();
            var into = new JointPose[skel.NodeCount];
            var scratch = new JointPose[skel.NodeCount];

            AnimationSampler.SampleBlendInto(skel,
                new[] { new ClipSample(turn, 0.5f, 0f), new ClipSample(lift, 0.4f, 1f), new ClipSample(turn, 0.8f, 0f) },
                into, scratch);

            AssertBitIdentical(Sampled(lift, skel, 0.4f), into);
        }

        [Fact]
        public void InvalidBuffersAndWeightsAreRefused()
        {
            Skeleton skel = BlendFixture.Chain2();
            AnimationClip lift = BlendFixture.Lift();
            var one = new[] { new ClipSample(lift, 0f, 1f) };
            var into = new JointPose[skel.NodeCount];
            var scratch = new JointPose[skel.NodeCount];

            Assert.ThrowsAny<ArgumentException>(() => AnimationSampler.SampleBlendInto(skel, one, new JointPose[1], scratch));
            Assert.ThrowsAny<ArgumentException>(() => AnimationSampler.SampleBlendInto(skel, one, into, new JointPose[3]));
            Assert.ThrowsAny<ArgumentException>(() => AnimationSampler.SampleBlendInto(skel, one, into, into));
            Assert.ThrowsAny<ArgumentException>(() =>
                AnimationSampler.SampleBlendInto(skel, new[] { new ClipSample(lift, 0f, -1f) }, into, scratch));
            Assert.ThrowsAny<ArgumentException>(() =>
                AnimationSampler.SampleBlendInto(skel, new[] { new ClipSample(lift, 0f, float.NaN) }, into, scratch));
            Assert.ThrowsAny<ArgumentException>(() =>
                AnimationSampler.SampleBlendInto(skel, new[] { new ClipSample(lift, 0f, float.PositiveInfinity) }, into, scratch));
            Assert.ThrowsAny<ArgumentException>(() =>
                AnimationSampler.SampleBlendInto(skel, new[] { new ClipSample(null!, 0f, 1f) }, into, scratch));
            Assert.Throws<ArgumentNullException>(() => AnimationSampler.SampleBlendInto(null!, one, into, scratch));
            Assert.Throws<ArgumentNullException>(() => AnimationSampler.SampleBlendInto(skel, one, null!, scratch));
            Assert.Throws<ArgumentNullException>(() => AnimationSampler.SampleBlendInto(skel, one, into, null!));
        }

        [Fact]
        public void ARefusedSampleLeavesIntoUntouched()
        {
            Skeleton skel = BlendFixture.Chain2();
            var into = new[] { Marker, Marker };
            var scratch = new JointPose[skel.NodeCount];

            Assert.ThrowsAny<ArgumentException>(() => AnimationSampler.SampleBlendInto(skel,
                new[] { new ClipSample(BlendFixture.Lift(), 0.5f, 1f), new ClipSample(BlendFixture.Turn(), 0.5f, float.NaN) },
                into, scratch));

            AssertBitIdentical(new[] { Marker, Marker }, into);
        }
    }

    [Collection("AllocSensitive")]
    public class AnimationSamplerBlendAllocationTests
    {
        [Fact]
        public void SampleBlendIntoAllocatesNothing()
        {
            Skeleton skel = BlendFixture.Chain2();
            var samples = new[] { new ClipSample(BlendFixture.Lift(), 0.25f, 0.4f), new ClipSample(BlendFixture.Turn(), 0.6f, 0.6f) };
            var into = new JointPose[skel.NodeCount];
            var scratch = new JointPose[skel.NodeCount];
            AnimationSampler.SampleBlendInto(skel, samples, into, scratch);   // warm up

            AllocAssert.NoPerCallAllocation("SampleBlendInto over 20 two-sample blends", () =>
            {
                for (int i = 0; i < 20; i++) AnimationSampler.SampleBlendInto(skel, samples, into, scratch);
            });
        }
    }
}
