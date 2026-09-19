using System;

namespace KhaozEngine.SegmentRig;

/// <summary>The body at one point in a block: the off shoulder's pitch and turn, the elbow's flexion, the
/// roll of the plate in the off hand, the small tip the torso answers with, and the braced stance the legs
/// take under it.</summary>
/// <param name="Shoulder">The off shoulder's swing, radians, positive FORWARD, for
/// <see cref="WalkPose.LeftArm"/>.</param>
/// <param name="Elbow">The off elbow's flexion, radians, never negative, for
/// <see cref="WalkPose.LeftElbow"/>.</param>
/// <param name="Yaw">The off shoulder's turn about the body's up axis, radians, positive carrying the arm
/// ACROSS the chest, for <see cref="WalkPose.LeftArmYaw"/>.</param>
/// <param name="Wrist">The plate's turn about the body's up axis at the fist, radians, for
/// <see cref="WalkPose.LeftWrist"/>. What FACES the plate at the attacker: see
/// <see cref="BlockRaise.RaiseWrist"/>.</param>
/// <param name="Lean">How far the torso tips, radians, for <see cref="WalkPose.TorsoLean"/>. NEGATIVE here,
/// which is away from the way the body faces: a body taking a blow gives with it.</param>
/// <param name="LeftLeg">The OFF-side hip's pitch, radians, positive forward, for
/// <see cref="WalkPose.LeftLeg"/>. Part of <see cref="BlockRaise.StanceAt"/>'s solve rather than a number of
/// its own.</param>
/// <param name="RightLeg">The other hip's pitch, radians, same sense, for
/// <see cref="WalkPose.RightLeg"/>.</param>
/// <param name="LeftKnee">The off-side knee's flexion, radians, never negative, for
/// <see cref="WalkPose.LeftKnee"/>.</param>
/// <param name="RightKnee">The other knee's flexion, radians, for
/// <see cref="WalkPose.RightKnee"/>.</param>
/// <param name="Drop">How far the HIPS come down, metres, for <see cref="WalkPose.Bob"/>. Negative, and
/// SOLVED from the knees rather than chosen: it is exactly what the bent legs got shorter by, so the soles
/// land back on the floor they started on. See <see cref="BlockRaise.StanceAt"/>.</param>
public readonly record struct BlockPose(
    float Shoulder, float Elbow, float Yaw, float Wrist = 0f, float Lean = 0f,
    float LeftLeg = 0f, float RightLeg = 0f, float LeftKnee = 0f, float RightKnee = 0f, float Drop = 0f);

/// <summary>
/// The flinch a body carrying a plate in its off hand answers a blow with: the plate comes up and across in
/// front of the chest for a beat and settles back to the side.
/// </summary>
/// <remarks>
/// TWO decisions that only make sense together. The plate HANGS at the side while the body walks and stands,
/// which is what takes it out of the chest, and the price of that is a guard that never moves. So it comes up
/// on every blow TAKEN, briefly, and it TURNS TO FACE the attacker as it comes: the body already faces what
/// it is fighting, so a quarter turn off the body's side is square at the thing hitting it
/// (<see cref="RaiseWrist"/>). The SIZE of it is subtle. The plate stays under the shoulder line, the body
/// does not turn to meet the attacker, and all the body itself does is tip back
/// (<see cref="RaiseLean"/>) and take the small braced stance below.
/// <para>THE FEET ARE ANCHORED, which is why the body's dip is no longer a number. It used to be one: the
/// whole body sank three centimetres on <see cref="WalkPose.Bob"/>, which carries the feet with it, so a
/// body blocking pushed its soles through the floor for half a second. What it does now is BRACE. The
/// off-side foot takes a slight step forward and the other one a slight step back
/// (<see cref="StepFraction"/>), both knees bend a touch past the rest knee
/// (<see cref="StanceKneeRadians"/>), and the hips settle by exactly what the bent legs got shorter by
/// (<see cref="StanceAt"/>). So the dip comes off the legs, it is about a centimetre rather than three, and
/// both soles stay on the ground the whole way through. <see cref="IdleBreath.RestStance"/> is the pattern
/// and the arithmetic is the same: the knee is the one thing tuned, and the hips and the thighs are solved
/// from it.</para>
/// <para>It is a REACTION rather than an action, which is the whole difference between this and
/// <see cref="AttackSwing"/>. There is no cadence to phase it against and no state on the wire that says a
/// body is blocking: one blow lands, the arm answers it once, and the envelope runs out. So this is an AGE
/// rather than a phase, it does not wrap, and a second blow inside the first raise restarts it from the
/// beginning rather than stacking a second one on top.</para>
/// <para>IT ADDS RATHER THAN REPLACING, which is the other place it parts company with the strokes. A swing
/// OWNS the weapon arm for as long as it runs, so it lerps that arm onto the stroke and back. A block lasts
/// under half a second on an arm that is still walking, and lerping it onto a fixed pose would stop the arm's
/// own swing dead for the length of the flinch and start it again afterwards, which reads as a stutter. The
/// raise is laid ON TOP of whatever the walk and the breath left the off arm at, so a body blocking mid stride
/// is still striding. A zero adds nothing, so a body that is not blocking composes to exactly the pose it
/// always did.</para>
/// <para>Headlessly testable by construction, <see cref="WalkCycle"/>'s rule: no scene, no clock, no engine
/// type.</para>
/// </remarks>
public static class BlockRaise
{
    /// <summary>How long the plate takes to come up, seconds. Fast enough to be an answer to the blow rather
    /// than a decision taken after it.</summary>
    public const float RaiseSeconds = 0.12f;

    /// <summary>How long the arm holds at the top, seconds. A beat, so the raise reads as a block rather than
    /// as a twitch that turned round the moment it arrived.</summary>
    public const float HoldSeconds = 0.05f;

    /// <summary>How long the arm takes to settle back to the side, seconds. Longer than the raise, because
    /// coming up is a reaction and going down is gravity.</summary>
    public const float SettleSeconds = 0.3f;

    /// <summary>The whole flinch, seconds, and the age past which nothing is drawn. Under half a second: a
    /// plate that is still up when the next blow lands is a guard rather than a block.</summary>
    public static float Seconds => RaiseSeconds + HoldSeconds + SettleSeconds;

    /// <summary>The off shoulder's pitch at the top of the raise, radians, positive forward. Half a radian,
    /// about twenty nine degrees: the arm comes off the hip and out in front of the body, which is what puts
    /// the plate clear of the chest once the elbow has folded the forearm across it.</summary>
    public const float RaiseShoulder = 0.5f;

    /// <summary>The off elbow's extra flexion at the top, radians. A REAL fold, about seventy degrees, where
    /// this used to be a token half: the forearm comes up and across in front of the chest and the upper arm
    /// stays down beside the ribs, which is the shape of a block rather than of a whole arm lifted stiff.
    /// </summary>
    public const float RaiseElbow = 1.2f;

    /// <summary>The off shoulder's turn at the top, radians, positive being across the chest. Twenty degrees,
    /// which carries the fist in off the hip so the plate covers the middle of the body rather than the side
    /// of it. It no longer has to face the plate as well: <see cref="RaiseWrist"/> does that, and does it
    /// whatever the arm is doing.</summary>
    public const float RaiseYaw = 0.35f;

    /// <summary>The plate's turn about the BODY's up axis at the top, radians, for
    /// <see cref="WalkPose.LeftWrist"/>, in that channel's own sense.
    /// <para>THE SUM OF THIS AND <see cref="RaiseYaw"/> IS A QUARTER TURN, which is the whole definition and
    /// the reason it is written as one. A hanging plate's face points out at the character's left, so exactly
    /// a quarter turn from there is dead ahead, which is
    /// where the attacker is: the body already faces whatever it is fighting. The shoulder's yaw supplies
    /// <see cref="RaiseYaw"/> of that turn on its way past, and this supplies the rest, so the plate arrives
    /// square whatever the shoulder and the elbow do under it. Nothing between the fist and the body can tip
    /// it off that, because every one of those joints pitches about the body's x and a pitch about x leaves
    /// the hand's own x alone.</para></summary>
    public const float RaiseWrist = RaiseYaw - (MathF.PI / 2f);

    /// <summary>How far the torso tips at the top, radians, NEGATIVE so the head goes away from the way the
    /// body faces. Two degrees: the body gives with the blow rather than recoiling from it, and anything a
    /// viewer can name as a movement is too much.</summary>
    public const float RaiseLean = -0.035f;

    /// <summary>How far past the REST knee (<see cref="IdleBreath.RestKneeRadians"/>) both knees bend at the
    /// top of the brace, radians. About ten degrees, which is the difference between a body standing and a
    /// body taking a blow on a plate, and it is the ONLY number the stance is tuned by: the step's own hip
    /// pitch and the drop at the hips are both solved from it in <see cref="StanceAt"/>.</summary>
    public const float StanceKneeRadians = 0.18f;

    /// <summary>How far each foot moves at the top of the brace, as a fraction of the body's REST HEIGHT.
    /// The off-side foot goes that far forward and the other that far back, so the gap between them opens
    /// by twice it. Six centimetres on a person, which is slight on purpose: a brace rather than a lunge.
    /// <para>A FRACTION rather than a length, <see cref="IdleBreath.RiseFraction"/>'s reason: a step is the
    /// one thing here measured in metres, so a body at 55 percent of a person steps 55 percent as far
    /// instead of straddling the ground it stands on.</para></summary>
    public const float StepFraction = 0.04f;

    /// <summary>The body at the top of the raise on a PERSON's rig, as the ten channels, for a caller
    /// comparing poses rather than sampling the envelope. <see cref="RaisedFor"/> is the same thing on any
    /// other body.</summary>
    public static BlockPose Raised => RaisedFor(BodyRig.Human);

    /// <summary>The body at the top of the raise on one rig: the arm's four channels, the torso's tip, and
    /// the braced stance solved for that body's own legs.</summary>
    /// <param name="rig">The body's own rig. The stance is solved off its two leg segments and its rest
    /// height, so a small body braces on its own proportions.</param>
    public static BlockPose RaisedFor(BodyRig rig) => Braced(1f, rig);

    /// <summary>How much of the raise is applied at an age, 0 to 1: up over <see cref="RaiseSeconds"/>, held
    /// through <see cref="HoldSeconds"/>, and back down over <see cref="SettleSeconds"/>. Zero at and before
    /// the blow and zero from <see cref="Seconds"/> on, so an age that has run past the end costs nothing and
    /// needs no clamp at the call site.</summary>
    /// <param name="ageSeconds">Seconds since the blow landed. Negative and non-finite values are zero.
    /// </param>
    /// <remarks>Smoothed at both ends of each ramp, <c>AttackTrajectory</c>'s reason: an arm that leaves the
    /// side at full rate reads as a snap, and one that stops dead at the top reads as a pose.</remarks>
    public static float WeightAt(float ageSeconds)
    {
        if (!float.IsFinite(ageSeconds) || ageSeconds <= 0f) return 0f;
        if (ageSeconds < RaiseSeconds) return Smooth(ageSeconds / RaiseSeconds);
        float held = ageSeconds - RaiseSeconds;
        if (held < HoldSeconds) return 1f;
        float settling = held - HoldSeconds;
        if (settling >= SettleSeconds) return 0f;
        return Smooth(1f - (settling / SettleSeconds));
    }

    /// <summary>The braced LEGS at a weight, solved so both soles stay exactly on the floor: how far each
    /// hip pitches, how far both knees bend, and how far the hips come down for it.</summary>
    /// <param name="weight">How much of the brace is applied, 0 to 1, from <see cref="WeightAt"/>. Values
    /// outside are clamped, and zero is four exact zeroes: a body that is not blocking stands as it stood.
    /// </param>
    /// <param name="rig">The body's own rig. Both leg segments and the rest height are read off it, so a
    /// small body braces on its own legs rather than on a person's angles.</param>
    /// <returns>The two hip pitches, the knee flexion both knees take, and the hips' drop, in the units
    /// <see cref="WalkPose"/> carries them in.</returns>
    /// <remarks>
    /// SOLVED RATHER THAN TUNED, exactly as <see cref="IdleBreath.RestStance"/> is, and for the same reason:
    /// a knee bent on its own swings its foot out behind the body and makes the leg shorter, so the sole
    /// leaves the ground in both axes at once. Here the foot is wanted somewhere OTHER than under the hip,
    /// which is the one difference, so the solve runs the other way round:
    /// <para>The KNEE is the input, at <paramref name="weight"/> of <see cref="StanceKneeRadians"/> past the
    /// rest knee. The law of cosines over the two segments gives what the bent leg spans, and the foot is
    /// wanted a step forward or back of the hip, so the vertical the leg still covers is the other side of
    /// that right triangle. The HIPS drop by the difference between it and a straight leg, which is what puts
    /// both soles back on y 0.</para>
    /// <para>The HIP pitches by the angle between the thigh and the hip-to-sole line (the same
    /// <c>atan2</c> the rest stance uses) plus the angle that line makes with the vertical, which is what
    /// carries each foot to its own end of the step. So the two legs differ by twice the second term and
    /// nothing else.</para>
    /// <para>SOLVED AT EVERY WEIGHT rather than solved once and scaled, which is the part worth reading
    /// twice. None of the three is linear in the knee, so a stance solved at the top and multiplied by an
    /// envelope would sit the soles under the floor everywhere between the ends. Feeding the weight into the
    /// knee and solving from there keeps them planted at every frame of the raise and of the settle by
    /// construction, and it still costs exactly zero at both ends: a zero knee spans the straight leg, so the
    /// drop and both pitches come out zero.</para>
    /// </remarks>
    public static (float LeftLeg, float RightLeg, float Knee, float Drop) StanceAt(float weight, BodyRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return default;
        // Both measured off the rig rather than assumed: the thigh is hip to knee, the shin knee to sole.
        float thigh = -rig.KneeFromHip.Y;
        float shin = rig.RightHip.Y + rig.KneeFromHip.Y;
        float knee = w * (IdleBreath.RestKneeRadians + StanceKneeRadians);
        float step = w * StepFraction * rig.RestHeightMetres;
        // What the bent leg spans, hip to sole, and how much of that span is left over as height once the
        // foot has been put a step out from under the hip.
        float span = MathF.Sqrt((thigh * thigh) + (shin * shin) + (2f * thigh * shin * MathF.Cos(knee)));
        float height = MathF.Sqrt(MathF.Max(0f, (span * span) - (step * step)));
        float toSole = MathF.Atan2(shin * MathF.Sin(knee), thigh + (shin * MathF.Cos(knee)));
        float toStep = MathF.Atan2(step, height);
        return (toSole + toStep, toSole - toStep, knee, height - rig.RightHip.Y);
    }

    /// <summary>The whole flinch at one weight: the arm's four channels and the torso's tip carrying it
    /// linearly, and the legs solved at it.</summary>
    static BlockPose Braced(float weight, BodyRig rig)
    {
        (float left, float right, float knee, float drop) = StanceAt(weight, rig);
        return new BlockPose(RaiseShoulder * weight, RaiseElbow * weight, RaiseYaw * weight,
            RaiseWrist * weight, RaiseLean * weight, left, right, knee, knee, drop);
    }

    /// <summary>The off arm and the braced legs at one age: <see cref="RaisedFor"/> carrying
    /// <see cref="WeightAt"/>, so the pose IS what the body is doing that frame rather than a target to be
    /// blended toward again.</summary>
    /// <param name="ageSeconds">Seconds since the blow landed.</param>
    /// <param name="rig">The body's own rig, for the stance's two leg segments and its step. Null braces on
    /// a person's.</param>
    public static BlockPose PoseAt(float ageSeconds, BodyRig? rig = null)
    {
        float weight = WeightAt(ageSeconds);
        return weight <= 0f ? default : Braced(weight, rig ?? BodyRig.Human);
    }

    /// <summary>Lays a raise over a walk pose on the OFF arm, the torso's tip and the legs: the off
    /// shoulder's two axes, its elbow, the turn of what that hand holds, the torso's lean, both hips, both
    /// knees and the hips' own drop. Everything else the walk and the breath drew is passed through
    /// untouched, the weapon arm included, so a body blocking mid fight is still swinging.</summary>
    /// <param name="walk">The body's own pose this frame, whatever cycles have already composed into it.
    /// </param>
    /// <param name="block">The raise, from <see cref="PoseAt"/>. A zero one returns the pose unchanged.
    /// </param>
    /// <returns>The pose with the raise added to those nine channels.</returns>
    /// <remarks>The lean and the whole stance ADD to whatever is already there, exactly as the arm channels
    /// do, so a body blocking mid stride keeps its walk's own legs and bob under the brace and a body
    /// blocking while it breathes keeps the breath's lean. Replacing any of them would stop the cycle
    /// underneath dead for the length of the flinch, which is the stutter this whole type is written to
    /// avoid.
    /// <para>The price of adding rather than replacing is that the soles are planted EXACTLY for a body whose
    /// walk is not moving its legs, which is what a body standing to take a blow is, and approximately for
    /// one caught mid stride, whose feet are in the air anyway. That is the same trade
    /// <see cref="IdleBreath.Compose"/> makes with the rest stance.</para></remarks>
    public static WalkPose Compose(in WalkPose walk, in BlockPose block)
    {
        if (block == default) return walk;
        return walk with
        {
            LeftArm = walk.LeftArm + block.Shoulder,
            LeftElbow = walk.LeftElbow + block.Elbow,
            LeftArmYaw = walk.LeftArmYaw + block.Yaw,
            LeftWrist = walk.LeftWrist + block.Wrist,
            TorsoLean = walk.TorsoLean + block.Lean,
            // The brace. The drop rides Bob, which carries the FEET with it, and that is the point rather
            // than the old bug: the legs above are bent by exactly what the drop is, so the hips come down
            // and the soles do not move at all.
            LeftLeg = walk.LeftLeg + block.LeftLeg,
            RightLeg = walk.RightLeg + block.RightLeg,
            LeftKnee = walk.LeftKnee + block.LeftKnee,
            RightKnee = walk.RightKnee + block.RightKnee,
            Bob = walk.Bob + block.Drop,
        };
    }

    // Smoothstep: zero slope at both ends of the ramp.
    static float Smooth(float t)
    {
        float c = Math.Clamp(t, 0f, 1f);
        return c * c * (3f - (2f * c));
    }
}
