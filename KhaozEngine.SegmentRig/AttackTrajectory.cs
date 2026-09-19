using System;

namespace KhaozEngine.SegmentRig;

/// <summary>The TIMING every fighting stroke shares: recover off the blow, stand still, strike late and fast.
/// The two styles differ in where the arm goes and agree exactly on when it goes there.</summary>
/// <remarks>
/// One phase machine rather than one per style, because the owner's ruling on the shape is about the CLOCK: a
/// fight has to read the same whatever is in the hand, and two copies of the same three boundaries are two
/// numbers that can drift apart. <see cref="AttackSwing"/> and <see cref="SlashSwing"/> hand it their own two
/// end poses and it does the rest.
/// <para>The segments, in order from the blow: the recovery out to <see cref="AttackSwing.RestPhase"/>,
/// smoothed at both ends so it neither jerks out of the reach nor stops dead at the rest, then NOTHING until
/// <see cref="AttackSwing.StrikePhase"/>, then the strike, eased in so the arm leaves the rest slowly and is
/// at its fastest the instant it arrives. The two ends of the cycle are the same pose by construction, which
/// is what lets a swing message seed the phase at zero without a snap.</para>
/// <para>A style may also hand over a MID-STRIKE key, which splits the strike alone and leaves the rest of
/// the clock untouched: the arm reaches that key over the first slice of the strike and cuts from it to the
/// blow over the remainder. That is how <see cref="SlashSwing"/> gets a stroke that goes out and across
/// rather than straight from the carry, and it is still no wind-up in the owner's sense, because it all
/// happens inside the same third of a second and nothing at all moves during the hold. Without a key the
/// arithmetic is the two-key one to the bit, which is what keeps the punch exactly as it was.</para>
/// </remarks>
public static class AttackTrajectory
{
    /// <summary>The arm at a point in a stroke between two end poses.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, with 0 and 1 both the impact. Values outside are
    /// wrapped, so a caller may hand over a raw accumulator, and anything that is not a number is the blow.
    /// </param>
    /// <param name="impact">The arm at the moment the blow lands, which is where the stroke starts and
    /// finishes.</param>
    /// <param name="rest">The arm between blows, which is where it spends most of the cadence.</param>
    public static AttackPose PoseAt(float phase, in AttackPose impact, in AttackPose rest) =>
        PoseAt(phase, impact, rest, null, 0f);

    /// <summary>The same, for a stroke that passes through a key on its way to the blow.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, as above.</param>
    /// <param name="impact">The arm at the moment the blow lands.</param>
    /// <param name="rest">The arm between blows.</param>
    /// <param name="key">The arm at the mid-strike key, reached inside the strike window and nowhere near
    /// the hold. Null for a stroke that goes straight from the rest to the blow.</param>
    /// <param name="keyFraction">How much of the STRIKE is spent reaching the key, 0 to 1, the remainder
    /// being the run from it to the blow. At or below zero, or at or above one, the key is ignored, so a
    /// style cannot accidentally ask for a segment of no length.</param>
    public static AttackPose PoseAt(
        float phase, in AttackPose impact, in AttackPose rest, AttackPose? key, float keyFraction)
    {
        float wrapped = phase - MathF.Floor(phase);
        if (!float.IsFinite(wrapped)) wrapped = 0f;
        bool landing = wrapped < AttackSwing.ImpactPhase;
        // The recovery. The arm leaves the blow and settles, and the first sliver of it is still the blow: the
        // damage number goes up on the same frame, so the follow-through is what the eye reads the hit off.
        if (wrapped < AttackSwing.RestPhase)
            return Blend(impact, rest, Smooth(wrapped / AttackSwing.RestPhase), landing);
        // The rest. Nothing moves for the middle of the cadence, which is the whole ruling: no wind-up, so
        // nothing tells the eye a blow is coming until it is already on its way. The end pose itself rather
        // than the blend at 1, so a caller comparing two frames of it gets the same bits both times.
        if (wrapped < AttackSwing.StrikePhase) return rest with { IsImpact = false };
        // The strike. Eased IN and nothing else, so it starts slowly and arrives at three times its own mean
        // rate, which is what makes a third of a second read as a blow rather than as a reach.
        float t = (wrapped - AttackSwing.StrikePhase) / (1f - AttackSwing.StrikePhase);
        if (key is not { } mid || keyFraction <= 0f || keyFraction >= 1f)
            return Blend(impact, rest, 1f - Snap(t), false);
        // The first slice of a keyed strike: out of the hold to the key, smoothed so it leaves the carry at
        // rest rather than jerking off it.
        if (t < keyFraction) return Blend(mid, rest, 1f - Smooth(t / keyFraction), false);
        // And the run home from the key, carrying the strike's own ease so the blow is still the fastest part
        // of the stroke.
        return Blend(impact, mid, 1f - Snap((t - keyFraction) / (1f - keyFraction)), false);
    }

    /// <summary>How much of the blow a stroke is carrying at a phase: 1 at the blow, 0 through the hold, and
    /// the recovery and the strike between. For a stroke whose channels are not an arm's, such as
    /// <see cref="Headbutt"/>, which lays its own pose over this amount.</summary>
    /// <param name="phase">Where in the stroke, 0 to 1, wrapped exactly as <see cref="PoseAt(float, in AttackPose, in AttackPose)"/>
    /// wraps it.</param>
    /// <param name="landing">Whether this phase is inside the follow-through, the punch's own
    /// <see cref="AttackPose.IsImpact"/>.</param>
    /// <remarks>Read off the two-key stroke itself between a unit blow and a zero rest rather than written
    /// out a second time, so the boundaries and the easings are this clock's by construction and a stroke
    /// timed by it cannot drift off the prediction gate that reads <see cref="AttackSwing.StrikePhase"/>.
    /// </remarks>
    public static float AmountAt(float phase, out bool landing)
    {
        AttackPose unit = PoseAt(phase, UnitBlow, default);
        landing = unit.IsImpact;
        return unit.Shoulder;
    }

    static readonly AttackPose UnitBlow = new(1f, 0f, 0f, 0f, true);

    // One point between the blow and the rest, on all four channels at once. ONE parameter drives them, so
    // every channel is monotone over each segment by construction and the stroke cannot wobble.
    static AttackPose Blend(in AttackPose impact, in AttackPose rest, float t, bool landing) => new(
        Lerp(impact.Shoulder, rest.Shoulder, t), Lerp(impact.Elbow, rest.Elbow, t),
        Lerp(impact.Yaw, rest.Yaw, t), Lerp(impact.Wrist, rest.Wrist, t), landing);

    static float Lerp(float from, float to, float t) => from + ((to - from) * t);

    // Smoothstep: zero slope at both ends, so the recovery neither jerks out of the reach nor stops dead.
    static float Smooth(float t)
    {
        float c = Math.Clamp(t, 0f, 1f);
        return c * c * (3f - (2f * c));
    }

    // Cubic ease IN: zero slope leaving the rest, three times the mean rate arriving.
    static float Snap(float t)
    {
        float c = Math.Clamp(t, 0f, 1f);
        return c * c * c;
    }
}
