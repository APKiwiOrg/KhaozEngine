using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// THE POSITIONAL CONTRACT on <see cref="WalkPose"/>, and the two channels appended to the end of it for a
/// consumer whose bodies leave the ground.
/// </summary>
/// <remarks>
/// A game builds poses of its own POSITIONALLY, so a channel inserted anywhere but the end silently
/// re-points every one of them at the wrong number and nothing fails to compile. That is why this suite
/// exists: it pins the ORDER rather than any single value, by constructing a pose with every channel set to
/// a distinct number positionally and reading each one back by name.
/// <para><see cref="WalkPose.RootPitch"/> and <see cref="WalkPose.RootRoll"/> are the current tail. They tip
/// the WHOLE body about the point it stands on, which is what a body with nothing under its soles does, and
/// nothing that was already producing poses writes either of them.</para>
/// </remarks>
public class WalkPoseChannelTests
{
    [Fact]
    public void TheAppendedRootChannelsDefaultToZeroAndDoNotDisturbTheSixteenBeforeThem()
    {
        // Constructed POSITIONALLY with the sixteen channels that existed before the append, each a distinct
        // number, and read back by NAME. An insertion anywhere in the list shifts every name past it.
        var pose = new WalkPose(
            1f, 2f, 3f, 4f,
            5f, 6f, 7f, 8f,
            9f, 10f, 11f, 12f,
            13f, 14f, 15f, 16f);

        Assert.Equal(1f, pose.LeftArm);
        Assert.Equal(2f, pose.RightArm);
        Assert.Equal(3f, pose.LeftLeg);
        Assert.Equal(4f, pose.RightLeg);
        Assert.Equal(5f, pose.LeftElbow);
        Assert.Equal(6f, pose.RightElbow);
        Assert.Equal(7f, pose.LeftKnee);
        Assert.Equal(8f, pose.RightKnee);
        Assert.Equal(9f, pose.Bob);
        Assert.Equal(10f, pose.Lean);
        Assert.Equal(11f, pose.RightArmYaw);
        Assert.Equal(12f, pose.RightWrist);
        Assert.Equal(13f, pose.TorsoRise);
        Assert.Equal(14f, pose.TorsoLean);
        Assert.Equal(15f, pose.LeftArmYaw);
        Assert.Equal(16f, pose.LeftWrist);

        // The appended pair defaults to zero, so a producer written before they existed is unchanged.
        Assert.Equal(0f, pose.RootPitch);
        Assert.Equal(0f, pose.RootRoll);

        // And they really are the LAST two, in this order.
        var tipped = new WalkPose(
            1f, 2f, 3f, 4f,
            5f, 6f, 7f, 8f,
            9f, 10f, 11f, 12f,
            13f, 14f, 15f, 16f,
            17f, 18f);
        Assert.Equal(17f, tipped.RootPitch);
        Assert.Equal(18f, tipped.RootRoll);
        Assert.Equal(pose with { RootPitch = 17f, RootRoll = 18f }, tipped);
    }

    [Fact]
    public void EveryExistingProducerLeavesTheRootChannelsAtZero()
    {
        // The rest pose, the walk at every phase, and the breath at every phase. None of them tips a body
        // about its own feet, because all three have ground under them by definition.
        Assert.Equal(0f, WalkPose.Rest.RootPitch);
        Assert.Equal(0f, WalkPose.Rest.RootRoll);

        const float dt = 1f / 60f;
        var cycle = new WalkCycle(BodyRig.Human);
        var at = Vector3.Zero;
        cycle.Advance(at, dt);
        for (int i = 0; i < 300; i++)
        {
            at += new Vector3(0f, 0f, 2f * dt);
            cycle.Advance(at, dt);
            Assert.Equal(0f, cycle.Pose.RootPitch);
            Assert.Equal(0f, cycle.Pose.RootRoll);
        }

        for (float t = 0f; t <= IdleBreath.PeriodSeconds; t += 0.02f)
        {
            WalkPose breath = IdleBreath.PoseAt(t, 99L);
            Assert.Equal(0f, breath.RootPitch);
            Assert.Equal(0f, breath.RootRoll);
            // And composing one over a pose that DOES tip leaves the tip alone, because the breath writes
            // neither channel and Compose only ever adds the ones it names.
            WalkPose swimming = WalkPose.Rest with { RootPitch = 1.4f, RootRoll = -0.2f };
            WalkPose laid = IdleBreath.Compose(swimming, breath, 1f);
            Assert.Equal(1.4f, laid.RootPitch);
            Assert.Equal(-0.2f, laid.RootRoll);
        }
    }

    /// <summary>
    /// A zero root tilt composes to EXACTLY the matrix it always did, bit for bit. That is the whole reason
    /// the rig skips the pair rather than multiplying by an identity: a float identity multiply is not
    /// guaranteed to be a no-op, and every grounded frame in both consumers goes through this path.
    /// </summary>
    [Fact]
    public void AGroundedBodyComposesToTheSameBitsItAlwaysDid()
    {
        BodyRig rig = BodyRig.Human;
        var pose = new BodyPose(new Vector3(7.25f, 1.5f, -3.75f), 0.9f);
        var walking = new WalkPose(
            LeftArm: 0.2f, RightArm: -0.2f, LeftLeg: -0.35f, RightLeg: 0.35f,
            LeftElbow: 0.31f, RightElbow: 0.4f, LeftKnee: 0.55f, RightKnee: 0f,
            Bob: -0.018f, Lean: 0.1f);

        Matrix4x4 expected = Matrix4x4.CreateTranslation(0f, -rig.RightHip.Y, 0f)
            * Matrix4x4.CreateRotationX(walking.Lean)
            * Matrix4x4.CreateTranslation(0f, rig.RightHip.Y, 0f)
            * Matrix4x4.CreateRotationY(pose.Yaw)
            * Matrix4x4.CreateTranslation(pose.Position + new Vector3(0f, walking.Bob, 0f));

        Assert.Equal(expected, rig.Body(pose, walking));
        Assert.Equal(expected, rig.Body(pose, walking with { RootPitch = 0f, RootRoll = 0f }));
    }

    /// <summary>
    /// THE ROOT TILT TURNS THE WHOLE BODY ABOUT THE POINT IT STANDS ON, which is the difference from
    /// <see cref="WalkPose.Lean"/> and the reason a swim or a fall needs it. Pitched a quarter turn, a body
    /// standing at the origin lies out flat with its neck base a body's height AHEAD of where it stood
    /// rather than above it, the legs turn with it, and the contact point is exactly where it was. A LEAN of
    /// the same size folds at the HIPS instead, which swings the soles right off the ground.
    /// </summary>
    [Fact]
    public void TheRootPitchLaysTheWholeBodyOutFlatAndTheRootRollBanksIt()
    {
        BodyRig rig = BodyRig.Human;
        var pose = new BodyPose(Vector3.Zero, 0f);
        Span<Matrix4x4> upright = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        Span<Matrix4x4> prone = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];

        TestBodies.Humanoid(rig, pose, WalkPose.Rest, upright);
        TestBodies.Humanoid(rig, pose, WalkPose.Rest with { RootPitch = MathF.PI * 0.5f }, prone);

        // Standing: the neck base is straight up. Prone: it is the same distance straight ahead.
        Assert.Equal(new Vector3(0f, rig.HeadFromFeet.Y, 0f), upright[TestBodies.Head].Translation);
        Vector3 head = prone[TestBodies.Head].Translation;
        Assert.Equal(0f, head.X, 5);
        Assert.Equal(0f, head.Y, 5);
        Assert.Equal(rig.HeadFromFeet.Y, head.Z, 5);

        // THE LEGS CAME WITH IT: every leg piece is drawn somewhere else, so this is the whole rig turning
        // rather than the upper body folding off a fixed pelvis.
        Assert.NotEqual(upright[TestBodies.ThighLeft], prone[TestBodies.ThighLeft]);
        Assert.NotEqual(upright[TestBodies.ShinLeft], prone[TestBodies.ShinLeft]);

        // And the CONTACT POINT is preserved, because the pivot is the point the pose names: a resting sole
        // sits at y 0 directly under its hip, which is on the pitch axis, so it does not move at all.
        Vector3 standingSole = TestBodies.Sole(rig, upright, TestBodies.ShinLeft);
        Vector3 proneSole = TestBodies.Sole(rig, prone, TestBodies.ShinLeft);
        Assert.Equal(0f, standingSole.Y, 5);
        Assert.Equal(standingSole.X, proneSole.X, 5);
        Assert.Equal(standingSole.Y, proneSole.Y, 5);
        Assert.Equal(standingSole.Z, proneSole.Z, 5);

        // A LEAN of the same size pivots at HIP height instead, which is what makes it the wrong channel for
        // a body with nothing under its soles: the feet swing up and back off the ground.
        Span<Matrix4x4> leaning = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        TestBodies.Humanoid(rig, pose, WalkPose.Rest with { Lean = MathF.PI * 0.5f }, leaning);
        Vector3 leanedHead = leaning[TestBodies.Head].Translation;
        Assert.Equal(rig.RightHip.Y, leanedHead.Y, 5);
        Vector3 leanedSole = TestBodies.Sole(rig, leaning, TestBodies.ShinLeft);
        Assert.Equal(rig.RightHip.Y, leanedSole.Y, 5);
        Assert.Equal(-rig.RightHip.Y, leanedSole.Z, 5);

        // The ROLL lifts the character's LEFT side, engine +x, the same sense the quadruped's own roll uses.
        Span<Matrix4x4> banked = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        TestBodies.Humanoid(rig, pose, WalkPose.Rest with { RootRoll = 0.4f }, banked);
        Vector3 leftHip = Vector3.Transform(Vector3.Zero, banked[TestBodies.ThighLeft]);
        Vector3 rightHip = Vector3.Transform(Vector3.Zero, banked[TestBodies.ThighRight]);
        Assert.True(leftHip.Y > rightHip.Y,
            $"the left hip is at {leftHip.Y} and the right at {rightHip.Y}, so a positive roll dropped the left");
    }
}
