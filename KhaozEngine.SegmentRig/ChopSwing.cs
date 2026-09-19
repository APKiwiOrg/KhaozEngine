using System;

namespace KhaozEngine.SegmentRig;

/// <summary>The weapon arm at one point in a chop: the shoulder's two axes, the elbow's flexion, the wrist
/// on the tool, and whether the tool is IN the target at this phase.</summary>
/// <param name="Shoulder">The weapon shoulder's swing, radians, positive FORWARD, for
/// <see cref="WalkPose.RightArm"/>. A PITCH, and it raises the arm inside whatever plane
/// <paramref name="Yaw"/> has turned the shoulder to, which is why a stroke that sweeps sideways still
/// carries a positive number here.</param>
/// <param name="Elbow">The weapon elbow's flexion, radians, never negative, for
/// <see cref="WalkPose.RightElbow"/>.</param>
/// <param name="Yaw">The weapon shoulder's turn about the body's up axis, radians, positive carrying the arm
/// OUT to the weapon side, for <see cref="WalkPose.RightArmYaw"/>. Negative is across the chest.</param>
/// <param name="Wrist">How far the tool is tipped in the fist, radians, positive rolling its head the way the
/// body faces, for <see cref="WalkPose.RightWrist"/>.</param>
/// <param name="IsImpact">Whether this phase is inside the hold at the target. A PREDICATE over the phase
/// rather than an event, so the function stays pure: the caller keys the sound off the rising edge between
/// two frames, which is the frame the tool arrives.</param>
public readonly record struct ChopPose(float Shoulder, float Elbow, float Yaw, float Wrist, bool IsImpact);

/// <summary>
/// The swing a chopping action draws: one stroke per cadence on the WEAPON ARM alone, wound back slowly over
/// the shoulder and brought down hard into the target, phased off the action clock rather than off distance
/// travelled.
/// </summary>
/// <remarks>
/// <see cref="WalkCycle"/>'s sibling and deliberately its opposite in one respect. A body here is loose rigid
/// pieces and every pose is a <see cref="WalkPose"/>. What the walk cannot do is run off a clock: it is a
/// pure function of ground covered, and a body standing at a tree covers none. This is the same eight numbers
/// driven from the action's own cadence instead.
/// <para>It touches four of them, all on the weapon arm (<see cref="BodyRig.RightShoulder"/>): the
/// shoulder's pitch and its yaw (<see cref="WalkPose.RightArm"/>, <see cref="WalkPose.RightArmYaw"/>), the
/// elbow (<see cref="WalkPose.RightElbow"/>) and the wrist on the tool
/// (<see cref="WalkPose.RightWrist"/>). So <see cref="Compose"/> can lay it over a walk pose and leave the
/// legs, the off arm, the bob and the lean exactly as the walk drew them. A chopping body is standing still,
/// so in practice it draws rest legs and a swinging arm.</para>
/// <para>A TREE IS FELLED SIDEWAYS, and that is the reason the shoulder has two axes at all. A stroke that
/// runs up and down one plane is how you split a log on a block and not how you take a trunk down: the tool
/// goes out to the weapon side, up beside the head, and comes back in across the weapon shoulder's own line
/// into the target at chest height. The YAW is what buys that, and it is composed OUTSIDE the pitch
/// (<see cref="BodyRig.Limb(System.Numerics.Vector3, float, float)"/>) so the pitch raises the arm inside a
/// plane the yaw has already swung out. Over the chop the head's horizontal path is 0.86 m against 0.51 m of
/// vertical, so the sweep is 1.7 times as horizontal as it is vertical.</para>
/// <para>PHASE 0 IS THE IMPACT and so is phase 1, which is what makes the picture agree with the authority:
/// a server resolves at the END of each cadence and reports it once, so the client resets the phase on that
/// report and the tool is at the target exactly as the sound plays. The two ends of the cycle are the SAME
/// pose for the same reason, so the reset is continuous rather than a snap.</para>
/// <para>THE PITCH AND THE ELBOW BOTH ROTATE THE TOOL, and that is worth reading before touching any number
/// below, because it inverts the obvious guess. A held piece rides the FOREARM with its own grip tilt ahead
/// of it, and every rotation in that chain but the yaw is about the same local x, so they add: the tool
/// points at <c>gripTilt + Wrist + headOffset - (Shoulder + Elbow)</c> from straight up, measured toward the
/// way the body faces. The head offset in that sum is the PIECE's own, which a game measures off its mesh: a
/// short-hafted tool whose head centre sits 0.0925 m forward of a 0.3375 m rise off the grip carries 0.27 rad
/// of it. So a shoulder swinging FORWARD rotates the tool head BACK, and where the fist IS and where the tool
/// POINTS are otherwise one number. The numbers below are tuned against the head's own body-space track,
/// which is the only thing a viewer sees.</para>
/// <para>THE WRIST IS WHAT SEPARATES THEM. With three joints on one axis, an arm placed to reach a target
/// pointed its tool wherever that placement left it, which on the wind-back was straight down the upper arm:
/// a stroke without it put the haft within 0.6 mm of the arm's own axis at the top, so the tool drew THROUGH
/// the limb holding it. The wrist adds the fourth term and pays for both halves of the fix, the tool standing
/// clear on the way up and the head leading on the way down. A consumer pins that clearance at every phase
/// against <see cref="BodyRig.UpperArmRadiusMetres"/> PLUS the haft's own half width, because a tool is a
/// stick rather than a line: this stroke stands 0.116 m off the arm's axis against a person's own 0.094,
/// which leaves 0.022 m of daylight between the two surfaces.</para>
/// <para>Headlessly testable by construction, like <see cref="WalkCycle"/>: no scene, no clock, no
/// renderer.</para>
/// </remarks>
public static class ChopSwing
{
    /// <summary>How much of the cadence the tool SITS in the target after it lands, as a fraction. The stroke
    /// starts here rather than ending here: phase 0 is the impact, so a roll message resetting the phase to
    /// zero lands on the blow and the hold is what follows it.</summary>
    public const float HoldPhase = 0.1f;

    /// <summary>Where the wind-back ends and the chop begins, as a fraction of the cadence. The lift owns
    /// the seven tenths between <see cref="HoldPhase"/> and here and the chop owns the last two, which is
    /// what a heavy tool looks like: the fall is the fast part by a factor of three and a half.</summary>
    public const float LiftPhase = 0.8f;

    /// <summary>The weapon shoulder's PITCH at the moment the tool is in the target, radians, positive forward.
    /// It raises the arm inside the plane <see cref="ImpactYaw"/> has turned the shoulder to, so at the blow
    /// it is what puts the fist at 0.74 m, chest height on a 1.5 m body, rather than at the hip.</summary>
    public const float ImpactShoulder = 0.55f;

    /// <summary>The elbow at impact, radians. A break rather than a lock: a sideways stroke arrives with the
    /// arm still slightly bent, and an elbow driven dead straight into a target is a stopped pendulum rather
    /// than a chop.</summary>
    public const float ImpactElbow = 0.25f;

    /// <summary>The weapon shoulder's YAW at the blow, radians, positive being out to the weapon side. NEGATIVE
    /// here: the sweep carries the shoulder a third of a radian past its own neutral plane toward the off side.
    /// That is a SHOULDER angle and not a distance, and the difference matters: it brings the head back across
    /// the weapon shoulder's own line and stops it 0.04 m short of the body's centre, which is what makes the
    /// blow read as coming through the target rather than as poking at it.</summary>
    public const float ImpactYaw = -0.3f;

    /// <summary>The wrist at the blow, radians, positive rolling the head the way the body faces. The tool is
    /// laid over toward the target here rather than standing up out of the fist: with the arm's own angles,
    /// a 0.6 grip tilt and a 0.27 head offset counted (0.6 + 1 + 0.27 - (0.55 + 0.25)), it puts the tool 1.07
    /// rad off vertical, which is 29 degrees above the horizontal, and carries the head 0.29 m in front of the
    /// fist and 0.91 m off the ground. That is a trunk at chest height, which is where a tree is cut.
    /// </summary>
    public const float ImpactWrist = 1f;

    /// <summary>The weapon shoulder's pitch at the top of the wind-back, radians. POSITIVE, and read that
    /// twice before comparing it to a one-plane stroke's negative: the yaw below has swung the shoulder's
    /// plane a quarter turn outward by this point, so pitching forward inside that plane carries the arm OUT
    /// and UP rather than out in front. The elbow ends up level with the shoulder and away from the ribs.
    /// </summary>
    public const float TopShoulder = 1.2f;

    /// <summary>The elbow at the top of the wind-back, radians. Half of what a one-plane stroke folds, because
    /// the elbow is not the only thing that can carry the head up: the yaw and the wrist do most of it here,
    /// and an elbow folded to 2.4 rad on a yawed-out arm tucks the tool behind the ear instead of cocking it
    /// beside the head.</summary>
    public const float TopElbow = 0.7f;

    /// <summary>The yaw at the top, radians, out to the weapon side. A quarter turn and a bit: the arm is
    /// carried clear of the ribs and the tool is beside the body rather than over it, which is the whole
    /// difference between a wind-up and a raised arm.</summary>
    public const float TopYaw = 1.25f;

    /// <summary>The wrist at the top, radians. Still forward of the resting lean, but 0.3 rad less than at the
    /// blow, so the chop ROLLS the blade through: the head comes off a cocked wrist and arrives on a laid-over
    /// one. With the arm's own angles this stands the tool up beside the head at 1.42 m, which is head height,
    /// 0.32 m outboard of the shoulder, and 0.116 m clear of the arm's own axis at the tightest phase in the
    /// stroke.</summary>
    public const float TopWrist = 0.7f;

    /// <summary>How long the swing takes to fade in when an action starts, and out when it stops, seconds.
    /// <see cref="WalkCycle.BlendSeconds"/>'s value and its reason: short enough to read as immediate, long
    /// enough that a stop does not freeze an arm mid air.</summary>
    public const float BlendSeconds = WalkCycle.BlendSeconds;

    /// <summary>The weapon arm at a point in the stroke.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, with 0 and 1 both the impact. Values outside are
    /// wrapped, so a caller may hand over a raw accumulator.</param>
    public static ChopPose PoseAt(float phase)
    {
        float wrapped = phase - MathF.Floor(phase);
        if (!float.IsFinite(wrapped)) wrapped = 0f;
        // The hold. The tool is in the target and nothing moves, which is what gives the blow somewhere to
        // land: a stroke that turns straight around at the bottom reads as a bounce.
        if (wrapped < HoldPhase) return Blend(0f, true);
        // The wind-back, eased at both ends so it neither jerks out of the hold nor stops dead at the top.
        if (wrapped < LiftPhase) return Blend(Smooth((wrapped - HoldPhase) / (LiftPhase - HoldPhase)), false);
        // The chop. Eased IN and nothing else, so the arm leaves the top slowly and is at its fastest the
        // instant it arrives. An eased strike lands softly, and the whole point of the stroke is that it
        // arrives.
        return Blend(1f - Snap((wrapped - LiftPhase) / (1f - LiftPhase)), false);
    }

    // One point between the blow and the top, on all four channels at once. ONE parameter drives them, which
    // is why the stroke cannot wobble: every channel is monotone over each segment by construction, and the
    // two segments differ only in the easing that reaches this.
    static ChopPose Blend(float t, bool impact) => new(
        Lerp(ImpactShoulder, TopShoulder, t), Lerp(ImpactElbow, TopElbow, t),
        Lerp(ImpactYaw, TopYaw, t), Lerp(ImpactWrist, TopWrist, t), impact);

    /// <summary>Lays a stroke over a walk pose on the weapon arm alone: its shoulder's two axes, its elbow
    /// and its wrist. Everything else the walk drew is passed through untouched, the breath
    /// (<see cref="IdleBreath.Compose"/>) included, and the two channels no other cycle writes are blended
    /// from zero rather than replaced.</summary>
    /// <param name="walk">The body's own pose this frame, from <c>WalkCycle.Pose</c>.</param>
    /// <param name="swing">The stroke, from <see cref="PoseAt"/>.</param>
    /// <param name="weight">How much of the stroke is applied, 0 to 1: the blend that eases an action in and
    /// out. Values outside are clamped.</param>
    /// <returns>The walk pose with the weapon arm's four channels blended toward the stroke.</returns>
    public static WalkPose Compose(in WalkPose walk, in ChopPose swing, float weight)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return walk;
        return walk with
        {
            RightArm = Lerp(walk.RightArm, swing.Shoulder, w),
            RightElbow = Lerp(walk.RightElbow, swing.Elbow, w),
            // The two channels only a stroke ever fills in, blended from the walk's own zero rather than
            // written straight over it, so a stop eases the sweep and the wrist out with the rest of the arm.
            RightArmYaw = Lerp(walk.RightArmYaw, swing.Yaw, w),
            RightWrist = Lerp(walk.RightWrist, swing.Wrist, w),
        };
    }

    static float Lerp(float from, float to, float t) => from + ((to - from) * t);

    // Smoothstep: zero slope at both ends, which is what keeps the lift from starting with a jerk.
    static float Smooth(float t)
    {
        float c = Math.Clamp(t, 0f, 1f);
        return c * c * (3f - (2f * c));
    }

    // Cubic ease IN: zero slope leaving the top, three times the mean rate arriving. The asymmetry is the
    // whole difference between a chop and a pendulum.
    static float Snap(float t)
    {
        float c = Math.Clamp(t, 0f, 1f);
        return c * c * c;
    }
}
