using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The SURFACE-CONTACT half of the movement step: what analytic terrain steeper than MoveTuning.MaxSlopeRadians does
// to a character that meets it. One concern (a steep surface is something you slide on, never a refusal), split out
// of the main CharacterMovement.cs so that file - already the engine's largest - does not grow, exactly as
// CharacterMovement.Fluid.cs, CharacterMovement.Momentum.cs, CharacterMovement.Landing.cs and
// CharacterMovement.Horizontal.cs did. Same partial type, same shared private core: every horizontal advance goes
// through AdvanceWallSlide, and StepCore hands a contact tick to ResolveSlide.
//
// This REPLACES the direction-aware ascent gate of 17.26.0 and its 17.26.1 min-reference fence (#369, #440), which
// two playtests voted down: a gate refuses movement, and refusal is not how terrain behaves. Refusal produced both
// reported bugs - one that a fence was too loose to stop (a repeated jump ratcheting up a sheer face, because the
// raised feet discounted the rise) and one that a tighter fence caused (sideways movement into a face while jumping
// reading as an invisible wall). The model here has neither failure mode available to it, because it never says no:
//
//   1. WALL SLIDE. A horizontal move whose destination ground stands more than MoveTuning.StepHeight above the feet
//      is a wall contact. The into-face component of the move dies and the along-face component survives, so
//      strafing along a cliff mid-jump keeps its lateral travel. Grounded and airborne, command path and momentum
//      path, one function.
//   2. NO TRACTION. Ground steeper than MoveTuning.MaxSlopeRadians grants no support, so gravity decomposes
//      against the surface and the character accelerates down the fall line until it reaches walkable ground, open
//      air, or water. Climbing self-defeats because there is no footing to climb from, which is what retires the
//      ascent gate rather than patching it a third time. The ONE exception is a SWALLOWED DESCENT (SlideWedged):
//      a tick that carried a real fall and committed measurably less of it than that fall demanded is being held
//      up by the world, so it is supported. Its motivating case is the concave crease, which without it is a
//      soft-lock (the character can neither slide out of it nor jump), and it is named for that case rather than
//      defined by it - see SlideWedged for what actually arms it and for the harmless open-face transient.
//
// Everything here is pure scalar arithmetic in a fixed order over the same pure delegates both heads hold, so a
// slide replays bit-identically through ClientPrediction.Reconcile. It adds NO carried state: the fall-line and
// contour speeds both live in MoveState.HorizontalVelocity and MoveState.VerticalVelocity, both of which already
// ride the wire, and the wedge rule reads nothing but the current tick's own values.
public static partial class CharacterMovement
{
    // How close the feet must be to steep ground under the CURRENT column for the character to be sliding ON it
    // rather than falling PAST it, in metres. It is the slide's answer to MoveTuning.GroundedEpsilon and exists for
    // the same reason: without a skin, a face that curves away by a hair under a tick's travel drops the character
    // into a one-tick free fall and re-catches it, which reads as chatter.
    //
    // Sized between the two things it has to separate. A slide holds the capsule exactly on the surface by
    // construction (the fall-line integration commits precisely the drop the horizontal travel needs, and the input
    // steer has no fall-line authority), so on the low side it only has to cover float noise and a gently convex
    // face. On the high side it must stay far below one tick of a jump - a jump clears 0.33 m in its first tick at
    // the shipped 9.8 m/s and 30 Hz - so a character that jumps beside a face escapes contact immediately instead of
    // having its launch resolved against the surface.
    private const float SlideContactSkin = 0.05f;

    // Below this the XZ part of a normal carries no direction to read (the surface is level, or the delegate handed
    // back something degenerate), so the face direction falls through to the movement direction. Matches the 1e-6
    // scale the command dead-zones and the momentum epsilon already use, squared for a length-squared test.
    private const float FaceNormalEpsilonSq = 1e-12f;

    // The smallest slope gate the SLIDE will read, in radians (~0.06 degrees). Not a clamp on the tuning: a game
    // keeps whatever MaxSlopeRadians it set, and the support decision, the wall contact and the slide contact all
    // still ask IsSteepGround about the raw value. This floors ONE thing, the terminal divide's view of the gate,
    // because that divide reads MaxFallSpeed / h and h is bounded below by sin(gate). A gate of zero (or negative,
    // which calls even level ground steep) makes that bound zero, and the smallest non-zero h a float normal can
    // express is about 3.5e-4 - so an unfloored divide hands back a terminal of ~144000 m/s on a surface a
    // fraction of a degree off level. Floored, the worst case is MaxFallSpeed / sin(this), and the wire ceiling
    // below is the second, independent bound on what can actually be committed.
    private const float MinSlideGateRadians = 1e-3f;

    // Per-axis ceiling on the horizontal velocity a slide may carry, in m/s, MIRRORING the wire's own clamp
    // (KhaozEngine.NetWorld MovementState.MaxHorizontalSpeed, which Locomotion sits below and cannot reference).
    // A slide's horizontal terminal is MaxFallSpeed / tan(surface angle), so it is largest on the SHALLOWEST face
    // the gate still calls steep: 50 m/s at the shipped 45 degree gate, but 137 m/s at a 20 degree one and
    // unbounded as the gate approaches level. Past this the wire would clamp what the sim committed, so the two
    // heads and the replicated copy would disagree about the same tick. Clamping here instead means they cannot.
    // SHALLOW-GATE CONSEQUENCE, and the reason this is a ceiling rather than a re-scale: when the clamp binds
    // (any gate below about 21 degrees at the default MaxFallSpeed) the committed horizontal no longer matches
    // the committed drop, so the velocity leaves the surface plane and the ground clamp starts correcting the
    // slide every tick instead of never. That is the honest degradation for a tuning whose slides are faster
    // than its own replication can describe, and it is strictly better than replicating a different number than
    // the one the sim used. A test pins this value against the NetWorld constant it mirrors.
    //
    // IT IS PER-AXIS AND HORIZONTAL-ONLY, because the wire's clamp is, and mirroring the wire is the whole job. So
    // it is a square box rather than a disc (a diagonal carry may reach 127 on each axis, sqrt(2) times the speed
    // an axis-aligned one may), and the vertical is untouched by it (that axis is bounded by MaxFallSpeed through
    // the terminal above). Both asymmetries are theoretical at any shipped tuning: the fastest fall-line speed a
    // slide can be handed on its UP phase is the launch that arrived, sqrt(JumpSpeed^2 + RunSpeed^2) = 15.5 m/s at
    // the defaults, and the down phase saturates at MaxFallSpeed / tan(gate) = 50 m/s at the 45 degree gate. Both
    // are far under 127 on either axis, so the clamp never binds and the shape it binds with cannot matter.
    private const float SlideCarrySpeedCeiling = 127f;

    /// <summary>True when a ground normal is steeper than the tuning's walkable gate. The single reading of
    /// "too steep" for the whole step: the support decision, the wall contact, and the slide contact all ask this one
    /// question of the same value, in the same way the retired gate asked it - <see cref="MathF.Acos"/> of the
    /// clamped Y against <see cref="MoveTuning.MaxSlopeRadians"/> - so nothing shifted at the threshold itself and a
    /// walkable slope is bit-identical to every release before this one.</summary>
    private static bool IsSteepGround(in Vector3 normal, in MoveTuning tuning)
        => MathF.Acos(Math.Clamp(normal.Y, 0f, 1f)) > tuning.MaxSlopeRadians;

    /// <summary>The face's OUTWARD horizontal direction: the normal's XZ projection, normalized. It points away from
    /// the face and down its fall line, so a positive dot with a velocity means travelling AWAY from the face and a
    /// negative one means INTO it.
    /// <para>When the projection is degenerate (a vertical-only normal, which a level surface has and which a
    /// mismatched normal/height delegate pair can also produce) there is no face direction to read, so the movement
    /// direction stands in as the face's own: the face is met head-on and the whole move dies. That is the
    /// conservative direction, and the only one that keeps a mismatched pair from admitting a move under
    /// terrain.</para>
    /// <para>THE RESULT IS NOT ALWAYS A UNIT VECTOR. When BOTH the normal's XZ and the velocity are degenerate there
    /// is no direction to be had from either source, and the return is the ZERO vector rather than an invented axis.
    /// Callers must read that as "this surface has no fall line", which is what it means: <c>AdvanceWallSlide</c>
    /// removes nothing from the move (the into-face dot is 0), and <c>ResolveSlide</c>'s tangent collapses to
    /// straight down, so the slide degenerates into the ordinary fall it should be. Only a mismatched normal/height
    /// delegate pair can reach it at a sane gate, because a vertical-only normal is not steep ground.</para></summary>
    private static (float x, float z) FaceDirection(in Vector3 normal, Vector2 velocity)
    {
        float lenSq = normal.X * normal.X + normal.Z * normal.Z;
        if (lenSq > FaceNormalEpsilonSq)
        {
            float inv = 1f / MathF.Sqrt(lenSq);
            return (normal.X * inv, normal.Z * inv);
        }
        float vSq = velocity.X * velocity.X + velocity.Y * velocity.Y;
        if (vSq <= FaceNormalEpsilonSq) return (0f, 0f);
        float vInv = 1f / MathF.Sqrt(vSq);
        return (-velocity.X * vInv, -velocity.Y * vInv);
    }

    /// <summary>Advance an XZ position by a horizontal velocity for one tick, WALL-SLIDING off analytic terrain that
    /// the step cannot reach: when the destination's ground normal is steeper than
    /// <see cref="MoveTuning.MaxSlopeRadians"/> AND its ground stands more than <see cref="MoveTuning.StepHeight"/>
    /// above the feet, the move keeps only its along-face component. Shared by the ordinary command path
    /// (<c>DesiredHorizontalCore</c>), the airborne momentum path, and the slide, so the rule cannot come to mean
    /// three different things depending on which one drove the tick.
    /// <para>BOTH CONDITIONS ARE LOAD-BEARING. The steepness test is what leaves walkable ground untouched: a fast
    /// run up a legal ramp can rise more than a StepHeight in one tick, and treating that as a wall would turn every
    /// steep-but-walkable hill into a fence at high speed. The height test is what makes this a CONTACT rather than
    /// the retired gate: ground within a step of the feet is something the character can be seated on, so it is
    /// admitted, and the no-traction rule (see <c>ResolveSlide</c>) is what makes doing so worthless rather than a
    /// climb.</para>
    /// <para>THE PROJECTION IS THE WHOLE FIX FOR THE REPORTED FEEL BUG. The retired gate refused the entire move the
    /// moment any of it pointed at a face, so holding a direction 45 degrees into a cliff while jumping lost the
    /// lateral half too - an invisible wall eating air control. Removing only the into-face component leaves the
    /// along-face travel exactly what it would have been with no face there at all.</para>
    /// <para>ANTI-TUNNEL. The projected move is re-tested against the same two conditions, and refused outright if it
    /// still lands in a wall. That only happens in a concave corner, where sliding along one face runs into another
    /// and there is genuinely nowhere to go, and it is what keeps the property the refusal gate used to carry alone:
    /// an XZ can never be committed under terrain and left for a later ground clamp to pop the capsule up a
    /// cliff.</para>
    /// <para><c>active</c> false is a tick with nothing to advance, and it skips the sampling entirely rather than
    /// evaluating the delegates at the unchanged position. <c>feetY</c> is the world Y of the character's FEET this
    /// tick: the capsule centre minus <see cref="MoveTuning.CapsuleHalfHeight"/>, since <see cref="MoveState.Position"/>
    /// is the capsule CENTRE.</para></summary>
    private static (float x, float z) AdvanceWallSlide(float x, float z, Vector2 velocity, bool active, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, Func<float, float, float> groundHeight,
        float feetY)
    {
        if (!active) return (x, z);
        float nx = x + velocity.X * dt;
        float nz = z + velocity.Y * dt;
        if (groundNormal is null) return (nx, nz);
        // Fixed order on both heads: the destination normal first, and its height only inside the steep branch, so a
        // walkable tick costs exactly the one delegate call the retired gate cost it.
        Vector3 destNormal = groundNormal(nx, nz);
        if (!IsSteepGround(destNormal, tuning)) return (nx, nz);
        if (groundHeight(nx, nz) - feetY <= tuning.StepHeight) return (nx, nz);

        (float fx, float fz) = FaceDirection(destNormal, velocity);
        float into = velocity.X * fx + velocity.Y * fz;
        if (into >= 0f) return (nx, nz);        // travelling away from the face: the wall is behind, nothing to remove
        float sx = velocity.X - into * fx;
        float sz = velocity.Y - into * fz;
        float tx = x + sx * dt;
        float tz = z + sz * dt;
        Vector3 tangentNormal = groundNormal(tx, tz);
        if (IsSteepGround(tangentNormal, tuning) && groundHeight(tx, tz) - feetY > tuning.StepHeight) return (x, z);
        return (tx, tz);
    }

    /// <summary>Whether the character is IN CONTACT with ground too steep to stand on, which is the one condition
    /// that turns a tick into a slide. Read from the START of the tick (the carried position and the ground under
    /// its own column), so it is a pure function of carried state and a reconcile replay reaches the same answer.
    /// <para>Three conjuncts, cheapest and most selective first so an ordinary tick pays almost nothing.
    /// <c>!Grounded</c> is not merely an optimisation: a character STANDING ON A PROP that bridges a steep gully is
    /// grounded on the prop, and the terrain normal beneath it must not slide it off. It is also self-consistent,
    /// because the support decision at the end of the previous tick already refused to ground the character on steep
    /// terrain - so a grounded character is, by construction, on walkable ground or on a prop. Then the contact test
    /// (one <c>groundHeight</c> call, which a character falling through open air fails immediately), and only then
    /// the normal.</para></summary>
    private static bool SlideContact(in MoveState state, in MoveTuning tuning, float halfHeight,
        Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal, out Vector3 normal)
    {
        normal = default;
        if (state.Grounded || groundNormal is null) return false;
        if (state.Position.Y - halfHeight > groundHeight(state.Position.X, state.Position.Z) + SlideContactSkin)
            return false;
        normal = groundNormal(state.Position.X, state.Position.Z);
        return IsSteepGround(normal, tuning);
    }

    /// <summary>What one SLIDING tick resolves to: the advanced XZ, the velocity the step is asking for (which
    /// <see cref="MoveState.CommandedVelocity"/> exports and the server anomaly check measures denial against), the
    /// IN-PLANE part of that velocity (fall line plus contour, which is what carries to the next tick, and which
    /// excludes the per-tick input steer), and the vertical velocity that replaces the ordinary gravity integrate
    /// for this tick.</summary>
    private readonly record struct SlideStep(float X, float Z, Vector2 Commanded, Vector2 Carry, float VerticalVelocity);

    /// <summary>The floor the terminal divide reads for a surface's horizontal normal magnitude: the sine of the
    /// tuning's slope gate, VALIDATED into <c>[MinSlideGateRadians, pi/2]</c> first. A steep normal always has
    /// <c>h > sin(MaxSlopeRadians)</c> by construction (the caller established <c>acos(ny) > MaxSlopeRadians</c>),
    /// so on any sane tuning this floor is never the binding value and the divide is exactly what it always was.
    /// It exists for the degenerate gate, where that guarantee is vacuous - see <see cref="MinSlideGateRadians"/>.
    /// </summary>
    private static float SlideFallLineFloor(in MoveTuning tuning)
        => MathF.Sin(Math.Clamp(tuning.MaxSlopeRadians, MinSlideGateRadians, MathF.PI * 0.5f));

    /// <summary>One tick on ground too steep to stand on: the carried velocity resolved into the surface plane,
    /// gravity accumulated along the fall line, and the result advanced through the same wall slide every other path
    /// uses.
    ///
    /// <para>THE SURFACE FRAME. The surface has a unit down-slope tangent <c>T = (ny*hx, -h, ny*hz)</c> and a unit
    /// CONTOUR <c>C = (-hz, 0, hx)</c> - where <c>ny</c> is the normal's Y, <c>h = sqrt(1 - ny*ny)</c> is its
    /// horizontal magnitude, and <c>(hx, hz)</c> is its XZ direction. <c>T</c>, <c>C</c> and the normal are mutually
    /// perpendicular unit vectors, so any velocity splits into exactly three scalars with nothing left over. Gravity
    /// along the tangent is exactly <c>g . T = Gravity * h</c>, and along the contour exactly zero (the contour is
    /// level by construction, which is why following it costs no drop). A vertical wall (<c>ny</c> 0, <c>h</c> 1)
    /// gives free fall with no horizontal, and a 45 degree face gives an equal split - both correct by
    /// inspection.</para>
    ///
    /// <para>WHAT DIES AT CONTACT, exactly: the INTO-SURFACE component alone. It is not subtracted, it is simply
    /// never read - the resolve projects onto <c>T</c> and <c>C</c> and rebuilds from those two, so the normal
    /// component is gone by construction. That is the inelastic half of the contact and the only thing about it that
    /// is inelastic. BOTH survivors are kept in full:
    /// <list type="bullet">
    /// <item>the CONTOUR speed, which is what a fast run ACROSS a face carries. Deleting it (which the first cut of
    /// this model did, by carrying the fall line alone) stopped a 14 m/s fall running parallel to a wall dead in one
    /// tick, on the tick it merely BRUSHED the wall it was running alongside. Gravity never touches it, so on a
    /// planar face it is conserved, and the character keeps running along the contour while it slides.</item>
    /// <item>the FALL-LINE speed, and it is SIGNED. A negative value is motion UP the face, which is exactly what a
    /// jump grazing one arrives with, and clamping it to zero deleted the whole launch on the contact tick. Gravity
    /// accumulates DOWNWARD along the fall line whatever the sign, so a rising slide decelerates, reverses, and comes
    /// back down on its own. That is not a route back to the #440 jump ratchet: the ratchet needed FOOTING on the
    /// face to re-launch from, steep ground grants none, and the one thing that does grant it here (the wedge rule
    /// below) cannot arm on a rising or apex tick because it requires an accumulated DOWNWARD speed.
    /// <para>SO A FACE IS A RAMP THAT PAYS OUT AND TAKES BACK, and the payout is LARGE - much larger than a jump.
    /// A contact keeps everything but the into-surface component, so the run INTO the face survives as in-plane
    /// motion and the signed fall line cashes it as altitude. Gravity decelerates the fall-line speed at
    /// <c>Gravity * h</c> and each metre along the fall line is <c>h</c> metres of height, so the two cancel and
    /// the reach is <c>v^2 / (2 * Gravity)</c> whatever the face angle: the launch's whole kinetic energy. A
    /// RUNNING JUMP at the shipped tuning launches at <c>sqrt(JumpSpeed^2 + RunSpeed^2)</c> = 15.5 m/s and is
    /// worth 4.8 m of reach against a bare vertical apex of 1.92 m, which is 2.4x. Measured on a near-gate
    /// 46 degree face, the best converter there is: 4.91 m above the base. That is INTENDED and is what a
    /// frictionless surface does with speed - players can briefly ride a face upward on jump energy, and cannot
    /// keep any of it, because the whole rise is handed back on the way down and nothing accumulates across
    /// cycles. The fixtures bound it by that energy rather than by a bare apex, which the run rows breach
    /// honestly.</para></item>
    /// </list>
    /// Because the rebuilt velocity lies entirely in the surface plane, on a planar face the committed drop is
    /// precisely the drop the committed horizontal travel needs: the ground clamp never has to correct the slide and
    /// the character stays glued to the surface instead of bouncing off it.</para>
    ///
    /// <para>INPUT STEERS ALONG THE CONTOUR ONLY, at the same <see cref="MoveTuning.AirControl"/>-scaled speed the
    /// ordinary airborne path already commands - no new knob. The fall-line component of the command is removed in
    /// BOTH directions, not only up-slope: a character with no footing can neither push itself up the face nor push
    /// itself down it, and the contour is also the only direction that keeps the body on the surface (it needs no
    /// drop to pay for). Allowing a down-slope push instead buys a hop off the surface every tick it is held, which
    /// is a visible bounce on any moderate slope.</para>
    ///
    /// <para>HOW THE STEER COMPOSES WITH THE CONTOUR MOMENTUM, exactly, because both now live on the same axis. The
    /// CARRY (what feeds the next tick) is the plane-resolved velocity plus gravity's fall-line step and NOTHING
    /// ELSE - the steer is not folded into it. The COMMANDED velocity for this tick is that carry plus one tick's
    /// steer. So the contour speed evolves only by CONTACT (a new face re-resolves it, a wall slide sheds it through
    /// the clip) and never by held input, while the steer is a per-tick term of at most the air-control-scaled walk
    /// or run speed that appears the tick a direction is held and is gone the tick it is released. A player therefore
    /// steers across a slide at a fixed rate ON TOP OF whatever contour momentum the fall gave them, and cannot pump
    /// the two into each other: holding a direction for a hundred ticks adds one tick's worth of speed, a hundred
    /// times over, to the position, and zero to the carry.</para>
    ///
    /// <para>WHICH MAKES THE CLIP READ THE COMMANDED VELOCITY, not the carry. The advance above is computed from
    /// <c>commanded</c>, so the displacement it commits contains the steer's travel, and
    /// <see cref="ClipCarryToAchieved"/> is handed BOTH vectors: the denial verdict is read from the commanded
    /// velocity that actually drove the tick, and the amount shed is read from the carry's own share of the
    /// displacement. Measuring the carry alone against that displacement instead - which the first cut of this
    /// model did - reads the steer's own travel as a collision denial and rescales the whole carry, fall line
    /// included, so a held steer opposing the carried contour erased a 14 m/s carry inside ten ticks and one tick
    /// of opposing strafe took 85% of a mixed carry's fall-line speed. The rule is symmetric and it is the rule
    /// this whole paragraph rests on: input adds NOTHING to the carry, and takes nothing from it either. Only
    /// geometry may shed it.</para>
    ///
    /// <para>TERMINAL VELOCITY is <see cref="MoveTuning.MaxFallSpeed"/>, read through the surface: the clamp is
    /// applied to the fall-line speed as <c>MaxFallSpeed / h</c> so that the VERTICAL component lands exactly on the
    /// terminal the ordinary fall obeys, and the horizontal stays consistent with it rather than growing without
    /// bound and floating the character off the face. It is applied to the MAGNITUDE, so the (unreachable at any
    /// shipped tuning) up-slope side is bounded too. <c>h</c> is floored by <see cref="SlideFallLineFloor"/> so a
    /// degenerate gate cannot turn the divide into a near-zero one, and the rebuilt horizontal is then clamped to
    /// <see cref="SlideCarrySpeedCeiling"/> so the sim can never commit a velocity its own wire cannot
    /// carry.</para></summary>
    private static SlideStep ResolveSlide(in MoveState state, Vector2 moveDir, float speedFraction, bool run,
        float dt, in MoveTuning tuning, in Vector3 normal, Func<float, float, Vector3>? groundNormal,
        Func<float, float, float> groundHeight, float speedScale, float halfHeight)
    {
        // The surface frame. ny is clamped and h is DERIVED from it rather than measured, so the tangent and the
        // contour are unit vectors by construction even if a consumer's delegate hands back a normal that is not
        // quite normalized. Only the DIRECTION comes from the raw XZ - and when that is degenerate too,
        // FaceDirection hands back the zero vector and the frame collapses to a straight-down tangent (see there).
        float ny = Math.Clamp(normal.Y, 0f, 1f);
        float h = MathF.Sqrt(MathF.Max(0f, 1f - ny * ny));
        (float hx, float hz) = FaceDirection(normal, moveDir);
        float tx = ny * hx, ty = -h, tz = ny * hz;
        float cx = -hz, cz = hx;

        // The carried velocity split onto the two in-plane axes. The third component (along the normal) is never
        // read, and that omission IS the contact.
        float fall = state.HorizontalVelocity.X * tx + state.VerticalVelocity * ty + state.HorizontalVelocity.Y * tz;
        float contour = state.HorizontalVelocity.X * cx + state.HorizontalVelocity.Y * cz;

        // Gravity accumulates along the fall line alone, then the terminal the vertical axis obeys, read through
        // the surface and floored against a degenerate gate.
        fall += tuning.Gravity * h * dt;
        float terminal = tuning.MaxFallSpeed / MathF.Max(h, SlideFallLineFloor(tuning));
        if (fall > terminal) fall = terminal;
        else if (fall < -terminal) fall = -terminal;

        var carry = new Vector2(
            Math.Clamp(fall * tx + contour * cx, -SlideCarrySpeedCeiling, SlideCarrySpeedCeiling),
            Math.Clamp(fall * tz + contour * cz, -SlideCarrySpeedCeiling, SlideCarrySpeedCeiling));
        float vVel = fall * ty;

        // The steer: this tick's commanded velocity with its whole fall-line component removed, which leaves it on
        // the contour axis alone. Added to the commanded velocity only, never to the carry.
        Vector2 commanded = carry;
        if (speedFraction > 0f)
        {
            float inputSpeed = (run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale * speedFraction;
            float steer = moveDir.X * inputSpeed * cx + moveDir.Y * inputSpeed * cz;
            commanded = new Vector2(carry.X + steer * cx, carry.Y + steer * cz);
        }

        (float x, float z) = AdvanceWallSlide(state.Position.X, state.Position.Z, commanded,
            commanded != Vector2.Zero, dt, tuning, groundNormal, groundHeight, state.Position.Y - halfHeight);
        return new SlideStep(x, z, commanded, carry, vVel);
    }

    /// <summary>Whether this SLIDING tick's fall was SWALLOWED by the geometry rather than delivered: the tick
    /// carried a real accumulated fall, and the position it committed descended measurably less than that fall
    /// demanded. A body whose descent the world is absorbing is being held up by the world, so that tick is
    /// supported whatever its column's normal says.
    ///
    /// <para>THE MOTIVATING CASE, which is a crease, and which is NOT the definition. In a concave crease - a
    /// V-gully, the inside of a rock cleft - the column under the feet reads steep, so support is refused. The fall
    /// line of either wall points straight into the other, so <see cref="AdvanceWallSlide"/> removes the whole
    /// horizontal, and the ground clamp then swallows the entire descent that horizontal was supposed to pay for.
    /// Nothing moves, nothing grounds, and a held jump can never fire because the character is never grounded and
    /// its coyote window expired long ago. Measured: 0 grounded ticks in 400, with the horizontal sign-flipping
    /// between the two walls forever. That is the soft-lock this closes, and it is where the name comes from. But
    /// the detector does not look for two faces, or for a pinch, or for any shape at all - it looks at one number,
    /// the shortfall - so read the name as shorthand, not as the condition.</para>
    ///
    /// <para>THE TEST, and why it is exactly two conjuncts. First, the tick's resolved vertical must be
    /// significantly DOWNWARD, so that gravity has genuinely accumulated a fall for the geometry to absorb. The bar
    /// is <c>Gravity * max(CoyoteTime, dt)</c>: the coyote window is the tuning's own statement of how long a body
    /// may be off the ground before that counts as a real fall rather than a blip, so the speed gravity reaches over
    /// it is the tuning's own reading of "actually falling", and the one-tick floor keeps a tuning with no coyote
    /// window at all from lowering the bar to any downward motion whatsoever. Second, the committed descent must
    /// fall SHORT of the descent the resolved velocity demanded, by at least one arming tick's worth. On a PLANAR
    /// face those two are equal by construction (the resolve puts the velocity in the surface plane, so the drop the
    /// travel needs is exactly the drop it takes), so the shortfall is float noise and this never fires there.</para>
    ///
    /// <para>THE OPEN-FACE TRANSIENT, known and harmless. A crease is not the only way to produce a real shortfall:
    /// ANY concave curvature can, because the resolve commits the drop the TANGENT PLANE at the start of the tick
    /// needs while the ground clamp seats the capsule on the actual surface at the end of it, and on a face that
    /// curves upward under one tick's travel the second is higher than the first. When that gap exceeds an arming
    /// tick's descent, an open creaseless face grants a supported tick, and a held jump fires from it. Measured on a
    /// parabolic bowl wall with no crease anywhere, over 4000 ticks of sliding into it: nothing at all at gentle
    /// curvature, then one supported tick 1.29 m up as the curvature sharpens, one at 2.79 m, two at 5.74 m, each
    /// firing a launch when the jump was held. It is bounded by construction and it is not a ratchet: the gap is a
    /// fraction of ONE tick's travel, it shrinks quadratically as the tick rate rises, and the same fixtures
    /// measured ZERO net altitude gain over those 4000 ticks (the peak never once passed the height the slide
    /// started from). A slide down a bowl briefly finding its feet is also the honest answer physically, which is
    /// why this is documented rather than fenced.</para>
    ///
    /// <para>WHY THE JUMP RATCHET CANNOT EXPLOIT IT. The arming condition is precisely what a jump-apex graze
    /// LACKS. At an apex the vertical speed is near zero by definition, and it stays under the bar for the whole
    /// 0.125 m either side of the top at the shipped tuning - so a character grazing a face at the top of its arc
    /// gets no footing, no re-launch, and no ratchet. Getting footing requires arriving with a real accumulated
    /// fall AND having it swallowed, and the fall is exactly what an apex does not have. That holds for the
    /// open-face transient above too, which is why it buys no altitude: it can only ever fire on the way DOWN.</para>
    ///
    /// <para>STATELESS AND TICK-LOCAL. Every input is this tick's own: the position it started at, the position it
    /// resolved to, its resolved vertical, dt and the tuning. Nothing is carried, nothing new rides the wire, and a
    /// reconcile replay of the same tick reaches the same answer. Support is granted for THAT TICK only, so a
    /// character left sitting in a crease reports a low-duty-cycle grounded pulse (support, then the next tick's
    /// fresh gravity, then support again) rather than steady footing - which is enough to jump, to refresh coyote,
    /// and to latch the swallowed fall as a landing, and is honest about a surface that is genuinely not a floor.
    /// </para></summary>
    /// <param name="startY">The capsule-centre Y the tick began at.</param>
    /// <param name="resolvedY">The capsule-centre Y the tick committed, after the ground clamp.</param>
    /// <param name="slideVVel">The vertical velocity the slide resolved for this tick (negative when falling).</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="tuning">Carries the gravity and coyote window the arming speed is read from.</param>
    private static bool SlideWedged(float startY, float resolvedY, float slideVVel, float dt, in MoveTuning tuning)
    {
        float arming = tuning.Gravity * MathF.Max(tuning.CoyoteTime, dt);
        if (slideVVel > -arming) return false;
        float demanded = -slideVVel * dt;       // > 0: what this tick's velocity asked the capsule to descend
        float delivered = startY - resolvedY;   // what the ground clamp actually let through
        return demanded - delivered >= arming * dt;
    }
}
