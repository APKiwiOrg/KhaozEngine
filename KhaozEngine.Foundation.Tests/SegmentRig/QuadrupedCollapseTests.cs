using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

public class QuadrupedCollapseTests
{
    static readonly QuadrupedPose Collapsed = new(
        LeftForeSwing: 0.75f, RightForeSwing: -0.35f,
        LeftHindSwing: -0.55f, RightHindSwing: 0.45f,
        LeftForeFlex: 1.2f, RightForeFlex: 0.85f,
        LeftHindFlex: 0.95f, RightHindFlex: 0.65f,
        HeadNod: 0.8f,
        RootOffset: new Vector3(0.08f, -0.42f, 0.04f), RootRoll: 1.22f,
        LeftForeSplay: 0.22f, RightForeSplay: 0.12f,
        LeftHindSplay: 0.16f, RightHindSplay: 0.28f);

    [Fact]
    public void Collapse_has_stable_start_midpoint_and_caller_authored_resting_endpoint()
    {
        Assert.Equal(QuadrupedPose.Rest, QuadrupedCollapse.PoseAt(0f, Collapsed));
        Assert.Equal(QuadrupedPose.Rest, QuadrupedCollapse.PoseAt(-1f, Collapsed));
        Assert.Equal(Collapsed, QuadrupedCollapse.PoseAt(1f, Collapsed));
        Assert.Equal(Collapsed, QuadrupedCollapse.PoseAt(2f, Collapsed));

        QuadrupedPose mid = QuadrupedCollapse.PoseAt(0.5f, Collapsed);
        Assert.Equal(Collapsed.RootOffset * 0.5f, mid.RootOffset);
        Assert.Equal(Collapsed.RootRoll * 0.5f, mid.RootRoll, 5);
        Assert.Equal(Collapsed.HeadNod * 0.5f, mid.HeadNod, 5);
        Assert.Equal(Collapsed.LeftForeFlex * 0.5f, mid.LeftForeFlex, 5);
        Assert.NotEqual(mid.LeftForeFlex, mid.RightForeFlex);
    }

    [Fact]
    public void Elapsed_sampling_reaches_the_same_endpoint_and_refuses_a_broken_duration()
    {
        Assert.Equal(QuadrupedPose.Rest, QuadrupedCollapse.PoseAt(0f, 0.8f, Collapsed));
        Assert.Equal(QuadrupedCollapse.PoseAt(0.5f, Collapsed),
            QuadrupedCollapse.PoseAt(0.4f, 0.8f, Collapsed));
        Assert.Equal(Collapsed, QuadrupedCollapse.PoseAt(0.8f, 0.8f, Collapsed));
        Assert.Equal(Collapsed, QuadrupedCollapse.PoseAt(8f, 0.8f, Collapsed));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuadrupedCollapse.PoseAt(0f, 0f, Collapsed));
        Assert.Throws<ArgumentOutOfRangeException>(() => QuadrupedCollapse.PoseAt(0f, float.NaN, Collapsed));
    }

    [Fact]
    public void Finite_elapsed_time_past_a_tiny_duration_reaches_the_resting_pose_without_overflow()
    {
        Assert.Equal(Collapsed, QuadrupedCollapse.PoseAt(1f, float.Epsilon, Collapsed));
        Assert.Equal(QuadrupedPose.Rest, QuadrupedCollapse.PoseAt(-1f, float.Epsilon, Collapsed));
    }

    [Fact]
    public void Start_mid_and_end_compose_to_finite_unscaled_joint_attached_transforms()
    {
        QuadrupedRig rig = TestBodies.Grazer;
        foreach (float progress in new[] { 0f, 0.5f, 1f })
        {
            QuadrupedPose pose = QuadrupedCollapse.PoseAt(progress, Collapsed);
            var pieces = new Matrix4x4[QuadrupedSkeleton.PieceCount];
            QuadrupedSkeleton.Compose(rig, BodyPose.Origin, WalkPose.Rest, pose, pieces);

            for (int i = 0; i < pieces.Length; i++)
            {
                AssertFinite(pieces[i]);
                Assert.True(Matrix4x4.Decompose(pieces[i], out Vector3 scale, out _, out _));
                Assert.Equal(Vector3.One, scale, new Vector3Comparer(0.0001f));
            }

            AssertJoint(rig.ForeHingeFromShoulder, pieces[QuadrupedSkeleton.UpperForeLeft],
                pieces[QuadrupedSkeleton.LowerForeLeft]);
            AssertJoint(rig.ForeHingeFromShoulder, pieces[QuadrupedSkeleton.UpperForeRight],
                pieces[QuadrupedSkeleton.LowerForeRight]);
            AssertJoint(rig.HindHingeFromHip, pieces[QuadrupedSkeleton.UpperHindLeft],
                pieces[QuadrupedSkeleton.LowerHindLeft]);
            AssertJoint(rig.HindHingeFromHip, pieces[QuadrupedSkeleton.UpperHindRight],
                pieces[QuadrupedSkeleton.LowerHindRight]);
        }
    }

    [Fact]
    public void Equal_splay_angles_mirror_outward_on_left_and_right_legs()
    {
        QuadrupedRig rig = TestBodies.Grazer;
        var pose = QuadrupedPose.Rest with { LeftForeSplay = 0.3f, RightForeSplay = 0.3f };
        Span<Matrix4x4> pieces = stackalloc Matrix4x4[QuadrupedSkeleton.PieceCount];
        QuadrupedSkeleton.Compose(rig, BodyPose.Origin, WalkPose.Rest, pose, pieces);

        Vector3 left = Vector3.Transform(new Vector3(0f, -0.5f, 0f),
            pieces[QuadrupedSkeleton.UpperForeLeft]);
        Vector3 right = Vector3.Transform(new Vector3(0f, -0.5f, 0f),
            pieces[QuadrupedSkeleton.UpperForeRight]);
        float leftOutward = left.X - rig.LeftShoulder.X;
        float rightOutward = right.X - rig.RightShoulder.X;

        Assert.True(leftOutward > 0f);
        Assert.True(rightOutward < 0f);
        Assert.Equal(leftOutward, -rightOutward, 5);
    }

    static void AssertJoint(Vector3 hinge, Matrix4x4 upper, Matrix4x4 lower)
    {
        Vector3 expected = Vector3.Transform(hinge, upper);
        Assert.Equal(expected, lower.Translation, new Vector3Comparer(0.0001f));
    }

    static void AssertFinite(Matrix4x4 matrix)
    {
        Assert.All(new[]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        }, value => Assert.True(float.IsFinite(value)));
    }

    sealed class Vector3Comparer(float tolerance) : System.Collections.Generic.IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y) => Vector3.Distance(x, y) <= tolerance;

        public int GetHashCode(Vector3 value) => value.GetHashCode();
    }
}
