using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// THE WALL CONTACT: what a horizontal move does when its destination is ground this tick cannot reach. Rule 1 of
// the two the steep-terrain model is built from - the module header of CharacterMovement.Slide.cs states both - and
// the one EVERY horizontal advance in the engine goes through: the ordinary command path (DesiredHorizontalCore),
// the airborne momentum path, and the slide's own resolve. Rule 2 (steep ground grants no traction, so gravity
// decomposes against it and the character rides the fall line) stays there beside the slide.
//
// Split out of CharacterMovement.Slide.cs when #498 grew that file past the file-size ratchet, exactly as
// CharacterMovement.Fluid.cs, CharacterMovement.Momentum.cs and CharacterMovement.Horizontal.cs were split out of
// CharacterMovement.cs: same partial type, same shared private core, one concern each. The geometry both rules read
// (FaceDirection, HeightPlaneNormal) and the reasoning for reading it off the HEIGHTS rather than the normal
// delegate (#468) live in CharacterMovement.Slide.cs, which is where a reader arriving from the slide will look
// for them, and nothing here re-derives any of it.
//
// Everything here is pure scalar arithmetic in a fixed order over the same pure delegates both heads hold, so a
// wall contact replays bit-identically through ClientPrediction.Reconcile, and it carries no state of its own.
public static partial class CharacterMovement
{
    // The float slack on the PROJECTED step's rise allowance in AdvanceWallSlide's anti-tunnel re-test, in metres.
    // Sibling of SlideRiseSlack (CharacterMovement.Slide.cs): same size, same reasoning, a DIFFERENT comparison.
    // They are two symbols rather than one because both are live at once on a sliding tick - that path's reach
    // already carries its own for the DESTINATION test - and one symbol standing for both would read as a single
    // tolerance being spent twice.
    //
    // ON A SLIDING TICK THE TWO COMPOSE, AND THIS IS THE PLACE THAT SAYS SO OUT LOUD. The reach a slide hands down
    // is SlideReach, which is already the slide's own resolved rise PLUS SlideRiseSlack, and the line below adds
    // this one on top of it. So the slide path's projected-step allowance is the slide's rise plus TWO millimetres,
    // not one, and its exposure is correspondingly two millimetres a tick: 0.06 m/s at 30 Hz and 0.24 at 120, twice
    // the figure the destination test carries. That is a composition rather than a bug, and it stays one: the
    // alternative is a conditional that subtracts a slack the caller happens to have included, which would make the
    // allowance depend on which path called and put a magic value in the middle of the one rule all three paths
    // share. The number is stated here instead, so the next reader costs it correctly rather than reading the
    // one-millimetre figure at SlideRiseSlack and assuming it covers the whole tick.
    //
    // WHY THE RE-TEST NEEDS ONE (#498). The projected velocity is the destination height plane's CONTOUR by
    // construction, since that is what removing the into-face component means. So on a planar face the rise it asks
    // for is exactly zero, and which side of the comparison it lands on is decided by rounding. Both sides are
    // differences of world heights (the feet come from the previous tick's clamp, the ask from a fresh sample), so
    // that rounding is proportional to the height magnitude, about 1e-7 of it per operand - a millimetre covers
    // several kilometres of world height, which is orders past anything the fleet authors. Against the #486 reach,
    // which is exactly 0 on every grounded walking tick, an unslacked comparison therefore refuses on noise. It did:
    // measured on Ruinborne's island at 17.31.0, strafing a bank stopped a walker DEAD, 32 of 63 (site, heading)
    // pairs kept under 90 percent of their commanded sideways travel and the worst kept 6 percent, for asks of
    // +0.000 to +0.021 m with eight of ten first blocks at +0.000 or +0.001.
    //
    // WHAT THE EXPOSURE IS, AND WHY THE TERRAIN BOUNDS IT RATHER THAN TIME. Like the slide's, this is a distance
    // per tick, so its worst case is a rate that scales with the tick rate: a millimetre a tick is 0.03 m/s at
    // 30 Hz and 0.12 at 120. To bank it a body must be pressed against a wall contact tick after tick while the
    // contour under it rises by between zero and one millimetre - and on the command path that body is a GROUNDED
    // one, so it creeps only for as long as the ground it creeps onto is still inside its own traction ceiling.
    // Past that the support decision at the END of the same tick takes its footing and the no-traction rule slides
    // it back down everything it gained. The ceiling is the bound, and no amount of holding the stick moves it.
    private const float ProjectedRiseSlack = 1e-3f;

    // How many times AdvanceWallSlide HALVES a refused projected step before refusing outright: SIX, so the ladder is
    // the full step, a half, a quarter, an eighth, a sixteenth, a thirty-second and a sixty-fourth, and the whole
    // cost is at most six extra delegate probe pairs on a CONTACT tick and nothing at all on any other tick.
    //
    // THE ASK IS QUADRATIC IN THE STEP, WHICH IS WHY THE FLOOR IS A FIXED FRACTION AND WHY IT IS THIS DEEP. A bend
    // of radius R in plan makes one tick's projected step ask for G * L^2 / (2R), with G the gradient at the contour
    // and L = speed * dt the step length: the step line is the tangent at the FULL destination, so it cuts inside
    // the contour by the sagitta of a chord, and that grows with the SQUARE of the step. Two consequences, and the
    // first ladder missed both. Shortening to a fraction k does not divide the ask by k, because the line is still
    // aimed at the full destination: it leaves k(2-k) of it, so an eighth rung still asks for 0.234 of the full ask
    // rather than an eighth of it. And L is not a constant of the engine - it is 0.05 m for a walk at 120 Hz and
    // 0.80 m for a run at 15 Hz, a factor of sixteen, so the ask spans a factor of 256 across ordinary configs.
    // Three rungs therefore covered a walk at 30 Hz (bends past about 5 m) and dead-stopped a RUN at the same rate
    // at any bend under about 21 m, which is ordinary gate-contour terrain. That was measured, not argued.
    //
    // WHAT A SIXTY-FOURTH BUYS, so nobody has to re-derive it. The floor rung's ask is k(2-k) = 2/64 = 0.031 of the
    // full one, so the worst FULL ask this ladder can get under is slack / 0.031 = 32 mm of rise, and the terrain
    // that reaches is L up to sqrt(2R * 0.032 / G). At a 5 m bend on 50 degree ground that is a 0.52 m step, which
    // covers a run at 30 Hz (0.40 m) with margin and a walk at 15 Hz likewise. Where the ask is instead a DISCRETE
    // feature the shorter rung does not reach (a lip, a rock, the far side of a walkable boundary) the ladder covers
    // any magnitude at all, because the sixty-fourth lands on ground that never rose.
    //
    // PAST THAT THE WALKER STILL MOVES, WHICH IS WHY A DEEPER FLOOR IS NOT A CHARACTER STANDING STILL. The rung that
    // clears is committed, so the tick that would once have been refused travels L/64 and the ride reads as a crawl
    // through the bend rather than a park - and a crawl is the correct answer for a bend that tight, because the
    // walker genuinely is being aimed into the face. Only an ask no rung can get under refuses, and on a smooth face
    // that means a bend radius of about a centimetre, which is a crease rather than a bend.
    //
    // The direction bias the ladder is compensating
    // for - the projection reads its contour at the DESTINATION rather than at the walker's own column, so it aims
    // a hair into a bending face whatever its length - is #502, and closing that is what would retire this ladder.
    private const int ProjectedStepRungs = 6;

    /// <summary>Advance an XZ position by a horizontal velocity for one tick, WALL-SLIDING off analytic terrain that
    /// the step cannot reach: when the destination's ground normal is steeper than
    /// <see cref="MoveTuning.MaxSlopeRadians"/> AND its ground stands more than <paramref name="reach"/> above the
    /// feet, the move keeps only its along-face component. Shared by the ordinary command path
    /// (<c>DesiredHorizontalCore</c>), the airborne momentum path, and the slide, so the rule cannot come to mean
    /// three different things depending on which one drove the tick.
    /// <para>THE STEEPNESS TEST IS WHAT LEAVES WALKABLE GROUND UNTOUCHED, and it is the whole of the ordinary path's
    /// cost: a fast run up a legal ramp can rise more than a StepHeight in one tick, and treating that as a wall would
    /// turn every steep-but-walkable hill into a fence at high speed. So the reach below is consulted about ONE kind
    /// of ground only - a destination this tick has already been told it cannot stand on.</para>
    /// <para>WHAT THE REACH IS, and why it is never <see cref="MoveTuning.StepHeight"/> (#468, #486). A step is
    /// something FOOTING buys: it is the height a character standing on the ground can lift a foot onto. No tick has
    /// bought a step onto ground past its own traction ceiling, whether or not it started with footing elsewhere, so
    /// the reach is the tick's OWN RESOLVED UPWARD MOTION and nothing else - zero while it falls, and zero while it
    /// walks on the flat. See <see cref="NoFootingReach"/> for the rule and for the two things a StepHeight reach
    /// cost: a two-tick limit cycle that walked up a 74 degree sea cliff at 2.5 m/s (#468), and the cliff-toe bounce
    /// that flickered a walking character through the falling pose at every steep-face base (#486). Both are the same
    /// shape - the clamp seated the capsule onto every column the admission let it reach, and the admission asked only
    /// whether that column was within a step.</para>
    /// <para>THE PROJECTION IS THE WHOLE FIX FOR THE REPORTED FEEL BUG. The retired gate refused the entire move the
    /// moment any of it pointed at a face, so holding a direction 45 degrees into a cliff while jumping lost the
    /// lateral half too - an invisible wall eating air control. Removing only the into-face component leaves the
    /// along-face travel exactly what it would have been with no face there at all.</para>
    /// <para>ANTI-TUNNEL, AND WHY IT SHORTENS BEFORE IT REFUSES (#498). The projected move is re-tested against the
    /// same two conditions, and one that still lands in a wall is SHORTENED rather than dropped: retested by repeated
    /// halving down to a sixty-fourth of itself (<see cref="ProjectedStepRungs"/>), committing the first endpoint
    /// that clears, and refused outright only when the sixty-fourth does not. Every committed endpoint has been
    /// tested at the point it actually lands on, so the property the refusal gate used to carry alone is untouched:
    /// an XZ can never be committed under terrain and left for a later ground clamp to pop the capsule up a
    /// cliff.</para>
    /// <para>WHAT ACTUALLY REACHES THE REFUSAL, since the text here used to claim it was a concave corner and give a
    /// reason that does not hold. The reason offered was that every point along the step is inside both faces, and
    /// that is false in general: the step is the CONTOUR of the destination's height plane, and on a crease that
    /// plane is read over a capsule-wide stencil straddling both faces, so where the step goes is a question about
    /// that plane rather than about either face. On the symmetric case - a walk driven straight into the corner - the
    /// plane is the exact bisector, the projection removes the WHOLE velocity, and every rung lands on the walker's
    /// own column, so the ladder leaves through the not-steep branch at rung 0 and this refusal is never reached at
    /// all. What does reach it is a projection that is non-zero and aimed at ground that is past the gate and rising
    /// at every rung down to a sixty-fourth: on a smooth face that needs a bend radius of about a centimetre, and
    /// the shape that produces it is a crease with the along-face direction running INTO it (a rising gully, an
    /// inside corner met off-axis). Both are pinned in WallContactTangentialTravelTests, and the second one is
    /// checked by mutation, because a refusal nothing exercises is a refusal nobody can delete safely.</para>
    /// <para>THE OLD TEXT HERE SAID THE REFUSAL ONLY HAPPENED IN A CONCAVE CORNER, AND THAT WAS MEASURABLY FALSE.
    /// The projected velocity is the destination height plane's CONTOUR, which is level by construction, so on
    /// ordinary open terrain what the re-test asks for is float rounding plus whatever the surface curves through
    /// under one tick of travel. Against the #486 reach, which is exactly 0 on a grounded walking tick, ANY ask at
    /// all was a refusal of the WHOLE move - and since the geometry is unchanged the next tick, a permanent one.
    /// On Ruinborne's island that stopped a walker dead while it strafed a bank at ten degrees off tangential, with
    /// no footing flip and no slide tick anywhere in the trace. <see cref="ProjectedRiseSlack"/> covers the rounding,
    /// the shortening covers the curvature, and a face that genuinely climbs across the whole of the shortest rung
    /// still refuses.</para>
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

        // A MOVE ENTIRELY INTO THE FACE HAS NO LADDER TO WALK. When the projection is exactly the zero vector every
        // rung's endpoint is (x, z) itself, so all seven probe the SAME point and all three ways out of the loop
        // return that same point: the not-steep branch, the under-allowance branch and the refusal all agree, and
        // they agree with the answer here. So the result is identical (up to the sign of a zero at the world
        // origin, where the old path's x + 0 normalized -0.0 to +0.0) and the probes are simply not made. This is
        // the head-on case (a walk driven straight into a wall, and the degenerate FaceDirection fallback where the
        // move direction stands in as the face's own), which is common enough to be worth a compare now that the
        // ladder is seven rungs deep rather than four.
        if (sx == 0f && sz == 0f) return (x, z);

        // The projected step, tested at its full length first and then down the shortening ladder. The allowance is
        // the tick's own reach plus the contour slack, and it is the SAME allowance at every rung: shortening moves
        // the endpoint, never the bar it has to clear, which is what keeps the #468 invariant exact rather than
        // approximately preserved. `scale` is exactly 1 on the first pass, so an admitted full-length step is
        // byte-identical to every release since the wall slide shipped.
        float allowance = reach + ProjectedRiseSlack;
        float scale = 1f;
        for (int rung = 0; rung <= ProjectedStepRungs; rung++, scale *= 0.5f)
        {
            float tx = x + sx * dt * scale;
            float tz = z + sz * dt * scale;
            // Same fixed order as the destination test above: the normal first, its height only inside the steep
            // branch, so a rung that lands on walkable ground costs one delegate call and not two.
            Vector3 tangentNormal = groundNormal(tx, tz);
            if (!IsSteepGround(tangentNormal, gate)) return (tx, tz);
            if (groundHeight(tx, tz) - feetY <= allowance) return (tx, tz);
        }
        return (x, z);
    }

    /// <summary>The height a tick may find its destination ground standing above its feet and still be SEATED on it
    /// rather than stopped by it (<see cref="AdvanceWallSlide"/>'s <c>reach</c>), for the paths whose horizontal is
    /// arbitrary with respect to the surface: the command path and the airborne momentum path. The name is about the
    /// DESTINATION rather than the tick: since #486 a tick that starts with footing and one that does not get the same
    /// number here, because the only ground this is ever read about is ground no tick has footing on.
    ///
    /// <para>A FOOTED TICK USED TO GET <see cref="MoveTuning.StepHeight"/> HERE, AND THAT WAS THE CLIFF-TOE BOUNCE
    /// (#486). The argument for it was that a step is what footing buys, which is a good argument about walkable
    /// ground and a wrong one here, because this number is only ever read about ground the tick has ALREADY been told
    /// it cannot stand on: <see cref="AdvanceWallSlide"/> consults the reach only after its steepness test says the
    /// destination is past this tick's traction ceiling. So a StepHeight admission let a walking character seat itself
    /// onto the toe of a cliff, which the support decision at the end of the very same tick then refused - costing it
    /// its footing, flickering the falling pose, and sliding it back onto the flat, from where it walked in again.
    /// Measured on a 60 degree face at the shipped tuning: 112 footing flips and 539 airborne ticks out of 600 at
    /// 30 Hz, and at 120 Hz and above no flicker at all but a permanent slide parked against the toe (461 airborne
    /// ticks out of 480). The admission and the support decision were reading the same ground and reaching opposite
    /// verdicts, and this is where they are made to agree.</para>
    ///
    /// <para>SO THE STEP SURVIVES EXACTLY WHERE IT IS A STEP. Ground at or under the tick's ceiling - walkable ground,
    /// and the band ground <see cref="TractionGate"/> holds a standing character on - never reaches this number at
    /// all, because <see cref="AdvanceWallSlide"/> has already returned on the steepness test. A step-up, a step-down,
    /// a stair glide and a walk onto a riser are byte-identical to every release since the wall slide shipped, and a
    /// fast run up a legal ramp is still not fenced by the height it gains in one tick. DESCENT onto steep ground is
    /// untouched too: a destination below the feet rises by a negative amount, which is admitted at any reach, so
    /// cresting onto a steep face from above still enters a slide, as does falling onto one.</para>
    ///
    /// <para>EVERY TICK NOW GETS ITS OWN RESOLVED UPWARD MOTION, and nothing else: <c>max(0, vVel * dt)</c>
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
    /// or falling past one keeps everything it had. Only the part of the move that was buying altitude is removed.
    /// That last sentence is a PROMISE, and between 17.29.0 and 17.31.0 the anti-tunnel re-test above quietly broke
    /// it on open terrain - see its own paragraph and #498. It is true again.</para>
    ///
    /// <para>THE SLIDING TICK TAKES THE SAME RULE through <see cref="SlideReach"/>, which is this one plus a float
    /// slack it needs and this one does not - see there.</para></summary>
    private static float NoFootingReach(in MoveState state, in MoveTuning tuning, float dt)
        => MathF.Max(0f, FallIntegrate(state.VerticalVelocity, tuning, dt) * dt);
}
