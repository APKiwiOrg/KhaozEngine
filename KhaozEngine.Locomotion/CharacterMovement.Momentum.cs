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

    // The floor under the slope gate's ascent test, in metres: a rise smaller than this is noise, not a climb, and is
    // never refused however slow the tick. It exists only to keep a near-level traverse ACROSS a steep face (where the
    // analytic height ripples by a rounding step) from reading as an ascent. It is NOT the ascent allowance - that is a
    // gradient, see AdvanceSlopeGated - and it can never be ridden into a climb, because it only outranks the gradient
    // term below a millimetre of travel per tick (3 cm/s at 30 Hz), where a fully permitted 1 mm rise is a crawl no
    // player is doing on purpose. Sized an order of magnitude above the float noise the height comparison carries at
    // ordinary overworld altitudes (~1e-4 m, and a couple of ulps even at 4 km up, where a float's own step is ~5e-4 m),
    // and 200x below what one 30 Hz walk step is allowed to rise at the default 45 deg gate.
    private const float SlopeAscentNoise = 1e-3f;

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
        // gate's rise reference (the LOWER of the feet and the ground beneath them) is blocked exactly as a commanded
        // step into the same face is. Momentum changes where the velocity comes from, never what the world is willing
        // to let it reach, and it buys no more admission onto a face than a jump does. An arc flying OUT over a canyon
        // meets the same steep normal with its destination ground below both terms and carries on, which is what stops
        // a cliff edge from freezing a flight in mid-air.
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
    /// steeper than <see cref="MoveTuning.MaxSlopeRadians"/>, AND this tick's rise onto it is steeper than the gate
    /// itself (see the ascent rule below), the whole move is refused and the position is unchanged. Shared by the
    /// ordinary command path (<c>DesiredHorizontalCore</c>) and the momentum path above, so the gate cannot come to
    /// mean two different things depending on which one drove the tick.
    /// <para>Only an ASCENT is refused. A steep normal alone says nothing about direction, so reading it on its own
    /// blocked walking OFF a cliff exactly like walking INTO one: the analytic-terrain path had no way to tell the two
    /// apart, and an overworld cliff edge behaved as a wall. A descent or a level traverse now falls through, the
    /// support floor finds nothing walkable, and gravity does the rest - the same asymmetry the Bepu-backed
    /// collide-and-slide already applies to props (<c>CharacterMovement.Collision.cs</c>).</para>
    /// <para>THE RISE IS MEASURED FROM THE LOWER OF THE FEET AND THE GROUND UNDER THE CURRENT COLUMN, and that is what
    /// stops vertical motion from buying an ascent. Reading it from the feet ALONE let a jump pay for the climb: a jump
    /// raises the feet, so near the apex a steep face's local ground stands level with them, the rise reads about zero,
    /// the sideways drift onto the face is admitted, the analytic ground clamp seats the character on the face, and the
    /// next jump repeats - about a jump height of free ascent per cycle, up a face no walk can enter. That is the
    /// playtested #440 exploit, and it is not a jump special case: any airtime discounted the face the same way, so a
    /// character merely FALLING past one while steering into it was seated partway up it. The floor of the two terms
    /// cannot be inflated by vertical motion, because a character that leaves the ground still measures against the
    /// ground it left.</para>
    /// <para>What that reference preserves. GROUNDED motion is untouched: the feet sit on the ground, so the minimum is
    /// a no-op and every walking case reads exactly as it did. A genuine DESCENT still falls through at any airtime,
    /// since a destination column below the current one is below both terms - walk-offs, jump-offs, falling past a
    /// face, and landing at a cliff toe are unchanged. And the gate only ever became MORE conservative (the reference
    /// can only move down, so the rise can only grow), which is what makes the ANTI-TUNNEL property survive for free:
    /// flying into a cliff face whose ground stands above the feet is refused exactly as before, so an XZ can never be
    /// committed under terrain and left for a later ground clamp to pop the capsule up the cliff.</para>
    /// <para>The one behaviour that changes off the exploit path is a character standing on a PROP above the terrain:
    /// its rise is now measured from the terrain under the prop rather than from the prop top, so stepping off a prop
    /// straight onto a steep face is refused where it used to be admitted. That is the same climb by a slower elevator,
    /// and the analytic gate has no prop height to read in any case - <c>groundHeight</c> is the terrain, and the prop
    /// support the swept collide-and-slide resolves is not known here. It costs one extra <c>groundHeight</c> sample,
    /// taken only inside the too-steep branch, so a walkable tick still pays exactly the delegate calls it always
    /// did.</para>
    /// <para>THE ASCENT ALLOWANCE IS A GRADIENT, NOT A HEIGHT, and that is what makes the gate scale-free. The rise is
    /// measured against the horizontal travel this tick actually intended: the move is refused when
    /// <c>rise &gt; max(SlopeAscentNoise, travel * tan(MaxSlopeRadians))</c>, which asks whether the tick climbs faster
    /// than the steepest WALKABLE ramp would have climbed over the same ground. A fixed height cannot ask that
    /// question, and the version that tried made the answer depend on speed: the same face was refused at 6 m/s and
    /// walked up at 6 cm/s, because a slow enough tick (a snared or slowed character, a short steering vector, a high
    /// tick rate) rises less than ANY fixed number. Two properties come out of the gradient form. A character standing
    /// ON a face of angle <c>a</c> rises <c>travel * tan(a)</c> per tick, and both sides of the comparison carry the
    /// same <c>travel</c>, so a face steeper than the gate is refused at every speed and every tick rate. And a
    /// character still on the flat below such a face can only enter the fraction of it that keeps the rise inside the
    /// ramp's, so the most it ever gains is ONE tick's ramp rise, once, after which it is standing on the face and
    /// fenced. Nothing accumulates, which is the whole failure mode being closed.</para>
    /// <para><c>tan(MaxSlopeRadians)</c> is the gate angle read as a gradient, not a new knob: there is nothing here to
    /// tune that the gate does not already say. It cannot be frozen into a literal either, because it has to follow the
    /// consumer's gate - pin it at 1 (the default 45 deg) and a game running a 30 deg gate would let a 40 deg face
    /// through, since that face rises 0.84 m per metre while the literal demands 1. The tangent is evaluated only
    /// inside the too-steep branch, where <see cref="MathF.Acos"/> has already established a gate below <c>pi/2</c>, so
    /// it is finite and positive there. A nonsense negative gate yields a negative one and the noise floor takes
    /// over, which refuses the ascent, the safe direction. It is one more transcendental on a line that already runs
    /// <see cref="MathF.Acos"/> on the same tuning value, so the two heads' agreement rests on exactly what it rested
    /// on before.</para>
    /// <para><c>active</c> false is a tick with nothing to advance, and it skips the gate entirely rather than
    /// evaluating the delegate at the unchanged position. It is passed in rather than derived from the velocity so an
    /// idle tick and a rooted-at-zero-speed tick keep their existing, and different, delegate-call behaviour: the
    /// rooted one still probes the normal at the position it did not move to, exactly as it did before. Both heights
    /// are sampled only when the normal already read steep, so a walkable tick costs exactly the delegate calls it
    /// always did, and both heads short-circuit identically.</para>
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
            if (MathF.Acos(ny) > tuning.MaxSlopeRadians)
            {
                // Pure scalar arithmetic in a fixed order, evaluated in the same sequence on both heads (destination
                // sample first, current column second). The rise is read first because a descent or a level traverse
                // (the common steep-destination case, a cliff edge) settles it without the travel or the tangent
                // being touched at all.
                float rise = groundHeight(nx, nz) - MathF.Min(feetY, groundHeight(x, z));
                if (rise > SlopeAscentNoise)
                {
                    float travel = MathF.Sqrt(velocity.X * velocity.X + velocity.Y * velocity.Y) * dt;
                    if (rise > travel * MathF.Tan(tuning.MaxSlopeRadians)) return (x, z);
                }
            }
        }
        return (nx, nz);
    }

    /// <summary>True when both components of <paramref name="v"/> are finite (neither NaN nor infinite). The Vector2
    /// companion to the Vector3 guard, for the carried horizontal velocity: it is the one new field that FEEDS the
    /// next tick, so a NaN reaching it would not merely corrupt one frame but permanently strand the character.</summary>
    private static bool IsFinite(in Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}
