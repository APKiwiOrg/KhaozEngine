using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The MOMENTUM half of the movement step: the airborne inertia resolve behind MoveTuning.AirMomentum, the
// collision clip that decides what survives into MoveState.HorizontalVelocity, and the slope-gated advance both
// the momentum path and the ordinary command path share. One concern (what a character in free flight carries),
// split out of the main CharacterMovement.cs so that file - already the engine's largest, and frozen by the
// file-size ratchet - does not grow, exactly as CharacterMovement.Fluid.cs did. Same partial type, same shared
// private core: StepCore calls AirborneMomentumMove on an airborne momentum tick, and stamps every tick's
// carried velocity through ClipToAchieved.
//
// The model is AIRBORNE-ONLY and OPT-IN. Grounded motion stays instant-to-target with no acceleration and no
// friction, and with the knob off nothing here is consumed at all: the carried velocity is still written every
// tick (so the field is always meaningful and always replicates), but the position advance goes through the
// ordinary command path, so a game that never opts in is bit-identical to the release before momentum existed.
public static partial class CharacterMovement
{
    // Dead-zone for a horizontal velocity magnitude, in m/s. Below this a direction cannot be recovered from the
    // vector by normalising it (the result is dominated by rounding, or is a divide by zero), so the resolve falls
    // through to the next direction source instead. Matches the 1e-6 scale the command dead-zones already use.
    private const float MomentumEpsilon = 1e-6f;

    // How much of an intended move may go unaccounted for before the clip treats the tick as genuinely DENIED, as a
    // fraction of the intended speed. 0.1% sits far above float rounding at overworld coordinates and far below any
    // denial worth clipping for: a wall, a step hold, or a play-area clamp removes whole percentages of the move,
    // never a thousandth of it.
    private const float ClipUndeniedTolerance = 1e-3f;

    // How far above the FEET a too-steep destination's ground may stand and still be walked onto, in metres. This is
    // the whole difference between an ascent and a descent in AdvanceSlopeGated, so it is deliberately the surface-
    // contact skin (SkinWidth, CharacterMovement.Collision.cs) and not a new tuning knob: the same epsilon that
    // decides "the capsule is resting on this surface" decides "the ground is at my feet rather than above them",
    // and one constant cannot be tuned into a free climb the way a knob could. It has to be big enough to absorb
    // float noise in the height comparison (~1e-4 m at overworld altitudes, so 100x margin) and small enough that a
    // real ascent never fits under it: even at the 45 deg gate itself, one 30 Hz walk step rises 0.2 m, twenty times
    // this, so a steep face is refused on the first tick that meets it whatever the tick rate.
    private const float SlopeAscentTolerance = SkinWidth;

    /// <summary>One airborne tick under <see cref="MoveTuning.AirMomentum"/>: resolve the intended velocity from the
    /// carried one plus the command, then advance the XZ through the same slope gate the ordinary command path uses.
    /// Returns the desired position and the velocity that produced it, which is also what
    /// <see cref="MoveState.CommandedVelocity"/> exports for this tick.
    /// <para>The target speed here deliberately omits the <see cref="MoveTuning.AirControl"/> term the non-momentum
    /// airborne path multiplies in. Under momentum, air control is the STEERING authority (how much of the commanded
    /// velocity is blended into the carried one), not a speed scale, so applying it twice would silently cap a
    /// half-control character's reachable airborne speed at half of what it can run at on the ground.</para></summary>
    private static (float x, float z, Vector2 velocity) AirborneMomentumMove(in MoveState s, Vector2 moveDir,
        float speedFraction, bool run, float dt, in MoveTuning t, Func<float, float, Vector3>? groundNormal,
        Func<float, float, float> groundHeight, float wade)
    {
        float targetSpeed = (run ? t.RunSpeed : t.WalkSpeed) * wade * s.SpeedScale * speedFraction;
        Vector2 v = ResolveAirborneVelocity(s.HorizontalVelocity, moveDir, targetSpeed, dt, t);
        // The slope gate still applies: a momentum flight into ground steeper than MaxSlopeRadians standing above the
        // feet is blocked exactly as a commanded step into the same face is. Momentum changes where the velocity
        // comes from, never what the world is willing to let it reach. An arc flying OUT over a canyon meets the same
        // steep normal with its ground far below the feet and carries on, which is what stops a cliff edge from
        // freezing a flight in mid-air.
        (float x, float z) = AdvanceSlopeGated(s.Position.X, s.Position.Z, v, v != Vector2.Zero, dt, t, groundNormal,
            groundHeight, s.Position.Y - t.CapsuleHalfHeight);
        return (x, z, v);
    }

    /// <summary>The per-tick airborne velocity resolve: blend the carried velocity toward the commanded one by the
    /// air-control fraction, conserve the speed that was already there, and let the command ACCELERATE but never
    /// brake. Pure arithmetic over its arguments, so both heads produce the identical result.</summary>
    /// <param name="carried">The velocity carried in from the previous tick (<see cref="MoveState.HorizontalVelocity"/>).</param>
    /// <param name="moveDir">The resolved unit command direction (XZ), or zero when there is no input.</param>
    /// <param name="targetSpeed">The full commanded speed this tick, air control EXCLUDED.</param>
    /// <param name="dt">Timestep in seconds, for the brake decay.</param>
    /// <param name="t">Carries <see cref="MoveTuning.AirControl"/> and <see cref="MoveTuning.AirBrakeAccel"/>.</param>
    private static Vector2 ResolveAirborneVelocity(Vector2 carried, Vector2 moveDir, float targetSpeed, float dt,
        in MoveTuning t)
    {
        // Air control is the steering authority over DIRECTION, clamped into [0,1] so a mis-set tuning cannot
        // overshoot the blend past the command (> 1) or steer away from it (< 0). At 1 the character has full
        // authority and can turn its arc through 180 degrees in a tick, still at the carried speed. At 0 nothing of
        // the command reaches the velocity at all and the arc is purely ballistic, which is the reading this knob
        // gains under momentum: the old model froze the horizontal instead of letting the arc fly out.
        float ac = Math.Clamp(t.AirControl, 0f, 1f);
        Vector2 target = moveDir * targetSpeed;         // (0,0) with no input, which is what makes a release hold
        Vector2 steered = carried + (target - carried) * ac;

        // The conserved speed: what the arc is already carrying, optionally bled toward the command. The brake is
        // ONE-DIRECTIONAL and only ever runs when the command is genuinely SLOWER than the arc, so it can bleed a
        // fast arc down and can never raise a slow one. Accelerating is the steer blend's job alone, and gating on
        // the target being slower is what keeps the two from overlapping: without the gate, a game that set a brake
        // alongside a low AirControl would find a snare SPEEDING UP a ballistic arc it is not even steering. The
        // bleed floors at targetSpeed rather than at 0, so an arc settles onto the speed the character can actually
        // move at and never undershoots into a crawl or a reverse.
        float carriedLen = carried.Length();
        float conserved = carriedLen;
        if (t.AirBrakeAccel > 0f && targetSpeed < conserved)
            conserved = MathF.Max(targetSpeed, conserved - t.AirBrakeAccel * dt);

        // Speed is the LARGER of the two, which is the whole asymmetry of the model: pressing forward on a slow arc
        // accelerates it, and pressing backward (or releasing) on a fast one cannot slow it. Braking an arc is
        // AirBrakeAccel's job alone, so a game gets to choose whether committed flight is honoured or bled, rather
        // than having the answer fall out of whichever direction the player happened to hold.
        float steeredLen = steered.Length();
        float speed = MathF.Max(steeredLen, conserved);

        // Direction, in order of preference: the steered blend when it has one, then the carried arc (the release
        // case, where the blend collapsed to zero because air control pulled it all the way onto a zero target),
        // then the raw command. All three degenerate together only when there is nothing carried AND no input, and
        // there the speed is 0 too, so the resulting velocity is zero regardless of which direction wins.
        Vector2 dir = steeredLen > MomentumEpsilon ? steered / steeredLen
            : carriedLen > MomentumEpsilon ? carried / carriedLen
            : moveDir;
        return dir * speed;
    }

    /// <summary>What of an intended horizontal velocity actually SURVIVED the resolve, projected back onto its own
    /// direction and clamped into <c>[0, |intended|]</c>. This is the only thing ever written to
    /// <see cref="MoveState.HorizontalVelocity"/>, and the clamp is what makes carrying that field safe: collision
    /// can only ever CLIP the stored velocity, never inject into it.
    /// <para>Free flight leaves it EXACTLY untouched: a tick that reached all but
    /// <see cref="ClipUndeniedTolerance"/> of what it aimed for was denied nothing, so the intent is returned as-is
    /// and never round-trips through the measurement at all. A head-on wall clips it to ~0, so an arc stopped by
    /// geometry does not resume the moment the character slides clear. A glancing wall keeps the direction and sheds
    /// the magnitude, which is what makes a wall-graze bleed speed instead of either killing the arc or preserving it
    /// whole. A depenetration nudge, a step-climb hold, or a play-area clamp can only ever reduce it, because of the
    /// upper clamp - without that clamp a one-tick push-out of a deeply embedded capsule would read as an enormous
    /// achieved velocity and launch the character on the next airborne tick.</para></summary>
    /// <param name="intended">The velocity the step commanded this tick (<see cref="MoveState.CommandedVelocity"/>).</param>
    /// <param name="start">The capsule-centre position the tick started from.</param>
    /// <param name="resolved">The capsule-centre position the tick actually committed.</param>
    /// <param name="dt">Timestep in seconds. A non-positive dt has no velocity to measure, so nothing is carried.</param>
    private static Vector2 ClipToAchieved(Vector2 intended, in Vector3 start, in Vector3 resolved, float dt)
    {
        float len = intended.Length();
        if (len <= MomentumEpsilon || dt <= 0f) return Vector2.Zero;
        Vector2 dir = intended / len;
        var achieved = new Vector2((resolved.X - start.X) / dt, (resolved.Z - start.Z) / dt);
        float along = Vector2.Dot(achieved, dir);
        // A move that reached essentially all of what it aimed for was not denied, so carry the INTENT through
        // unchanged rather than re-deriving it from the committed position. Re-deriving is what erodes an arc: the
        // clamp below discards every positive rounding excursion and keeps every negative one, so a re-measured
        // carry can only ever ratchet DOWN. It is invisible near the origin and shaves a couple of centimetres per
        // second off a ten-second arc at overworld range, where the coordinate is large enough that one float step
        // is a measurable fraction of a tick's travel. Only a REAL denial (a wall, a step hold, a play-area clamp)
        // needs the measured value, and there the shortfall is far larger than this tolerance.
        if (along >= len * (1f - ClipUndeniedTolerance)) return intended;
        return dir * Math.Clamp(along, 0f, len);
    }

    /// <summary>Advance an XZ position by a horizontal velocity for one tick, subject to the DIRECTION-AWARE terrain
    /// slope gate: when a <paramref name="groundNormal"/> delegate is supplied, the ground at the DESTINATION is
    /// steeper than <see cref="MoveTuning.MaxSlopeRadians"/>, AND that ground stands more than
    /// <c>SlopeAscentTolerance</c> above <paramref name="feetY"/>, the whole move is refused and the position is
    /// unchanged. Shared by the ordinary command path (<c>DesiredHorizontalCore</c>) and the momentum path above, so
    /// the gate cannot come to mean two different things depending on which one drove the tick.
    /// <para>Only an ASCENT is refused. A steep normal alone says nothing about direction, so reading it on its own
    /// blocked walking OFF a cliff exactly like walking INTO one: the analytic-terrain path had no way to tell the two
    /// apart, and an overworld cliff edge behaved as a wall. A descent or a level traverse now falls through, the
    /// support floor finds nothing walkable, and gravity does the rest - the same asymmetry the Bepu-backed
    /// collide-and-slide already applies to props (<c>CharacterMovement.Collision.cs</c>).</para>
    /// <para>The comparison is against the FEET, not the ground the character is currently standing on, and that is
    /// what keeps the gate anti-tunnel while airborne: flying into a cliff face whose ground stands above the feet is
    /// still refused, so an XZ can never be committed under terrain and left for a later ground clamp to pop the
    /// capsule up the cliff. Standing on a prop at the cliff top raises the feet with it, so stepping off still
    /// reads as the descent it is.</para>
    /// <para><c>active</c> false is a tick with nothing to advance, and it skips the gate entirely rather than
    /// evaluating the delegate at the unchanged position. It is passed in rather than derived from the velocity so an
    /// idle tick and a rooted-at-zero-speed tick keep their existing, and different, delegate-call behaviour: the
    /// rooted one still probes the normal at the position it did not move to, exactly as it did before. The height is
    /// sampled only when the normal already read steep, so a walkable tick costs exactly the delegate calls it always
    /// did, and both heads short-circuit identically.</para>
    /// <para><c>feetY</c> is the world Y of the character's FEET this tick: the capsule centre minus
    /// <see cref="MoveTuning.CapsuleHalfHeight"/>, since <see cref="MoveState.Position"/> is the capsule CENTRE.</para></summary>
    private static (float x, float z) AdvanceSlopeGated(float x, float z, Vector2 velocity, bool active, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, Func<float, float, float> groundHeight,
        float feetY)
    {
        if (!active) return (x, z);
        float nx = x + velocity.X * dt;
        float nz = z + velocity.Y * dt;
        if (groundNormal is not null)
        {
            float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
            if (MathF.Acos(ny) > tuning.MaxSlopeRadians && groundHeight(nx, nz) > feetY + SlopeAscentTolerance)
                return (x, z);
        }
        return (nx, nz);
    }

    /// <summary>True when both components of <paramref name="v"/> are finite (neither NaN nor infinite). The Vector2
    /// companion to the Vector3 guard, for the carried horizontal velocity: it is the one new field that FEEDS the
    /// next tick, so a NaN reaching it would not merely corrupt one frame but permanently strand the character.</summary>
    private static bool IsFinite(in Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}
