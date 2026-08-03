using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// THE TRACTION DECISION AND WHAT A SLIDE COSTS (#475, 17.30.0). Sixth round of the steep-terrain chain, and the
// first one that is not about an exploit: it is about the gate BOUNDARY being a cliff edge in the model where the
// terrain has none.
//
// WHAT WAS MEASURED. MoveTuning.MaxSlopeRadians was a bare per-tick binary, re-decided from scratch every tick, and
// ResolveSlide committed the full fall-line projection of gravity the instant a surface crossed it. Both halves of
// that fail on real ground. A Ruinborne beach-to-plateau bank whose columns run 40.0 to 41.8 degrees against a
// 40 degree gate flipped footing 43 times in 330 ticks and stalled 2.73 m up a 7.6 m bank, gaining and losing the
// same ground. Widening the gate past that bank fixed that bank (330/330 footing, 0 flips) and moved the boundary
// onto the next feature: at a 46 degree gate, banks peaking at 46.4 and 48.8 degrees chattered the same way (24 and
// 29 flips over 300 ticks). Any threshold lands inside some terrain's slope distribution, so no tuning value
// downstream can fix this. And a surface one degree past the gate threw a character down at the same gravity
// strength as an 80 degree one, which is why marginal ground read as ice.
//
// TWO MECHANISMS, both default-on, and they compose without knowing about each other:
//
//   1. TRACTION HYSTERESIS. The decision is STATE-DEPENDENT. A character that HAS footing keeps it up to
//      MaxSlopeRadians PLUS MoveTuning.TractionHysteresisRadians, and a character that has NONE regains it only at
//      or under MaxSlopeRadians. So a bank that wobbles across the gate is a single edge instead of a strobe, and
//      landing on ground past the gate still slides. The memory is MoveState.Grounded, which the sim already
//      carries and the wire already replicates, so this adds NO state anywhere. See TractionGate.
//   2. SLIDE FRICTION. The fall-line acceleration RAMPS IN over MoveTuning.SlideFrictionRampRadians past the gate
//      instead of arriving at full strength, so a surface a degree past the gate slides gently and a sheer face
//      slides hard. See SlideFrictionScale, and SlideFallLineStep for the one rule that keeps it from becoming an
//      altitude source.
//
// ONE TRACTION TRUTH PER TICK. StepCore resolves the gate ONCE, at the top of the tick, from the footing the tick
// STARTED with, and every consumer is handed that one number: the support decision, the wall contact
// (AdvanceWallSlide), the slide contact (SlideContact), the slide's own resolve, and the wedge's standable-underfoot
// reading. Nothing re-derives it. That matters more here than it did when the gate was a constant, because a gate
// that is a function of state can be read at two different moments and give two different answers, and a support
// decision that disagrees with the wall contact driving it is exactly the chatter this file exists to remove.
//
// WHAT HYSTERESIS DOES NOT OPEN. The band is a ceiling on what FOOTING may keep, never a route to footing: a tick
// with no footing reads the base gate, so ground past the gate cannot be mounted from a slide, from a landing, or
// from a jump apex. The steepest ground a character can be standing on is therefore exactly gate plus band, whatever
// path it took to get there, and ground past that refuses support on every tick alike and slides. The #468
// invariant is untouched and is about the other case entirely: a tick WITHOUT footing may never end higher than its
// own resolved vertical motion allows, and no tick without footing reads the band at all.
//
// Both mechanisms are pure scalar arithmetic in a fixed order over carried state and the same pure delegates both
// heads hold, so a reconcile replay of a tick reaches the same gate and the same friction scale it did live.
public static partial class CharacterMovement
{
    /// <summary>The slope gate THIS TICK is decided against, in radians: the tuning's
    /// <see cref="MoveTuning.MaxSlopeRadians"/>, widened by <see cref="MoveTuning.TractionHysteresisRadians"/> when the
    /// character already HAS footing.
    /// <para>The asymmetry is the whole mechanism. Grip is granted at the gate and kept to the gate plus the band, so
    /// a walk up a bank whose columns straddle the gate by a degree or two holds one continuous decision, while a
    /// body arriving without footing (a landing, a slide, an apex graze) is judged at the bare gate and slides off
    /// ground the band would have let it keep. Two characters on the same column can therefore disagree about it,
    /// which is correct and is the point: one of them is already standing.</para>
    /// <para><paramref name="hadFooting"/> is <see cref="MoveState.Grounded"/> as the tick STARTED, never a value
    /// computed part-way through it. It is the memory, so reading it after this tick's own support decision has moved
    /// it would make the rule self-referential and hand the band to a character that just lost its footing.</para>
    /// <para>A band that is zero, negative, or NaN reads as NO hysteresis and the gate is the bare
    /// <see cref="MoveTuning.MaxSlopeRadians"/>, which is the pre-17.30.0 behaviour exactly. That is what a
    /// <c>default(MoveTuning)</c> gets, and it is the harmless direction for an un-configured struct: the alternative
    /// (treating a missing band as some plausible default) would make the accidental case the loosest setting there
    /// is. The band is NOT clamped at the top: a game with an 89 degree gate and a 3 degree band is saying that a
    /// standing character keeps its feet on anything, and the honest answer to that tuning is to let it.</para></summary>
    private static float TractionGate(bool hadFooting, in MoveTuning tuning)
    {
        float band = tuning.TractionHysteresisRadians;
        // Written as `band > 0f` rather than a Max so a NaN band takes the no-band branch: a NaN gate would make
        // `acos(ny) > gate` false for every surface, which is "footing everywhere" - the one direction a degenerate
        // tuning must not fail in.
        return hadFooting && band > 0f ? tuning.MaxSlopeRadians + band : tuning.MaxSlopeRadians;
    }

    /// <summary>True when a ground normal is steeper than the gate it is being judged against. The single reading of
    /// "too steep" for the whole step: the support decision, the wall contact, the slide contact and the wedge all ask
    /// this one question, in the same way the retired 17.26 gate asked it (<see cref="MathF.Acos"/> of the clamped Y
    /// against a radian threshold), so nothing shifted at the threshold arithmetic itself and a walkable slope is
    /// bit-identical to every release before this one.
    /// <para><paramref name="gate"/> comes from <see cref="TractionGate"/> and is resolved once per tick. Callers do
    /// not pass a tuning here on purpose: taking the threshold as a value is what makes it impossible for one call
    /// site to quietly read the bare <see cref="MoveTuning.MaxSlopeRadians"/> while another reads the widened
    /// one.</para></summary>
    private static bool IsSteepGround(in Vector3 normal, float gate)
        => MathF.Acos(Math.Clamp(normal.Y, 0f, 1f)) > gate;

    /// <summary>True when the ANALYTIC terrain under a resolved capsule position refuses traction at this tick's gate.
    /// The one "may this seat grant footing" question, asked in one place by every footing grant that reads terrain.
    /// <para>It is a function rather than a local because the support decision and the step-down hold ask exactly this
    /// and used to ask it as two different expressions. The support decision guarded its test with <c>onGround</c>,
    /// which only reaches drops within <see cref="MoveTuning.GroundedEpsilon"/>, so throughout the step-down hold's own
    /// band - the drops between that and <see cref="MoveTuning.StepHeight"/> - the guard was vacuously false and NO
    /// traction test ran at all on the surface the hold was seating onto. That was #470, and one function is what
    /// keeps a third caller from re-opening it.</para>
    /// <para>PROP SUPPORT ALWAYS WINS. Only the analytic terrain can be traction-less here: <paramref name="groundY"/>
    /// at or below <paramref name="terrainGroundY"/> says no prop raised the floor, and <paramref name="propGrounded"/>
    /// says no prop is pushing the capsule up. A plank bridging a ravine, a ledge bolted to a cliff and a stair built
    /// against a mountain all still carry a character, exactly as they did.</para>
    /// <para>The normal is sampled at the RESOLVED position rather than the start one, because it is THIS tick's
    /// support being decided. Each caller keeps its own cheap precondition in front of the call, so an airborne tick
    /// and a prop-supported tick pay nothing for the rule at all.</para></summary>
    /// <param name="pos">The position this tick resolved to, whose column the normal is sampled at.</param>
    /// <param name="propGrounded">True when a prop is holding the capsule up.</param>
    /// <param name="groundY">The resolved support height (analytic terrain plus walkable props).</param>
    /// <param name="terrainGroundY">The analytic terrain's own support height at the same column.</param>
    /// <param name="groundNormal">The consumer's classification delegate, or null for no classification at all.</param>
    /// <param name="gate">This tick's traction gate, from <see cref="TractionGate"/>.</param>
    private static bool RefusesTraction(in Vector3 pos, bool propGrounded, float groundY, float terrainGroundY,
        Func<float, float, Vector3>? groundNormal, float gate)
        => !propGrounded && groundY <= terrainGroundY && groundNormal is not null &&
           IsSteepGround(groundNormal(pos.X, pos.Z), gate);

    /// <summary>How much of gravity's fall-line pull a slide on this surface actually gets, in <c>[0, 1]</c>: the
    /// friction ramp, <c>clamp((slope - gate) / SlideFrictionRampRadians, 0, 1)</c>.
    ///
    /// <para>WHY A RAMP AND NOT A COEFFICIENT. The thing being fixed is a DISCONTINUITY: the classifier says walkable
    /// at the gate and full-gravity slide a hair past it, so a surface the player reads as "a bit steep" behaves
    /// exactly like a cliff. A ramp makes the transition continuous in the one variable the classifier is
    /// discontinuous in, which is the smallest change that removes the cliff edge, and it costs one
    /// <see cref="MathF.Acos"/> on a tick that is already sliding.</para>
    ///
    /// <para>IT READS THE HEIGHT PLANE, NOT THE CLASSIFICATION NORMAL, and that is the same split #468 settled for
    /// every other number that decides where a capsule ends up. <paramref name="ny"/> is the Y of the
    /// central-difference plane <c>ResolveSlide</c> built its fall line and contour from, so the friction describes
    /// the surface the body is actually on and the surface the ground clamp is about to seat it to. Reading the
    /// smoothed classification normal instead would let a delegate reporting 60 degrees accelerate a body at full
    /// strength down a plane that is really 46, which is the ice-cliff feel this ramp exists to remove.</para>
    ///
    /// <para>AT OR UNDER THE GATE THE SCALE IS ZERO, which a sliding tick can only reach when the consumer's
    /// classification and its height field disagree about a patch (the classification called it steep, the heights
    /// say it is standable). The fall line is then FROZEN: the body keeps whatever carry it arrived with, and gravity
    /// neither adds to it nor takes from it, which is the same honest rest the <c>h ~ 0</c> case already produced
    /// there. "No acceleration is no altitude" was the claim here, and it was too strong. A frozen fall line is the
    /// one state in which a body can hold a RISING contour, so it can creep upward at up to the slide's rise slack
    /// per tick (1 mm), and only across ground the height field itself reads walkable, since the scale leaves zero
    /// the moment the plane under the capsule passes the gate. See <c>SlideRiseSlack</c> in
    /// <c>CharacterMovement.Slide.cs</c> for that bound and why it is the ramp's honest cost.</para>
    ///
    /// <para>A ramp that is zero, negative, or NaN turns friction OFF (scale 1, the pre-17.30.0 full-strength slide),
    /// which is what a <c>default(MoveTuning)</c> reads and is the same harmless direction
    /// <see cref="TractionGate"/> takes.</para></summary>
    /// <param name="ny">The Y of the unit surface plane, already clamped into <c>[0, 1]</c>.</param>
    /// <param name="gate">This tick's traction gate, from <see cref="TractionGate"/>.</param>
    /// <param name="tuning">Carries <see cref="MoveTuning.SlideFrictionRampRadians"/>.</param>
    private static float SlideFrictionScale(float ny, float gate, in MoveTuning tuning)
    {
        float ramp = tuning.SlideFrictionRampRadians;
        if (!(ramp > 0f)) return 1f;   // negated > so a NaN ramp reads as "no friction" rather than poisoning the scale
        float past = MathF.Acos(ny) - gate;
        if (!(past > 0f)) return 0f;
        return past >= ramp ? 1f : past / ramp;
    }

    /// <summary>One tick of gravity on a slide's SIGNED fall-line speed, with the friction ramp applied to the
    /// downhill half alone. Positive is down the fall line.
    ///
    /// <para>THE SIGN RULE IS THE WHOLE SAFETY ARGUMENT, and it is not a detail. Scaling gravity down on a RISING
    /// slide would be an energy source: a graze that arrives at <c>v</c> rides to <c>v^2 / (2 * Gravity * scale)</c>
    /// instead of <c>v^2 / (2 * Gravity)</c>, so at a scale of an eighth a running jump onto a marginal face reaches
    /// eight times as high, and as the scale approaches zero at the gate itself the reach is unbounded. A player who
    /// then steps off the top onto walkable ground has climbed for free, which is the #440 ratchet again by another
    /// route. So gravity DECELERATES a rising slide at full strength, always, and only the accelerating half is
    /// scaled. That is also what friction physically is: a dissipative force that opposes motion, never one that
    /// helps a body up.</para>
    ///
    /// <para>The consequence worth stating plainly is that the reach bound every steep-terrain fixture measures
    /// against - <c>v^2 / (2 * Gravity)</c>, the launch's whole kinetic energy - is UNCHANGED by friction, because
    /// the up phase it bounds is unchanged. What changes is the way back down, which is slower, and the energy the
    /// face hands back, which is now less than it took.</para>
    ///
    /// <para>THE CROSSING TICK IS SPLIT EXACTLY rather than taking one strength for the whole of it: full-strength
    /// gravity for the <c>-fall / accel</c> seconds the body is still rising, then the scaled strength for what is
    /// left of the tick. A whole-tick approximation either over-decelerates (safe, but a kink whose size depends on
    /// the tick rate) or under-decelerates (not safe at all). The split has neither problem and is three operations.
    /// </para>
    ///
    /// <para>A scale of 1 or an accel of zero returns the plain integrate, bit for bit, so a game that turns friction
    /// off has the 17.28.0 slide back exactly and does not pay for a rounding difference on the crossing
    /// tick.</para></summary>
    /// <param name="fall">The fall-line speed carried into this tick, signed (negative is up the fall line).</param>
    /// <param name="accel">Gravity's full-strength fall-line acceleration, <c>Gravity * h</c>.</param>
    /// <param name="scale">The friction scale from <see cref="SlideFrictionScale"/>.</param>
    /// <param name="dt">Timestep in seconds.</param>
    private static float SlideFallLineStep(float fall, float accel, float scale, float dt)
    {
        // accel <= 0 covers a degenerate or inverted gravity, where there is no downhill half to scale and the plain
        // integrate is what every release before friction did.
        if (accel <= 0f || scale >= 1f) return fall + accel * dt;
        if (fall >= 0f) return fall + accel * scale * dt;
        float rising = -fall / accel;   // seconds of rise left at FULL-strength deceleration
        return rising >= dt ? fall + accel * dt : accel * scale * (dt - rising);
    }
}
