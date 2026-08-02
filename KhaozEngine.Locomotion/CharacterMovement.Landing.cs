namespace KhaozEngine.Locomotion;

// The LANDING half of the movement step: the one-tick impact latch StepCore stamps onto MoveState.LandingImpactSpeed.
// One concern (what hitting the ground reports), split out of the main CharacterMovement.cs so that file - already the
// engine's largest - does not grow, exactly as CharacterMovement.Fluid.cs and CharacterMovement.Momentum.cs did. Same
// partial type, same shared private core: StepCore calls LandingImpact once, immediately before the jump step.
public static partial class CharacterMovement
{
    /// <summary>The landing-impact latch: the DOWNWARD speed (m/s, non-negative) a ground contact is about to erase on
    /// the ONE tick a character transitions airborne to grounded, and exactly 0 on every other tick. This is
    /// <see cref="MoveState.LandingImpactSpeed"/>, the authoritative fall-damage input - see that field for the full
    /// contract (the <see cref="MoveTuning.MaxFallSpeed"/> cap, the swim cases, the buffered-jump case).
    /// <para>It is a pure function of three facts StepCore already has, which is what makes it correct on EVERY landing
    /// path rather than on the ones someone remembered to instrument. There are four sites in StepCore that set
    /// <c>grounded</c> and zero a negative <c>vVel</c>: the support-floor snap (a plain landing on terrain or a prop),
    /// the stair-climb ground-stick, the step-down grounded-hold, and the paced step-up climb. The last three are all
    /// gated on having been grounded LAST tick (either explicitly, or because they only fire mid-climb), so only the
    /// first is normally a transition - but this latch does not depend on that reasoning. It reads the transition itself,
    /// so a path that starts landing tomorrow is covered the day it does, and a path that merely STAYS grounded reports
    /// nothing however it got there.</para>
    /// <para>It is deliberately evaluated BEFORE the jump step, because that step un-grounds a character whose buffered
    /// jump fires on the very tick it lands. The impact happened, so it is reported, and the tick ends airborne with a
    /// nonzero impact: suppressing it would let a bunny-hop cancel fall damage.</para></summary>
    /// <param name="wasGrounded">The carried <see cref="MoveState.Grounded"/> from the START of the tick.</param>
    /// <param name="groundedNow">Whether the step has resolved this tick as grounded (read before the jump step).</param>
    /// <param name="fallSpeed">This tick's integrated vertical velocity (m/s, positive up) as it stood BEFORE any
    /// landing zeroed it: gravity applied and the <see cref="MoveTuning.MaxFallSpeed"/> terminal clamp already
    /// enforced.</param>
    /// <returns><c>-fallSpeed</c> on a landing tick with a downward velocity, 0 otherwise. Never negative, never
    /// non-finite (a pathological tuning that produced an infinite fall reports 0 rather than propagating the infinity
    /// into a consumer's damage curve, matching the step's own defence-in-depth on its other outputs).</returns>
    private static float LandingImpact(bool wasGrounded, bool groundedNow, float fallSpeed) =>
        !wasGrounded && groundedNow && fallSpeed < 0f && float.IsFinite(fallSpeed) ? -fallSpeed : 0f;
}
