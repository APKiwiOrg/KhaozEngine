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

    // How much wider than the ordinary height stencil AdvanceWallSlide reads its SECOND face direction over: FIVE
    // times it, so 2.0 m at the fleet's 0.4 m capsule radius. A multiple rather than a metre literal, because the
    // ordinary read is the capsule's own width (StencilRadius) and a fixed metre span would mean something different
    // for a giant than for a rat.
    //
    // WHAT A 0.4 M FACE DIRECTION COSTS (#501). A central difference at the capsule radius describes a 0.4 m FACET of
    // a bank rather than the bank, and the along-face speed a facet leaves is the commanded speed times the sine of
    // the angle between the command and that facet's outward. So where the facet's outward lands anti-parallel to the
    // command the projection keeps NOTHING - and that point is a stable attractor, because the walker's own along-face
    // drift carries it there while the drift rate vanishes on arrival. It arrives, it stops, the geometry under it
    // never changes, and every later tick sheds the same move for the same reason. On Ruinborne's island the 0.4 m
    // outward read 180.000 degrees off the command with 0.002 of the speed surviving, and the worst censused ride
    // made 0.028 of its commanded travel with 248 of 300 ticks dead. What the player sees is a walker that is fully
    // footed and upright, holding a direction, going nowhere: no falling pose, no slide, no refusal.
    //
    // WHY FIVE. Two measurements over the noisy-bank fixture in WallFaceAttractorTests, and they agree. The first
    // sweeps the stencil against the WORST face direction any position along the traverse produces, as the fraction
    // of the commanded speed a projection onto it would keep there:
    //
    //     stencil    0.4 m    0.8 m    1.2 m    1.6 m    2.0 m    2.4 m    3.2 m    4.0 m
    //     lean 68    0.003    0.270    0.296    0.302    0.334    0.341    0.355    0.353
    //     lean 75    0.001    0.150    0.177    0.184    0.216    0.224    0.239    0.236
    //     lean 79    0.001    0.081    0.108    0.115    0.148    0.155    0.171    0.168
    //
    // The 0.4 m column reaches zero at every heading, which is the whole bug: a fallback whose own direction has a
    // zero along the traverse only MOVES the attractor, and the census behind #501 says so in as many words. Two and
    // three times the stencil are the ones that matter to rule out, and they are ruled out by their own worst case
    // sitting at or barely above WideFaceKeepFraction: a fallback direction that is itself inside the band that
    // triggered the fallback is not a second opinion. Five clears that band by half again.
    //
    // The second measurement is the rides themselves, as the worst of the twelve, in fractions of commanded travel:
    //
    //     stencil            2x       3x       4x       5x       6x       8x      10x
    //     worst ride       0.28     0.38     0.86     0.86     0.83     0.83     0.44
    //
    // Four and five are the plateau. Ten collapses a ride because a 4 m read spans more bank than bank, and the
    // agreement between the two measurements is what picks five over four: at four the wide direction's own worst
    // keep is 0.115 against a 0.10 trigger, and at five it is 0.148. Ruinborne's real island agrees at the same
    // widths: at its worst censused site the 0.4 m read keeps 0.002 while 1 m keeps 0.286, 2 m 0.384 and 4 m 0.413.
    //
    // WHAT IT COSTS. Four more height probes on a CONTACT tick and nothing at all on any other tick, since the wide
    // read is made only after the steepness and reach tests have already said this destination is a wall. Against the
    // ladder's up-to-seven probe pairs on the same tick that is a small addition to a path that is already the rare
    // one.
    private const float WideFaceStencilScale = 5f;

    // The first of the two things that make AdvanceWallSlide stop believing its narrow face: the kept along-face
    // speed, as a fraction of the commanded speed, at or under which the facet is not describing a wall. A TENTH.
    // Below this the projection is shedding more than nine tenths of the move, which is not what a wall does to a
    // command aimed along it.
    //
    // WHERE THE NUMBER COMES FROM. The 266-row Ruinborne census behind #501 measured two populations rather than one
    // continuum: every dead-stop row it found kept 0.002 or less, and the healthy contact rows beside them kept 0.2
    // and up. A tenth sits in the gap, fifty times the dead rows and half the lowest healthy one, so it is a line
    // drawn through empty space rather than through either population. The census warns specifically that a 0.25 line
    // does NOT have that property, since it cuts a continuum of real contacts. Swept on the fixture, as the worst of
    // the twelve rides in fractions of commanded travel:
    //
    //     trigger        0.02     0.05     0.10     0.15     0.25
    //     worst ride     0.69     0.82     0.86     0.08     0.08
    //
    // The cliff on the high side is the control heading, the one that never had the attractor: at 0.15 and 0.25 the
    // fallback starts firing on healthy contacts and makes that ride worse than doing nothing. The slope on the low
    // side is the fallback engaging too late to catch the drift. A tenth is not a compromise between the two, it is
    // the measured plateau.
    //
    // THE ENGINE'S OWN CREASES LIVE ABOVE THIS LINE, WHICH IS THE REASON IT IS A LINE AT ALL RATHER THAN NO TEST.
    // A rising gully met off-axis keeps 0.28 of its command through the narrow face, and its narrow face is the
    // TRUTH there: a crease is a real feature at capsule scale, and the wide read averages its two walls to almost
    // nothing, leaving a projection small enough for the ladder's shortest rung to squeeze a step onto the far wall.
    // That is the #468 shape the refusal exists to forbid, and it is what taking the wide face UNCONDITIONALLY
    // reintroduced: measured, A_rising_gully_crease_is_refused goes to 2.4 mm of climb and 150 of 300 ticks airborne,
    // and it passes at every trigger tried here. So the narrow face keeps its job wherever it is still doing it.
    private const float WideFaceKeepFraction = 0.1f;

    // The second of the two, and the one that keeps the first from becoming a park of its own. THE THRESHOLD ABOVE IS
    // A SWITCHING SURFACE, AND THE NARROW FIELD DRIVES THE WALKER STRAIGHT ONTO IT: inside the region the wide face
    // carries the walker along the bank and out, while just outside it the narrow face is PAST anti-parallel, so its
    // along-face travel points back toward the attractor. Two candidate motions that oppose across a boundary make
    // that boundary a park, and the walker rides it instead of the attractor - the fallback having relocated the bug
    // rather than removed it. Measured on the fixture at a lean of 79 degrees, walking at 30 Hz: 0.31 of the commanded
    // travel with the fallback engaging and disengaging on 138 of 300 ticks, against 0.29 with no fallback at all.
    //
    // So the wide face is ALSO taken whenever the two candidate travels oppose by more than a right angle, which is
    // what this constant compares against (zero: a plain sign test on their dot product). That cannot leave a park
    // anywhere, and the argument is short. On the keep boundary the narrow face is only ever used when it agrees with
    // the wide one to within a right angle, so both sides of that boundary travel the same way. On the new boundary
    // the two are exactly perpendicular, so again neither side opposes the other. There is no surface left where the
    // two available motions point against each other, which is the only thing that can hold a walker still. Measured:
    // the same 79 degree ride goes to 1.04 of its commanded travel, and the worst of the twelve rides goes from 0.31
    // to 0.86.
    //
    // HYSTERESIS WOULD BE THE TEXTBOOK ANSWER TO A CHATTERING THRESHOLD AND IS NOT AVAILABLE HERE. It needs carried
    // state, and this file has none by construction, because that is what makes a wall contact replay bit-identically
    // through ClientPrediction.Reconcile. A geometric test that cannot chatter is the version of the same idea that
    // costs no state.
    private const float WideFaceOpposedDot = 0f;

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
    /// <para>WHEN THE FACE IS READ WIDE, AND WHY ONLY THE DIRECTION EVER IS (#501). The face comes from a central
    /// difference at the capsule radius, which on metre-wavelength micro-geometry describes a 0.4 m FACET rather than
    /// the bank - and where that facet's outward lands anti-parallel to the command, the projection keeps nothing at
    /// all. That is a stable fixed point rather than a coincidence, since the walker's own drift carries it in while
    /// the drift rate vanishes as it arrives, so it reads as a footed, upright walker holding a direction and going
    /// nowhere. So the face is re-read over <see cref="WideFaceStencilScale"/> times the stencil, which has no zero
    /// along a traverse to fall into, and the ORIGINAL velocity is re-projected onto it whenever the narrow face
    /// either keeps less than <see cref="WideFaceKeepFraction"/> of the command or wants to travel against the wide
    /// face (<see cref="WideFaceOpposedDot"/>). Everything else keeps the narrow face, which is what leaves a crease
    /// alone. The steepness test above and the ladder below still read the ordinary narrow values throughout, so a
    /// wider direction can change where the walker goes and never what altitude it may take.</para>
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
        //
        // AND IT IS READ WIDE (#501), over WideFaceStencilScale times the stencil every other height-plane read uses.
        // At the capsule's own width the direction is a 0.4 m facet of the surface rather than the surface, and where
        // that facet's outward drifts anti-parallel to the command the projection below keeps nothing - a stable
        // attractor a walker falls into and cannot leave. The constant carries the measured stencil profile, the
        // reason the width is five and not two or ten, and the reason the wide read is taken on every contact rather
        // than as a fallback. What the wide read is allowed to decide is only the DIRECTION: the steepness test above
        // and the ladder below still read the ordinary narrow heights, which is what keeps #468 exact - see the
        // ladder's own note.
        (float fx, float fz) = FaceDirection(HeightPlaneNormal(nx, nz, StencilRadius(tuning), groundHeight, destNormal), velocity);
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

        // A MOVE ENTIRELY INTO THE FACE HAS NO LADDER TO WALK, so an exactly zero projection short-circuits. Every
        // rung's endpoint would be (x, z) itself, so all seven probe the SAME point and all three ways out of the
        // loop return it: the not-steep branch, the under-allowance branch and the refusal all agree, and they agree
        // with the answer here. So the result is identical (up to the sign of a zero at the world origin, where the
        // old path's x + 0 normalized -0.0 to +0.0) and the probes are simply not made. Two shapes reach it, and
        // both mean there is nothing along this face to keep: a move driven straight at the face, and the degenerate
        // FaceDirection fallback where the move direction stands in as the face's own (see there).
        //
        // THE BLOCK ABOVE USED TO CALL THIS CASE COMMON, AND THAT WAS THE #501 MISREADING. What was common on real
        // terrain was a NEAR-zero projection, and near-zero is not a quiet variant of head-on: it is a bank whose
        // 0.4 m facet has drifted anti-parallel to the command, which is a face direction read at the wrong scale
        // rather than a wall. Exact zero still means what it says and still short-circuits. Near-zero is now the
        // wide read's business, two lines down.
        if (sx == 0f && sz == 0f) return (x, z);

        // THE SECOND, WIDER FACE, AND THE TWO THINGS THAT MAKE THE MOVE TAKE IT INSTEAD (#501). The narrow projection
        // above stands unless the facet it came from is provably not describing the wall, and there are exactly two
        // ways to be sure of that from this tick alone:
        //
        //   IT KEEPS ALMOST NOTHING (WideFaceKeepFraction). A face that sheds more than nine tenths of a command
        //   aimed along it is anti-parallel to that command, which is the attractor itself.
        //
        //   IT WANTS TO TRAVEL AGAINST THE BANK (WideFaceOpposedDot). Past the anti-parallel point the facet's
        //   along-face direction FLIPS, so the narrow answer is to walk back the way the wide face says the surface
        //   runs. Without this clause the first one is a trap: the walker is driven onto the keep boundary from
        //   outside and pushed off it from inside, and parks on the boundary instead of on the attractor. Both
        //   constants carry the measurement.
        //
        // Anything else keeps the narrow face, which is what leaves the engine's creases alone: at a rising gully met
        // off-axis the narrow face is the truth, it keeps 0.28 of the command, and the two travels agree.
        (float wx, float wz) = FaceDirection(
            HeightPlaneNormal(nx, nz, StencilRadius(tuning) * WideFaceStencilScale, groundHeight, destNormal),
            velocity);
        float wideInto = velocity.X * wx + velocity.Y * wz;
        float wsx = velocity.X - wideInto * wx;
        float wsz = velocity.Y - wideInto * wz;
        float speedSq = velocity.X * velocity.X + velocity.Y * velocity.Y;
        if (sx * sx + sz * sz < WideFaceKeepFraction * WideFaceKeepFraction * speedSq
            || sx * wsx + sz * wsz < WideFaceOpposedDot)
        {
            sx = wsx;
            sz = wsz;
            // The wide face can be head-on too, and then the paragraph above it applies to it word for word.
            if (sx == 0f && sz == 0f) return (x, z);
        }

        // The projected step, tested at its full length first and then down the shortening ladder. The allowance is
        // the tick's own reach plus the contour slack, and it is the SAME allowance at every rung: shortening moves
        // the endpoint, never the bar it has to clear, which is what keeps the #468 invariant exact rather than
        // approximately preserved. `scale` is exactly 1 on the first pass, so an admitted full-length step is
        // byte-identical to every release since the wall slide shipped.
        //
        // THIS LADDER IS WHAT MAKES A WIDE FACE DIRECTION SAFE (#501 leaning on #468). The anti-tunnel invariant here
        // is DIRECTION-INDEPENDENT by construction: every endpoint committed below has passed the same steepness and
        // height tests, against the delegates' own REAL values at the point it actually lands on, whatever direction
        // pointed it there. So the face above chooses only WHERE along the surface the walker goes, never WHAT
        // ALTITUDE it may take, and a coarse direction aimed at ground that climbs is refused by exactly the rungs
        // that refuse a fine one. That is also why the #468 mixed-surface scar - the direction and the heights must
        // describe the same surface - does not bar reading the direction wide: the heights that admit or refuse are
        // still the narrow, real ones. The SLIDE has no such separation, since it commits the drop its plane says,
        // which is why nothing in #501 changes what ResolveSlide reads.
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
