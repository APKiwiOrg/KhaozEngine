using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The idle breath: how far it moves a body, that it moves the body ABOVE THE HIPS and leaves the feet on the
/// ground, how long it takes, that a crowd does not breathe in unison, and that a body doing anything at all
/// does not breathe at all. The legs' REST STANCE is here too, because it rides the same pose and the same
/// blend: a soft knee held at one angle, with the swing and the settle that keep each sole on the floor under
/// its own hip.
/// </summary>
/// <remarks>
/// The amplitudes are the bulk of the file and that is deliberate. This is a feature whose whole
/// specification is that it is nearly invisible, so the thing most worth pinning is that nobody has quietly
/// turned it up: a breath that reads as motion at a glance is a regression however good it looks in a diff.
/// </remarks>
public class IdleBreathTests
{
    const long Seed = 4242L;

    // The swing either side of rest, which is HALF each constant: the constants are the whole travel.
    static float ArmSwing => IdleBreath.ArmSwayRadians * 0.5f;
    static float LeanSwing => IdleBreath.LeanRadians * 0.5f;
    static float RiseSwing(BodyRig rig) => IdleBreath.RiseFraction * rig.RestHeightMetres * 0.5f;

    [Fact]
    public void NothingMovesFurtherThanTheStatedNumbersOverAWholeBreath()
    {
        BodyRig rig = BodyRig.Human;
        (float Leg, float Knee, float Bob) stance = IdleBreath.RestStance(rig);
        float mostUp = 0f, mostDown = 0f, mostBack = 0f, mostForward = 0f, mostSway = 0f;
        for (float t = 0f; t <= IdleBreath.PeriodSeconds; t += 0.005f)
        {
            WalkPose pose = IdleBreath.PoseAt(t, Seed, rig);
            mostUp = MathF.Max(mostUp, pose.TorsoRise);
            mostDown = MathF.Min(mostDown, pose.TorsoRise);
            mostForward = MathF.Max(mostForward, pose.TorsoLean);
            mostBack = MathF.Min(mostBack, pose.TorsoLean);
            mostSway = MathF.Max(mostSway, MathF.Abs(pose.LeftArm));

            // The legs are out of the BREATH. They hold one rest stance, the same three numbers at every
            // phase, so nothing about them is a wave: a standing body's legs do not move when it breathes.
            // The body's own LEAN is still exactly zero, which is the channel that would tip the whole body.
            Assert.Equal(stance.Leg, pose.LeftLeg);
            Assert.Equal(stance.Leg, pose.RightLeg);
            Assert.Equal(stance.Knee, pose.LeftKnee);
            Assert.Equal(stance.Knee, pose.RightKnee);
            Assert.Equal(stance.Bob, pose.Bob);
            Assert.Equal(0f, pose.Lean);
            // The elbows rest BENT and hold there. The one channel that is a constant rather than a wave, and
            // the same on both arms: an arm that folds through the cycle reads as a fidget.
            Assert.Equal(IdleBreath.RestElbowRadians, pose.LeftElbow);
            Assert.Equal(IdleBreath.RestElbowRadians, pose.RightElbow);
            // Both arms the same sign and the same size: a pendulum under a moving body, not a gait.
            Assert.Equal(pose.LeftArm, pose.RightArm);
            // And nothing in a breath tips the whole body about its ROOT, which is the pair only air and
            // water write.
            Assert.Equal(0f, pose.RootPitch);
            Assert.Equal(0f, pose.RootRoll);
        }

        // Every travel is the constant it is named for, and no more. A tenth of a percent of slack, for the
        // sampling step rather than for the numbers.
        Assert.InRange(mostUp - mostDown, IdleBreath.RiseFraction * rig.RestHeightMetres * 0.99f,
            IdleBreath.RiseFraction * rig.RestHeightMetres * 1.001f);
        Assert.InRange(mostForward - mostBack, IdleBreath.LeanRadians * 0.99f,
            IdleBreath.LeanRadians * 1.001f);
        Assert.InRange(mostSway * 2f, IdleBreath.ArmSwayRadians * 0.99f, IdleBreath.ArmSwayRadians * 1.001f);

        // And in absolute terms, which is the claim a reader of this file actually wants: eighteen
        // millimetres and a couple of degrees on a person, over four seconds. The ceiling is what this test
        // is for: at a typical camera the body is drawn about 40 pixels tall, so the rise below is half a
        // pixel and the crown moves about one. Anything meaningfully over these reads as motion at a glance,
        // which is the regression, however good it looked in the diff that turned it up.
        Assert.True(mostUp - mostDown < 0.019f, $"the body rises {mostUp - mostDown} m, which is visible");
        Assert.True(mostForward - mostBack < 0.041f, $"the chest tips {mostForward - mostBack} rad");
        Assert.True(mostSway * 2f < 0.091f, $"the arms sway {mostSway * 2f} rad, which is a gesture");
    }

    /// <summary>
    /// THE FEET STAY ON THE GROUND, which is the whole reason the breath lives above the hips. Measured
    /// through the real piece chain rather than through a transcription of it, at 64 phases of a whole
    /// breath, for a person and for a short body: both soles sit at exactly y 0 and at exactly the z they
    /// stand at, and both legs draw at the same transform they draw at when the body is not breathing at all.
    /// </summary>
    /// <remarks>
    /// The numbers this replaces, on a person: a breath that rose on <c>WalkPose.Bob</c> and tipped on
    /// <c>WalkPose.Lean</c>, which carry the WHOLE body, put each sole 8.9 mm UNDER the floor at the bottom
    /// of the exhale and 9.1 mm over it at the top, and the lean slid both of them 12.4 mm each way along z.
    /// <para>The CROWN is asserted in the same pass, and it is the half that stops this test passing on a
    /// breath that was simply switched off: the head still travels the full rise, because the torso pivots
    /// and lifts about the same hip height the whole body would have leaned about.</para>
    /// </remarks>
    [Fact]
    public void TheSolesStayOnTheGroundThroughAWholeBreathAndTheCrownStillRises()
    {
        var pose = new BodyPose(Vector3.Zero, 0f);
        Span<Matrix4x4> rest = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        Span<Matrix4x4> at = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        foreach (BodyRig rig in new[] { BodyRig.Human, TestBodies.Small })
        {
            // The datum is the body at its IDLE, not at WalkPose.Rest: the legs hold a rest stance, so the
            // transform they are meant to hold through the cycle is the stance's rather than the bare pose's.
            // The stance is a constant, so any phase gives the same one.
            TestBodies.Humanoid(rig, pose,
                IdleBreath.Compose(WalkPose.Rest, IdleBreath.PoseAt(0f, Seed, rig), 1f), rest);
            Vector3 leftAtRest = TestBodies.Sole(rig, rest, TestBodies.ShinLeft);
            Vector3 rightAtRest = TestBodies.Sole(rig, rest, TestBodies.ShinRight);
            // The idle really does stand ON the floor, or every assertion below is against the wrong datum:
            // the pieces are authored with the sole exactly on y 0 and the stance's own bob keeps it there.
            Assert.Equal(0f, leftAtRest.Y, 4);
            Assert.Equal(0f, rightAtRest.Y, 4);

            float lowest = float.MaxValue, highest = float.MinValue;
            for (int i = 0; i < 64; i++)
            {
                float seconds = IdleBreath.PeriodSeconds * i / 64f;
                WalkPose breath = IdleBreath.Compose(
                    WalkPose.Rest, IdleBreath.PoseAt(seconds, Seed, rig), 1f);
                TestBodies.Humanoid(rig, pose, breath, at);

                foreach (int shin in new[] { TestBodies.ShinLeft, TestBodies.ShinRight })
                {
                    Vector3 sole = TestBodies.Sole(rig, at, shin);
                    Vector3 datum = shin == TestBodies.ShinLeft ? leftAtRest : rightAtRest;
                    Assert.Equal(0f, sole.Y, 4);
                    Assert.Equal(datum.Z, sole.Z, 4);
                    Assert.Equal(datum.X, sole.X, 4);
                }

                // And the whole leg, not only the point named the sole: a thigh or a shin drawn at anything
                // but its resting transform is a leg that moved, wherever on it the sole happened to land.
                foreach (int leg in new[]
                {
                    TestBodies.ThighLeft, TestBodies.ThighRight,
                    TestBodies.ShinLeft, TestBodies.ShinRight,
                })
                {
                    Assert.Equal(rest[leg], at[leg]);
                }

                float crown = Vector3.Transform(
                    new Vector3(0f, rig.RestHeightMetres, 0f), at[TestBodies.Torso]).Y;
                lowest = MathF.Min(lowest, crown);
                highest = MathF.Max(highest, crown);
            }

            // The counterweight. A breath that moved nothing at all would pass every assertion above, so the
            // crown has to travel the rise the constants promise.
            float rise = IdleBreath.RiseFraction * rig.RestHeightMetres;
            Assert.InRange(highest - lowest, rise * 0.98f, rise * 1.02f);
        }
    }

    /// <summary>
    /// THE REST STANCE, measured through the real chain: every sole sits on the floor AND directly under its
    /// own hip, at every phase, on both rigs, with the knee at the one number the stance is tuned by. That
    /// pair is the whole of what the stance solves for, and either half alone is easy to pass: a straight leg
    /// keeps the sole on the floor and a bent one with no swing keeps it off the hip.
    /// </summary>
    /// <remarks>
    /// The failure this catches is a knee bent WITHOUT the two corrections that pay for it. Break a knee
    /// alone and the foot swings out behind the body and the body gets shorter, so the sole leaves the ground
    /// in both axes at once: on a person at this flexion that is 28 mm of foot behind the hip and 0.8 mm of
    /// sole under the floor, neither of them large and both of them exactly the kind of thing that reads as a
    /// body sliding rather than as a body standing.
    /// </remarks>
    [Fact]
    public void TheStanceHoldsEachSoleOnTheFloorAndUnderItsOwnHip()
    {
        var pose = new BodyPose(Vector3.Zero, 0f);
        Span<Matrix4x4> at = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        foreach (BodyRig rig in new[] { BodyRig.Human, TestBodies.Small })
        {
            for (int i = 0; i < 64; i++)
            {
                float seconds = IdleBreath.PeriodSeconds * i / 64f;
                WalkPose idle = IdleBreath.PoseAt(seconds, Seed, rig);
                Assert.Equal(IdleBreath.RestKneeRadians, idle.LeftKnee, 6);
                Assert.Equal(IdleBreath.RestKneeRadians, idle.RightKnee, 6);

                TestBodies.Humanoid(rig, pose, IdleBreath.Compose(WalkPose.Rest, idle, 1f), at);
                foreach ((int thigh, int shin) in new[]
                {
                    (TestBodies.ThighLeft, TestBodies.ShinLeft),
                    (TestBodies.ThighRight, TestBodies.ShinRight),
                })
                {
                    // The hip is the thigh piece's own origin, so its drawn position is that transform's
                    // translation, and the sole is the far end of the shin.
                    Vector3 hip = Vector3.Transform(Vector3.Zero, at[thigh]);
                    Vector3 sole = TestBodies.Sole(rig, at, shin);
                    Assert.Equal(0f, sole.Y, 4);
                    Assert.Equal(hip.X, sole.X, 4);
                    Assert.Equal(hip.Z, sole.Z, 4);
                }
            }
        }
    }

    /// <summary>
    /// THE HEAD RIDES THE TORSO, which is what makes it breathe with the chest instead of hanging in the air
    /// where the body used to be. Its transform is the neck base carried through the torso frame, exactly,
    /// and a torso rise lifts it by that rise while the shins under it do not move at all.
    /// </summary>
    [Fact]
    public void TheHeadRidesTheTorsoFrameAndTheLegsDoNot()
    {
        BodyRig rig = BodyRig.Human;
        var pose = new BodyPose(Vector3.Zero, 0f);
        Span<Matrix4x4> at = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        Span<Matrix4x4> risen = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];

        TestBodies.Humanoid(rig, pose, WalkPose.Rest, at);
        Assert.Equal(
            Matrix4x4.CreateTranslation(rig.HeadFromFeet) * at[TestBodies.Torso], at[TestBodies.Head]);
        Assert.Equal(rig.HeadFromFeet, at[TestBodies.Head].Translation);

        const float Rise = 0.05f;
        TestBodies.Humanoid(rig, pose, WalkPose.Rest with { TorsoRise = Rise }, risen);
        Assert.Equal(
            Matrix4x4.CreateTranslation(rig.HeadFromFeet) * risen[TestBodies.Torso],
            risen[TestBodies.Head]);
        Assert.Equal(at[TestBodies.Head].Translation.Y + Rise, risen[TestBodies.Head].Translation.Y, 5);
        // And the legs are outside that frame, which is the same claim the soles make: a rise moves the head
        // and the chest and leaves the feet on the ground.
        Assert.Equal(at[TestBodies.ShinLeft], risen[TestBodies.ShinLeft]);
        Assert.Equal(at[TestBodies.ShinRight], risen[TestBodies.ShinRight]);
    }

    [Fact]
    public void TheElbowsRestBentAndTheBendIsSofterThanTheWalksOwn()
    {
        // Arms hanging dead straight are the loudest thing about a body standing still. A held break at both
        // elbows is what a body at ease has, and it is smaller than the bend a WALK carries, so easing out of
        // a stop opens the arms rather than closing them.
        Assert.InRange(IdleBreath.RestElbowRadians, 0.15f, 0.25f);
        Assert.True(IdleBreath.RestElbowRadians < WalkCycle.ElbowBendRadians,
            $"the resting elbow is {IdleBreath.RestElbowRadians}, at or past the walk's own "
            + $"{WalkCycle.ElbowBendRadians}");

        // Held rather than swung: the same number at every phase, on both arms.
        for (float t = 0f; t <= IdleBreath.PeriodSeconds; t += 0.01f)
        {
            WalkPose pose = IdleBreath.PoseAt(t, Seed);
            Assert.Equal(IdleBreath.RestElbowRadians, pose.LeftElbow);
            Assert.Equal(IdleBreath.RestElbowRadians, pose.RightElbow);
        }

        // And it arrives with the blend rather than snapping on, which is what keeps the arms from clicking
        // into place a third of a second after a body stops.
        WalkPose breath = IdleBreath.PoseAt(0.7f, Seed);
        Assert.Equal(IdleBreath.RestElbowRadians * 0.5f,
            IdleBreath.Compose(WalkPose.Rest, breath, 0.5f).LeftElbow, 6);
        Assert.Equal(0f, IdleBreath.Compose(WalkPose.Rest, breath, 0f).LeftElbow);
    }

    [Fact]
    public void TheChestRisesAndTipsBackTogetherAndTheArmsFollowAQuarterOfACycleLater()
    {
        // The peak of the inhale, found rather than assumed, so this reads the pose the way an eye does.
        float peak = 0f;
        float highest = float.MinValue;
        for (float t = 0f; t < IdleBreath.PeriodSeconds; t += 0.001f)
        {
            float rise = IdleBreath.PoseAt(t, Seed).TorsoRise;
            if (rise <= highest) continue;
            highest = rise;
            peak = t;
        }

        WalkPose full = IdleBreath.PoseAt(peak, Seed);
        Assert.True(full.TorsoRise > 0f, "the torso is not lifted at the top of the inhale");
        // BACK on the inhale, which is a negative lean: a positive one carries the head forward.
        Assert.True(full.TorsoLean < -LeanSwing * 0.99f, $"the chest leans {full.TorsoLean} at the top of the inhale");
        // The arms are passing through their own middle as the chest arrives, which is what a quarter cycle
        // of lag means, and they are at their own peak a quarter of a period later.
        Assert.True(MathF.Abs(full.LeftArm) < ArmSwing * 0.05f,
            $"the arms are at {full.LeftArm} rather than mid swing at the top of the inhale");
        float lagged = IdleBreath.PoseAt(peak + (IdleBreath.PeriodSeconds * 0.25f), Seed).LeftArm;
        Assert.True(MathF.Abs(lagged) > ArmSwing * 0.99f,
            $"the arms only reached {lagged} a quarter cycle after the inhale");
    }

    [Fact]
    public void OneBreathTakesTheWholePeriodAndComesBackToWhereItStarted()
    {
        for (float t = 0f; t < IdleBreath.PeriodSeconds; t += 0.05f)
        {
            WalkPose now = IdleBreath.PoseAt(t, Seed);
            WalkPose next = IdleBreath.PoseAt(t + IdleBreath.PeriodSeconds, Seed);
            Assert.Equal(now.TorsoRise, next.TorsoRise, 5);
            Assert.Equal(now.TorsoLean, next.TorsoLean, 5);
            Assert.Equal(now.LeftArm, next.LeftArm, 5);
        }

        // And exactly ONE cycle in that period: the body is at the top of the inhale once and at the bottom
        // once, which a doubled frequency would fail while every wrap assertion above still passed.
        int peaks = 0;
        for (float t = 0f; t < IdleBreath.PeriodSeconds; t += 0.002f)
        {
            float before = IdleBreath.PoseAt(t - 0.002f, Seed).TorsoRise;
            float now = IdleBreath.PoseAt(t, Seed).TorsoRise;
            float after = IdleBreath.PoseAt(t + 0.002f, Seed).TorsoRise;
            if (now > before && now >= after) peaks++;
        }
        Assert.Equal(1, peaks);
    }

    [Fact]
    public void ThePoseIsContinuousAcrossTheWrapAndEasesRatherThanCorners()
    {
        // The largest step the pose takes over a hundredth of a second, sampled across two whole periods so
        // the wrap is inside the window. A corner at the wrap, or at either end of the breath, shows up here
        // as one step several times the rest.
        const float step = 0.01f;
        float worst = 0f, total = 0f;
        int samples = 0;
        for (float t = 0f; t < IdleBreath.PeriodSeconds * 2f; t += step)
        {
            WalkPose a = IdleBreath.PoseAt(t, Seed);
            WalkPose b = IdleBreath.PoseAt(t + step, Seed);
            float delta = MathF.Abs(b.TorsoRise - a.TorsoRise) + MathF.Abs(b.TorsoLean - a.TorsoLean)
                + MathF.Abs(b.LeftArm - a.LeftArm);
            worst = MathF.Max(worst, delta);
            total += delta;
            samples++;
        }
        Assert.True(worst < (total / samples) * 2f,
            $"the worst step is {worst} against a mean of {total / samples}, which is a corner");

        // The turn at the top of the breath is a settle rather than a bounce: a sine's rate is zero there,
        // and a triangle's is not.
        float atPeak = MathF.Abs(IdleBreath.PoseAt(1f, 0L).TorsoRise - IdleBreath.PoseAt(1f + step, 0L).TorsoRise);
        float atMiddle = MathF.Abs(IdleBreath.PoseAt(0f, 0L).TorsoRise - IdleBreath.PoseAt(step, 0L).TorsoRise);
        // Seed 0 hashes to an offset of exactly zero, so t = 0 is the middle of its breath and t = 1 is a
        // quarter of a period later, which is the top of it.
        Assert.True(atPeak < atMiddle, $"the breath moves {atPeak} at its turn against {atMiddle} mid cycle");
    }

    [Fact]
    public void TwoBodiesBreatheOutOfStepAndTheSameBodyAlwaysTheSameWay()
    {
        WalkPose one = IdleBreath.PoseAt(0f, 1L);
        WalkPose two = IdleBreath.PoseAt(0f, 2L);
        Assert.True(MathF.Abs(one.TorsoRise - two.TorsoRise) > RiseSwing(BodyRig.Human) * 0.1f,
            "two neighbouring ids are drawn "
            + $"{MathF.Abs(one.TorsoRise - two.TorsoRise)} m apart, so they breathe together");

        // Not two ids that happen to differ: a run of neighbouring ids, which is how they are usually handed
        // out, spread across the cycle rather than landing in a clump.
        var seen = new List<float>();
        for (long id = 1; id <= 24; id++) seen.Add(IdleBreath.PhaseFor(id));
        seen.Sort();
        float widest = 0f;
        for (int i = 1; i < seen.Count; i++) widest = MathF.Max(widest, seen[i] - seen[i - 1]);
        Assert.True(widest < 0.25f, $"24 neighbouring ids leave a {widest} gap in the cycle, so they clump");

        // And it is a hash rather than a roll: the same body breathes the same way in every client that draws
        // it, and in every run.
        Assert.Equal(IdleBreath.PhaseFor(7L), IdleBreath.PhaseFor(7L));
        Assert.InRange(IdleBreath.PhaseFor(long.MaxValue), 0f, 1f);
        Assert.InRange(IdleBreath.PhaseFor(long.MinValue), 0f, 1f);
        Assert.InRange(IdleBreath.PhaseFor(0L), 0f, 1f);
    }

    [Fact]
    public void AShortBodyBreathesAtItsOwnSizeAndOnlyTheRiseScales()
    {
        WalkPose person = IdleBreath.PoseAt(0.9f, Seed, BodyRig.Human);
        WalkPose small = IdleBreath.PoseAt(0.9f, Seed, TestBodies.Small);

        // The rise is a LENGTH, so it scales with the body exactly as the stride does.
        Assert.Equal(person.TorsoRise * TestBodies.SmallScale, small.TorsoRise, 6);
        // The angles are shapes and hold at any size, the resting elbow included.
        Assert.Equal(person.TorsoLean, small.TorsoLean, 6);
        Assert.Equal(person.LeftArm, small.LeftArm, 6);
        Assert.Equal(person.LeftElbow, small.LeftElbow, 6);
        // And no rig at all is a person, which is what a body nobody has classified draws at.
        Assert.Equal(person.TorsoRise, IdleBreath.PoseAt(0.9f, Seed).TorsoRise, 6);
    }

    [Fact]
    public void ComposeAddsTheBreathToWhateverTheBodyIsAlreadyDoing()
    {
        var walk = new WalkPose(LeftArm: 0.4f, RightArm: -0.4f, LeftLeg: -0.6f, RightLeg: 0.6f,
            LeftElbow: 0.3f, RightElbow: 0.55f, LeftKnee: 0.2f, RightKnee: 0.7f, Bob: -0.02f, Lean: 0.1f,
            RightArmYaw: 0.9f, RightWrist: 0.7f, TorsoRise: 0.003f, TorsoLean: -0.01f);
        WalkPose breath = IdleBreath.PoseAt(0.6f, Seed);

        Assert.Equal(walk, IdleBreath.Compose(walk, breath, 0f));
        Assert.Equal(walk, IdleBreath.Compose(walk, breath, -3f));

        WalkPose full = IdleBreath.Compose(walk, breath, 1f);
        Assert.Equal(walk.TorsoRise + breath.TorsoRise, full.TorsoRise, 6);
        Assert.Equal(walk.TorsoLean + breath.TorsoLean, full.TorsoLean, 6);
        Assert.Equal(walk.LeftArm + breath.LeftArm, full.LeftArm, 6);
        Assert.Equal(walk.RightArm + breath.RightArm, full.RightArm, 6);
        Assert.Equal(walk.LeftElbow + breath.LeftElbow, full.LeftElbow, 6);
        Assert.Equal(walk.RightElbow + breath.RightElbow, full.RightElbow, 6);
        // The legs and the bob, which the REST STANCE writes: added the same way the arms are, so a body
        // easing into its idle settles onto its knees over the blend rather than snapping onto them. In
        // practice a body with any walk in it carries no idle at all, so this only ever adds to a rest pose.
        Assert.Equal(walk.LeftLeg + breath.LeftLeg, full.LeftLeg, 6);
        Assert.Equal(walk.RightLeg + breath.RightLeg, full.RightLeg, 6);
        Assert.Equal(walk.LeftKnee + breath.LeftKnee, full.LeftKnee, 6);
        Assert.Equal(walk.RightKnee + breath.RightKnee, full.RightKnee, 6);
        Assert.Equal(walk.Bob + breath.Bob, full.Bob, 6);
        // AND THE ONE THAT TIPS THE WHOLE BODY, which nothing in an idle writes: a breath adds nothing to the
        // walk's lean, so a body carrying one leans exactly as far as its gait asked for.
        Assert.Equal(walk.Lean, full.Lean);
        Assert.Equal(0f, breath.Lean);
        // The stance is what those four carry, and it is the same at any phase.
        (float leg, float knee, float bob) = IdleBreath.RestStance(BodyRig.Human);
        Assert.Equal(leg, breath.LeftLeg, 6);
        Assert.Equal(knee, breath.LeftKnee, 6);
        Assert.Equal(bob, breath.Bob, 6);
        // And the two channels only a STROKE ever writes. A breath is a sway of hanging arms, and a body mid
        // stroke carries no breath at all (see the blend), so adding a sideways shoulder or a wrist here
        // would be adding it to a number nothing else in the frame agreed to.
        Assert.Equal(walk.RightArmYaw, full.RightArmYaw);
        Assert.Equal(walk.RightWrist, full.RightWrist);
        Assert.Equal(0f, breath.RightArmYaw);
        Assert.Equal(0f, breath.RightWrist);

        WalkPose half = IdleBreath.Compose(walk, breath, 0.5f);
        Assert.Equal(walk.TorsoRise + (breath.TorsoRise * 0.5f), half.TorsoRise, 6);
        // Out of range clamps rather than doubling the breath.
        Assert.Equal(full.TorsoRise, IdleBreath.Compose(walk, breath, 9f).TorsoRise, 6);
    }

    [Fact]
    public void TheBlendCutsOutTheFrameABodyMovesAndTakesTheWholeBlendToComeBack()
    {
        const float dt = 1f / 60f;
        float weight = 1f;
        // Anything at all takes it, in ONE frame rather than over a fade.
        Assert.Equal(0f, IdleBreath.AdvanceWeight(weight, busy: true, dt));

        weight = 0f;
        int frames = 0;
        while (weight < 1f && frames < 1000)
        {
            weight = IdleBreath.AdvanceWeight(weight, busy: false, dt);
            frames++;
        }
        Assert.Equal(1f, weight);
        // The whole blend, near enough one frame: the ease is a rate limit rather than a curve.
        Assert.InRange(frames * dt, IdleBreath.BlendSeconds - dt, IdleBreath.BlendSeconds + dt);
        // Half way through it is half applied, so the return is gradual rather than a switch that waits.
        Assert.Equal(0.5f, IdleBreath.AdvanceWeight(0f, busy: false, IdleBreath.BlendSeconds * 0.5f), 5);
        // And it never runs past full or backwards on a frame that took no time.
        Assert.Equal(1f, IdleBreath.AdvanceWeight(1f, busy: false, dt));
        Assert.Equal(0.25f, IdleBreath.AdvanceWeight(0.25f, busy: false, -5f), 5);
    }
}
