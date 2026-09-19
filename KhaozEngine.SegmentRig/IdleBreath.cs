using System;

namespace KhaozEngine.SegmentRig;

/// <summary>
/// The breath a body draws while it is standing still: one slow cycle of the torso rising and the chest
/// tipping back, with the hanging arms following a quarter of a cycle behind it, and a soft break at both
/// elbows and both knees the whole time.
/// </summary>
/// <remarks>
/// <see cref="WalkCycle"/>'s sibling, and the smaller of the two by an order of magnitude. A body here is a
/// set of loose rigid pieces with no skin weights, so every pose is a <see cref="WalkPose"/>. This is the
/// same channels again, driven off a wall clock rather than off ground covered, because a body that is doing
/// nothing has none.
/// <para>IT IS MEANT TO BE INVISIBLE AT A GLANCE, which is the whole specification. A standing body that
/// reads as MOVING is worse than one that reads as still: the eye is drawn to it, and what it finds when it
/// looks is a mannequin swaying. What this buys is the second look, where a body that was breathing all
/// along stops looking switched off. So every number below is at the scale where a viewer notices it only by
/// comparing two moments. The numbers were raised once, to three times the rise and the sway and twice the
/// lean, because the first set moved the crown of a standing body half a pixel at a typical camera, and a
/// thing too small to rasterize is not invisible at a glance, it is absent.</para>
/// <para>EVERY CONSTANT IS THE WHOLE TRAVEL, top of the inhale to the bottom of the exhale, so the swing
/// either side of rest is HALF of it. That is the way a breath is measured: a rise of four millimetres is
/// four millimetres from the bottom of it to the top, not eight. <see cref="PoseAt"/> is where the halving
/// happens, once.</para>
/// <para>The curve is a plain sine rather than a triangle or a saw, which is the easing: its rate is zero at
/// the top of the inhale and at the bottom of the exhale, so the turn at each end is a settle rather than a
/// corner, and it is continuous across the wrap by construction.</para>
/// <para>THE BREATH ITSELF LIVES ENTIRELY ABOVE THE HIPS, which is the reason the two channels below are
/// named for the torso rather than for the body. A first version rose on <see cref="WalkPose.Bob"/> and
/// tipped on <see cref="WalkPose.Lean"/>, both of which carry the WHOLE body: at the bottom of every exhale
/// a person's soles sat 9 mm under the floor, and the lean slid them 12 mm each way across the ground they
/// were standing on. A walk can afford both because a walk is lifting its feet anyway. A body standing still
/// cannot, and feet sinking in and out of the ground is exactly the tell this is supposed to be too small to
/// produce. So the rise and the tip go on <see cref="WalkPose.TorsoRise"/> and
/// <see cref="WalkPose.TorsoLean"/>, and the crown still travels the same 18 mm it always did because both
/// pivot at the same hip height (<see cref="BodyRig.Torso(in BodyPose, in WalkPose)"/>).</para>
/// <para>THE LEGS GET A CONSTANT STANCE, and it is the one thing here that is not a breath. A standing
/// body's knees are never locked straight, so <see cref="RestStance"/> puts a soft break in both of them and
/// swings both thighs forward by exactly the angle that carries each foot back under its own hip, and the
/// hips settle by what the bent leg is shorter than the straight one so the SOLES STAY AT ZERO by
/// construction rather than by a number that was tuned until they looked planted. Every channel of it is a
/// CONSTANT held across the whole cycle, exactly as <see cref="RestElbowRadians"/> is: nothing about the
/// stance is a wave, so a standing body reads as a body at ease rather than as one shifting its weight, and
/// the invisible-at-a-glance rule above still holds.</para>
/// <para>THE PHASE IS SEEDED PER BODY, which is the one part that is not cosmetic. A crowd of bodies all
/// breathing on the same clock is a chorus line, and a viewer reads that as a mechanism instantly even when
/// a single body reads as nothing at all. The seed is the body's own identity and the offset is a
/// deterministic hash of it, so two clients watching one crowd agree on the offsets BETWEEN its bodies. They
/// do not agree on where the crowd as a whole is in its cycle, and are not meant to: the clock these offsets
/// are added to is each client's own, started when that client did. What is shared is the shape of the
/// crowd, not its moment.</para>
/// <para>Headlessly testable by construction, like its sibling: no scene, no clock, no renderer.</para>
/// </remarks>
public static class IdleBreath
{
    /// <summary>How long one whole breath takes, seconds: in, out, and back to where it started. Slow on
    /// purpose. A body at rest breathes about fifteen times a minute, and four seconds is that, which also
    /// puts the fastest part of the cycle an order of magnitude under a walk's.</summary>
    public const float PeriodSeconds = 4f;

    /// <summary>How far the TORSO rises over a breath, as a fraction of the body's own REST HEIGHT. A length
    /// rather than an angle, so it is the one number here that has to scale with the body: a body at 55
    /// percent of a person breathes 55 percent as far, which falls out of taking this off the rig's
    /// <see cref="BodyRig.RestHeightMetres"/> rather than off a constant in metres.
    /// <para>Eighteen millimetres on a person, near enough the walk's own 20 mm bob, so a standing body
    /// covers about as much ground as a walking one and takes four seconds to do it instead of one. At a
    /// typical camera that is half a pixel of rise on a body drawn 40 pixels tall, and about a pixel at the
    /// crown once the lean below is in it, which is the smallest a thing can be and still be drawn.</para>
    /// <para>The number did not move when the breath came off the feet, and it did not have to: the torso
    /// pivots and rises about hip height, which is where the whole body used to lean from, so the crown
    /// travels exactly what it did and only the soles changed.</para>
    /// </summary>
    public const float RiseFraction = 0.012f;

    /// <summary>How far the chest tips through a breath, radians, and the chest tips BACK as it fills: the
    /// lean is negative at the top of the inhale. Just over two degrees of whole travel, which carries the
    /// crown of a person a couple of centimetres.
    /// <para>Raised HALF as far as the other two rather than the same, which is a decision and not a
    /// rounding. It was made when the lean still pivoted the WHOLE body about the hip and the soles slid the
    /// opposite way from the head, which is a much earlier tell than a head drifting: the ground under a foot
    /// is a fixed reference and the air over a head is not. The tip is above the hips now and the soles do
    /// not move at all, so that reason is spent, but the number stays where it was accepted.</para>
    /// </summary>
    public const float LeanRadians = 0.04f;

    /// <summary>How far a hanging arm swings through a breath, radians. BOTH arms take the same sign, which
    /// is what makes it a pendulum under a moving body rather than a gait: a walk's two arms are always
    /// opposed, and one frame of this pose with the arms opposed would read as the start of a step.
    /// <para>The largest of the three in pixels, about 1.3 at the fist, because the arm is the longest lever
    /// on the body and the only one whose far end hangs in open air rather than sitting against the ground
    /// or buried in the torso.</para></summary>
    public const float ArmSwayRadians = 0.09f;

    /// <summary>How far a resting elbow is broken, radians, the same on both arms and held there through the
    /// whole cycle rather than swung. An arm hanging dead straight is the single loudest thing about a body
    /// standing still: a real one keeps a few degrees at the elbow whatever else it is doing, and eleven and a
    /// half degrees is that. It is the only channel here that is not a wave, deliberately, because a folding
    /// elbow reads as a fidget where a held break reads as a body at ease.
    /// <para>Under half the bend the WALK carries (<see cref="WalkCycle.ElbowBendRadians"/>), so easing out of
    /// a stop opens the arms rather than closing them, and the two never both apply: a body with any walk in
    /// it carries no breath at all.</para>
    /// <para>It also buys clearance rather than costing it. The elbow carries both fists forward, and at a
    /// straight elbow a held plate passes through the front of the left thigh: at this bend it stands clear
    /// of it at the tightest phase of the sway.</para></summary>
    public const float RestElbowRadians = 0.2f;

    /// <summary>How far a resting KNEE is broken, radians, the same on both legs and held there through the
    /// whole cycle. The leg's answer to <see cref="RestElbowRadians"/> and a smaller number than it, because
    /// a leg is under load: a shade under six degrees, which is enough to round the knee off the way a
    /// standing body's is and far short of the crouch a bigger one reads as.
    /// <para>It is the ONLY number the stance is tuned by. The thigh's forward swing and the drop at the hip
    /// are both SOLVED from it (<see cref="RestStance"/>), so a change here keeps the soles on the floor and
    /// the feet under the hips without either being re-tuned beside it.</para></summary>
    public const float RestKneeRadians = 0.10f;

    /// <summary>How far behind the breath the arms are, as a fraction of the cycle. A quarter, so the arms
    /// are at their fastest as the torso reaches the top of the inhale and at rest as the chest turns
    /// around: they are hanging off the body rather than driven, and a lag is what hanging looks like.
    /// </summary>
    public const float ArmLagPhase = 0.25f;

    /// <summary>How long the breath takes to fade IN once a body has finished stopping, seconds. Twice
    /// <see cref="WalkCycle.BlendSeconds"/> and for the opposite reason: the walk eases out fast enough to
    /// read as immediate, and this eases in slowly enough that nobody sees it arrive.</summary>
    public const float BlendSeconds = 0.3f;

    /// <summary>The legs' resting stance for one body: how far both thighs swing forward, how far both knees
    /// break, and how far the hips settle for it. Every one of the three is a CONSTANT, the same at every
    /// phase of the breath, so this is a pose the legs are held at rather than anything that moves.</summary>
    /// <param name="rig">The body's own rig. The two lengths this is solved from (hip to knee, knee to sole)
    /// are the rig's, so a small body's stance is its own rather than a person's angles on a small body.</param>
    /// <returns>The thigh's forward swing, the knee's flexion, and the body's bob, in the units
    /// <see cref="WalkPose"/> carries them in.</returns>
    /// <remarks>
    /// SOLVED RATHER THAN TUNED, which is what keeps the soles exactly on the floor. Bend a knee alone and
    /// the foot swings out behind the body and the body gets shorter, so the sole leaves the ground in both
    /// axes at once. Two corrections put it back, and both fall out of the triangle the bent leg makes:
    /// <para>The FOOT goes back under the hip by swinging the whole leg forward through the angle whose
    /// tangent is the shin's own backward reach over the leg's total drop, which is the <c>atan2</c> below.
    /// At that angle the knee-to-sole line and the hip-to-knee line cancel in z exactly, so the sole sits
    /// directly under the hip whatever the flexion is.</para>
    /// <para>The HIPS settle by the difference between the straight leg and the bent one, which is the law of
    /// cosines over the two segments: a leg bent at the knee spans less than the two bones laid end to end,
    /// and dropping the body by that much is what puts the sole back on y 0. The bob is NEGATIVE for that
    /// reason, and it is small (under a millimetre on a person at this flexion), which is why the stance
    /// reads as a knee rather than as a crouch.</para>
    /// <para>Pure and rig-taking, so a test can pin it without a scene, and so the composition that draws it
    /// is the only place these three numbers exist.</para>
    /// </remarks>
    public static (float Leg, float Knee, float Bob) RestStance(BodyRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        // Both measured off the rig rather than assumed: the thigh is hip to knee, the shin knee to sole.
        float thigh = -rig.KneeFromHip.Y;
        float shin = rig.RightHip.Y + rig.KneeFromHip.Y;
        float knee = RestKneeRadians;
        float leg = MathF.Atan2(shin * MathF.Sin(knee), thigh + (shin * MathF.Cos(knee)));
        float vertical = MathF.Sqrt((thigh * thigh) + (shin * shin) + (2f * thigh * shin * MathF.Cos(knee)));
        return (leg, knee, vertical - rig.RightHip.Y);
    }

    /// <summary>Where in its own breath a body is at a moment, 0 to 1, with the body's own offset already
    /// folded in.</summary>
    /// <param name="seconds">The idle clock, seconds. Any value: it is wrapped.</param>
    /// <param name="seed">The body's identity, usually whatever id the world knows it by.</param>
    public static float PhaseAt(float seconds, long seed)
    {
        float phase = (seconds / PeriodSeconds) + PhaseFor(seed);
        phase -= MathF.Floor(phase);
        return float.IsFinite(phase) ? phase : 0f;
    }

    /// <summary>One body's offset into the breath, 0 to 1, hashed from its identity so a crowd does not
    /// inhale together.</summary>
    /// <param name="seed">The body's identity, usually whatever id the world knows it by.</param>
    /// <remarks>A fixed integer mix rather than a <c>Random</c>: it is pure, it needs no state to carry
    /// across frames, and every client that draws this body lands on the same offset for it. Ids are usually
    /// handed out in sequence, so the mix has to break up neighbours rather than merely spread the range,
    /// which is what an avalanching hash does and what taking the id modulo anything does not.
    /// <para>A seed of zero is the one input that comes back exactly zero through the whole mix. A world that
    /// hands out a zero id and cares should offset it.</para>
    /// </remarks>
    public static float PhaseFor(long seed)
    {
        ulong x = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL);
        x ^= x >> 29;
        x = unchecked(x * 0xBF58476D1CE4E5B9UL);
        x ^= x >> 32;
        // The top 24 bits over 2^24: every float in [0, 1) this can produce is exact.
        return (x >> 40) / 16777216f;
    }

    /// <summary>The idle alone at a moment, as a pose to be laid OVER whatever else the body is doing: the
    /// torso's rise and tip, both shoulders, the resting break at both elbows, and the legs' resting stance
    /// (<see cref="RestStance"/>) on both hips, both knees and the body's own bob. The body's LEAN is exactly
    /// zero, because nothing here tips the whole body, and the three stance channels are constants rather
    /// than anything that moves through the cycle.</summary>
    /// <param name="seconds">The idle clock, seconds.</param>
    /// <param name="seed">The body's identity, which decides its offset into the cycle.</param>
    /// <param name="rig">The body's own rig, for the height the rise is a fraction of. Null breathes at a
    /// person's size.</param>
    public static WalkPose PoseAt(float seconds, long seed, BodyRig? rig = null)
    {
        float phase = PhaseAt(seconds, seed);
        // Positive filling the chest, so the rise, the backward tip and the arms all read off one number.
        float breath = MathF.Sin(phase * MathF.Tau);
        float sway = MathF.Sin((phase - ArmLagPhase) * MathF.Tau);
        BodyRig body = rig ?? BodyRig.Human;
        float height = body.RestHeightMetres;
        float arm = sway * ArmSwayRadians * 0.5f;
        (float leg, float knee, float bob) = RestStance(body);
        return new WalkPose(
            LeftArm: arm, RightArm: arm,
            // The stance: both legs the same and both held there, so this is a body standing rather than a
            // body shifting its weight. The bob is what keeps the soles on the ground at that knee.
            LeftLeg: leg, RightLeg: leg, LeftKnee: knee, RightKnee: knee, Bob: bob,
            // The one channel that is not a wave: the arms rest bent, and they stay bent through the cycle.
            LeftElbow: RestElbowRadians, RightElbow: RestElbowRadians,
            TorsoRise: breath * RiseFraction * 0.5f * height,
            // Negated: the chest goes BACK as it fills, and a positive lean is the head going forward.
            TorsoLean: -breath * LeanRadians * 0.5f);
    }

    /// <summary>Lays an idle over a body's own pose: the two shoulders, the two elbows and the torso's own
    /// rise and lean, plus the legs' stance on the two hips, the two knees and the body's bob. The body's
    /// LEAN passes through untouched, because nothing in an idle tips the whole body.</summary>
    /// <param name="pose">The body's pose this frame, from <see cref="WalkCycle.Pose"/>.</param>
    /// <param name="breath">The breath, from <see cref="PoseAt"/>.</param>
    /// <param name="weight">How much of it is applied, 0 to 1, from <see cref="AdvanceWeight"/>. Values
    /// outside are clamped.</param>
    /// <remarks>ADDED rather than blended toward, and that is deliberate: a stroke REPLACES what the arm was
    /// doing, and a breath is a small displacement of whatever the body is already at. In practice the two
    /// never meet, because a body with any walk or any action in it carries no breath at all (see
    /// <see cref="AdvanceWeight"/>), so this only ever adds to a rest pose. It is written as an addition
    /// anyway, because the alternative reads as though a breath could overwrite a stride.</remarks>
    public static WalkPose Compose(in WalkPose pose, in WalkPose breath, float weight)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return pose;
        return pose with
        {
            LeftArm = pose.LeftArm + (breath.LeftArm * w),
            RightArm = pose.RightArm + (breath.RightArm * w),
            LeftElbow = pose.LeftElbow + (breath.LeftElbow * w),
            RightElbow = pose.RightElbow + (breath.RightElbow * w),
            TorsoRise = pose.TorsoRise + (breath.TorsoRise * w),
            TorsoLean = pose.TorsoLean + (breath.TorsoLean * w),
            // The stance. Added on the same terms as the arms, so it fades in with the blend rather than
            // snapping the knees the frame a body stops, and a body mid walk (which carries no idle at all)
            // keeps its gait's own legs exactly.
            LeftLeg = pose.LeftLeg + (breath.LeftLeg * w),
            RightLeg = pose.RightLeg + (breath.RightLeg * w),
            LeftKnee = pose.LeftKnee + (breath.LeftKnee * w),
            RightKnee = pose.RightKnee + (breath.RightKnee * w),
            Bob = pose.Bob + (breath.Bob * w),
        };
    }

    /// <summary>This frame's blend, from last frame's.</summary>
    /// <param name="weight">Last frame's blend, 0 to 1.</param>
    /// <param name="busy">Whether the body has ANY walk or any action in it this frame, which is the walk's
    /// own blend and any stroke's own blend being anything other than zero.</param>
    /// <param name="dt">Seconds since the last frame.</param>
    /// <remarks>
    /// IN OVER <see cref="BlendSeconds"/> AND OUT IN ONE FRAME, which is not the symmetry the walk has. A
    /// breath is what a body does when it has nothing else to do, so anything else starting takes it
    /// immediately, and it comes back only once the walk has finished easing out, which puts the whole return
    /// at <see cref="WalkCycle.BlendSeconds"/> plus this.
    /// <para>The cut is what keeps the breath OUT of a walk. Faded out over a blend instead, it would add a
    /// pendulum sway to both arms across the first two tenths of every step, on top of the walk's own arms
    /// swinging the opposite way from each other, which is exactly the frame a viewer is watching hardest.
    /// What is cut is a third of a centimetre and a degree, mid stride, in the frame the legs start moving:
    /// there is nothing there to see.</para>
    /// </remarks>
    public static float AdvanceWeight(float weight, bool busy, float dt)
    {
        if (busy) return 0f;
        if (dt < 0f) dt = 0f;
        return MathF.Min(1f, Math.Clamp(weight, 0f, 1f) + (dt / BlendSeconds));
    }
}
