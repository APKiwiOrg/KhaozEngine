using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// LEVELLING A WALL CONTACT'S SURVIVING TRAVEL ON THE COLUMN THE WALKER IS STANDING ON (#502): the one correction
// AdvanceWallSlide makes to what its face hands back, its two gates, its sign, and the measurements all four are
// chosen on.
//
// Split out of CharacterMovement.WallContact.cs when the round-two sign gate grew that file past the file-size
// ratchet, exactly as CharacterMovement.WallContact.cs was itself split out of CharacterMovement.Slide.cs when
// #498 grew THAT one: same partial type, same shared private core, one concern each. The wall contact rule lives
// next door and is where a reader arrives from, the geometry both rules read (FaceDirection, HeightPlaneNormal)
// lives in CharacterMovement.Slide.cs, and nothing here re-derives any of it.
//
// Everything here is pure scalar arithmetic in a fixed order over quantities the caller already holds, so it
// carries no state and a wall contact still replays bit-identically through ClientPrediction.Reconcile.
public static partial class CharacterMovement
{
    // WHEN A WALL CONTACT IS ALLOWED TO LEVEL ITS PROJECTED TRAVEL AGAINST THE GROUND THE WALKER IS STANDING ON
    // (#502), the first of three: how close the walker's own column has to stand to the tick's own traction gate,
    // in radians, for that ground to count as the FACE THAT STOPPED IT rather than as somewhere else it happens
    // to be. Two degrees.
    //
    // WHY THERE IS A TEST HERE AT ALL. A walker pinned by a wall contact is resting ON its ceiling, so the plane
    // under it IS the face and levelling against it is exactly right. A walker on the walkable floor of a trough
    // with a steep flank one step ahead is not: the plane under IT has an up-grade of its own, and levelling
    // against that would forbid a character from walking up ground it can simply walk. Measured over the census,
    // levelling with no such test costs 460 top-tier regressions against the pre-#501 reference, with rides on
    // every trough shape turned around and driven backwards along the axis.
    //
    // WHY IT IS A BAND RATHER THAN THE GATE ITSELF. A wall contact fires when the DESTINATION is past the gate,
    // so the walker's own column is one step back down the surface and is therefore always a little UNDER it - by
    // the surface's gradient ramp times the step's radial component, which is 0.7 degrees on the fixtures' bank
    // and up to about 3 on the noisy one. Testing against the bare gate finds the walker past it almost never:
    // measured, a band of zero leaves the bank ride exactly as the pre-#502 build has it, 541 footing flips and
    // the 8 m run at 15 Hz parked for 149 of its 150 ticks.
    //
    // THE BAND IS ONE-SIDED, AND THE CODE ALWAYS WAS: THIS IS THE PROSE CATCHING UP (round two). The test below is
    // `IsSteepGround(ownPlane, gate - band)`, which asks whether the own column is steeper than the gate LESS the
    // band and has no upper bound at all, so a walker resting on ground far steeper than its own gate passes it.
    // An earlier draft of this block said "within two degrees of the gate", which describes a two-sided window
    // that has never been coded. The one-sided form is the correct one and is kept deliberately: a walker standing
    // on ground PAST its traction ceiling is even more certainly resting on the face that stopped it than one
    // standing a hair under it, and the band exists to catch the near-miss below the gate rather than to exclude
    // anything above it. The sweep below moved only the lower edge for that reason.
    //
    // AND THE SUBTRACTION IS CLAMPED AT ZERO, which is a consumer-safety statement rather than a tuning one.
    // `MoveTuning.MaxSlopeRadians` is a consumer's number and nothing stops one setting it under two degrees;
    // there `gate - band` goes negative, `acos(ny) > negative` is true for every surface including a level one,
    // and the band would silently stop testing anything at all. Clamped, a level floor reads acos 0, which is not
    // greater than 0, and the gate still refuses - so the rule degrades to "no levelling" rather than to
    // "levelling everywhere" on a degenerate tuning. It binds on no tuning the fleet ships.
    //
    // WHERE THE TWO DEGREES COMES FROM: the whole #498 and #501 referee set, run at every band from a quarter of
    // a degree to eight, at five keeps across this rule's other constant, with the four improvement-direction
    // park rows excluded because they are what the fix is for.
    //
    //     band          0.25    0.5    0.75      1      2      3      4      6      8
    //     referees       RED    RED     RED  green  green  green    RED    RED    RED
    //     bank fixed     yes    yes     yes    yes    yes    yes    yes    yes    yes
    //
    // AND ITS TWO EDGES ARE DIFFERENT KINDS OF EDGE, which is the part worth carrying forward. The UPPER one is
    // real: at six and eight the band reaches trough columns, and WallFaceTroughTests reds on its axis rows and
    // its flank window - the walkable up-grade being eaten, which is the failure the band exists to prevent. The
    // LOWER one is not: at a quarter, a half and three quarters of a degree the only thing that reds is
    // WallFaceAttractorTests' noisy-bank rides, whose efficiency moves up to 0.049 for a HUNDREDTH of a degree
    // of meaningless rotation (the runnable control lives in WallContactOwnColumnTests, the skipped-by-default
    // perturbation test). So the band is bounded above by geometry
    // and below by noise, and two degrees is the middle of the window both leave green.
    private const float OwnColumnCeilingBand = 2f * MathF.PI / 180f;

    // WHEN A WALL CONTACT IS ALLOWED TO LEVEL (#502), the second of three: how much of its projected travel has to
    // SURVIVE the levelling for the levelled version to be taken.
    //
    // WHAT THE NUMBER IS, GEOMETRICALLY, WHICH IS THE ONLY WAY TO SET IT HONESTLY. The projected travel is
    // perpendicular to the DESTINATION face's outward by construction, and levelling makes it perpendicular to
    // the WALKER's own. So what survives is exactly the cosine of the angle between those two outwards, and this
    // constant is that cosine: 0.988 is 8.9 degrees. Which makes it a statement about terrain rather than about
    // floats - one tick's step subtends L/R radians of a bend of radius R in plan, so the rule corrects bends down
    // to R = L / 0.1552 = 6.4 L. At Ruinborne's 30 Hz that is a 1.3 m bend at a walk and a 2.6 m bend at a run,
    // and at the 15 Hz run the fixtures sweep it is a 5.2 m bend. Tighter than that the walker keeps exactly the
    // behaviour it had before this rule existed, which is the #498 ladder shortening its step.
    //
    // THE COMPARISON IS BLIND TO THE SIGN OF THE ANGLE, AND SAYING SO IS WHAT KEEPS IT HONEST (round two). What
    // the arithmetic below tests is `|s|^2 - ownInto^2 >= keep^2 |s|^2`, and `ownInto` enters SQUARED, so the test
    // is `|cos(angle)| <= sqrt(1 - keep^2)` - an ABSOLUTE cosine. The levelled vector is likewise invariant under
    // negating the own-column outward, since it removes a projection onto a direction rather than a signed amount
    // along one. So this gate cannot tell a bend that turns one way from a bend that turns the other, it was never
    // able to, and nothing about it should be read as choosing a direction. That is exactly why a SEPARATE sign
    // test is needed and why round one shipped without one: the two gates below answer different questions, and
    // this one only ever answers "are these two reads describing one surface".
    //
    // WHERE THE NUMBER COMES FROM. Swept over the 3824-ride census (six troughs at one-degree leans from -75 to
    // +75 plus the noisy bank from 40 to 89, walk and run at 15 and 30 Hz) against the pre-#501 reference by the
    // #501 arc's own criterion, and over the sixty rides of the WallContactTangentialTravelTests bank sweep at
    // the same time, since one of those two is what the rule is FOR and the other is what it must not cost. At
    // the shipped band of two degrees:
    //
    //     keep          0.980  0.982  0.985  0.988  0.990  0.992  0.994  0.995
    //     regr >0.75       14     13     12      9      9      4      4      3
    //     regr >0.30      172    174    172    167    169    161    160    159
    //     gains >0.75       9     11     11     11     11      8     12      9
    //     referees        RED  green  green  green  green  green      -      -
    //     bank flips        2      2      2      2      2      2      2      2
    //     bank stall        0      0      0      0      0      0      0    142
    //     bank floor    0.999  0.999  0.999  0.999  0.999  0.999  0.999  0.047
    //
    // THE HIGH SIDE IS A CLIFF AND THE LOW SIDE IS A SLOPE, which is the same shape #501's own two constants
    // have. At 0.995 the rule stops reaching the 8 m bend at a 0.80 m step - 5.71 degrees of bend per step is
    // exactly what 0.995 excludes - and that whole ride goes back to the park the #498 ladder leaves it in, 142
    // of its 150 ticks stalled. Below 0.982 nothing about the mechanism breaks, the referee set simply starts
    // reding as the rule corrects disagreements that are terrain rather than a bend. 0.988 is the middle of the
    // window the referees leave green, with three thousandths of margin at each end of it and seven to the cliff.
    //
    // THAT TABLE WAS SWEPT WITHOUT THE SIGN GATE BELOW AND IS KEPT RATHER THAN RE-SWEPT, WHICH IS A DELIBERATE
    // CHOICE WORTH NAMING. The sign gate strictly SHRINKS the set of ticks this keep is consulted on (it is an
    // AND, and it fires first), so every referee the table records as green is green under a rule that does less,
    // and the two cliffs the columns are chosen on are properties of the geometry at the ticks that still level.
    // What the table can no longer be read as is a census count for the shipped build: those are re-taken under
    // the sign gate and are in CHANGELOG.md's own #502 entry.
    private const float OwnColumnLevelKeep = 0.988f;

    /// <summary>The #502 correction itself: <see cref="AdvanceWallSlide"/>'s surviving projected travel
    /// <c>(sx, sz)</c>, made LEVEL on the height plane under the walker by removing its component along that
    /// plane's own outward <c>(ox, oz)</c> - or handed straight back where the correction is not this rule's to
    /// make. Pure arithmetic on quantities the caller already holds: the four height probes that read the own
    /// plane are the caller's, and no delegate is touched here.
    ///
    /// <para>WHY THERE IS A CORRECTION AT ALL. The face says which component of the move the wall eats. It does
    /// NOT say what altitude the survivor takes, and on a face that bends in plan it quietly buys some: the
    /// contour it hands back is level on the plane at the DESTINATION, and a step along that line taken from the
    /// walker's own column cuts inside the contour by the sagitta of a chord, so it climbs by <c>G L^2 / 2R</c>
    /// whatever its length. A walker leaning on a bank is resting exactly ON its traction ceiling, so the only
    /// endpoints that climb at all are past it: the ladder commits the longest one inside the allowance, the
    /// support decision at the end of the same tick reads ground past the ceiling and slides the walker back
    /// down, it walks in again, and the ride is a slow oscillation across its own ceiling. That is what the
    /// player reports as getting stuck sprinting alongside a bank and as the falling pose flickering on and off
    /// while walking one.</para>
    ///
    /// <para>WHY THE WALKER'S OWN COLUMN AND NOT SOME POINT ALONG THE STEP, measured from the geometry rather
    /// than argued. One tick's step from the gate contour of a circular bank of radius R, along the contour of
    /// the plane read at a fraction m of the way to the destination, at a rung fraction k:</para>
    /// <code>
    ///     anchor m    k = 1          k = 1/2        k = 1/8        k = 1/64
    ///     0.0         -G L^2 / 2R    -k^2 of it     -k^2 of it     -k^2 of it     DOWNHILL at every rung
    ///     0.5          0             +0.25 of it    +0.11 of it    +0.015 of it   worse as it shortens
    ///     1.0         +G L^2 / 2R    +0.75 of it    +0.23 of it    +0.031 of it   UPHILL at every rung
    /// </code>
    /// <para>The midpoint cancels the climb at full length and is still declined, because its ask gets WORSE as
    /// the ladder shortens: the step line stays aimed at the full step's midpoint, so a half rung asks for a
    /// quarter of a figure the full rung asked nothing for, and a ladder whose rungs are not monotone is one
    /// nobody can reason about. The walker's own column descends by the sagitta at every rung and is quadratic in
    /// the rung rather than k(2-k), so where a shape does make the ask positive the ladder underneath is 128
    /// times more effective against it. WallContactOwnColumnTests carries this table as a fixture.</para>
    ///
    /// <para>A NAIVE RE-ANCHOR - READING THE FACE ITSELF AT THE WALKER'S COLUMN - WAS MEASURED AND IS DEAD, and
    /// this is the paragraph that stops the next round rediscovering it. The walker's own column is not always
    /// part of the face that stopped it. On the rising gully in WallContactTangentialTravelTests the walker
    /// stands in the crease, where the capsule-wide stencil straddles both walls and cancels them, so the plane
    /// there is the FLOOR's: its outward points down-gully, and projecting onto its contour removes the up-gully
    /// velocity and keeps the into-wall component. Measured, that turns the anti-tunnel refusal into a seat -
    /// 9.4 mm of climb and 150 of 300 ticks airborne against a fixture that pins 0 and 0. Reading BOTH the narrow
    /// and the wide face there is the same failure (6.5 mm, 150 airborne, and the walker driven backwards down
    /// the gully), and the midpoint is too (2.1 mm, 150 airborne). Levelling is not re-anchoring: it keeps the
    /// destination's answer about WHICH WAY THE WALL PUSHES and corrects only the altitude that answer was
    /// buying, so the gully still refuses at every rung.</para>
    ///
    /// <para>NOTHING HERE IS A SECOND OPINION ABOUT ALTITUDE EITHER. Like the wide read beside it, this decides
    /// only WHERE along the surface the walker is pointed: the ladder underneath tests whatever it is handed
    /// against the delegates' own real heights at the point it actually lands on, and refuses it there. The #468
    /// mixed-surface rule is therefore kept exactly, and it is worth naming which surface each quantity comes
    /// from, because the mix is the thing that file's scar is about. The DIRECTION removed by the wall is the
    /// destination column's height plane. The DIRECTION removed by this correction is the walker's own column's
    /// height plane. The HEIGHTS that admit or refuse any of it are the delegate's own, read at the endpoint each
    /// rung actually lands on. All three are the same height field read at three points, never a normal delegate
    /// and never an interpolation, so no quantity here describes a surface a different quantity is about to be
    /// compared against.</para>
    ///
    /// <para>A ZERO <c>(ox, oz)</c> IS THE CALLER SAYING "NOT THIS TICK", AND IT NEEDS NO BRANCH OF ITS OWN. The
    /// caller hands back the zero vector when the own column fails <see cref="OwnColumnCeilingBand"/>, and
    /// <c>FaceDirection</c> hands back the same zero vector for a level own column (it is passed a ZERO velocity
    /// on purpose, so its degenerate branch returns zero instead of standing the movement direction in as the
    /// face's own). On that vector <c>ownInto</c> is exactly 0, the sign gate below refuses, and the travel is
    /// returned untouched - so a walker at the toe of a cliff on flat ground keeps the answer it had before this
    /// rule existed, however the band happens to fall.</para>
    ///
    /// <para>Squared throughout, so the keep costs no square root, exactly as the #501 comparison does.</para>
    /// </summary>
    private static (float x, float z) LevelOnOwnColumn(float sx, float sz, float ox, float oz)
    {
        float ownInto = sx * ox + sz * oz;

        // THE THIRD GATE, AND THE ONE ROUND ONE SHIPPED WITHOUT: THE LEVELLING MAY ONLY EVER REMOVE RISE, NEVER
        // ADD IT (#502 round two). `ownInto` is the travel's component along the own plane's own OUTWARD, which
        // is its DOWNHILL direction, so to first order in the step the un-levelled travel's rise over one tick is
        // `-G * ownInto * dt` and the levelled one's is exactly zero. Levelling therefore lowers what the step
        // asks the surface for when `ownInto` is negative and RAISES it when `ownInto` is positive, by the same
        // sagitta either way. Nothing above discriminates: the keep is an absolute cosine (see the constant) and
        // the band is a steepness test, so without this line the rule applies the same correction to both signs
        // and is right on exactly one of them.
        //
        // WHICH SIGN A SHAPE HANDS IT IS DECIDED BY WHICH WAY THE FACE BENDS IN PLAN, and both signs are ordinary
        // terrain. A CONVEX bend - the outside of a spur, a headland, the bank the whole of #502 was reported on -
        // puts the destination anchor above the walker's own, `ownInto` is negative, and the levelling removes
        // the climb the anchor table above measures. A CONCAVE bend - a cove, a bowl, the inside of a curve -
        // MIRRORS it: the destination anchor already DESCENDS by the sagitta and the walker's own column is the
        // one that climbs, so `ownInto` is positive and the same arithmetic replaces the correct answer with the
        // wrong one.
        //
        // THAT WAS MEASURED, ON THE EXACT MIRROR OF THE FIXTURE THE FIX WAS BUILT ON, AND IT IS WHY THIS LINE
        // EXISTS. Every bank fixture in the #498/#501/#502 arc bends the same way, so round one's sixty rides
        // could not see it. On the mirrored sixty - same radii, same leans, same speeds, same rates, uphill
        // outward instead of inward - the pre-#502 engine reads 0.948 to 1.049 of commanded travel with 6 footing
        // flips, 171 airborne ticks of 12600 and 1.51e-5 m of creep per tick, and round one reads 0.827 to 2.044
        // with 471 flips, 13225 airborne and 3.47e-4 of creep. The worst single row is the 8 m concave bend at a
        // walk at 120 Hz and a LEAN OF ZERO - a walker holding a purely tangential heading - which spends 1187 of
        // its 1200 ticks airborne and covers 2.04 times the travel the stick asked for, because it is being slid
        // rather than walked. Same file, same numbers, other sign.
        //
        // WITH THE GATE, BOTH SIGNS ARE EXACT. The mirrored sixty come back to the pre-#502 engine's own ride to
        // every digit on every row, and the convex sixty are unchanged from round one to every digit on every
        // row: the correction fires on one bend sign and is arithmetically absent on the other. Both families are
        // pinned in WallContactOwnColumnTests, red-first against round one.
        //
        // WHY A SIGN TEST RATHER THAN A PROBE OF THE REAL ASK. Comparing the two candidate endpoints' actual
        // heights would be a fifth height probe on a contact tick, and it would decide the same thing: the two
        // asks differ by exactly `G * ownInto * dt` to first order and the surface's second order is what the
        // ladder underneath is FOR. So the sign is the whole of the information at the scale this rule works at,
        // it costs one comparison against a zero this method already computed, and the probe budget of the block
        // stays at four.
        //
        // AND IT IS `< 0` RATHER THAN `<= 0` OR AN EPSILON ON PURPOSE. At `ownInto` exactly zero the two answers
        // are the same vector, so the branch cannot matter. Near zero the levelling moves the travel by
        // `|ownInto|`, which is by definition as small as the thing being tested, so there is no cliff on either
        // side of this line and nothing for an epsilon to buy. The strict form also makes the zero-vector case
        // above a no-op without a second test.
        if (ownInto >= 0f) return (sx, sz);

        float lx = sx - ownInto * ox, lz = sz - ownInto * oz;
        if (lx * lx + lz * lz < OwnColumnLevelKeep * OwnColumnLevelKeep * (sx * sx + sz * sz)) return (sx, sz);
        return (lx, lz);
    }
}
