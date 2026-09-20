using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

public class ButcherSwingTests
{
    [Fact]
    public void The_cutting_pose_loops_on_elapsed_seconds_at_the_selected_cycle_duration()
    {
        const float cycle = 0.8f;

        WalkPose start = ButcherSwing.PoseAt(0f, cycle);
        WalkPose middle = ButcherSwing.PoseAt(cycle * 0.5f, cycle);
        WalkPose end = ButcherSwing.PoseAt(cycle, cycle);

        Assert.Equal(start, end);
        Assert.NotEqual(start, middle);
        AssertPoseClose(middle, ButcherSwing.PoseAt((cycle * 4f) + (cycle * 0.5f), cycle));
        AssertPoseClose(middle, ButcherSwing.PoseAt((cycle * 8f) + (cycle * 0.5f), cycle));
    }

    [Fact]
    public void A_longer_harvest_runs_more_normal_speed_cycles_instead_of_stretching_one()
    {
        const float cycle = 0.8f;
        WalkPose quarter = ButcherSwing.PoseAt(cycle * 0.25f, cycle);

        AssertPoseClose(quarter, ButcherSwing.PoseAt(3.2f + (cycle * 0.25f), cycle));
        AssertPoseClose(quarter, ButcherSwing.PoseAt(6.4f + (cycle * 0.25f), cycle));
        AssertPoseClose(ButcherSwing.PoseAt(3.2f, cycle), ButcherSwing.PoseAt(6.4f, cycle));
    }

    [Fact]
    public void The_loop_is_finite_and_refuses_nonpositive_or_nonfinite_cycle_durations()
    {
        foreach (float elapsed in new[] { 0f, 0.2f, 0.4f, 0.8f, 12.3f, float.NaN })
        {
            WalkPose pose = ButcherSwing.PoseAt(elapsed, 0.8f);
            Assert.All(new[]
            {
                pose.LeftArm, pose.RightArm, pose.LeftElbow, pose.RightElbow,
                pose.LeftArmYaw, pose.RightArmYaw, pose.LeftWrist, pose.RightWrist, pose.TorsoLean,
            }, value => Assert.True(float.IsFinite(value)));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ButcherSwing.PoseAt(0f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ButcherSwing.PoseAt(0f, -1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ButcherSwing.PoseAt(0f, float.PositiveInfinity));
    }

    [Fact]
    public void A_finite_elapsed_time_cannot_overflow_a_tiny_cycle_into_nonfinite_channels()
    {
        WalkPose positive = ButcherSwing.PoseAt(1f, float.Epsilon);
        WalkPose negative = ButcherSwing.PoseAt(-1f, float.Epsilon);

        AssertFinite(positive);
        AssertFinite(negative);
        Assert.Equal(ButcherSwing.PoseAt(0f, float.Epsilon), positive);
        Assert.Equal(ButcherSwing.PoseAt(0f, float.Epsilon), negative);
        AssertPoseClose(ButcherSwing.PoseAt(0.6f, 0.8f), ButcherSwing.PoseAt(-0.2f, 0.8f));
    }

    [Fact]
    public void Compose_blends_only_the_cutting_channels()
    {
        var under = new WalkPose(0.1f, -0.1f, 0.2f, -0.2f, Bob: -0.03f, Lean: 0.08f,
            RootPitch: 0.2f, RootRoll: -0.1f);

        Assert.Equal(under, ButcherSwing.Compose(under, 0.4f, 0.8f, 0f));
        WalkPose full = ButcherSwing.Compose(under, 0.4f, 0.8f, 1f);

        Assert.Equal(under.LeftLeg, full.LeftLeg);
        Assert.Equal(under.RightLeg, full.RightLeg);
        Assert.Equal(under.Bob, full.Bob);
        Assert.Equal(under.Lean, full.Lean);
        Assert.Equal(under.RootPitch, full.RootPitch);
        Assert.Equal(under.RootRoll, full.RootRoll);
        Assert.NotEqual(under.RightArm, full.RightArm);
        Assert.NotEqual(under.LeftArm, full.LeftArm);
        Assert.True(full.TorsoLean > under.TorsoLean);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.4f)]
    [InlineData(0.8f)]
    public void Contact_points_a_standard_blade_down_and_forward_across_common_grip_angles(float gripAngle)
    {
        const float cycle = 0.8f;
        WalkPose contact = ButcherSwing.PoseAt(cycle * 0.5f, cycle);
        Span<Matrix4x4> at = stackalloc Matrix4x4[HumanoidSkeleton.PieceCount];
        HumanoidSkeleton.Compose(BodyRig.Human, BodyPose.Origin, contact, at);
        Matrix4x4 held = SegmentSockets.Held(
            BodyRig.Human,
            Matrix4x4.CreateRotationX(gripAngle),
            at[HumanoidSkeleton.ForearmRight],
            contact.RightWrist);

        Vector3 grip = Vector3.Transform(Vector3.Zero, held);
        Vector3 tip = Vector3.Transform(new Vector3(0f, 0.45f, 0f), held);
        Vector3 blade = Vector3.Normalize(tip - grip);

        Assert.True(tip.Y < grip.Y - 0.15f,
            $"the contact tip is at {tip.Y} above a grip at {grip.Y} for angle {gripAngle}");
        Assert.True(tip.Z > grip.Z + 0.08f,
            $"the contact tip is at {tip.Z} behind a grip at {grip.Z} for angle {gripAngle}");
        Assert.True(Vector3.Dot(blade, -Vector3.UnitY) > 0.35f,
            $"the contact blade points {blade} instead of down for angle {gripAngle}");
    }

    static void AssertFinite(in WalkPose pose)
    {
        Assert.All(new[]
        {
            pose.LeftArm, pose.RightArm, pose.LeftElbow, pose.RightElbow,
            pose.LeftArmYaw, pose.RightArmYaw, pose.LeftWrist, pose.RightWrist, pose.TorsoLean,
        }, value => Assert.True(float.IsFinite(value)));
    }

    static void AssertPoseClose(in WalkPose expected, in WalkPose actual)
    {
        float[] expectedChannels =
        [
            expected.LeftArm, expected.RightArm, expected.LeftLeg, expected.RightLeg,
            expected.LeftElbow, expected.RightElbow, expected.LeftKnee, expected.RightKnee,
            expected.Bob, expected.Lean, expected.RightArmYaw, expected.RightWrist,
            expected.TorsoRise, expected.TorsoLean, expected.LeftArmYaw, expected.LeftWrist,
            expected.RootPitch, expected.RootRoll,
        ];
        float[] actualChannels =
        [
            actual.LeftArm, actual.RightArm, actual.LeftLeg, actual.RightLeg,
            actual.LeftElbow, actual.RightElbow, actual.LeftKnee, actual.RightKnee,
            actual.Bob, actual.Lean, actual.RightArmYaw, actual.RightWrist,
            actual.TorsoRise, actual.TorsoLean, actual.LeftArmYaw, actual.LeftWrist,
            actual.RootPitch, actual.RootRoll,
        ];
        for (int i = 0; i < expectedChannels.Length; i++)
            Assert.InRange(MathF.Abs(expectedChannels[i] - actualChannels[i]), 0f, 1e-5f);
    }
}
