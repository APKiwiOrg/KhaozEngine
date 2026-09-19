using System;
using System.Numerics;

namespace KhaozEngine.SegmentRig;

/// <summary>A four-legged body at one point in a headbutt: the nod, the lunge, how the trunk tips and sinks
/// over its legs, the braced legs themselves, and whether the blow is landing at this phase.</summary>
/// <param name="HeadNod">How far the muzzle dips about the poll, radians, positive DOWN, for
/// <see cref="QuadrupedPose.HeadNod"/>.</param>
/// <param name="Surge">How far the whole body is carried forward, metres, for
/// <see cref="QuadrupedPose.Surge"/>.</param>
/// <param name="Pitch">How far the whole body tips about the shoulder joints, radians, positive dipping the
/// muzzle end, for <see cref="QuadrupedPose.Pitch"/>. SOLVED from the braced legs, not chosen: see
/// <see cref="Headbutt"/>.</param>
/// <param name="Bob">How far the shoulder joints rise, metres, negative sinking, for
/// <see cref="QuadrupedPose.Bob"/>. Solved with the pitch.</param>
/// <param name="ForeSwing">Both forelegs' swing about the shoulder, radians, positive forward. Solved.</param>
/// <param name="ForeFlex">Both carpi's fold, radians, never negative.</param>
/// <param name="HindSwing">Both hind legs' swing about the hip, radians, positive forward. Solved.</param>
/// <param name="HindFlex">Both hocks' fold, radians, never negative.</param>
/// <param name="IsImpact">Whether this phase is inside the follow-through, <see cref="AttackPose.IsImpact"/>'s
/// contract exactly.</param>
public readonly record struct HeadbuttPose(
    float HeadNod, float Surge, float Pitch, float Bob,
    float ForeSwing, float ForeFlex, float HindSwing, float HindFlex, bool IsImpact);

/// <summary>Where a four-legged body is in its fighting stroke this frame and how much of it is drawn: the
/// same phase and weight a person-shaped body throws its punch at, carried to a body that has no arm to throw
/// it with.</summary>
/// <param name="Phase">Where in the stroke, 0 to 1, with 0 the blow.</param>
/// <param name="Weight">How much of the stroke is drawn, 0 to 1.</param>
public readonly record struct QuadrupedStrike(float Phase, float Weight)
{
    /// <summary>No strike: a body standing or walking and fighting nothing.</summary>
    public static QuadrupedStrike None => default;
}

/// <summary>
/// A FOUR-LEGGED body's attack: the head drops and drives forward with the poll leading, the body lunges a
/// hand's width over hooves that stay where they stood, and it all settles back to a still guard until the
/// next blow.
/// </summary>
/// <remarks>
/// <see cref="AttackSwing"/>'s four-legged sibling, and the only stroke here a body with no arm can throw.
/// The punch is drawn on arm channels a four-legged body does not read, so a grazing animal fighting back on
/// it stands perfectly still.
/// <para>THE CLOCK IS THE PUNCH'S, exactly: the amount of the blow at each phase is read off
/// <see cref="AttackTrajectory"/> itself, so phase 0 and 1 are both the blow, the body recovers by
/// <see cref="AttackSwing.RestPhase"/>, stands still on guard until <see cref="AttackSwing.StrikePhase"/> and
/// strikes eased in. That is also what keeps a consumer's own prediction gate right for a four-legged body:
/// it reads those two constants and this stroke has no boundaries of its own.</para>
/// <para>THE SIZE is a cattle-sized grazer's. The nod and the lunge are what the eye reads at a third-person
/// camera distance, and nothing else is chosen: the trunk's tip, its sink and every leg angle are
/// solved.</para>
/// <para>THE LEGS ARE SOLVED RATHER THAN TUNED, <see cref="BlockRaise.StanceAt"/>'s rule on four legs. The
/// two chosen folds (<see cref="ForeBraceRadians"/>, <see cref="HindBraceRadians"/>) say how long each braced
/// leg is. The shoulders sink until the foreleg's reach meets its hoof, the trunk tips about them until the
/// hind leg's reach meets its own, and each upper leg swings until it points at its hoof. So every hoof is
/// exactly where it stood at every phase, and the whole motion rides both parent frames through
/// <see cref="QuadrupedRig.Strike"/>, so no socket parts from its ball either.</para>
/// <para>THE TRUNK TIPS THE WAY THE LEGS PUT IT, which on these proportions is the rump settling a degree and
/// a half rather than the muzzle end dipping. A hind leg cannot reach further than straight, and its hoof
/// already stands behind the hip, so carrying the hip forward over it has to bring the hip down. The fore
/// hoof stands ahead of the shoulder, so the same lunge swings the foreleg through vertical and asks almost
/// nothing of the shoulders. Dipping the front even one degree instead needs the carpus folded past thirty,
/// which reads as an animal going down on its knees rather than bracing into a blow.</para>
/// <para>Headlessly testable by construction: no scene, no clock, no renderer.</para>
/// </remarks>
public static class Headbutt
{
    /// <summary>How far the muzzle dips about the poll at the blow, radians. About twenty nine degrees, so the
    /// forehead comes round to face the target with the poll leading.</summary>
    public const float NodRadians = 0.5f;

    /// <summary>How far the whole body lunges forward at the blow, metres. About a hand's width, a lurch
    /// rather than a charge.</summary>
    public const float SurgeMetres = 0.15f;

    /// <summary>How far each carpus folds at the blow, radians. About eleven degrees: a foreleg braced under a
    /// lunge, not locked, for <see cref="ChopSwing.ImpactElbow"/>'s reason.</summary>
    public const float ForeBraceRadians = 0.2f;

    /// <summary>How far each hock folds at the blow, radians. About six degrees, less than the carpus, because
    /// the hind leg is the one pushing and a pushing leg is nearly straight.</summary>
    public const float HindBraceRadians = 0.1f;

    /// <summary>The body at a point in the stroke.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, with 0 and 1 both the blow. Values outside are wrapped
    /// and anything that is not a number is the blow, <see cref="AttackSwing.PoseAt"/>'s rule.</param>
    /// <param name="rig">The body's rig. The legs are solved off its joints, so another four-legged body
    /// braces on its own.</param>
    /// <returns>The pose, all zeroes through the guard.</returns>
    public static HeadbuttPose PoseAt(float phase, QuadrupedRig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        float amount = AttackTrajectory.AmountAt(phase, out bool landing);
        return amount > 0f ? Braced(amount, rig, landing) : default;
    }

    /// <summary>Lays a strike over the gait: every channel it moves is ADDED at the weight, so a body
    /// striking mid stride is still striding. The roll and both yaws are the gait's alone.</summary>
    /// <param name="gait">The body's own four-legged pose this frame.</param>
    /// <param name="strike">The strike, from <see cref="PoseAt"/>.</param>
    /// <param name="weight">How much of it is applied, 0 to 1. Values outside are clamped, and zero or not a
    /// number returns the gait untouched.</param>
    /// <remarks>The hooves are planted exactly at full weight over a standing gait. Below full weight a pose
    /// solved for full weight is scaled rather than solved again, which lets a hoof drift a few millimetres
    /// for the blend at either end of a fight: <see cref="BlockRaise.Compose"/>'s trade.</remarks>
    public static QuadrupedPose Compose(in QuadrupedPose gait, in HeadbuttPose strike, float weight)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (!(w > 0f) || strike == default) return gait;
        return gait with
        {
            LeftForeSwing = gait.LeftForeSwing + (strike.ForeSwing * w),
            RightForeSwing = gait.RightForeSwing + (strike.ForeSwing * w),
            LeftHindSwing = gait.LeftHindSwing + (strike.HindSwing * w),
            RightHindSwing = gait.RightHindSwing + (strike.HindSwing * w),
            LeftForeFlex = gait.LeftForeFlex + (strike.ForeFlex * w),
            RightForeFlex = gait.RightForeFlex + (strike.ForeFlex * w),
            LeftHindFlex = gait.LeftHindFlex + (strike.HindFlex * w),
            RightHindFlex = gait.RightHindFlex + (strike.HindFlex * w),
            Bob = gait.Bob + (strike.Bob * w),
            HeadNod = gait.HeadNod + (strike.HeadNod * w),
            Surge = gait.Surge + (strike.Surge * w),
            Pitch = gait.Pitch + (strike.Pitch * w),
        };
    }

    // The whole strike at one amount of the blow, solved in the body's side plane. A point there is X ahead
    // and Y up, engine z and y, and both sides of the body share one solve because nothing here leans left or
    // right. Solved at every amount rather than once and scaled, BlockRaise.StanceAt's reason: none of it is
    // linear in the lunge.
    static HeadbuttPose Braced(float amount, QuadrupedRig rig, bool landing)
    {
        float surge = amount * SurgeMetres;
        float foreFlex = amount * ForeBraceRadians;
        float hindFlex = amount * HindBraceRadians;
        Vector2 shoulder = Side(rig.RightShoulder);
        Vector2 hip = Side(rig.RightHip);
        Vector2 foreHinge = Side(rig.ForeHingeFromShoulder);
        Vector2 hindHinge = Side(rig.HindHingeFromHip);
        Vector2 foreHoof = shoulder + foreHinge - new Vector2(0f, rig.ForeSoleFromHinge);
        Vector2 hindHoof = hip + hindHinge - new Vector2(0f, rig.HindSoleFromHinge);
        Vector2 foreLeg = Leg(foreHinge, rig.ForeSoleFromHinge, foreFlex, fore: true);
        Vector2 hindLeg = Leg(hindHinge, rig.HindSoleFromHinge, hindFlex, fore: false);

        // The SHOULDERS are the pitch's pivot, so only the lunge and the sink move them: the sink puts them
        // exactly the braced foreleg's reach above its hoof.
        Vector2 ahead = foreHoof - shoulder - new Vector2(surge, 0f);
        float foreReach = foreLeg.Length();
        float bob = ahead.Y + MathF.Sqrt(MathF.Max(0f, (foreReach * foreReach) - (ahead.X * ahead.X)));

        // The HIPS ride the spine out of the moved shoulders, and the pitch turns the spine until the braced
        // hind leg's reach meets its hoof: the law of cosines over spine, hind leg and shoulder to hoof, taking
        // the smaller of the two turns that close it.
        Vector2 moved = shoulder + new Vector2(surge, bob);
        Vector2 toHoof = hindHoof - moved;
        Vector2 spine = hip - shoulder;
        float hindReach = hindLeg.Length();
        float cosine = (toHoof.LengthSquared() + spine.LengthSquared() - (hindReach * hindReach))
                       / MathF.Max(2f * toHoof.Length() * spine.Length(), 1e-6f);
        float spread = MathF.Acos(Math.Clamp(cosine, -1f, 1f));
        float turn = Heading(toHoof) - Heading(spine);
        float pitch = Smaller(Wrap(turn - spread), Wrap(turn + spread));

        // Each HOOF back into its own leg's frame, and the upper leg swung until the braced leg points at it.
        Vector2 foreTarget = Tip(foreHoof - moved, -pitch);
        Vector2 hindTarget = Tip(hindHoof - moved, -pitch) + shoulder - hip;
        float foreSwing = Down(foreTarget) - Down(foreLeg);
        float hindSwing = Down(hindTarget) - Down(hindLeg);

        return new HeadbuttPose(amount * NodRadians, surge, pitch, bob,
            foreSwing, foreFlex, hindSwing, hindFlex, landing);
    }

    static Vector2 Side(Vector3 point) => new(point.Z, point.Y);

    // The sole in its upper leg's frame at a fold: QuadrupedRig.ForeHinge folds the cannon back and
    // HindHinge folds it forward, about the hinge.
    static Vector2 Leg(Vector2 hinge, float cannon, float flex, bool fore) => new(
        hinge.X + ((fore ? -cannon : cannon) * MathF.Sin(flex)),
        hinge.Y - (cannon * MathF.Cos(flex)));

    // A tip about the side plane's origin in Matrix4x4.CreateRotationX's own sense: positive carries a point
    // ahead of the origin down. It adds to Heading and takes away from Down.
    static Vector2 Tip(Vector2 point, float pitch)
    {
        float cos = MathF.Cos(pitch), sin = MathF.Sin(pitch);
        return new Vector2((point.Y * sin) + (point.X * cos), (point.Y * cos) - (point.X * sin));
    }

    // Up is zero and ahead is a quarter turn. Only ever differenced, so its origin does not matter.
    static float Heading(Vector2 v) => MathF.Atan2(v.X, v.Y);

    // Straight down is zero and ahead is positive: a limb's swing adds to it, which is BodyRig.Limb's sense.
    static float Down(Vector2 v) => MathF.Atan2(v.X, -v.Y);

    static float Wrap(float angle) => angle - (MathF.Tau * MathF.Floor((angle + MathF.PI) / MathF.Tau));

    static float Smaller(float a, float b) => MathF.Abs(a) <= MathF.Abs(b) ? a : b;
}
