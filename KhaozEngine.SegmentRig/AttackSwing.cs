using System;

namespace KhaozEngine.SegmentRig;

/// <summary>The weapon arm at one point in a fighting swing: the shoulder's two axes, the elbow's flexion,
/// the wrist on the weapon, and whether the blow is landing at this phase.</summary>
/// <param name="Shoulder">The weapon shoulder's swing, radians, positive FORWARD, for
/// <see cref="WalkPose.RightArm"/>.</param>
/// <param name="Elbow">The weapon elbow's flexion, radians, never negative, for
/// <see cref="WalkPose.RightElbow"/>.</param>
/// <param name="Yaw">The weapon shoulder's turn about the body's up axis, radians, positive carrying the arm
/// OUT to the weapon side, for <see cref="WalkPose.RightArmYaw"/>. Negative is across the chest.</param>
/// <param name="Wrist">How far the weapon is tipped in the fist, radians, positive rolling its point the way
/// the body faces, for <see cref="WalkPose.RightWrist"/>.</param>
/// <param name="IsImpact">Whether this phase is inside the follow-through at full reach. A PREDICATE over the
/// phase rather than an event, <see cref="ChopPose.IsImpact"/>'s contract exactly.</param>
public readonly record struct AttackPose(float Shoulder, float Elbow, float Yaw, float Wrist, bool IsImpact);

/// <summary>
/// The swing a BARE HANDED fight draws: a punch on the weapon arm alone, thrown from a guard at the ribs
/// straight at whatever is in front, phased off the blows a game reports rather than off distance travelled.
/// </summary>
/// <remarks>
/// One of the two stroke shapes a fight has. This one is for an EMPTY hand, and a body holding a weapon takes
/// <see cref="SlashSwing"/> instead (<see cref="AttackStyles"/> dispatches between them off a style the game
/// picks). A body with no hand to put a weapon in punches too, and a four-legged one lays
/// <see cref="Headbutt"/> over its gait off the same phase instead.
/// <para>PHASE 0 IS THE IMPACT and so is phase 1, which is what puts the picture on the authority's beat: a
/// server resolves a blow at the end of a cadence and tells every watching client in the same frame it draws
/// the damage, so seeding the phase at zero on that arrival lands the fist as the number goes up. The two
/// ends of the cycle are the SAME pose, so a seed is continuous rather than a snap.</para>
/// <para>THERE IS NO WIND-UP, and it is the whole shape of the cycle. A stroke that draws back over most of
/// its cadence tells the eye a blow is coming a second and a half before it lands, and over a fight of twenty
/// swings that reads as an arm waving rather than as blows being traded. So the arm RESTS. It recovers off
/// the blow over <see cref="RestPhase"/>, holds the guard through the middle of the cadence with nothing
/// moving at all, and throws the strike after <see cref="StrikePhase"/>, which is the last eighth of the
/// cadence and about a third of a second at a typical one, eased in so it is fastest as it arrives.</para>
/// <para>THE STRIKE CAN BE PREDICTED, which falls out of the rest above: the cadence is known, so an arm that
/// is still running after one blow throws the next one at the moment the authority's next blow is due. The
/// event arriving re-seeds phase 0, so a late one snaps the arm back onto the blow by at most the network
/// jitter and an early one is a recovery that started a few frames sooner. Whether to predict at all is the
/// GAME's call and is not decided here: a stroke thrown after the killing blow swings at nothing, so a
/// consumer gates its own prediction on the target still being on its feet.</para>
/// <para>Headlessly testable by construction, like <see cref="ChopSwing"/> and <see cref="WalkCycle"/>: no
/// scene, no clock, no renderer.</para>
/// </remarks>
public static class AttackSwing
{
    /// <summary>How much of the cadence still counts as the blow LANDING, as a fraction. A tenth of a second
    /// at a typical cadence: long enough for a frame to catch the rising edge, short enough that the
    /// follow-through is over before the eye has left the damage number.</summary>
    public const float ImpactPhase = 0.04f;

    /// <summary>Where the recovery ends and the guard is reached, as a fraction of the cadence. The arm comes
    /// off the blow inside the first tenth and then stops, which is what leaves the middle of the cadence
    /// still.</summary>
    public const float RestPhase = 0.1f;

    /// <summary>Where the guard ends and the strike begins, as a fraction of the cadence. The last eight
    /// percent and a bit, which is about a third of a second at a typical cadence: the whole blow happens
    /// inside the moment before it lands, and everything before it is a body standing on guard.</summary>
    public const float StrikePhase = 0.88f;

    /// <summary>The weapon shoulder's pitch at the moment the blow lands, radians, positive forward. Most of
    /// a right angle: the arm is out in front of the body at about shoulder height rather than swinging up
    /// from the hip, which is what makes a punch read as thrown at the thing in front.</summary>
    public const float ImpactShoulder = 1.35f;

    /// <summary>The elbow at the blow, radians. Nearly straight, and not dead straight for
    /// <see cref="ChopSwing.ImpactElbow"/>'s reason: a locked elbow reads as a pose rather than as an arm
    /// that arrived somewhere.</summary>
    public const float ImpactElbow = 0.15f;

    /// <summary>The yaw at the blow, radians, positive being out to the weapon side. NEGATIVE and small: the
    /// fist comes in a little across the body's own centre line, because the thing being hit is in front of
    /// the body rather than off its weapon shoulder.</summary>
    public const float ImpactYaw = -0.2f;

    /// <summary>The wrist at the blow, radians, positive rolling the weapon's point the way the body faces.
    /// Small, because a bare fist has nothing in it to lead: this is the hand turning over into the punch.
    /// </summary>
    public const float ImpactWrist = 0.35f;

    /// <summary>The weapon shoulder's pitch on guard, radians. ZERO: the upper arm hangs where a standing
    /// body's does and the elbow does all the folding, which is what a guard is.</summary>
    public const float GuardShoulder = 0f;

    /// <summary>The elbow on guard, radians. Folded past a right angle, which puts the fist at 0.89 m on a
    /// 1.5 m body, up at the ribs and a hand's width in front of them.</summary>
    public const float GuardElbow = 1.8f;

    /// <summary>The yaw on guard, radians, out to the weapon side. Slightly out, so the elbow clears the ribs
    /// rather than folding into them.</summary>
    public const float GuardYaw = 0.3f;

    /// <summary>The wrist on guard, radians. Less rolled over than at the blow, so the strike turns the hand
    /// through as well as carrying it forward.</summary>
    public const float GuardWrist = 0.15f;

    /// <summary>How long the swing takes to fade in when a fight starts and out when it stops, seconds.
    /// <see cref="ChopSwing.BlendSeconds"/>'s value and its reason.</summary>
    public const float BlendSeconds = ChopSwing.BlendSeconds;

    /// <summary>How many cadences one swing keeps the arm going for before it eases out. A little over one:
    /// a fight is knowable only from the blows that land in it, so an arm still moving a fifth of a cadence
    /// after the last one is what covers an authoritative update arriving late. A fight that has ENDED never
    /// spends it, because a consumer gates its prediction on the fight still standing rather than on this
    /// running out.</summary>
    public const float HoldCadences = 1.2f;

    /// <summary>How long one swing keeps the arm going before it eases out, seconds.</summary>
    /// <param name="cadenceSeconds">That body's cadence, SECONDS. The caller's number: a game that derives it
    /// from its own update rate does that arithmetic on its own side and hands over the seconds.</param>
    public static float HoldSecondsFor(float cadenceSeconds) => cadenceSeconds * HoldCadences;

    /// <summary>How long a swing that ENDED the fight keeps the arm going, seconds: the recovery off that
    /// blow and nothing after it, so the arm comes down off the kill instead of standing on guard waiting for
    /// a blow that is never coming.</summary>
    /// <param name="cadenceSeconds">That body's cadence, seconds, exactly as above.</param>
    public static float RecoverySecondsFor(float cadenceSeconds) => cadenceSeconds * RestPhase;

    /// <summary>The weapon arm at a point in the stroke.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, with 0 and 1 both the impact. Values outside are
    /// wrapped, so a caller may hand over a raw accumulator.</param>
    public static AttackPose PoseAt(float phase) => AttackTrajectory.PoseAt(phase, Impact, Guard);

    /// <summary>Lays a swing over a walk pose on the weapon arm alone: its shoulder's two axes, its elbow and
    /// its wrist. Everything else the walk drew is passed through untouched, the breath included, and the two
    /// channels no other cycle writes are blended from zero rather than replaced.
    /// <see cref="ChopSwing.Compose"/>'s contract exactly, over the other stroke.</summary>
    /// <param name="walk">The body's own pose this frame, from <c>WalkCycle.Pose</c>.</param>
    /// <param name="swing">The stroke, from <see cref="PoseAt"/>.</param>
    /// <param name="weight">How much of the stroke is applied, 0 to 1. Values outside are clamped.</param>
    /// <returns>The walk pose with the weapon arm's four channels blended toward the stroke.</returns>
    public static WalkPose Compose(in WalkPose walk, in AttackPose swing, float weight)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return walk;
        return walk with
        {
            RightArm = Lerp(walk.RightArm, swing.Shoulder, w),
            RightElbow = Lerp(walk.RightElbow, swing.Elbow, w),
            RightArmYaw = Lerp(walk.RightArmYaw, swing.Yaw, w),
            RightWrist = Lerp(walk.RightWrist, swing.Wrist, w),
        };
    }

    /// <summary>The arm at the blow, as the four channels, for a caller comparing poses rather than sampling
    /// the cycle.</summary>
    public static AttackPose Impact => new(ImpactShoulder, ImpactElbow, ImpactYaw, ImpactWrist, true);

    /// <summary>The arm on guard, which is where it sits through the middle of every cadence.</summary>
    public static AttackPose Guard => new(GuardShoulder, GuardElbow, GuardYaw, GuardWrist, false);

    static float Lerp(float from, float to, float t) => from + ((to - from) * t);
}
