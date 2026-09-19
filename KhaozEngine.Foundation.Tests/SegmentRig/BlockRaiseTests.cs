using System;
using System.Numerics;
using KhaozEngine.SegmentRig;
using Xunit;

namespace KhaozEngine.Tests.SegmentRig;

/// <summary>
/// The flinch a plate in the off hand answers a blow with: the envelope, the arm it moves, and how far up the
/// plate gets.
/// </summary>
/// <remarks>
/// The other half of the carry rule. A plate that hangs at the side never moves, so it comes up for a beat on
/// every blow taken, and the size of it is SUBTLE: under half a second, and the plate never above the chest.
/// It also TURNS TO FACE the attacker on the way up, the elbow folds for real, and the body gives a couple of
/// percent. Everything measured here goes through the real composition, because a plate-shaped thing near an
/// arm looks fine in a screenshot whichever way its face is pointing.
/// </remarks>
public class BlockRaiseTests
{
    [Fact]
    public void NothingIsDrawnBeforeTheBlowOrAfterTheFlinchHasRunOut()
    {
        Assert.Equal(0f, BlockRaise.WeightAt(0f));
        Assert.Equal(0f, BlockRaise.WeightAt(-1f));
        Assert.Equal(0f, BlockRaise.WeightAt(float.NaN));
        Assert.Equal(default, BlockRaise.PoseAt(0f));
        Assert.Equal(default, BlockRaise.PoseAt(-1f));

        // Under half a second end to end: a plate still up when the next blow lands is a guard, and this is
        // a flinch.
        Assert.True(BlockRaise.Seconds < 0.5f, "the flinch lasts " + BlockRaise.Seconds + " s");
        Assert.Equal(0f, BlockRaise.WeightAt(BlockRaise.Seconds));
        Assert.Equal(0f, BlockRaise.WeightAt(0.5f));
        Assert.Equal(default, BlockRaise.PoseAt(0.5f));
        // And it is CONTINUOUS at both ends, so the arm neither snaps off the side nor stops dead back on it.
        Assert.True(BlockRaise.WeightAt(0.001f) < 0.01f);
        Assert.True(BlockRaise.WeightAt(BlockRaise.Seconds - 0.001f) < 0.01f);
    }

    [Fact]
    public void ThePlateIsAllTheWayUpInsideAnEighthOfASecondAndBackDownBeforeHalfOne()
    {
        Assert.True(BlockRaise.RaiseSeconds <= 0.15f);
        Assert.Equal(1f, BlockRaise.WeightAt(BlockRaise.RaiseSeconds), 5);
        Assert.Equal(1f, BlockRaise.WeightAt(0.15f), 5);
        // The peak really is inside the first 0.15 s and nothing after it goes higher, sampled rather than
        // argued: the envelope is three segments and a wrong boundary between two of them is a plate that
        // comes up late.
        float peak = 0f, peakAt = 0f;
        for (float age = 0f; age <= 0.5f; age += 0.001f)
        {
            float weight = BlockRaise.WeightAt(age);
            Assert.InRange(weight, 0f, 1f);
            if (weight <= peak) continue;
            peak = weight;
            peakAt = age;
        }
        Assert.Equal(1f, peak, 5);
        Assert.True(peakAt <= 0.15f, "the raise peaks at " + peakAt + " s");
        // Up fast and down slow, which is what makes it read as an answer to the blow rather than a decision.
        Assert.True(BlockRaise.SettleSeconds > BlockRaise.RaiseSeconds);
        // The pose carries the envelope, so the arm at the top IS the raise and nothing has to weight it twice.
        Assert.Equal(BlockRaise.Raised, BlockRaise.PoseAt(BlockRaise.RaiseSeconds));
        Assert.Equal(BlockRaise.RaiseShoulder * 0.5f, BlockRaise.PoseAt(HalfWay()).Shoulder, 3);
    }

    [Fact]
    public void TheRaiseTouchesTheOffArmTheTorsoAndTheLegsAndNothingElseAtAll()
    {
        // Every channel filled with something recognisable, so a raise that wrote the wrong one shows up as a
        // number that moved rather than as a zero that stayed zero.
        WalkPose walk = Busy();

        BlockPose block = BlockRaise.PoseAt(BlockRaise.RaiseSeconds);
        WalkPose blocked = BlockRaise.Compose(walk, block);
        Assert.Equal(walk.LeftArm + BlockRaise.RaiseShoulder, blocked.LeftArm, 5);
        Assert.Equal(walk.LeftElbow + BlockRaise.RaiseElbow, blocked.LeftElbow, 5);
        Assert.Equal(walk.LeftArmYaw + BlockRaise.RaiseYaw, blocked.LeftArmYaw, 5);
        Assert.Equal(walk.LeftWrist + BlockRaise.RaiseWrist, blocked.LeftWrist, 5);
        // The torso's tip and the whole brace ADD as well, which is what keeps a body blocking mid stride
        // still striding under it and one blocking while it stands still leaning with its breath.
        Assert.Equal(walk.TorsoLean + BlockRaise.RaiseLean, blocked.TorsoLean, 5);
        Assert.Equal(walk.LeftLeg + block.LeftLeg, blocked.LeftLeg, 5);
        Assert.Equal(walk.RightLeg + block.RightLeg, blocked.RightLeg, 5);
        Assert.Equal(walk.LeftKnee + block.LeftKnee, blocked.LeftKnee, 5);
        Assert.Equal(walk.RightKnee + block.RightKnee, blocked.RightKnee, 5);
        Assert.Equal(walk.Bob + block.Drop, blocked.Bob, 5);
        // Everything else byte for byte, the weapon arm included: a body cut mid swing raises its plate
        // without dropping its blade.
        Assert.Equal(walk with
        {
            LeftArm = blocked.LeftArm, LeftElbow = blocked.LeftElbow, LeftArmYaw = blocked.LeftArmYaw,
            LeftWrist = blocked.LeftWrist, TorsoLean = blocked.TorsoLean, Bob = blocked.Bob,
            LeftLeg = blocked.LeftLeg, RightLeg = blocked.RightLeg,
            LeftKnee = blocked.LeftKnee, RightKnee = blocked.RightKnee,
        }, blocked);

        // A zero raise is the pose untouched, which is what lets this compose unconditionally.
        Assert.Equal(walk, BlockRaise.Compose(walk, BlockRaise.PoseAt(0f)));
        Assert.Equal(walk, BlockRaise.Compose(walk, BlockRaise.PoseAt(BlockRaise.Seconds)));

        // It ADDS rather than replacing, which is the whole reason a blocking body is still walking: the arm
        // it lands on is not the arm the pose came in with.
        Assert.NotEqual(BlockRaise.RaiseShoulder, blocked.LeftArm);
    }

    /// <summary>
    /// A SLASH IN THE SAME FRAME COMES THROUGH UNTOUCHED. The two cycles own opposite arms, so a body cut
    /// while it is swinging raises the plate and finishes the stroke, and this is the assertion that they do
    /// not share a channel: the weapon arm's three, the wrist that points its blade, and both legs are
    /// identical whether the block is composed over the stroke or not.
    /// </summary>
    [Fact]
    public void ASlashRunningInTheSameFrameIsNotTouchedByTheRaise()
    {
        WalkPose walk = Busy();
        AttackPose stroke = AttackStyles.PoseAt(AttackStyle.Slash, 0.35f);
        WalkPose swung = AttackSwing.Compose(walk, stroke, 1f);
        WalkPose both = BlockRaise.Compose(swung, BlockRaise.PoseAt(BlockRaise.RaiseSeconds));

        Assert.Equal(swung.RightArm, both.RightArm, 6);
        Assert.Equal(swung.RightElbow, both.RightElbow, 6);
        Assert.Equal(swung.RightArmYaw, both.RightArmYaw, 6);
        Assert.Equal(swung.RightWrist, both.RightWrist, 6);
        // The legs are the BRACE's and nothing else's: a slash writes neither, so what is on them is exactly
        // what the block put there.
        BlockPose block = BlockRaise.PoseAt(BlockRaise.RaiseSeconds);
        Assert.Equal(swung.LeftLeg + block.LeftLeg, both.LeftLeg, 6);
        Assert.Equal(swung.RightLeg + block.RightLeg, both.RightLeg, 6);
        // And the stroke really did write that arm, so this is not two zeroes agreeing with each other.
        Assert.NotEqual(walk.RightArm, swung.RightArm);
    }

    /// <summary>
    /// The lean: at its number at the top, exactly zero before the blow and exactly zero once the flinch has
    /// run out. Zero at both ends is the assertion that matters, because a body left tipped back by a blow it
    /// took half a second ago is a permanent posture change rather than a flinch.
    /// </summary>
    [Fact]
    public void TheBodyTipsBackAtTheTopAndIsExactlyLevelAtBothEnds()
    {
        Assert.Equal(BlockRaise.RaiseLean, BlockRaise.PoseAt(BlockRaise.RaiseSeconds).Lean, 6);
        // AWAY from the way the body faces. The sign is the intent rather than a taste: a body leaning into a
        // blow it is taking reads as the attacker.
        Assert.True(BlockRaise.RaiseLean < 0f);
        // Two degrees. Anything a viewer can name as a movement is too much.
        Assert.InRange(MathF.Abs(BlockRaise.RaiseLean), 0.01f, 0.06f);

        foreach (float age in new[] { -1f, 0f, BlockRaise.Seconds, 0.5f, 5f })
        {
            Assert.Equal(0f, BlockRaise.PoseAt(age).Lean);
            Assert.Equal(0f, BlockRaise.PoseAt(age).Drop);
            Assert.Equal(0f, BlockRaise.PoseAt(age).LeftLeg);
            Assert.Equal(0f, BlockRaise.PoseAt(age).RightLeg);
            Assert.Equal(0f, BlockRaise.PoseAt(age).LeftKnee);
            Assert.Equal(0f, BlockRaise.PoseAt(age).RightKnee);
        }
        // And it rides the SAME envelope the arm does, so the body gives with the plate rather than on its
        // own clock.
        float half = BlockRaise.WeightAt(HalfWay());
        Assert.Equal(BlockRaise.RaiseLean * half, BlockRaise.PoseAt(HalfWay()).Lean, 6);
    }

    /// <summary>
    /// THERE IS NO TUNED BOB. The flinch used to sink the whole body three centimetres on
    /// <c>WalkPose.Bob</c>, which carries the feet with it, and a blocking body pushed its soles through the
    /// floor. There is no tuned bob left to do it with: <see cref="BlockPose"/> carries no such channel and
    /// <see cref="BlockRaise"/> carries no such constant, and the only thing that reaches <c>WalkPose.Bob</c>
    /// is the drop <see cref="BlockRaise.StanceAt"/> SOLVED from the knees, which the bent legs pay back
    /// exactly.
    /// </summary>
    [Fact]
    public void NothingPutsATunedBobOnTheBodyAnyMore()
    {
        Assert.DoesNotContain(typeof(BlockPose).GetProperties(), p => p.Name == "Bob");
        Assert.DoesNotContain(typeof(BlockRaise).GetFields(), f => f.Name.Contains("Bob", StringComparison.Ordinal));

        // What does reach the bob is the solve and only the solve, on a body whose own pose has none.
        BlockPose block = BlockRaise.PoseAt(BlockRaise.RaiseSeconds);
        Assert.Equal(block.Drop, BlockRaise.Compose(WalkPose.Rest, block).Bob, 6);
        // And the drop is a centimetre or so rather than three, because it is what the legs got shorter by.
        Assert.True(block.Drop < 0f, "the hips rose by " + block.Drop + " m");
        Assert.InRange(-block.Drop, 0.005f, 0.03f);
        Assert.Equal(0.0090f, -block.Drop, 4);
    }

    /// <summary>
    /// THE FEET ARE ANCHORED. Both soles sit on the floor at every point of the raise on a body standing
    /// still, the off-side foot ends a step in front of the other one at the top, and both are level again at
    /// rest and once the flinch has run out.
    /// </summary>
    /// <remarks>
    /// Measured through the real composition rather than off the constants: the solve could be right and
    /// never reach the joints. Both rigs, because the step is a LENGTH and a small body that stepped a
    /// person's step would straddle whatever it stood on.
    /// </remarks>
    [Fact]
    public void BothSolesStayOnTheFloorThroughTheRaiseAndTheFeetStepApartAtTheTop()
    {
        var pose = new BodyPose(Vector3.Zero, 0f);
        Span<Matrix4x4> at = stackalloc Matrix4x4[TestBodies.HumanoidPieceCount];
        foreach (BodyRig rig in new[] { BodyRig.Human, TestBodies.Small })
        {
            // Five points through the flinch: before it, on the way up, at the top, settling, and past the
            // end of it. A stance solved once and scaled would pass the ends and fail the middle.
            foreach (float age in new[]
            {
                0f, BlockRaise.RaiseSeconds * 0.5f, BlockRaise.RaiseSeconds,
                BlockRaise.RaiseSeconds + BlockRaise.HoldSeconds + (BlockRaise.SettleSeconds * 0.5f),
                BlockRaise.Seconds,
            })
            {
                WalkPose walk = BlockRaise.Compose(WalkPose.Rest, BlockRaise.PoseAt(age, rig));
                TestBodies.Humanoid(rig, pose, walk, at);
                Vector3 left = TestBodies.Sole(rig, at, TestBodies.ShinLeft);
                Vector3 right = TestBodies.Sole(rig, at, TestBodies.ShinRight);
                // A millimetre, on a body that is standing rather than striding.
                Assert.Equal(0f, left.Y, 3);
                Assert.Equal(0f, right.Y, 3);
                // And neither foot wanders sideways: the brace is forward and back, in the plane the body
                // faces.
                Assert.Equal(rig.LeftHip.X, left.X, 4);
                Assert.Equal(rig.RightHip.X, right.X, 4);
            }

            // AT THE TOP the off-side foot is forward and the other is back, by twice the step.
            float step = BlockRaise.StepFraction * rig.RestHeightMetres;
            (Vector3 top, Vector3 back) = Feet(rig, BlockRaise.RaiseSeconds);
            Assert.Equal(step, top.Z, 3);
            Assert.Equal(-step, back.Z, 3);
            Assert.Equal(2f * step, top.Z - back.Z, 3);

            // AND LEVEL AT BOTH ENDS, which is what makes it a flinch rather than a stance the body is left
            // standing in.
            foreach (float age in new[] { 0f, BlockRaise.Seconds })
            {
                (Vector3 one, Vector3 other) = Feet(rig, age);
                Assert.Equal(0f, one.Z, 5);
                Assert.Equal(0f, other.Z, 5);
            }
        }
    }

    /// <summary>
    /// BOTH KNEES BEND PAST THE REST KNEE and the hips settle for it, which is where the body's dip comes
    /// from. The knee is the one number the stance is tuned by and the other three are solved off it, the
    /// rest stance's own arrangement.
    /// </summary>
    [Fact]
    public void BothKneesBendPastTheRestKneeAndTheHipsSettleForIt()
    {
        BlockPose top = BlockRaise.PoseAt(BlockRaise.RaiseSeconds);
        Assert.Equal(IdleBreath.RestKneeRadians + BlockRaise.StanceKneeRadians, top.LeftKnee, 5);
        Assert.Equal(top.LeftKnee, top.RightKnee, 6);
        Assert.True(top.LeftKnee > IdleBreath.RestKneeRadians,
            "the block bends the knee to " + top.LeftKnee + " against a rest knee of "
            + IdleBreath.RestKneeRadians);
        // A touch: ten degrees past a standing body's own break, not a crouch.
        Assert.InRange(BlockRaise.StanceKneeRadians, 0.1f, 0.3f);

        // The hips come down between half a centimetre and three, and it is SOLVED: the drop is exactly what
        // the two bent segments span less than the straight leg, once the foot has stepped out from under the
        // hip.
        Assert.InRange(-top.Drop, 0.005f, 0.03f);
        (float leftLeg, float rightLeg, float knee, float drop) =
            BlockRaise.StanceAt(1f, BodyRig.Human);
        Assert.Equal(top.LeftLeg, leftLeg, 6);
        Assert.Equal(top.RightLeg, rightLeg, 6);
        Assert.Equal(top.LeftKnee, knee, 6);
        Assert.Equal(top.Drop, drop, 6);
        // The off-side hip pitches further forward than the other one, which is what puts that foot in
        // front: the two differ by twice the angle the step subtends and by nothing else.
        Assert.True(leftLeg > rightLeg, "the off-side hip is at " + leftLeg + " against " + rightLeg);
        // A zero weight is four exact zeroes, so a body that is not blocking stands exactly as it stood.
        Assert.Equal(default, BlockRaise.StanceAt(0f, BodyRig.Human));
        Assert.Equal(default, BlockRaise.StanceAt(-1f, BodyRig.Human));
    }

    /// <summary>
    /// THE PLATE FACES THE ATTACKER. The body already faces whatever it is fighting, so the assertion is that
    /// the plate's own face normal ends up down engine +z, and it is read off the real composition rather
    /// than off the constants: the turn survives the shoulder's yaw, the shoulder's swing and the elbow's
    /// fold, or it is not a turn that a block can use.
    /// </summary>
    [Fact]
    public void ThePlateFacesForwardAtTheTopAndOutToTheSideAtRest()
    {
        // AT REST it is the carry's own answer, out at the character's left, untouched by the new axis:
        // nothing turns a plate that is not answering a blow.
        Vector3 resting = Face(0f);
        Assert.Equal(1f, resting.X, 3);
        Assert.Equal(0f, resting.Z, 3);

        // AT THE TOP it is square at whatever the body faces, and it is DEAD square rather than merely
        // forward: the turn is a quarter of a circle off the side, and the arm under it cannot tip it, which
        // is the whole reason the axis is the body's rather than the forearm's.
        Vector3 raised = Face(BlockRaise.RaiseSeconds);
        Assert.True(raised.Z > 0f, "the plate faces z " + raised.Z + ", which is backwards");
        Assert.True(raised.Z > MathF.Abs(raised.X) && raised.Z > MathF.Abs(raised.Y),
            "the plate faces (" + raised.X + ", " + raised.Y + ", " + raised.Z + "), which is not forward");
        Assert.Equal(1f, raised.Z, 2);
        Assert.Equal(0f, raised.X, 2);
        // Not quite zero in y, and the residue is the LEAN rather than slop: the torso tips the whole upper
        // body back and the arm rides it, so the face lifts by a fraction of a degree more than a hair.
        Assert.InRange(raised.Y, 0f, 0.03f);

        // The two halves of that quarter turn, so a later change to either shows up here rather than as a
        // plate 20 degrees off: the shoulder's yaw carries part of it and the hand's turn carries the rest.
        Assert.Equal(-MathF.PI / 2f, BlockRaise.RaiseWrist - BlockRaise.RaiseYaw, 5);
        Assert.True(BlockRaise.RaiseWrist < 0f);
        // The difference and one sign do not fix the other sign. The shoulder yaws the arm ACROSS the body,
        // which is positive, and a flipped one would still satisfy the two lines above with a plate swung
        // out behind the elbow.
        Assert.True(BlockRaise.RaiseYaw > 0f);

        // And it TURNS THROUGH, so the plate sweeps round rather than snapping: half way up it is half way
        // round, pointing out between the side and the front.
        Vector3 midway = Face(HalfWay());
        Assert.InRange(midway.Z, 0.2f, 0.8f);
        Assert.InRange(midway.X, 0.6f, 1f);
    }

    /// <summary>
    /// The ELBOW really folds, which is what puts the forearm across the front of the chest with the upper
    /// arm left down beside the ribs. Read off the drawn transforms rather than off the constant: the number
    /// could be right and never reach the joint.
    /// </summary>
    [Fact]
    public void TheElbowFoldsAndTheFistEndsUpInFrontOfTheBody()
    {
        Assert.InRange(BlockRaise.RaiseElbow, 1.1f, 1.3f);
        Assert.True(BlockRaise.RaiseElbow > BlockRaise.RaiseShoulder * 2f,
            "the elbow folds " + BlockRaise.RaiseElbow + " against a shoulder swing of "
            + BlockRaise.RaiseShoulder + ", so the arm is being lifted rather than folded");

        Matrix4x4[] rest = TestHeldPieces.Standing(WalkPose.Rest);
        Matrix4x4[] top = TestHeldPieces.Standing(
            BlockRaise.Compose(WalkPose.Rest, BlockRaise.PoseAt(BlockRaise.RaiseSeconds)));
        Vector3 shoulder = BodyRig.Human.LeftShoulder;
        Vector3 elbow = Vector3.Transform(Vector3.Zero, top[TestBodies.ForearmLeft]);
        Vector3 fist = Vector3.Transform(BodyRig.Human.HandFromElbow, top[TestBodies.ForearmLeft]);
        Vector3 hanging = Vector3.Transform(BodyRig.Human.HandFromElbow, rest[TestBodies.ForearmLeft]);

        // The ELBOW stays down beside the ribs: barely off the shoulder's own line, and a long way under it.
        Assert.True(elbow.Y < shoulder.Y - 0.2f, "the elbow is at y " + elbow.Y + ", up at the shoulder");
        Assert.True(MathF.Abs(elbow.X - shoulder.X) < 0.12f,
            "the elbow swung to x " + elbow.X + " from a shoulder at " + shoulder.X);
        // The FIST comes up, forward and in off the hip, which is the fold rather than a lift.
        Assert.True(fist.Z > hanging.Z + 0.2f, "the fist only reached z " + fist.Z);
        Assert.True(fist.Y > hanging.Y + 0.2f, "the fist only reached y " + fist.Y);
        Assert.True(fist.X < hanging.X - 0.05f, "the fist stayed out at x " + fist.X);
        // And it is the fist that travelled, not the elbow: the forearm crossed and the upper arm did not.
        Assert.True(fist.Z - elbow.Z > 0.15f,
            "the forearm reaches only " + (fist.Z - elbow.Z) + " m in front of its own elbow");
    }

    // Both soles in the body's own frame at one age, off side first, on a body standing still.
    static (Vector3 Left, Vector3 Right) Feet(BodyRig rig, float age)
    {
        var at = new Matrix4x4[TestBodies.HumanoidPieceCount];
        TestBodies.Humanoid(rig, new BodyPose(Vector3.Zero, 0f),
            BlockRaise.Compose(WalkPose.Rest, BlockRaise.PoseAt(age, rig)), at);
        return (TestBodies.Sole(rig, at, TestBodies.ShinLeft),
            TestBodies.Sole(rig, at, TestBodies.ShinRight));
    }

    // Where the envelope is exactly half way up, which is the middle of the raise ramp by the smoothstep's own
    // symmetry.
    static float HalfWay() => BlockRaise.RaiseSeconds * 0.5f;

    // The plate's own face normal in the body's frame at one age, out of the real composition. The piece is
    // authored facing its own +z, so this is the direction it presents.
    static Vector3 Face(float age)
    {
        WalkPose walk = BlockRaise.Compose(WalkPose.Rest, BlockRaise.PoseAt(age));
        Matrix4x4 held = TestHeldPieces.HeldInOffHand(TestHeldPieces.Standing(walk), walk.LeftWrist);
        return Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, held));
    }

    // A body doing something in every channel, so a raise that wrote the wrong one shows as a number that
    // moved rather than as a zero that stayed zero.
    static WalkPose Busy() => new(
        LeftArm: 0.11f, RightArm: 0.22f, LeftLeg: 0.33f, RightLeg: 0.44f,
        LeftElbow: 0.55f, RightElbow: 0.66f, LeftKnee: 0.77f, RightKnee: 0.88f,
        Bob: -0.02f, Lean: 0.05f, RightArmYaw: 0.9f, RightWrist: 0.7f,
        TorsoRise: 0.004f, TorsoLean: -0.01f, LeftArmYaw: 0.06f, LeftWrist: 0.08f);
}
