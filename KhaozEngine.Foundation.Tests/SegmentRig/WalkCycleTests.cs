using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The procedural walk, which is a pure function of ground covered and nothing else, so it is pinned here
/// without a scene, a device or a server. What matters is the GAIT: legs opposed to each other, each arm
/// opposed to its own leg, and a stop that eases out instead of freezing a leg mid air.
/// </summary>
public class WalkCycleTests
{
    const float Dt = 1f / 60f;

    /// <summary>Walks a body forward at a steady speed for a while, and hands back the cycle.</summary>
    static WalkCycle Walked(float metresPerSecond, float seconds, out Vector3 at) =>
        Walked(BodyRig.Human, metresPerSecond, seconds, out at);

    /// <summary>The same, on one body's own rig, which is where the stride comes from.</summary>
    static WalkCycle Walked(BodyRig rig, float metresPerSecond, float seconds, out Vector3 at)
    {
        var cycle = new WalkCycle(rig);
        at = Vector3.Zero;
        cycle.Advance(at, Dt);
        for (float t = 0f; t < seconds; t += Dt)
        {
            at += new Vector3(0f, 0f, metresPerSecond * Dt);
            cycle.Advance(at, Dt);
        }
        return cycle;
    }

    [Fact]
    public void AStandingBodyAnswersEveryAngleZero()
    {
        var cycle = new WalkCycle();
        Assert.Equal(WalkPose.Rest, cycle.Pose);

        // And it stays at rest however long it stands there, including through the first sample, where there
        // is nothing to measure against and a naive cycle reads the whole world coordinate as one frame of
        // travel.
        var at = new Vector3(40f, 0f, -25f);
        for (int i = 0; i < 120; i++) cycle.Advance(at, Dt);
        Assert.Equal(WalkPose.Rest, cycle.Pose);
        Assert.Equal(0f, cycle.Weight);
        Assert.Equal(0f, cycle.Phase);
    }

    [Fact]
    public void ASteadyWalkSwingsTheLimbsPeriodically()
    {
        // Long enough for the blend to saturate, so the amplitude below is the real one.
        WalkCycle cycle = Walked(1.2f, 1f, out Vector3 at);
        Assert.Equal(1f, cycle.Weight, 3);

        // A full stride of travel from here brings the phase, and so every angle, back to where it was.
        WalkPose before = cycle.Pose;
        float travelled = 0f;
        while (travelled < WalkCycle.StrideMetres - 1e-4f)
        {
            float step = MathF.Min(1.2f * Dt, WalkCycle.StrideMetres - travelled);
            at += new Vector3(0f, 0f, step);
            travelled += step;
            cycle.Advance(at, Dt);
        }
        WalkPose after = cycle.Pose;
        Assert.Equal(before.RightLeg, after.RightLeg, 3);
        Assert.Equal(before.LeftLeg, after.LeftLeg, 3);

        // And it really swings both ways over that stride rather than sitting at one angle: sampled across a
        // whole cycle the leg reaches most of the amplitude in each direction.
        float low = float.MaxValue, high = float.MinValue;
        for (int i = 0; i < 200; i++)
        {
            at += new Vector3(0f, 0f, WalkCycle.StrideMetres / 200f);
            cycle.Advance(at, Dt);
            low = MathF.Min(low, cycle.Pose.RightLeg);
            high = MathF.Max(high, cycle.Pose.RightLeg);
        }
        Assert.InRange(high, WalkCycle.SwingRadians * 0.95f, WalkCycle.SwingRadians);
        Assert.InRange(low, -WalkCycle.SwingRadians, -WalkCycle.SwingRadians * 0.95f);

        // A gait hangs its arms in the body's OWN plane and holds nothing in either hand, so the two channels
        // a stroke owns stay at zero the whole way round. A walk that started writing them would put a
        // sideways shoulder under every step and a wrist under a weapon nobody is swinging.
        Assert.Equal(0f, cycle.Pose.RightArmYaw);
        Assert.Equal(0f, cycle.Pose.RightWrist);
    }

    [Fact]
    public void TheLegsAreOpposedAndEachArmIsOpposedToItsOwnLeg()
    {
        // Sampled all the way through a stride, so this is the gait rather than one lucky frame.
        var cycle = new WalkCycle();
        var at = Vector3.Zero;
        cycle.Advance(at, Dt);
        bool sawSwing = false;
        for (int i = 0; i < 240; i++)
        {
            at += new Vector3(0f, 0f, 1.2f * Dt);
            cycle.Advance(at, Dt);
            WalkPose pose = cycle.Pose;
            Assert.Equal(-pose.RightLeg, pose.LeftLeg, 5);
            // The arms are opposed to their own legs AND shorter than them, which is the amplitude that
            // separates a walk from a march.
            Assert.Equal(-pose.RightLeg * WalkCycle.ArmSwingScale, pose.RightArm, 5);
            Assert.Equal(-pose.LeftLeg * WalkCycle.ArmSwingScale, pose.LeftArm, 5);
            sawSwing |= MathF.Abs(pose.RightLeg) > 0.1f;
        }
        Assert.True(sawSwing, "nothing ever swung, so the antiphase above held over four zeroes");
    }

    /// <summary>
    /// An arm swings LESS than the leg it is opposed to, by exactly the scale, and the scale is a real
    /// reduction rather than a number that happens to be one. Matched amplitudes are most of what makes a
    /// cycle read as a marionette: a real arm counterbalances rather than carrying, and it hangs from a
    /// joint that never has to clear the ground.
    /// </summary>
    [Fact]
    public void TheArmsSwingLessFarThanTheLegs()
    {
        Assert.InRange(WalkCycle.ArmSwingScale, 0.4f, 0.8f);

        var cycle = new WalkCycle();
        var at = Vector3.Zero;
        cycle.Advance(at, Dt);
        float leg = 0f, arm = 0f;
        for (int i = 0; i < 240; i++)
        {
            at += new Vector3(0f, 0f, 1.2f * Dt);
            cycle.Advance(at, Dt);
            leg = MathF.Max(leg, MathF.Abs(cycle.Pose.RightLeg));
            arm = MathF.Max(arm, MathF.Abs(cycle.Pose.RightArm));
        }
        Assert.Equal(WalkCycle.SwingRadians, leg, 2);
        Assert.Equal(WalkCycle.SwingRadians * WalkCycle.ArmSwingScale, arm, 2);
        Assert.True(arm < leg - 0.1f, "the arm swings " + arm + " against the leg's " + leg);
    }

    [Fact]
    public void RunningTurnsTheLegsOverTwiceAsFastAsWalking()
    {
        // The point of accumulating off DISTANCE: a running body moves twice as fast, so it covers a stride
        // in half the time with no second set of numbers anywhere.
        WalkCycle walk = Walked(1.2f, 0.5f, out _);
        WalkCycle run = Walked(2.4f, 0.5f, out _);
        Assert.Equal(1f, walk.Weight, 3);
        Assert.Equal(1f, run.Weight, 3);
        // Half a second of each, from the same start: the runner is twice as far through the cycle. Half a
        // second is deliberate, because the phase WRAPS, and a run long enough to lap the walk would compare
        // a wrapped phase against an unwrapped one and read as no relationship at all.
        Assert.True(run.Phase < 1f, "the run lapped the cycle, so the phases below are not comparable");
        Assert.Equal(walk.Phase * 2f, run.Phase, 3);
    }

    /// <summary>
    /// A SHORT BODY TURNS ITS LEGS OVER FASTER over the same ground, which is most of what reads as small.
    /// The phase is travelled over stride, so a 0.77 m stride advances the cycle 1 over 0.55 times as fast
    /// per metre as a person's 1.4 m one. Everything else in the pose is a SHAPE and is identical at both
    /// sizes: the same swing, the same knee, the same arm scale, so a short body walks the human's cycle
    /// rather than a second one that could drift.
    /// </summary>
    [Fact]
    public void AShortBodyTurnsItsLegsOverFasterThanAPersonOverTheSameGround()
    {
        Assert.Equal(WalkCycle.StrideMetres, BodyRig.Human.StrideMetres, 4);
        Assert.Equal(WalkCycle.StrideMetres * TestBodies.SmallScale, TestBodies.Small.StrideMetres, 4);
        // A cycle nobody handed a rig walks like a person rather than dividing by a zero stride.
        Assert.Equal(WalkCycle.StrideMetres, default(WalkCycle).Stride, 4);
        Assert.Equal(TestBodies.Small.StrideMetres, new WalkCycle(TestBodies.Small).Stride, 4);

        // Half a second of the same walking speed each, from the same start, so the two have covered exactly
        // the same ground. Short enough that neither has lapped, because the phase wraps and a lapped cycle
        // would compare a wrapped phase against an unwrapped one and read as no relationship at all.
        WalkCycle person = Walked(BodyRig.Human, 1.2f, 0.5f, out _);
        WalkCycle small = Walked(TestBodies.Small, 1.2f, 0.5f, out _);
        Assert.Equal(1f, person.Weight, 3);
        Assert.Equal(1f, small.Weight, 3);
        Assert.True(small.Phase < 1f, "the short body lapped the cycle, so the phases below are not comparable");
        Assert.Equal(person.Phase / TestBodies.SmallScale, small.Phase, 3);

        // And the POSE is the human's exactly at the same phase, which is what says the cycle was scaled in
        // ONE place. Walked side by side over ground in the same ratio as the strides, so the two land on the
        // same phase rather than on the same metres: that is the whole point of the paragraph above.
        var side = new WalkCycle(BodyRig.Human);
        var scaled = new WalkCycle(TestBodies.Small);
        Vector3 sideAt = Vector3.Zero, scaledAt = Vector3.Zero;
        side.Advance(sideAt, Dt);
        scaled.Advance(scaledAt, Dt);
        for (int i = 0; i < 40; i++)
        {
            scaledAt += new Vector3(0f, 0f, 1.2f * Dt);
            sideAt += new Vector3(0f, 0f, 1.2f * Dt / TestBodies.SmallScale);
            side.Advance(sideAt, Dt);
            scaled.Advance(scaledAt, Dt);
        }
        Assert.Equal(side.Phase, scaled.Phase, 4);
        WalkPose human = side.Pose;
        WalkPose shrunk = scaled.Pose;
        Assert.Equal(human.RightLeg, shrunk.RightLeg, 3);
        Assert.Equal(human.LeftArm, shrunk.LeftArm, 3);
        Assert.Equal(human.RightKnee, shrunk.RightKnee, 3);
        Assert.Equal(human.LeftElbow, shrunk.LeftElbow, 3);
        Assert.Equal(human.Bob, shrunk.Bob, 3);

        // The same claim in METRES, which is the one a playtest can see: over 0.6 m the short body is three
        // quarters of the way through its cycle and the person is not yet half. Under a whole stride,
        // because the short body laps at 0.77 m and a wrapped phase compares as smaller.
        WalkCycle shortRun = Walked(TestBodies.Small, 1f, 0.6f, out _);
        WalkCycle personRun = Walked(BodyRig.Human, 1f, 0.6f, out _);
        Assert.True(shortRun.Phase < 1f && shortRun.Phase > personRun.Phase + 0.2f,
            "a short body over 0.6 m reached phase " + shortRun.Phase + " against a person's " + personRun.Phase);
    }

    /// <summary>
    /// THE KNEE, which is the joint that decides whether a walk reads as walking. Four claims, and the first
    /// is the one that looks BROKEN rather than merely wrong when it goes: a knee that goes negative is a leg
    /// bending forwards at the knee, and a plain sine would do it for half of every stride.
    /// </summary>
    [Fact]
    public void TheKneeFlexesThroughTheSwingAndLocksThroughTheStance()
    {
        var cycle = new WalkCycle();
        var at = Vector3.Zero;
        cycle.Advance(at, Dt);
        Assert.Equal(0f, cycle.Pose.RightKnee);   // standing, before anything moves

        // A full stride at full speed, sampled fine enough that a spike could not hide between samples.
        WalkCycle walking = Walked(3f, 1f, out at);
        Assert.Equal(1f, walking.Weight, 3);
        float peak = 0f, peakPhase = 0f, worstStance = 0f;
        for (int i = 0; i < 200; i++)
        {
            at += new Vector3(0f, 0f, WalkCycle.StrideMetres / 200f);
            walking.Advance(at, Dt);
            WalkPose pose = walking.Pose;

            // NEVER NEGATIVE, either side, anywhere in the stride.
            Assert.True(pose.RightKnee >= 0f, "the right knee bent backwards to " + pose.RightKnee);
            Assert.True(pose.LeftKnee >= 0f, "the left knee bent backwards to " + pose.LeftKnee);

            if (pose.RightKnee > peak) (peak, peakPhase) = (pose.RightKnee, walking.Phase);
            // The STANCE half of the right leg's cycle is where that leg is travelling backwards, which is
            // where it is on the ground holding the body up. A knee bent there is a body sinking into a
            // curtsey on every step.
            if (walking.Phase is > 0.25f and < 0.75f)
                worstStance = MathF.Max(worstStance, pose.RightKnee);
        }
        Assert.Equal(WalkCycle.KneeFlexRadians, peak, 2);
        // The peak lands in the middle of the SWING half, where the leg passes under the body and the foot
        // needs the clearance. Phase 0 is that point for the right leg, so the peak is at either end of the
        // wrapped range.
        Assert.True(peakPhase < 0.05f || peakPhase > 0.95f,
            "the knee peaked at phase " + peakPhase + ", which is not mid swing");
        Assert.True(worstStance < 0.02f, "the knee was bent " + worstStance + " rad through the stance");
    }

    /// <summary>
    /// The BOB: the body rides highest when a leg is vertical under it and lowest at double support, twice per
    /// stride. It only ever sinks, because the standing pose is already the tall one and a body that rose
    /// above it would leave the ground.
    /// </summary>
    [Fact]
    public void TheBodyBobsTwicePerStrideAndIsHighestAtMidStance()
    {
        WalkCycle cycle = Walked(3f, 1f, out Vector3 at);
        Assert.Equal(1f, cycle.Weight, 3);

        var bobs = new List<(float Phase, float Bob)>();
        for (int i = 0; i < 200; i++)
        {
            at += new Vector3(0f, 0f, WalkCycle.StrideMetres / 200f);
            cycle.Advance(at, Dt);
            bobs.Add((cycle.Phase, cycle.Pose.Bob));
            Assert.True(cycle.Pose.Bob <= 0f, "the body rose " + cycle.Pose.Bob + " m off its standing height");
        }

        // MID STANCE is where a leg is vertical, which is phase 0 and phase 0.5: both legs pass through
        // vertical together, one going each way, so those are the two moments the body is tallest.
        foreach (float mid in new[] { 0f, 0.5f })
            Assert.True(MathF.Abs(Nearest(bobs, mid)) < 0.001f,
                "the body was " + Nearest(bobs, mid) + " m down at mid stance " + mid);

        // DOUBLE SUPPORT is a quarter turn either side of those, legs split, and it is the full drop.
        foreach (float split in new[] { 0.25f, 0.75f })
            Assert.Equal(-WalkCycle.BobMetres, Nearest(bobs, split), 3);
    }

    /// <summary>
    /// The LEAN, which is the one thing in the cycle that speed itself decides rather than distance. A walk
    /// stands upright and a run tips into it, and the switch blends rather than snapping.
    /// </summary>
    [Fact]
    public void RunningLeansTheBodyForwardAndWalkingDoesNot()
    {
        // The threshold has to sit BETWEEN a walking pace and a running one or it is either always or never
        // true. A human-scaled body walks at about 1.5 m/s and runs at about 3.0.
        const float walkSpeed = 1.5f;
        const float runSpeed = 3.0f;
        Assert.InRange(WalkCycle.RunMetresPerSecond, walkSpeed + 0.1f, runSpeed - 0.1f);

        WalkCycle walk = Walked(walkSpeed, 1f, out _);
        Assert.Equal(0f, walk.RunWeight);
        Assert.Equal(0f, walk.Pose.Lean);

        WalkCycle run = Walked(runSpeed, 1f, out Vector3 at);
        Assert.Equal(1f, run.RunWeight, 3);
        Assert.Equal(WalkCycle.RunLeanRadians, run.Pose.Lean, 3);

        // And it eases back out over the blend rather than snapping upright, the same way the swing does. Half
        // a blend of walking is part way there and a whole one is home.
        for (float t = 0f; t < WalkCycle.BlendSeconds * 0.5f; t += Dt)
        {
            at += new Vector3(0f, 0f, walkSpeed * Dt);
            run.Advance(at, Dt);
        }
        Assert.InRange(run.Pose.Lean, 0.01f, WalkCycle.RunLeanRadians - 0.01f);
        for (float t = 0f; t < WalkCycle.BlendSeconds; t += Dt)
        {
            at += new Vector3(0f, 0f, walkSpeed * Dt);
            run.Advance(at, Dt);
        }
        Assert.Equal(0f, run.Pose.Lean);
    }

    /// <summary>
    /// The ELBOWS: bent the whole time a body walks, and bent FURTHER on the arm that is swinging forward. The
    /// constant part is what stops the arms reading as planks and the swinging part is what stops the two of
    /// them reading as one rocking beam.
    /// </summary>
    [Fact]
    public void TheElbowsCarryABendAndFoldFurtherOnTheForwardArm()
    {
        WalkCycle cycle = Walked(1.5f, 1f, out Vector3 at);
        float least = float.MaxValue, most = float.MinValue;
        bool sawForwardFolded = false;
        for (int i = 0; i < 200; i++)
        {
            at += new Vector3(0f, 0f, WalkCycle.StrideMetres / 200f);
            cycle.Advance(at, Dt);
            WalkPose pose = cycle.Pose;
            least = MathF.Min(least, pose.RightElbow);
            most = MathF.Max(most, pose.RightElbow);
            // The arm that is forward is the more folded of the two, every frame, either way round.
            if (MathF.Abs(pose.RightArm) > 0.1f)
            {
                Assert.Equal(pose.RightArm > pose.LeftArm, pose.RightElbow > pose.LeftElbow);
                sawForwardFolded = true;
            }
        }
        Assert.True(sawForwardFolded, "no frame had an arm meaningfully forward of the other");
        Assert.Equal(WalkCycle.ElbowBendRadians, least, 2);
        Assert.Equal(WalkCycle.ElbowBendRadians + WalkCycle.ElbowSwingFlexRadians, most, 2);
    }

    /// <summary>The value nearest one phase in a sampled stride, so a claim about "at phase 0.25" does not
    /// turn on a sample landing exactly there.</summary>
    static float Nearest(List<(float Phase, float Bob)> samples, float phase)
    {
        float best = 0f, distance = float.MaxValue;
        foreach ((float sampled, float bob) in samples)
        {
            // Wrapped, so phase 0 is as close to 0.99 as it is to 0.01.
            float gap = MathF.Abs(sampled - phase);
            gap = MathF.Min(gap, 1f - gap);
            if (gap >= distance) continue;
            (best, distance) = (bob, gap);
        }
        return best;
    }

    [Fact]
    public void AStopDecaysTheSwingToRestWithinTheBlend()
    {
        WalkCycle cycle = Walked(1.2f, 1f, out Vector3 at);
        Assert.NotEqual(WalkPose.Rest, cycle.Pose);

        // Standing still on the spot from here. Half the blend in it is part way down, which is what makes it
        // a blend rather than a cut, and by the end of the blend it is fully at rest with no leg left in the
        // air. Then it stays there.
        for (float t = 0f; t < WalkCycle.BlendSeconds * 0.5f; t += Dt) cycle.Advance(at, Dt);
        Assert.InRange(cycle.Weight, 0.05f, 0.95f);

        for (float t = 0f; t < WalkCycle.BlendSeconds; t += Dt) cycle.Advance(at, Dt);
        Assert.Equal(0f, cycle.Weight);
        Assert.Equal(WalkPose.Rest, cycle.Pose);
        for (int i = 0; i < 60; i++) cycle.Advance(at, Dt);
        Assert.Equal(WalkPose.Rest, cycle.Pose);
    }

    [Fact]
    public void ATeleportIsNotTravel()
    {
        // A respawn across the world would otherwise read as one frame of sprinting and spin the phase to an
        // arbitrary place, so the body would land mid stride with its legs somewhere nobody chose.
        WalkCycle cycle = Walked(1.2f, 1f, out _);
        float before = cycle.Phase;

        cycle.Teleport();
        cycle.Advance(new Vector3(400f, 0f, -400f), Dt);
        Assert.Equal(before, cycle.Phase, 5);
        // And the swing eases out from there, because the body is standing where it arrived.
        Assert.True(cycle.Weight < 1f);
    }
}
