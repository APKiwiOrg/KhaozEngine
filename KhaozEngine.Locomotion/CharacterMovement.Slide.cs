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
//   1. WALL SLIDE. A horizontal move whose destination ground stands above what the tick can REACH is a wall
//      contact. The into-face component of the move dies and the along-face component survives, so strafing along
//      a cliff mid-jump keeps its lateral travel. Grounded and airborne, command path and momentum path, one
//      function. The reach is a MoveTuning.StepHeight while the character is GROUNDED, because a step is what
//      footing buys, and the tick's OWN RESOLVED UPWARD MOTION when it is not (#468, 17.29.0): a tick with no
//      footing may never end higher than its own velocity carried it, so altitude on steep ground never comes
//      from the ground clamp. See NoFootingReach and SlideReach.
//   2. NO TRACTION. Ground steeper than the tick's TRACTION GATE grants no support, so gravity decomposes
//      against the surface and the character accelerates down the fall line until it reaches walkable ground, open
//      air, or water. Climbing self-defeats because there is no footing to climb from, which is what retires the
//      ascent gate rather than patching it a third time. The ONE exception is A BODY THE WORLD IS HOLDING UP
//      (SlideWedged), read either from the geometry (the plane the HEIGHTS describe across the capsule's own
//      footprint is standable) or from the dynamics (the tick carried a real fall and committed measurably less of
//      it than that fall demanded). Its motivating case is the concave crease, which without it is a soft-lock
//      (the character can neither slide out of it nor jump). Neither reading can tell a gully from a face on its
//      own, so support ALSO requires the ground under the capsule to FOLD BACK ON ITSELF: some pair of fall lines
//      across the footprint ring must oppose by more than 120 degrees (#468). See SlideWedged and
//      OpposingFallLines.
//
// WHERE THE GEOMETRY COMES FROM, and the invariant that buys (#468, 17.29.0). Both rules above resolve against a
// PLANE, and a consumer hands the step TWO surfaces to read one from: its ground-NORMAL delegate and its ground-HEIGHT
// field. They are not the same surface. A real terrain sampler smooths its normals over a stencil wider than the
// height field's own detail, so the plane the normal reports and the column the ground clamp seats to disagree
// everywhere - and the resolve-then-clamp cycle PUMPS ENERGY wherever they do. The resolve commits the drop ITS plane
// needs, the clamp seats the capsule on the OTHER surface, and the difference is altitude that no velocity paid for.
// Measured on the #468 cliff patch with the reach rule below already in place: at 120 Hz, 44 of 360 headings still
// net-climbed, the worst gaining 389 m in 20 seconds with VerticalVelocity reading +21 m/s, no jump anywhere and not
// one footing grant. Tightening the reach could not close it, because the reach was being measured against the same
// disagreeing plane.
//
// So the two delegates get ONE JOB EACH, and that split is the fix:
//
//   - The HEIGHT FIELD IS THE GEOMETRY. On a tick with no footing, the fall-line tangent, the contour, the
//     wall-contact face direction and the reach admission all come from a plane read off the heights themselves, by a
//     central difference at capsule scale (HeightPlaneNormal). THE PLANE THE SLIDE RESOLVES AGAINST IS THE SURFACE THE
//     CLAMP SEATS TO, so the clamp is only ever correcting float noise, and it can never hand back altitude the
//     resolve did not already account for.
//   - The NORMAL DELEGATE CLASSIFIES, and does nothing else: is this ground too steep to stand on (IsSteepGround),
//     and does the ground under the footprint fold back on itself (OpposingFallLines). Smoothing is a stability
//     FEATURE there - a per-sample classification would flicker the support decision with it - and it is free,
//     because a classification decides what a tick IS, never where the capsule ends up.
//
// WHERE THE GATE COMES FROM (#475, 17.30.0). Every steepness test in this file takes the threshold as a VALUE rather
// than reading MoveTuning.MaxSlopeRadians for itself, because the gate is state-dependent now: a character that
// already has footing is judged against MaxSlopeRadians plus a hysteresis band, and one that has none against the
// bare gate. StepCore resolves it once per tick and hands the same number to the wall contact, the slide contact, the
// resolve and the wedge. The rule, the band, and the slide friction that rides beside it all live in
// CharacterMovement.Traction.cs. Nothing in this file re-derives any of it.
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

    /// <summary>The face's OUTWARD horizontal direction: a surface plane normal's XZ projection, normalized. It points
    /// away from the face and down its fall line, so a positive dot with a velocity means travelling AWAY from the
    /// face and a negative one means INTO it. Every caller on a no-footing path hands it the HEIGHT-derived plane
    /// (<see cref="HeightPlaneNormal"/>), never the classification normal - see the module header for why that
    /// distinction is the whole of #468.
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

    // The smallest half-width the height stencil will be read over, in metres. The stencil is
    // MoveTuning.CapsuleRadius either side of the point (see HeightPlaneNormal), and a tuning may set that to
    // anything at all, zero included - which would turn the central difference's 1/(2r) into a divide by zero. A
    // millimetre is orders below any capsule the fleet authors, so this only ever binds on a degenerate tuning, and
    // there it keeps the divide finite instead of poisoning the position with an infinity.
    private const float MinStencilRadius = 1e-3f;

    /// <summary>The local surface plane READ OFF THE HEIGHT FIELD, as a unit normal: a central difference of the
    /// ground height at <see cref="MoveTuning.CapsuleRadius"/> either side of the point, x first and then z.
    ///
    /// <para>THIS IS THE GEOMETRY A NO-FOOTING TICK RESOLVES AGAINST (#468), and the module header says why: the
    /// ground clamp seats the capsule on the HEIGHT FIELD, so anything that decides where the capsule ends up has to
    /// be reading the same surface, or the difference between the two becomes free altitude. The normal delegate is
    /// not that surface - a terrain sampler smooths its normals, and a smoothed normal is a different plane
    /// everywhere the height field has detail under the stencil.</para>
    ///
    /// <para>THE STENCIL IS THE CAPSULE RADIUS, and it is a FIXED tuning value on purpose. It has to span movement
    /// scale rather than sample scale (a plane read at float resolution would be as noisy as the height field is,
    /// and would resolve a slide against detail the capsule cannot even stand on), and it must not depend on the
    /// tick rate, the speed, or the heading - anything a player controls is a dial an exploiter can turn, which is
    /// exactly how the retired <see cref="MoveTuning.StepHeight"/> admission was played. Capsule radius is the
    /// engine's existing statement of "how wide is this body", and the wedge ring already scales by it.</para>
    ///
    /// <para>IT IS A CENTRAL DIFFERENCE, four reads, rather than the three of a forward one. A forward stencil is
    /// asymmetric: the plane it reports depends on which way +x and +z happen to point in the world, so on a creased
    /// face there are headings whose stencil straddles a crease on one side only and reads a plane tilted in the
    /// player's favour. The central difference has no preferred direction to aim at, and one extra delegate read is
    /// the whole price. Both reads are at FIXED offsets in a FIXED order over the same pure delegate both heads
    /// hold, so a reconcile replay of this tick derives the same plane bit for bit.</para>
    ///
    /// <para>A DEGENERATE GRADIENT HANDS BACK <paramref name="classification"/> UNCHANGED. Below the same 1e-12
    /// length-squared floor <see cref="FaceDirection"/> reads (a gradient of 1e-6 m/m - level by any measure the
    /// fleet cares about), the heights carry no direction, and a NaN from a misbehaving delegate fails the same test.
    /// Falling back to the classification normal is what keeps a consumer whose height field is flat while its normal
    /// delegate reports a face behaving exactly as it did before this rule existed, rather than having its slide
    /// silently collapse into a hover.</para></summary>
    private static Vector3 HeightPlaneNormal(float x, float z, in MoveTuning tuning,
        Func<float, float, float> groundHeight, in Vector3 classification)
    {
        float r = MathF.Max(tuning.CapsuleRadius, MinStencilRadius);
        float inv2r = 0.5f / r;
        float gx = (groundHeight(x + r, z) - groundHeight(x - r, z)) * inv2r;
        float gz = (groundHeight(x, z + r) - groundHeight(x, z - r)) * inv2r;
        float mSq = gx * gx + gz * gz;
        // Written as a negated > so a NaN gradient takes the fallback too, rather than falling through to build a
        // NaN normal that the position guard would then have to catch downstream.
        if (!(mSq > FaceNormalEpsilonSq)) return classification;
        // The unit normal of the plane z = h0 + gx*dx + gz*dz is (-gx, 1, -gz) / sqrt(1 + gx^2 + gz^2). Built from
        // scalars in a fixed order rather than through Vector3.Normalize, matching the rest of this file.
        float inv = 1f / MathF.Sqrt(mSq + 1f);
        return new Vector3(-gx * inv, inv, -gz * inv);
    }

    /// <summary>Advance an XZ position by a horizontal velocity for one tick, WALL-SLIDING off analytic terrain that
    /// the step cannot reach: when the destination's ground normal is steeper than
    /// <see cref="MoveTuning.MaxSlopeRadians"/> AND its ground stands more than <paramref name="reach"/> above the
    /// feet, the move keeps only its along-face component. Shared by the ordinary command path
    /// (<c>DesiredHorizontalCore</c>), the airborne momentum path, and the slide, so the rule cannot come to mean
    /// three different things depending on which one drove the tick.
    /// <para>BOTH CONDITIONS ARE LOAD-BEARING. The steepness test is what leaves walkable ground untouched: a fast
    /// run up a legal ramp can rise more than a StepHeight in one tick, and treating that as a wall would turn every
    /// steep-but-walkable hill into a fence at high speed. The height test is what makes this a CONTACT rather than
    /// the retired gate: ground the character can be seated on is admitted, and the no-traction rule (see
    /// <c>ResolveSlide</c>) is what makes doing so worthless rather than a climb.</para>
    /// <para>WHAT THE REACH IS, and why it is not always <see cref="MoveTuning.StepHeight"/> (#468). A step is
    /// something FOOTING buys: it is the height a character standing on the ground can lift a foot onto. A tick with
    /// no footing has bought nothing, so its reach is its OWN RESOLVED UPWARD MOTION and nothing else - zero while it
    /// falls. See <see cref="NoFootingReach"/> for the rule and for what a StepHeight reach cost when it was handed to
    /// every tick alike: a two-tick limit cycle walked up a 74 degree sea cliff at 2.5 m/s, because the clamp seated
    /// the capsule onto every column the admission let it reach and the admission asked only whether that column was
    /// within a step.</para>
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
    /// is the capsule CENTRE. <c>gate</c> is the tick's ONE traction gate (<see cref="TractionGate"/>), so a grounded
    /// character's wall contact reads the same widened threshold its support decision does - without that, a run up a
    /// bank the hysteresis band is holding footing on would meet a fence made of the very ground it is standing
    /// on.</para></summary>
    private static (float x, float z) AdvanceWallSlide(float x, float z, Vector2 velocity, bool active, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, Func<float, float, float> groundHeight,
        float feetY, float reach, float gate)
    {
        if (!active) return (x, z);
        float nx = x + velocity.X * dt;
        float nz = z + velocity.Y * dt;
        if (groundNormal is null) return (nx, nz);
        // Fixed order on both heads: the destination normal first, and its height only inside the steep branch, so a
        // walkable tick costs exactly the one delegate call the retired gate cost it.
        Vector3 destNormal = groundNormal(nx, nz);
        if (!IsSteepGround(destNormal, gate)) return (nx, nz);
        if (groundHeight(nx, nz) - feetY <= reach) return (nx, nz);

        // THE FACE DIRECTION COMES FROM THE HEIGHTS (#468), not from destNormal. destNormal has already done its one
        // job on the line above - the steepness classification - and its idea of downhill is exactly what disagreed
        // with the column the ground clamp seats to. See HeightPlaneNormal.
        (float fx, float fz) = FaceDirection(HeightPlaneNormal(nx, nz, tuning, groundHeight, destNormal), velocity);
        float into = velocity.X * fx + velocity.Y * fz;
        // NO OUTWARD EARLY-OUT. Until 17.29.0 a move with `into >= 0` was admitted here unconditionally, on the
        // argument that the face direction is the destination column's own downhill so an outward move cannot be
        // climbing that column. That argument holds only if the direction and the height field describe the SAME
        // surface, which under a smoothed normal they did not: measured 4.4 m/s of clamp-fed climb during a fall at
        // 30 Hz, admitted by this branch, invisible at 30 Hz in the net only because the fall line outran it. The
        // heights have now settled the question one line above - this destination stands more than the reach above
        // the feet - so an outward move that the heights say rises past the reach is a wall contact like any other,
        // and the projection below is what a wall contact does. On a plane that agrees with its own heights the case
        // cannot arise at all (moving down the plane lands lower, so the reach test returned already), which is why
        // this costs the ordinary path nothing.
        float sx = velocity.X - into * fx;
        float sz = velocity.Y - into * fz;
        float tx = x + sx * dt;
        float tz = z + sz * dt;
        Vector3 tangentNormal = groundNormal(tx, tz);
        if (IsSteepGround(tangentNormal, gate) && groundHeight(tx, tz) - feetY > reach) return (x, z);
        return (tx, tz);
    }

    /// <summary>The height a tick may find its destination ground standing above its feet and still be SEATED on it
    /// rather than stopped by it (<see cref="AdvanceWallSlide"/>'s <c>reach</c>), for the paths whose horizontal is
    /// arbitrary with respect to the surface: the command path and the airborne momentum path.
    ///
    /// <para>A GROUNDED tick keeps <see cref="MoveTuning.StepHeight"/> exactly as before, so every walkable path -
    /// a step-up, a step-down, a stair glide, a walk into the toe of a cliff - is byte-identical to every release
    /// since the wall slide shipped. Footing is what a step is bought with, and a grounded character has it: either
    /// the support decision at the end of the previous tick granted it (which on steep terrain it refuses, so that
    /// character is standing on walkable ground or on a prop), or <see cref="SlideWedged"/> did. The wedge is the
    /// third way to be grounded and it does hand a StepHeight reach to the tick after it fires - probed as a ratchet
    /// and it does not pay, because a wedge grant needs an accumulated fall SWALLOWED by ground that folds back on
    /// itself, and a tick that spent its fall being swallowed has no upward velocity left to convert, so the step it
    /// buys is handed straight back by the fall the next tick accumulates.</para>
    ///
    /// <para>A TICK WITH NO FOOTING GETS ITS OWN RESOLVED UPWARD MOTION, and nothing else: <c>max(0, vVel * dt)</c>
    /// against the gravity integrate step 2 is about to commit. So a falling tick may be seated only at or below the
    /// height it started at (it can still meet a face and slide down it, and it can still land on ground it fell
    /// onto - what it cannot do is END HIGHER than it began), and a rising one may reach exactly as far up as its own
    /// velocity carries it and no further. That is the whole invariant of #468 stated as one number: ALTITUDE ON
    /// STEEP GROUND COMES ONLY FROM REAL VELOCITY, NEVER FROM THE GROUND CLAMP.</para>
    ///
    /// <para>WHY IT IS ENFORCED HERE AND NOT AT THE CLAMP. The clamp cannot be capped: its job is to forbid
    /// penetration, and a clamp that refuses to raise a capsule leaves it INSIDE the terrain, trading a climb exploit
    /// for a tunnel. So the rule lands on the horizontal that would have needed the raise - which is exactly what the
    /// wall contact already is. A move whose destination stands above the allowance is a WALL, its into-face component
    /// dies, and its along-face component survives untouched, so a character sliding down a face, strafing across one,
    /// or falling past one keeps everything it had. Only the part of the move that was buying altitude is removed.</para>
    ///
    /// <para>THE SLIDING TICK TAKES THE SAME RULE through <see cref="SlideReach"/>, which is this one plus a float
    /// slack it needs and this one does not - see there.</para></summary>
    private static float NoFootingReach(in MoveState state, in MoveTuning tuning, float dt)
        => state.Grounded
            ? tuning.StepHeight
            : MathF.Max(0f, FallIntegrate(state.VerticalVelocity, tuning, dt) * dt);

    // The float slack on a SLIDING tick's rise allowance, in metres, and the one number in this rule that is a
    // tolerance rather than a physical quantity.
    //
    // WHY THE SLIDE NEEDS ONE AND NO OTHER PATH DOES. A sliding tick advances by a velocity that lies IN THE SURFACE
    // PLANE by construction, so on a planar face the rise it asks for EQUALS its own resolved vertical motion - the
    // allowance and the ask are the same number computed two different ways, and which side of the comparison they
    // land on is then decided by rounding. Getting that wrong is not cosmetic: a rising graze is a move INTO the
    // face, so a false wall verdict sheds its whole up-slope component and deletes the signed fall line's ride,
    // which is behaviour 17.28.0 shipped deliberately and pins with its own fixtures. The two sides are differences
    // of world heights, so their rounding is proportional to the height magnitude (about 1e-7 of it per operand):
    // a millimetre covers several kilometres of world height, which is orders past anything the fleet authors.
    //
    // IT IS A DISTANCE PER TICK, SO ITS WORST CASE IS A RATE THAT SCALES WITH THE TICK RATE: a millimetre a tick is
    // 0.03 m/s at 30 Hz and 0.12 m/s at 120. That is stated plainly because the rest of this rule is deliberately
    // scale-free (the reach IS the tick's own resolved motion, which shrinks with dt exactly as the ask does) and
    // this one constant is not. It stays a constant anyway: it covers the ROUNDING of a difference of world heights,
    // and rounding does not get smaller when the tick does.
    //
    // WHY THE WORST CASE IS NOT REACHABLE, stated honestly - the earlier argument here ("input has no authority over
    // the fall line, so there is no dial") was true and beside the point, because the collector would be geometry
    // rather than input. The real reason is that COLLECTING IT REQUIRES HOLDING A CONTOUR AND GRAVITY WILL NOT LET
    // YOU. To bank a millimetre every tick the surface under the capsule must rise, tick after tick, by between zero
    // and one millimetre more than that tick's own descent - which is to say the body must track a near-level contour
    // across the face indefinitely. It cannot: gravity's pull along the fall line is compulsory and accumulates every
    // tick regardless of input, the steer is confined to the contour axis and adds nothing to the carry, so any body
    // that starts on a contour is accelerating off it down the fall line by the next tick. A contour hold is not a
    // thing a player can sustain, so neither is the worst case. The heading sweep in ClampRatchetTests measures this
    // rather than arguing it: 360 headings at four tick rates, none of which nets above its start.
    private const float SlideRiseSlack = 1e-3f;

    /// <summary>The rise allowance for a SLIDING tick: the same rule <see cref="NoFootingReach"/> states (a tick
    /// with no footing may end no higher than its own resolved vertical motion carries it) read off the SLIDE's own
    /// resolved vertical rather than the gravity integrate, plus <see cref="SlideRiseSlack"/>.
    /// <para>It is not a formality here. The 17.28.0 slide keeps the capsule glued to a PLANAR face by construction -
    /// the committed drop is exactly the drop the committed travel needs - but nothing makes a consumer's normal
    /// delegate agree with its own height field, and a smoothed or lower-resolution normal field over a heightmap
    /// (which is what a real terrain sampler hands back) disagrees everywhere. There the tangent plane's drop is not
    /// the surface's drop, and without this the ground clamp seats the difference as ALTITUDE on a sliding tick:
    /// measured on the #468 cliff patch at 120 Hz, 31 consecutive sliding ticks each seated 0.085 m higher than the
    /// last while <c>VerticalVelocity</c> read -2 to -8 m/s, for 2.26 m of climb out of nothing. A tick that is
    /// falling may not end higher than it started, and that is as true of a slide as of a fall.</para></summary>
    private static float SlideReach(float slideVVel, float dt)
        => MathF.Max(0f, slideVVel * dt) + SlideRiseSlack;

    /// <summary>One tick of gravity on the carried vertical velocity, terminal clamp included: the ordinary
    /// (non-sliding) vertical integrate, lifted out of <c>StepCore</c> step 2 so that <see cref="NoFootingReach"/>
    /// reads the SAME number step 2 is about to commit rather than a second copy of the arithmetic that could drift
    /// away from it.</summary>
    private static float FallIntegrate(float verticalVelocity, in MoveTuning tuning, float dt)
    {
        float v = verticalVelocity - tuning.Gravity * dt;
        return v < -tuning.MaxFallSpeed ? -tuning.MaxFallSpeed : v;
    }

    /// <summary>Whether the character is IN CONTACT with ground too steep to stand on, which is the one condition
    /// that turns a tick into a slide. Read from the START of the tick (the carried position and the ground under
    /// its own column), so it is a pure function of carried state and a reconcile replay reaches the same answer.
    /// <para>Three conjuncts, cheapest and most selective first so an ordinary tick pays almost nothing.
    /// <c>!Grounded</c> is not merely an optimisation: a character STANDING ON A PROP that bridges a steep gully is
    /// grounded on the prop, and the terrain normal beneath it must not slide it off. It is very nearly
    /// self-consistent too, because the support decision at the end of the previous tick already refused to ground the
    /// character on steep terrain. The exception is <see cref="SlideWedged"/>, which is a THIRD way to end a tick
    /// grounded and the only one that can do it on steep ground: the tick after a wedge grant therefore skips the
    /// slide entirely and takes the ordinary command path with a grounded character's reach. That is deliberate -
    /// the wedge's whole purpose is to let a body the world is holding up act like it is being held up - and it
    /// cannot ratchet, for the reason set out in <see cref="NoFootingReach"/>. Then the contact test
    /// (one <c>groundHeight</c> call, which a character falling through open air fails immediately), and only then
    /// the normal.</para>
    /// <para><c>gate</c> is the tick's traction gate. A sliding tick has no footing by the first conjunct, so the gate
    /// it is handed is always the bare <see cref="MoveTuning.MaxSlopeRadians"/> and the hysteresis band cannot widen
    /// what counts as a slide. It is passed rather than re-read so there is one gate per tick and no call site that
    /// can drift from it (see <see cref="TractionGate"/>).</para></summary>
    private static bool SlideContact(in MoveState state, float halfHeight,
        Func<float, float, float> groundHeight, Func<float, float, Vector3>? groundNormal, float gate,
        out Vector3 normal)
    {
        normal = default;
        if (state.Grounded || groundNormal is null) return false;
        if (state.Position.Y - halfHeight > groundHeight(state.Position.X, state.Position.Z) + SlideContactSkin)
            return false;
        normal = groundNormal(state.Position.X, state.Position.Z);
        return IsSteepGround(normal, gate);
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
        Func<float, float, float> groundHeight, float speedScale, float halfHeight, float gate)
    {
        // The surface frame, READ OFF THE HEIGHT FIELD (#468). `normal` classified this tick as a slide and is done.
        // the plane the fall line and the contour are built from is the central-difference one under the capsule, so
        // the drop this resolve commits is the drop the ground clamp is about to seat (see HeightPlaneNormal, and the
        // module header for what the two disagreeing cost). ny is clamped and h is DERIVED from it rather than
        // measured, so the tangent and the contour are unit vectors by construction even if a consumer's delegate
        // hands back a normal that is not quite normalized. Only the DIRECTION comes from the raw XZ - and when that
        // is degenerate too, FaceDirection hands back the zero vector and the frame collapses (see there).
        //
        // A LEVEL HEIGHT PATCH UNDER A STEEP CLASSIFICATION now resolves to h ~ 0: no fall-line gravity and no drop,
        // so a body that arrives at rest on a flat micro-ledge inside a face STAYS there, un-grounded (no jump, no
        // coyote), and any speed it arrives with carries it off the ledge unimpeded because nothing damps the
        // contour. That is the honest reading of a consumer whose classification and geometry disagree about one
        // patch, and it is a rest, never a climb: the invariant this frame exists to buy is about altitude, and a
        // level patch pays none.
        Vector3 plane = HeightPlaneNormal(state.Position.X, state.Position.Z, tuning, groundHeight, normal);
        float ny = Math.Clamp(plane.Y, 0f, 1f);
        float h = MathF.Sqrt(MathF.Max(0f, 1f - ny * ny));
        (float hx, float hz) = FaceDirection(plane, moveDir);
        float tx = ny * hx, ty = -h, tz = ny * hz;
        float cx = -hz, cz = hx;

        // The carried velocity split onto the two in-plane axes. The third component (along the normal) is never
        // read, and that omission IS the contact.
        float fall = state.HorizontalVelocity.X * tx + state.VerticalVelocity * ty + state.HorizontalVelocity.Y * tz;
        float contour = state.HorizontalVelocity.X * cx + state.HorizontalVelocity.Y * cz;

        // Gravity accumulates along the fall line alone, RAMPED IN BY THE SLIDE FRICTION over the band past the gate
        // (#475): a surface a degree too steep to stand on pulls at a fraction of gravity, a sheer one at all of it,
        // so the classifier's boundary stops being a cliff edge in the feel. The ramp reads the SAME height plane the
        // frame above was built from, and it scales the DOWNHILL half alone - a rising slide keeps full-strength
        // deceleration, which is what stops friction from becoming free altitude. See SlideFrictionScale and
        // SlideFallLineStep in CharacterMovement.Traction.cs. Then the terminal the vertical axis obeys, read through
        // the surface and floored against a degenerate gate.
        fall = SlideFallLineStep(fall, tuning.Gravity * h, SlideFrictionScale(ny, gate, tuning), dt);
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
            commanded != Vector2.Zero, dt, tuning, groundNormal, groundHeight, state.Position.Y - halfHeight,
            SlideReach(vVel, dt), gate);

        // RE-SEAT DOWN ONTO THE SURFACE, and only ever down. The ground clamp covers one direction of the
        // resolve/surface disagreement - a capsule that ends BELOW the terrain is lifted onto it - and nothing
        // covered the other. A capsule that ends slightly ABOVE the surface simply stays there, and because the
        // slide replaces the gravity integrate with the fall-line one, it is never pulled back: the error is
        // re-committed every tick and DRIFTS. The plane is sampled now rather than analytic, so it carries the
        // height field's own float noise - about ulp(worldHeight)/CapsuleRadius of gradient error, which becomes
        // ulp(worldHeight) * speed * dt / CapsuleRadius of drop error per tick. Measured on the 20 degree gate
        // fixture (1300 m of world height, a 96 m/s slide): 2.4e-4 m a tick, which crosses SlideContactSkin after
        // ~210 ticks, drops the capsule out of slide contact in mid-face and parks its whole carry. At ordinary
        // world heights and speeds the same arithmetic gives ~1e-5 m a tick, so it takes kilometres of continuous
        // sliding to matter - but it accumulates in one direction and so it does eventually matter.
        //
        // Seating the capsule onto the surface when it ends above it (and only within SlideContactSkin, so a
        // genuine departure - a convex crest, a launch - is untouched) makes the model's own claim true rather
        // than approximate: A SLIDE HOLDS THE CAPSULE ON THE SURFACE. It CANNOT feed the ratchet this release
        // exists to kill, and that is structural rather than argued: the correction only ever lowers the committed
        // vertical, never raises it, so every bound stated in terms of "no higher than" survives it untouched.
        float seatedY = groundHeight(x, z) + halfHeight;
        float endY = state.Position.Y + vVel * dt;
        if (endY > seatedY && endY - seatedY <= SlideContactSkin && dt > 0f)
            vVel = (seatedY - state.Position.Y) / dt;

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
    /// <para>THE OPEN-FACE TRANSIENT WAS NOT HARMLESS, AND THE THIRD CONJUNCT IS WHAT ANSWERS IT (#468). A crease is
    /// not the only way to produce a real shortfall: ANY concave curvature can, because the resolve commits the drop
    /// the TANGENT PLANE at the start of the tick needs while the ground clamp seats the capsule on the actual
    /// surface at the end of it. 17.28.0 measured that on a parabolic bowl (a handful of transient supported ticks
    /// over 4000, worth no altitude) and documented it as harmless. On a real cliff it is not: where the ground
    /// NORMAL is smoothed over a wider stencil than the height field's own detail - which is what an ordinary terrain
    /// sampler does - every tick's tangent plane disagrees with the surface under it, the shortfall is structural
    /// rather than occasional, and the open face granted support steadily. Measured on the authored sea cliff: five
    /// grants inside one climb, each one a full jump for a player holding the button, with the probe ring reading
    /// 0 of 8 samples walkable and its fall lines spread across a mere 1 to 3 degrees. That is not a wedge, it is a
    /// FACE - and a face is exactly the thing steep ground is supposed to grant nothing on.</para>
    ///
    /// <para>SO SUPPORT ALSO REQUIRES THE FALL LINES TO GENUINELY OPPOSE (<see cref="OpposingFallLines"/>). A wedge
    /// is a place with no way out along the ground, which is a statement about SHAPE, and the shortfall alone cannot
    /// see shape. The ring can: an open face's samples all fall the same way (measured 1 to 3 degrees apart), a true
    /// gully's oppose across the crease at about 180. The soft-lock this rule exists for keeps its escape and the
    /// face stops paying out.</para>
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
    /// <param name="resolved">The capsule-centre position the tick committed, the centre of the probe ring.</param>
    /// <param name="groundNormal">The ground-normal delegate the ring is sampled through. Null cannot reach here (a
    /// slide requires it), and reads as "no shape to see", so no wedge.</param>
    /// <param name="groundHeight">The height field the body-scale plane is read from.</param>
    /// <param name="gate">This tick's traction gate. A wedge arms only on a sliding tick, which has no footing, so it
    /// is the bare <see cref="MoveTuning.MaxSlopeRadians"/>: REGAINING footing is judged at the gate, never at the
    /// hysteresis band, so a wedge cannot be a back door onto ground a standing character would have kept.</param>
    private static bool SlideWedged(float startY, float resolvedY, float slideVVel, float dt, in MoveTuning tuning,
        in Vector3 resolved, Func<float, float, Vector3>? groundNormal, Func<float, float, float> groundHeight,
        float gate)
    {
        // BEING HELD UP CAN BE READ TWO WAYS, and either one arms the rule. The SHAPE test below is required by both,
        // so what differs is only the evidence that this particular body is resting rather than falling.
        //
        //   - THE BODY-SCALE READING (#468, and the direct one): the plane the HEIGHTS describe across this capsule's
        //     own footprint is STANDABLE. Then the body is standing on standable ground, whatever a point sample of
        //     the normal delegate says about the column under its centre. A degenerate gradient reads as LEVEL here
        //     (hence the UnitY fallback, rather than the classification normal ResolveSlide falls back to): a height
        //     field with no slope across a whole capsule is flat ground, and flat ground holds a body up.
        //   - THE DYNAMIC READING (the original): the tick carried a real accumulated fall and committed measurably
        //     less of it than that fall demanded, so the world absorbed the difference.
        //
        // The first became necessary when the geometry moved to the height field, and the crease is why. The dynamic
        // reading watches for a fall that was DEMANDED and not delivered, and a slide resolving against the body-scale
        // plane correctly demands no fall at all in a crease bottom - the plane there is level, because the capsule
        // spans both walls. So nothing arms, and the soft-lock this whole rule exists to close came back: measured on
        // the V-gully fixture as 0 grounded ticks in 400, the capsule at rest on the crease floor and a held jump that
        // never fires. The symptom vanished because the model got better at seeing the shape that caused it.
        bool standableUnderfoot =
            !IsSteepGround(HeightPlaneNormal(resolved.X, resolved.Z, tuning, groundHeight, Vector3.UnitY), gate);
        if (!standableUnderfoot)
        {
            float arming = tuning.Gravity * MathF.Max(tuning.CoyoteTime, dt);
            if (slideVVel > -arming) return false;
            float demanded = -slideVVel * dt;       // > 0: what this tick's velocity asked the capsule to descend
            float delivered = startY - resolvedY;   // what the ground clamp actually let through
            if (demanded - delivered < arming * dt) return false;
        }

        // THE SHAPE TEST IS REQUIRED EITHER WAY, and it is what keeps the body-scale reading from becoming "footing
        // wherever the stencil blends". A capsule a centimetre past the TOE of a cliff spans mostly flat ground, so
        // its body-scale plane reads walkable - and granting footing there would put footing ON the face, which
        // pinned fixtures rightly forbid and which is one blend away from the #440 ratchet. The ring tells the two
        // apart exactly: at a toe every fall line still points the same way (off the face), while a crease is the
        // shape whose fall lines oppose. Measured: the toe grants nothing, the V-gully grants and escapes.
        //
        // It is LAST because it is the only conjunct that costs a fan of delegate calls, and for an ORDINARY tick the
        // cheap tests above do reject outright - a grounded or walkable tick never reaches SlideWedged at all. WHAT
        // THAT DOES NOT DO is keep the fan rare on the terrain this rule was written for: on a smoothed-normal face
        // the dynamic conjuncts both pass on essentially every sliding tick past about 2.5 m/s of fall, because the
        // arming speed is small and the resolve/clamp disagreement always leaves some shortfall. So budget the fan at
        // ONCE PER SLIDING CHARACTER PER TICK: 17 ring samples of the normal delegate, and a normal delegate over a
        // heightmap is itself commonly four height fetches, which is ~68 height fetches a tick for one character on a
        // face. That is affordable for the handful of sliding characters a frame usually has and is NOT affordable for
        // a crowd of them, which is the honest shape of the cost and the thing to measure before putting a hundred
        // NPCs on a cliff.
        return groundNormal is not null && OpposingFallLines(resolved.X, resolved.Z, tuning.CapsuleRadius, groundNormal);
    }

    // HOW FAR APART two of the probe ring's fall lines must point before the ground under the capsule counts as a
    // WEDGE rather than a face, as a cosine (-0.5 is 120 degrees).
    //
    // The two cases this separates were both measured, and they are nowhere near each other: an open cliff face
    // spreads its fall lines by 1 to 3 degrees across the ring (they are all the same face, so they all fall the
    // same way), and a true gully opposes at about 180 (the whole point of a gully is that its two walls fall into
    // each other). Anything from about 10 to about 170 degrees would separate the measured pair, so the threshold is
    // chosen on what the number MEANS rather than by splitting that range: at 120 degrees two unit fall lines sum to
    // a vector of magnitude 2*cos(60) = 1, no longer than either of them alone. Below that the ring still agrees on
    // a direction to leave by and the ground is a face, however folded. Past it the samples cancel each other and
    // there is no downhill left to take, which IS the wedge. It leaves 40x margin on the open-face side and 60
    // degrees on the gully side, so neither case is anywhere near the edge.
    private const float WedgeOpposingCos = -0.5f;

    /// <summary>Whether the ground under the capsule FOLDS BACK ON ITSELF: whether any two samples of a fixed ring
    /// have horizontal fall lines pointing more than <see cref="WedgeOpposingCos"/> apart. This is the shape half of
    /// the wedge rule - see <see cref="SlideWedged"/> for why a shortfall alone cannot tell a gully from a face, and
    /// what an open face cost when it was allowed to.
    ///
    /// <para>THE RING IS <c>TreadFanOffsets</c>, the established footprint fan (the centre plus rings at 0.65 and
    /// 0.95 of the capsule radius, eight directions each), reused rather than reinvented so there is ONE statement in
    /// the engine of what "under the capsule" spans. It is read here through the analytic ground-normal delegate
    /// instead of the physics ray fan, which is the only difference: same offsets, same fixed order.</para>
    ///
    /// <para>A sample whose normal has no horizontal component is LEVEL and contributes no fall line, so it is
    /// skipped rather than given an invented direction - level ground under part of the footprint is walkable ground,
    /// and if the capsule could be supported by it the support decision would already have said so. A sample whose
    /// normal points DOWNWARD is skipped for a different reason: an inverted normal is an overhang, or a delegate
    /// that has wrapped past vertical, and its XZ projection is the direction the surface rises rather than the
    /// direction it falls. Reading it as a fall line hands the ring a sample pointing the opposite way to its
    /// neighbours and MANUFACTURES an opposing pair out of one bad sample - a wedge grant, on an overhang, which is
    /// the one place a body most obviously is not being held up. Everything else
    /// is pure scalar arithmetic in a fixed order over the same delegate both heads hold, so a reconcile replay of
    /// this tick reaches the same verdict, and the early exit on the first opposing pair cannot change it because ANY
    /// opposing pair is the answer.</para></summary>
    private static bool OpposingFallLines(float x, float z, float radius, Func<float, float, Vector3> groundNormal)
    {
        Vector2[] offsets = TreadFanOffsets;
        Span<float> fx = stackalloc float[offsets.Length];
        Span<float> fz = stackalloc float[offsets.Length];
        int n = 0;
        foreach (Vector2 off in offsets)
        {
            Vector3 normal = groundNormal(x + off.X * radius, z + off.Y * radius);
            if (normal.Y < 0f) continue;   // an overhang has no fall line to read; see the summary
            float lenSq = normal.X * normal.X + normal.Z * normal.Z;
            if (lenSq <= FaceNormalEpsilonSq) continue;
            float inv = 1f / MathF.Sqrt(lenSq);
            fx[n] = normal.X * inv;
            fz[n] = normal.Z * inv;
            n++;
        }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (fx[i] * fx[j] + fz[i] * fz[j] < WedgeOpposingCos) return true;
        return false;
    }
}
