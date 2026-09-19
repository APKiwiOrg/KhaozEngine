using System;
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
        Assert.Equal(middle, ButcherSwing.PoseAt((cycle * 4f) + (cycle * 0.5f), cycle));
        Assert.Equal(middle, ButcherSwing.PoseAt((cycle * 8f) + (cycle * 0.5f), cycle));
    }

    [Fact]
    public void A_longer_harvest_runs_more_normal_speed_cycles_instead_of_stretching_one()
    {
        const float cycle = 0.8f;
        WalkPose quarter = ButcherSwing.PoseAt(cycle * 0.25f, cycle);

        Assert.Equal(quarter, ButcherSwing.PoseAt(3.2f + (cycle * 0.25f), cycle));
        Assert.Equal(quarter, ButcherSwing.PoseAt(6.4f + (cycle * 0.25f), cycle));
        Assert.Equal(ButcherSwing.PoseAt(3.2f, cycle), ButcherSwing.PoseAt(6.4f, cycle));
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
}
