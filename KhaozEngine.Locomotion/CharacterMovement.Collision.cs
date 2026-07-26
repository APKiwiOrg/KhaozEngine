using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

// The COLLISION half of the movement step: substepped swept collide-and-slide against an IPhysicsWorld, the
// step-up probe that mounts stairs and curbs, and the support/tread/grade probes those two decide from, plus
// the tuning constants they share. One concern (how the capsule resolves against solid geometry), split out of
// the main CharacterMovement.cs so that file stays about the STEP itself, following the same partial-file
// precedent as CharacterMovement.CameraRelativeDir.cs and CharacterMovement.Fluid.cs. Same partial type, so
// StepCore calls straight into these with no seam. No behaviour of its own: every entry point here is called
// only from StepCore.
public static partial class CharacterMovement
{
    // Swept collide-and-slide tuning. SubstepFraction keeps each swept query <= a fraction of the capsule radius
    // so a fast move (jump/run/terminal fall) can never advance past a thin wall in one sweep (anti-tunnel).
    // The walkable-contact pass-through advances the remainder UNSWEPT, so this must stay below ~1.0 (each substep
    // under one radius) or such an advance could mask and tunnel a wall in the same substep.
    private const float SubstepFraction = 0.5f;
    private const int   SlideIterations = 4;
    private const float SkinWidth       = 0.01f;
    // Downward reach of the wall-slide gravity GATE (NOT the support height itself, which step 4 owns): a walkable
    // floor within this far below the feet means "supported", so the wall slide keeps its usual on-slope projection;
    // beyond it the slide must not cancel gravity. > StepHeight + SkinWidth (a step you could mount still counts as
    // supported) and < body height (so it is a feet-local probe, not a whole-body one).
    private const float FloorProbeReach = 0.5f;
    // Step-up eligibility floor: a contact whose normal.Y is BELOW -StepUpNormalY points sharply DOWN (a ceiling /
    // overhang / eave) and is never a step - reject it so pressing up into one spends no probe sweeps. The UPPER
    // bound is the walkable slope gate, not a constant: a contact is only a step-up candidate once it has already
    // failed the walkable test (a walkable normal is SUPPORT, handled above, not a face). The old symmetric
    // |normal.Y| < 0.5 precheck was too tight on the top edge: a SHORT riser / tread LIP grazed by the capsule's
    // bottom cap reports an up-TILTED normal (measured normal.Y ~0.5-0.75 at effective risers <= ~0.18 m), which the
    // 0.5 cap rejected outright, so a real mountable lip on uneven ground never even attempted a step-up. TryStepUp's
    // own up/forward/down sweeps are the real gate (a true wall finds no ledge within StepHeight and falls through to
    // the slide), so widening the precheck to "not walkable, not a ceiling" only lets genuine lips through.
    private const float StepUpNormalY = 0.5f;
    // Flat-tread gate for a LIP-band step-up (a contact normal in [StepUpNormalY, cosMaxSlope), the widened band). Such
    // a step-up is committed ONLY when it lands on a near-FLAT surface (ledge normal.Y at or above this), i.e. a real
    // stair tread / curb / doorstep top. A CONVEX prop flank (dome, boulder, rounded rock) whose flank normal happens
    // to fall in the lip band also passes TryStepUp's own walkable-ledge test - its rounded top is walkable - so the
    // capsule used to climb the prop from its base (the Capsule_BlockedAtDomeBase invariant). A sphere/curve never
    // presents a dead-flat landing under the footprint (its landing normal tilts, ~0.85 on the dome), while a real
    // step's tread is ~1.0, so this cleanly separates a mountable step from a prop flank. Only gates the WIDENED band;
    // the classic near-vertical (|n.Y| < StepUpNormalY) step-up is unchanged (a tread of any walkable slope mounts).
    private const float LipLandingFlatNormalY = 0.9f;
    // Tangent co-pace grade floor (step 4b): the steepest stair grade (rise/run) paced to the EXACT surface tangent.
    // On a steeper stair the co-paced horizontal advances a touch faster than the surface rises - a bounded, sub-tread
    // lead the depenetrate/support pass absorbs - which is the safe side; under-advancing would float the capsule off
    // the surface between mounts and drop it. It also floors the throttle so a discrete whole-riser step-up cannot
    // inflate the apparent grade and crawl the mount to a stall. ~37 deg; below the 45 deg default slope gate so a
    // normal ramp is never in this regime, and near the 0.30/0.40 dungeon/consumer stair grade (0.75) it wires.
    private const float MaxClimbGrade = 0.72f;
    // Grade band (rise/run) for the continuous-run detection + the DESCENT signal (§E4). MinPacedGrade is the detection
    // floor: a SurfaceGradeAhead reading below it is too shallow to be a stair run, so the climb signal stays off (an
    // ordinary ramp is never in this regime; a 0.30/0.40 dungeon/consumer stair is 0.75, well above). MaxPacedGrade
    // caps the grade before the descent signal derives commanded-forward * grade, bounding a bad SurfaceGradeAhead
    // sample. [0.3, 2.0] brackets every real stair. (ASCENT no longer reads the grade for its magnitude - it is the
    // smoothed applied rise, climbEwma - so only descent uses MaxPacedGrade.)
    private const float MinPacedGrade = 0.3f;
    private const float MaxPacedGrade = 2.0f;
    // THE single descent-signal-magnitude authority (m/s): the maximum |ClimbRate| a descent may export, sized to stay
    // inside the quantized wire range (+/-6.35 m/s) so it always round-trips. A continuous run-descent reads its full
    // co-paced surface rate up to here (it is NOT paced by MaxStepClimbSpeed - understating a fast run-descent left a
    // residual bob the glide could not track). The discrete single-step-down (step 4a-down) deliberately rides the
    // gentler MaxStepClimbSpeed pacing rate under this same ceiling (a doorstep eases down, it does not snap), so both
    // descent paths clamp through this one authority (the step-down via a MIN with the pacing rate) rather than two.
    private const float MaxDescentSignalRate = 6.0f;
    // EWMA smoothing rate (1/s) for the exported climb SIGNAL (step 4b): ClimbRate is an exponentially-weighted moving
    // average of the actually-applied per-tick rise rate over a paced run, so it converges to the sim's TRUE emergent
    // rate and the render-glide equilibrium offset (signal - achieved)/SlopeGlideRate settles to ~0 (no half-riser
    // hover). The window (time constant 1/rate) is DERIVED from the riser cadence: the applied rise arrives in a
    // per-riser burst (~1 riser every ~7 ticks at a sub-cap walk - the sparser cadence, ~3 ticks at a capped run), so
    // the average must span roughly one walk cadence (~0.23 s at 30 Hz) to flatten the burst into a steady rate the
    // feed-forward can ride without chattering. 5/s (tau 0.20 s, ~6 ticks) sits just under one walk cadence: enough to
    // smooth the burst, quick enough to keep the run-start transient short. alpha = 1 - exp(-rate*dt) is a deterministic
    // function of the fixed dt, so both heads agree exactly.
    private const float ClimbSignalSmoothingRate = 5.0f;
    // Fraction of the FIRST in-run applied-rate sample used to SEED the EWMA (vs warming it from 0). The first tick of a
    // run is usually a paced MOUNT at the cap (~MaxStepClimbSpeed), which overstates a sub-cap walk's achieved rate,
    // while warming from 0 leaves the feed-forward too low and sinks the render below the feet for a full time-constant
    // at a run (the worse artifact - feet clipping the steps). Seeding to 0.7 of the first sample lands the initial
    // signal between the mount spike and the achieved rate, so neither a walk over-floats nor a run under-sinks at the
    // handoff while the EWMA converges over the next ~tau. It only shifts the first run tick; every later tick is the
    // plain EWMA. Deterministic, and still updated ONLY on a run tick, so fall-purity is untouched.
    private const float ClimbSignalSeedFraction = 0.7f;
    // Depenetration-to-clearance passes before each sweep: push the capsule out of any prop/wall overlap to a small
    // positive clearance so the sweep starts provably outside and yields a REAL contact normal (Bepu reports a
    // useless t=0 zero-normal from a touching start). A few passes clear an inner corner (two simultaneous contacts).
    private const int   DepenIterations = 4;

    /// <summary>Move the capsule from <paramref name="start"/> toward <paramref name="target"/> by a substepped
    /// swept collide-and-slide over <see cref="IPhysicsWorld.SweepCapsule"/>. The displacement is split into
    /// substeps no longer than <see cref="SubstepFraction"/> * the capsule radius, so even a near-terminal fall or
    /// fast jump never crosses a face. Deterministic (Bepu Sweep is deterministic single-threaded; the substep
    /// count is a deterministic length).</summary>
    private static Vector3 SweptMove(IPhysicsWorld world, CapsuleShape capsule, Vector3 start, Vector3 target,
        in MoveTuning t, bool grounded, bool restHold, Func<float, float, float> groundHeight, out bool steppedUp, out float steppedFloorY)
    {
        steppedUp = false; steppedFloorY = 0f;
        Vector3 full = target - start;
        float fullLen = full.Length();
        if (fullLen <= 1e-6f) return target;

        float maxStep = MathF.Max(0.01f, t.CapsuleRadius * SubstepFraction);
        int substeps = (int)MathF.Ceiling(fullLen / maxStep);
        if (substeps < 1) substeps = 1;
        Vector3 stepDelta = full / substeps;

        Vector3 pos = start;
        for (int i = 0; i < substeps; i++)
        {
            pos = SlideSubstep(world, capsule, pos, stepDelta, t, grounded, restHold, groundHeight, out bool stepped, out float floorY);
            if (stepped) { steppedUp = true; steppedFloorY = floorY; }
        }
        return pos;
    }

    /// <summary>Collide-and-slide one substep's displacement: sweep, advance to the contact minus a skin, then by
    /// contact class - a WALKABLE contact (floor/slope/prop-top, normal up enough to stand on) passes the remainder
    /// through (step 4 sets the resting Y so the capsule follows the surface), a STEEP contact (wall/riser) either
    /// steps up over a low ledge or projects the remainder onto the contact plane (slide), iterating to resolve
    /// inner corners.</summary>
    private static Vector3 SlideSubstep(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 delta,
        in MoveTuning t, bool grounded, bool restHold, Func<float, float, float> groundHeight, out bool steppedUp, out float steppedFloorY)
    {
        steppedUp = false; steppedFloorY = 0f;
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        for (int iter = 0; iter < SlideIterations; iter++)
        {
            // Depenetrate to a small POSITIVE clearance FIRST, so the sweep below starts provably outside every
            // static and returns a real contact normal. A capsule resting against a prop/wall sits exactly TANGENT
            // (the swept move leaves a SkinWidth gap, then settles to ~touching), and a sweep from a touching start
            // reports t=0 with a zero normal - no slide plane. Without this, that case had to GUESS into-vs-along,
            // which froze a strafe, hung a fall, or tunneled. We query depenetration with a slightly INFLATED
            // capsule (radius + SkinWidth): a tangent real capsule registers as overlapping the inflated probe, so
            // the MTV pushes it to ~SkinWidth real clearance (a same-size query returns nothing at exact tangent and
            // would leave it stuck). After this the sweep is clean: a fall sweeps PARALLEL to a wall it is beside
            // (clear -> falls), a strafe sweeps along the contact tangent (clear -> slides), a walk straight in hits
            // at t>0 with a real normal (slides / blocks). Terrain is analytic (not in the world), so flat ground
            // never triggers this; only props/walls do. Iterated to clear an inner corner (two simultaneous
            // contacts). One-sided meshes only contact from the front, so the MTV always pushes OUT - never through.
            CapsuleShape probe = new(capsule.Radius + SkinWidth, capsule.Length);
            for (int d = 0; d < DepenIterations; d++)
            {
                if (!world.ComputePenetration(probe, Pose.At(pos), out Vector3 push) || push.LengthSquared() <= 1e-12f) break;
                // WALKABLE-CONTACT REST DEPENETRATION IS VERTICAL (mirrors the settle pass in StepCore): a grounded
                // capsule with NO horizontal command, pushed off a tilted WALKABLE surface, takes only the vertical
                // component, so a resting capsule cannot creep down-slope; a steep (wall/riser) normal keeps the full
                // MTV so walking into a wall still pushes out horizontally. Gated on no command (not just grounded)
                // because a fast run-climb IS grounded yet embeds in the risers, and its horizontal depenetration is
                // load-bearing for extracting the swept climb - suppressing it there re-embeds the capsule and
                // collapses support (the StairRunTangentPacing regression). cosMaxSlope is the slope gate at the top
                // of this method. NaN-safe: push.LengthSquared() > 1e-12 guards the length divide above.
                if (restHold)
                {
                    float pl = push.Length();
                    if (push.Y >= cosMaxSlope * pl) push = new Vector3(0f, push.Y, 0f);
                }
                pos += push;   // MTV is direction*depth; the inflated overlap depth lands the real capsule ~SkinWidth clear
            }

            float dist = delta.Length();
            if (dist <= 1e-6f) break;
            Vector3 dir = delta / dist;
            if (!world.SweepCapsule(capsule, Pose.At(pos), dir, dist, out SweepHit hit))
            {
                pos += delta;     // clear path for the remainder of this substep
                break;
            }
            pos += dir * MathF.Max(0f, hit.Distance - SkinWidth);
            Vector3 remaining = delta - dir * hit.Distance;
            Vector3 n = hit.Normal;
            if (n.LengthSquared() <= 1e-12f)
            {
                // Degenerate contact: the sweep started TANGENT to a one-sided mesh (a building wall/eave) so Bepu
                // returns t=0 with a ZERO normal - no slide plane. 8.5.3's depenetrate-to-clearance cannot prevent
                // this (`ComputePenetration` reports NO overlap for a one-sided face at a tangent), so the capsule
                // CAN reach the touching state - e.g. a jump that arrives airborne pressing in lands flush on the
                // wall, or jumps up into a downward-facing eave that overhangs the wall base. A bare stop froze the
                // capsule mid-wall; a single recovered normal + slide frees the flat wall but NOT a two-face wedge
                // (eave above + wall ahead), where the back-off re-sweep can even start inside adjacent geometry.
                //
                // INVARIANT: a one-sided mesh has no inner-face contacts, so it can never SUPPORT the capsule from
                // below. The DOWNWARD (gravity) component must therefore ALWAYS proceed here - step 4 re-clamps it
                // at the real support floor - which guarantees the capsule can fall out of ANY overhang/eave/corner
                // wedge. The HORIZONTAL is still recovered + slid along the wall (so a strafe is not frozen and a
                // walk straight in stays blocked); when no normal is recoverable the horizontal is blocked but
                // gravity still wins.
                pos.Y += MathF.Min(0f, remaining.Y);                       // gravity escape: never blocked by a one-sided mesh (step 4 clamps)
                if (TryContactNormal(world, capsule, pos, dir, out Vector3 recovered))
                {
                    if (recovered.Y >= cosMaxSlope) { Vector3 h = remaining; h.Y = 0f; pos += h; break; }  // walkable floor: pass horizontal
                    // No step-up attempt here: TryStepUp from this flush-tangent start was tried and proven unable to
                    // seat (its sweeps fail from tangent), so a mountable lip routes via the main-path step-up on the
                    // re-approach tick after this step-off.
                    pos += recovered * SkinWidth;                          // step off the wall so the next sweep is clean
                    Vector3 horiz = new(remaining.X, 0f, remaining.Z);     // slide the horizontal along the wall's HORIZONTAL plane,
                    Vector3 nH = new(recovered.X, 0f, recovered.Z);        // then RE-SWEEP it (continue) so a perpendicular wall (corner) still blocks
                    if (nH.LengthSquared() > 1e-12f) { nH = Vector3.Normalize(nH); horiz -= Vector3.Dot(horiz, nH) * nH; }
                    delta = horiz;
                    continue;
                }
                break;
            }
            n = Vector3.Normalize(n);

            // Walkable ground / slope / prop-top (normal up enough to STAND on, n.Y >= cos(maxSlope)): this is
            // FLOOR, not a wall - do NOT block horizontal travel. Advance the remainder and let step 4 (analytic
            // terrain + the downward support sweep) set the resting Y, so the capsule follows the surface (walks
            // across a domed rock, mounts it on landing) instead of being walled-off by its own support.
            if (n.Y >= cosMaxSlope)
            {
                pos += remaining;
                break;
            }

            // Step-up: only while grounded, over an eligible NON-walkable contact (a wall / riser / up-tilted tread
            // lip), on the horizontal remainder. A near-vertical riser is eligible anywhere; an up-tilted LIP - which
            // the old |n.Y| < 0.5 cap wrongly rejected on very short risers - is eligible near the terrain floor (see
            // StepUpEligible). TryStepUp self-validates via its up/forward/down sweeps, so a real wall finds no ledge
            // within StepHeight and slides. Climbs a stair tread / curb / doorstep lip.
            if (grounded && StepUpEligible(n.Y, pos, t, cosMaxSlope, groundHeight) &&
                TryStepUp(world, capsule, pos, remaining, n, t, groundHeight, out Vector3 stepped, out float landedNy) &&
                LipLandingOk(n.Y, landedNy))
            {
                steppedUp = true; steppedFloorY = stepped.Y;
                pos = stepped;
                break;
            }

            // Wall slide: project the remainder onto the contact plane (block + slide along the wall). But a wall -
            // including an UP-TILTED one-sided face (a building eave/awning pocket gives a real normal like
            // n=(0.90,0.38,-0.20)) - must never hold the capsule UP against gravity. That projection bleeds the
            // downward component, and over the iterations a concave pocket bleeds it to zero, freezing the capsule
            // mid-air (the tester's "stuck on the wall / under the awning" pin, vVel railing to terminal while the
            // position never moves). Authority on "am I supported" is a downward ray fan from the feet, NOT this
            // contact normal: while descending with no walkable floor under the feet, keep the FULL downward
            // remainder so gravity always wins (step 4 still clamps it at the real support floor). With a floor under
            // the feet (grounded / landing / a rock side) this is byte-identical to the old projection, so the
            // convex + grounded paths and reconciliation are unchanged.
            Vector3 slid = remaining - Vector3.Dot(remaining, n) * n;
            if (remaining.Y < 0f && !WalkableFloorUnderFeet(world, capsule, pos, t))
            {
                // Gravity escape: apply the downward remainder DIRECTLY to the position, not via slid.Y. At a wedge
                // the contact is ~tangent (hit.Distance < SkinWidth), so the next iteration's sweep advances pos by
                // ~0 and would never realize a delta.Y - the capsule would stay frozen even with gravity "in" delta.
                // Stepping pos.Y here makes gravity win every tick (the gate already proved no floor within reach;
                // step 4 still clamps at the real support floor). The horizontal slides on as usual.
                pos.Y += remaining.Y;
                slid.Y = 0f;
            }
            delta = slid;
        }
        return pos;
    }

    /// <summary>True when a walkable floor sits within <see cref="FloorProbeReach"/> below the capsule's feet, found
    /// by a fixed 5-ray downward fan (centre + ±0.7R on each footprint axis). RAYS, not a capsule sweep: a ray has no
    /// radius, so it never starts tangent to a one-sided face and never returns the zero-normal degeneracy that
    /// defeats a capsule sweep there - it cleanly answers "is there ground under me". Used ONLY as the gravity GATE
    /// in the wall slide; the actual support height still comes from step 4's capsule sweep, so a thin tread that
    /// falls between the rays is still stood on (step 4 clamps it - the gate only decides whether to let gravity
    /// through this tick). Deterministic: a fixed ray order over the deterministic Bepu raycast, with a small slope
    /// epsilon so a contact exactly at the slope limit cannot flip branches across runtimes.</summary>
    private static bool WalkableFloorUnderFeet(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, in MoveTuning t)
    {
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        float feetY = pos.Y - t.CapsuleHalfHeight + SkinWidth;     // just above the feet, cast down
        float r = 0.7f * capsule.Radius;
        Span<Vector2> fan = stackalloc Vector2[] { new(0f, 0f), new(r, 0f), new(-r, 0f), new(0f, r), new(0f, -r) };
        for (int i = 0; i < fan.Length; i++)
        {
            var origin = new Vector3(pos.X + fan[i].X, feetY, pos.Z + fan[i].Y);
            if (world.Raycast(origin, -Vector3.UnitY, FloorProbeReach, out RayHit hit) &&
                // The floor must sit genuinely BELOW the feet. The ray origin is placed SkinWidth ABOVE the feet
                // plane, so a real support surface lies at least SkinWidth down (distance >= SkinWidth). A hit
                // CLOSER than that means the origin is embedded in the solid - the feet are inside / below the
                // surface, not resting on top of it - which Bepu reports as a degenerate zero-distance up-normal
                // hit. Counting that as floor was the paced step-up's embedded-riser false positive: on the capped
                // (throttled) mount pose the feet sit inside a solid building-step riser, this fan returned true,
                // the validated-cap discriminator kept the smooth stair cap for a single deep riser, and the mount
                // was depenetrated back off the step every tick (buzz at flat height). Reject the embedded hit so
                // that pose reads unsupported and the discriminator commits the probe's landed seat instead.
                hit.Distance >= SkinWidth &&
                hit.Normal.Y >= cosMaxSlope - 1e-4f)
                return true;
        }
        return false;
    }

    /// <summary>Finds the highest WALKABLE tread top under the capsule footprint that sits in the step-climb band (from
    /// the feet up to <see cref="MoveTuning.StepHeight"/> above them) and returns the capsule-centre Y that rests on it
    /// (<paramref name="treadCentreY"/>). This is the MOUNT counterpart to <see cref="WalkableFloorUnderFeet"/>'s gravity
    /// gate: where that answers "is there floor below my feet" for the wall slide, this answers "is there a tread I am
    /// mounting AT or ABOVE my feet" for step 4's support floor. RAYS (radius-less) so a footprint STRADDLING a tread
    /// shallower than the capsule diameter still finds the tread top through the clear air above it - exactly the contact
    /// step 4's full-radius downward capsule sweep MISSES at a staircase base (the sweep grazes the vertical riser front
    /// and returns a steep, off-footprint normal both of its guards reject). The origins sit a full StepHeight ABOVE the
    /// feet, so at a PARTIAL mount - the paced feet still below the tread top - the ray starts in clear air above the
    /// tread and drops onto it, never embedded in it. The reach spans only the band DOWN TO the feet plane, so the fan
    /// never invents support in open air below the feet (a ledge walk-off finds no tread and falls) and never fights a
    /// step-DOWN (a lower tread ahead is below the feet, out of the band - left to the downward sweep and gravity).
    /// Deterministic: a fixed 5-ray fan over the deterministic Bepu raycast, keeping the highest hit. Follows the
    /// one-sided-mesh-safe convention of <see cref="WalkableFloorUnderFeet"/> - a radius-less downward ray never hits the
    /// zero-normal degeneracy a capsule sweep does, and reads a consumer +Y-wound tread top cleanly.</summary>
    private static bool WalkableTreadUnderFeet(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, in MoveTuning t,
        out float treadCentreY)
    {
        treadCentreY = 0f;
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        float feetY = pos.Y - t.CapsuleHalfHeight;
        float originY = feetY + t.StepHeight + SkinWidth;   // above any tread within one step of the feet
        float reach = t.StepHeight + SkinWidth;             // reach down to the feet plane, never below it
        float radius = capsule.Radius;
        float bestY = float.NegativeInfinity;
        foreach (Vector2 off in TreadFanOffsets)
        {
            var origin = new Vector3(pos.X + off.X * radius, originY, pos.Z + off.Y * radius);
            if (world.Raycast(origin, -Vector3.UnitY, reach, out RayHit hit) &&
                // Reject an embedded origin (distance < SkinWidth => the tread top is at/above the band ceiling, or the
                // origin sits inside a solid) exactly as WalkableFloorUnderFeet does, so a ray that starts buried in
                // geometry cannot report a spurious zero-distance up-normal hit as a tread.
                hit.Distance >= SkinWidth &&
                hit.Normal.Y >= cosMaxSlope - 1e-4f)   // a walkable tread top, not a steep riser face
            {
                float hitY = originY - hit.Distance;   // tread top world Y
                if (hitY > bestY) bestY = hitY;
            }
        }
        if (float.IsNegativeInfinity(bestY)) return false;
        treadCentreY = bestY + t.CapsuleHalfHeight;    // capsule centre resting on that tread top
        return true;
    }

    // Footprint sample offsets (fractions of the capsule radius) for WalkableTreadUnderFeet: the centre plus two rings
    // (inner 0.65 R, outer 0.95 R) of 8 directions each. A dense DISC sample, not the 5-ray cross WalkableFloorUnderFeet
    // uses, because at a PARTIAL mount the capsule centre sits IN FRONT of the riser (the step-up raised Y but the paced
    // horizontal has not carried the centre onto the tread yet), so only the LEADING ARC of the footprint overlaps the
    // shallow tread. The near-rim ring reaches that arc from any approach heading (head-on or angled), where the 0.7 R
    // cross falls short of it and the mount collapses. Rim (~R) samples cannot invent open-air support: the fan is gated
    // behind grounded + elevated-on-a-step + a missed downward sweep, and every hit is a real walkable tread within one
    // StepHeight of the feet, so it only reports a tread the capsule body already spans.
    private static readonly Vector2[] TreadFanOffsets = BuildTreadFanOffsets();

    private static Vector2[] BuildTreadFanOffsets()
    {
        // The 0.95 R outer ring deliberately sits just PAST the downward sweep's 0.9 R UnderFootprint reach: that ~0.02 m
        // window (0.95 R - 0.9 R = 0.05 R = 0.02 m at radius 0.4) is exactly what lets a partial mount's LEADING arc find
        // the tread the sweep's gate rejects. Do NOT widen it blind - a ring >= 1.0 R would reach a full radius back and
        // re-grab a tread the capsule is STEPPING OFF (the descent regression in ConsumerStairBaseMountTests guards this).
        ReadOnlySpan<float> rings = stackalloc float[] { 0.65f, 0.95f };
        var offsets = new Vector2[1 + rings.Length * 8];
        offsets[0] = Vector2.Zero;
        int n = 1;
        foreach (float ring in rings)
            for (int k = 0; k < 8; k++)
            {
                float a = k * (MathF.PI / 4f);
                offsets[n++] = new Vector2(ring * MathF.Cos(a), ring * MathF.Sin(a));
            }
        return offsets;
    }

    // Tangent co-pace next-riser probe (step 4b, NextRiserAhead). Just BEYOND a steep face ahead, sample the floor at
    // these forward offsets past the face plane, looking for the mountable tread on top. Each stays within a seatable
    // tread's depth (>= a footprint), so at least one lands cleanly on the next tread of any stair steep enough to be
    // paced; on a solid back wall every sample falls INSIDE the wall body and is rejected as embedded (below). A small
    // spread (not one magic offset) is robust to the exact face position across dt/radius/grade.
    private static readonly float[] NextTreadProbeOffsets = { 0.05f, 0.15f, 0.25f };
    // A found tread counts as "the NEXT riser" only if it sits at least this far ABOVE the current feet - excludes the
    // same-level deep tread a single riser seats onto (and the flat top of a run), which must NOT co-pace.
    private const float NextRiserMinRise = 0.02f;

    /// <summary>True when a MOUNTABLE next stair riser - a steep face with a walkable tread on top, strictly higher and
    /// within <see cref="MoveTuning.StepHeight"/> - sits within about one capsule radius directly ahead of
    /// <paramref name="pos"/> along the horizontal travel direction <paramref name="dirXZ"/>. This is the "am I on a
    /// CONTINUOUS run that KEEPS climbing" test the tangent co-pace gates on: a mountable riser ahead means the climb
    /// keeps going (co-pace the horizontal so a runner glides up at the honest grade-limited speed); anything else -
    /// a clear path (a single-riser seat onto a deep tread, or the top of the run) OR a steep face that is NOT a
    /// climbable riser (a building's back WALL close behind the doorstep, or an overhang) - must NOT be throttled, so
    /// the load-bearing forward seat clears its depenetration pushback and the mount completes.
    ///
    /// Two-stage. First a capsule SWEEP asks "is there a steep obstacle in FRONT" (a sweep, not a downward ray, so it
    /// reads the same for an analytic-terrain approach as for a physics floor; a walkable RAMP ahead is not a riser and
    /// reads clear). Then, crucially, a downward RAY fan just BEYOND that face verifies it is a RISER I will climb NEXT
    /// and not a wall: a mountable riser has a walkable tread on top (free space above it), strictly higher than the
    /// feet and within StepHeight, so a ray dropped from the top of the step-climb band lands on that tread through
    /// clear air. A tall back WALL (or an overhang) is SOLID through that band, so the ray ORIGIN is embedded in it and
    /// Bepu returns a zero-distance hit - rejected exactly like <see cref="WalkableFloorUnderFeet"/> rejects an
    /// embedded feet-fan hit. RAYS (not the capsule sweep <see cref="TryStepUp"/> uses) are essential: a fast RUN races
    /// its footprint embedded into the riser it is mounting, and a capsule sweep from that overlapping start degenerates
    /// (t=0), which would disable the co-pace on exactly the ticks it must smooth. A ray dropped from clear air above
    /// the next tread has no such degeneracy.
    ///
    /// This is the 10.68 regression fix: the original bare steep-face test fired on the tall wall a footprint behind a
    /// compound doorstep and throttled the single-riser mount into a flat-height stall. The tread-on-top check leaves a
    /// genuine stair (whose next riser IS a mountable, higher tread through clear air) co-pacing, so run-up-stairs
    /// smoothness is untouched, while a wall/overhang/deep-tread reads false and the mount keeps its full seat.
    /// Deterministic (fixed Bepu sweep + a fixed ray fan along fixed directions).</summary>
    private static bool NextRiserAhead(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector2 dirXZ, in MoveTuning t)
    {
        float lenSq = dirXZ.LengthSquared();
        if (lenSq <= 1e-12f) return false;
        Vector2 d = dirXZ / MathF.Sqrt(lenSq);
        var dir = new Vector3(d.X, 0f, d.Y);
        // One radius of reach catches the immediate next riser of any stair steep enough to be paced (its tread is
        // within a footprint); a deeper tread (a shallower, unpaced stair) reads clear, which is correct.
        float reach = capsule.Radius;
        if (!world.SweepCapsule(capsule, Pose.At(pos), dir, reach, out SweepHit hit)) return false;   // clear ahead
        bool steep = hit.Normal.LengthSquared() <= 1e-12f                                              // one-sided face, or
                     || Vector3.Normalize(hit.Normal).Y < MathF.Cos(t.MaxSlopeRadians);                // a steep (non-walkable) face
        if (!steep) return false;                                                                      // a walkable ramp is not a riser
        // Verify a MOUNTABLE tread on top of that face. The face plane sits at pos + dir*(hit.Distance + radius) (the
        // capsule's leading surface); sample just past it and drop a ray from the top of the step-climb band.
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        float feetY = pos.Y - t.CapsuleHalfHeight;
        // This face-distance estimate assumes a non-embedded initial sweep. An embedded (zero-distance) sweep puts the
        // sample ~a radius past the true face, so the tread reads clear and the throttle is skipped that tick; steady
        // state co-pacing keeps that from persisting.
        float faceAhead = hit.Distance + capsule.Radius;
        float originY = feetY + t.StepHeight + SkinWidth;   // top of the band; reach StepHeight down to the feet plane
        foreach (float off in NextTreadProbeOffsets)
        {
            Vector3 sample = pos + dir * (faceAhead + off);
            var origin = new Vector3(sample.X, originY, sample.Z);
            if (!world.Raycast(origin, -Vector3.UnitY, t.StepHeight + SkinWidth, out RayHit rh)) continue;
            // Reject an embedded origin: distance < SkinWidth means the sample sits INSIDE a solid (the back wall), not
            // on a tread with clear air above it. (Bepu reports a zero-distance up-normal hit from inside a body.)
            if (rh.Distance < SkinWidth) continue;
            if (rh.Normal.Y < cosMaxSlope - 1e-4f) continue;                     // not a walkable tread (a sloped face)
            float treadY = originY - rh.Distance;
            if (treadY > feetY + NextRiserMinRise)                              // a NEXT step UP, not the same-level tread
                return true;
        }
        return false;
    }

    // Forward sample distances (as multiples of the capsule radius) for SurfaceGradeAhead: a spread spanning a few
    // treads (a paced tread is within a footprint) so the least-squares slope averages out per-tread step noise and the
    // exact riser phase, reading the true stair grade rather than a single center-to-riser distance (which over-reads
    // the run by ~a radius and under-reads the grade, racing the forward). Starts past the immediate riser.
    private static readonly float[] GradeProbeDistances = { 0.75f, 1.25f, 1.75f, 2.25f, 2.75f, 3.25f };

    /// <summary>The local stair GRADE (rise/run, SIGNED: + ascending, - descending) ahead of the capsule, measured
    /// SURFACE-to-surface so it is independent of the capsule radius (a center-to-riser distance over-reads the run by
    /// ~a radius and under-reads the grade). Ray-samples the walkable-surface height at a spread of forward distances
    /// (<see cref="GradeProbeDistances"/>, spanning a few treads) and least-squares fits the slope, so the estimate
    /// averages out the per-tread staircase and the riser phase into the true grade. Radius-less downward rays (highest
    /// walkable hit per point) so a straddled tread reads cleanly, over a band that reaches BOTH up and down from the
    /// feet so a descent (surface below the feet) reads too. False when fewer than two forward points find a walkable
    /// surface (a gap / the top or bottom of the run), leaving the caller to the ordinary walk.</summary>
    private static bool SurfaceGradeAhead(IPhysicsWorld world, CapsuleShape capsule, Vector3 start, Vector2 dirXZ,
        in MoveTuning t, float feetY, out float grade)
    {
        grade = 0f;
        float lenSq = dirXZ.LengthSquared();
        if (lenSq <= 1e-12f) return false;
        Vector2 d = dirXZ / MathF.Sqrt(lenSq);
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        // Least-squares accumulators over (distance, surfaceRise) sample pairs.
        int n = 0;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (float mult in GradeProbeDistances)
        {
            float dist = mult * capsule.Radius;
            float px = start.X + d.X * dist, pz = start.Z + d.Y * dist;
            // The surface at `dist` ahead sits within ~dist of the feet either way (a <=45 deg walkable stair up OR down):
            // cast down from above the highest it could be, over a band that reaches down to feetY - dist too, and take
            // the highest walkable hit at this XZ (the tread top; risers are vertical, so a downward ray reads the tread).
            float originY = feetY + dist + t.CapsuleHalfHeight;
            float reach = 2f * dist + t.CapsuleHalfHeight;
            if (!world.Raycast(new Vector3(px, originY, pz), -Vector3.UnitY, reach, out RayHit rh)) continue;
            if (rh.Distance < SkinWidth || rh.Normal.Y < cosMaxSlope - 1e-4f) continue;
            float rise = (originY - rh.Distance) - feetY;   // surface height relative to the current feet (signed)
            n++; sx += dist; sy += rise; sxx += (double)dist * dist; sxy += (double)dist * rise;
        }
        if (n < 2) return false;
        double denom = n * sxx - sx * sx;
        if (denom <= 1e-9) return false;
        grade = (float)((n * sxy - sx * sy) / denom);       // least-squares slope = rise/run = the signed stair grade
        return true;
    }

    // Recovery sweep: pull back this many radii along -dir (a provably clear start, since a tangent capsule touches
    // at the surface) and re-sweep this many radii forward to read the contact normal. The re-contact sits at
    // t = RecoverBackRadii * radius; the forward range is wider because Bepu's mesh sweep does not report a hit that
    // lands in the far portion of the swept range (empirically it must sit within ~half) - so it is swept to
    // RecoverSweepRadii * radius (> 2x the contact distance) to be registered reliably.
    private const float RecoverBackRadii  = 1f;
    private const float RecoverSweepRadii = 3f;
    // Step-up down-sweep range, as a multiple of StepHeight (sibling to RecoverSweepRadii, same Bepu half-range
    // rationale). TryStepUp raises the pose a full StepHeight then sweeps back DOWN to settle onto the ledge, so a
    // SHORT step's tread sits up to StepHeight below - right at HALF of a bare StepHeight range, the far portion where
    // Bepu's triangle-mesh sweep under-reports a hit (only solid convex risers, whose sweeps report reliably, mounted;
    // the identical one-sided mesh tread was silently dropped). Doubling the range puts every in-band ledge in the near
    // half so the mesh tread registers; the strictly-higher-than-pos.Y guard still rejects any step-DOWN the longer
    // reach can now touch, so the ACCEPTED band is unchanged [pos.Y, pos.Y + StepHeight] - only Bepu's reliability improves.
    private const float StepDownSweepRangeSteps = 2f;

    /// <summary>Recover the contact normal Bepu withholds when a capsule sweep starts TANGENT to a one-sided mesh
    /// (it reports t=0 with a zero normal, leaving no slide plane). The capsule is pulled back along
    /// <paramref name="dir"/> to a provably clear start and the sweep is re-run, so Bepu returns a real surface
    /// normal at the face it re-contacts (at distance <c>RecoverBackRadii * radius</c>). Only the NEAREST hit's
    /// NORMAL is read - the capsule is not advanced by this query - so the wider sweep range cannot tunnel; the
    /// nearest hit is the touched face. Returns false when the move is parallel to the face (the back-off stays
    /// tangent, no normal recoverable), in which case the caller leaves the substep be and the next tick re-tries.
    /// Used only on the rare degenerate one-sided-mesh contact, so the convex-prop path (rocks, tree-trunk hulls;
    /// depenetrated to clearance) never reaches here.</summary>
    private static bool TryContactNormal(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 dir,
        out Vector3 normal)
    {
        normal = default;
        Vector3 backed = pos - dir * (RecoverBackRadii * capsule.Radius);
        if (world.SweepCapsule(capsule, Pose.At(backed), dir, RecoverSweepRadii * capsule.Radius, out SweepHit hit)
            && hit.Normal.LengthSquared() > 1e-12f)
        {
            normal = Vector3.Normalize(hit.Normal);
            return true;
        }
        return false;
    }

    /// <summary>Whether a NON-walkable contact normal (the walkable case is handled before this is consulted) is a
    /// step-up candidate worth probing. A sharply-DOWN ceiling/overhang normal (<c>n.Y &lt;= -<see cref="StepUpNormalY"/></c>)
    /// or a walkable one (<c>n.Y &gt;= cosMaxSlope</c>) is never a step. A NEAR-VERTICAL riser/wall
    /// (<c>|n.Y| &lt; StepUpNormalY</c>) is a candidate ANYWHERE - the unchanged classic gate. An UP-TILTED tread LIP
    /// (<c>n.Y</c> in <c>[StepUpNormalY, cosMaxSlope)</c>), which a capsule's rounded bottom cap grazes on a short
    /// riser and which the old <c>|n.Y| &lt; 0.5</c> cap rejected outright, is a candidate only when the capsule sits
    /// within a <see cref="MoveTuning.StepHeight"/> of the analytic terrain floor: a short step / curb / doorstep
    /// mounted from ~ground level, the fragile case the widening targets. Well above the terrain (mid-climb on a tall
    /// stair stack) the capsule already mounts via the near-vertical contacts, so firing the extra up-tilted step-ups
    /// there is redundant and only presses a fast run deeper into the risers (the StairRunTangentPacing penetration
    /// pin). KNOWN LIMITATION: the up-tilted-lip near-floor gate keys off the ANALYTIC terrain height only, so a short
    /// lip sitting on TOP of a prop platform more than a StepHeight above terrain fails the gate and still dead-stalls
    /// (pre-existing behaviour - narrowed by this widening, not regressed; the near-vertical band above is unaffected).
    /// The proper fix is to gate on elevation above the current SUPPORT floor including props, tracked at
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/31.
    /// <paramref name="cosMaxSlope"/> is the walkable slope gate; <paramref name="groundHeight"/> the analytic
    /// terrain sampler.</summary>
    private static bool StepUpEligible(float ny, in Vector3 pos, in MoveTuning t, float cosMaxSlope,
        Func<float, float, float> groundHeight)
    {
        if (ny <= -StepUpNormalY || ny >= cosMaxSlope) return false;   // a ceiling/overhang, or walkable support: never a step
        if (ny < StepUpNormalY) return true;                           // a near-vertical riser/wall: a candidate anywhere
        float terrainCentreY = groundHeight(pos.X, pos.Z) + t.CapsuleHalfHeight;
        return pos.Y <= terrainCentreY + t.StepHeight + SkinWidth;     // an up-tilted lip: only near the terrain floor
    }

    /// <summary>Gate a step-up that PASSED <see cref="TryStepUp"/> by the surface it landed on. A near-vertical
    /// contact (<paramref name="contactNy"/> below <see cref="StepUpNormalY"/> - the classic band) is always accepted:
    /// its behaviour is unchanged. An UP-TILTED lip-band contact (the widened band) is accepted only when it settles
    /// on a near-FLAT tread (<paramref name="landedNy"/> at or above <see cref="LipLandingFlatNormalY"/>) - a real
    /// stair/curb/doorstep top. This is what keeps the widened band from riding up a CONVEX prop flank (dome, rounded
    /// rock): the prop's rounded top is walkable enough to pass TryStepUp, but the landing under the footprint is
    /// tilted, not flat, so the capsule is left to slide/block at the base (the Capsule_BlockedAtDomeBase invariant)
    /// instead of climbing it.</summary>
    private static bool LipLandingOk(float contactNy, float landedNy)
        => contactNy < StepUpNormalY || landedNy >= LipLandingFlatNormalY;

    /// <summary>Classic up/forward/down step probe over the horizontal remainder: sweep up by
    /// <see cref="MoveTuning.StepHeight"/> (headroom), sweep forward, sweep down; accept only if it lands on a
    /// walkable-slope ledge strictly higher than the start (a stair tread/curb). A vertical wall has no such ledge
    /// within StepHeight, so this returns false and the caller slides. Returns the stepped capsule centre and, in
    /// <paramref name="landedNormalY"/>, the up-component of the ledge surface it settled on (1 = a dead-flat tread;
    /// lower = a slope), so the caller can insist a lip-band step-up land on a genuine flat tread and not ride a
    /// convex prop flank.</summary>
    private static bool TryStepUp(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 remaining,
        in Vector3 contactNormal, in MoveTuning t, Func<float, float, float> groundHeight, out Vector3 stepped, out float landedNormalY)
    {
        stepped = pos; landedNormalY = 0f;
        Vector3 horiz = new(remaining.X, 0f, remaining.Z);
        float horizLen = horiz.Length();
        if (horizLen <= 1e-6f) return false;
        // Probe forward PERPENDICULAR to the riser edge - straight UP the stairs (opposite the contact's horizontal
        // normal) - not along the raw (possibly angled) move direction. An angled approach used to drive this probe
        // SIDEWAYS: pressed against a stair-shaft side wall, the along-move probe swept into that wall, failed to
        // advance, and the riser fell through to a wall-slide that killed the forward (up-stairs) motion - the climb
        // wedged in the corner. Climbing square to the tread instead lets the side wall merely shave the lateral
        // (a normal flat-ground wall slide) while the ascent continues. Falls back to the move direction when the
        // contact normal has no usable horizontal component (degenerate one-sided hit).
        Vector3 nHoriz = new(contactNormal.X, 0f, contactNormal.Z);
        Vector3 horizDir = nHoriz.LengthSquared() > 1e-8f ? Vector3.Normalize(-nHoriz) : horiz / horizLen;
        float step = t.StepHeight;
        // Probe forward at least the capsule radius: the per-tick remainder after the contact is only a few mm
        // (walk speed * dt minus the swept distance), far too short to carry the raised capsule over the step lip
        // and onto the tread. One radius clears a curb/tread whose depth is within the capsule footprint; a real
        // wall still blocks the forward sweep entirely (caught by the "no forward progress" guard below), so this
        // only helps genuine ledges.
        float probeLen = MathF.Max(horizLen, capsule.Radius);

        // 1. Up by StepHeight (stop short of any ceiling).
        Vector3 up = pos;
        if (world.SweepCapsule(capsule, Pose.At(pos), Vector3.UnitY, step, out SweepHit upHit))
            up.Y += MathF.Max(0f, upHit.Distance - SkinWidth);
        else
            up.Y += step;

        // 2. Forward along the horizontal remainder from the raised pose.
        Vector3 fwd = up;
        if (world.SweepCapsule(capsule, Pose.At(up), horizDir, probeLen, out SweepHit fwdHit))
            fwd += horizDir * MathF.Max(0f, fwdHit.Distance - SkinWidth);
        else
            fwd += horizDir * probeLen;
        // No forward progress above the obstacle => it is a wall, not a step.
        float advanced = Vector3.Distance(new Vector3(fwd.X, 0f, fwd.Z), new Vector3(pos.X, 0f, pos.Z));
        if (advanced <= 1e-4f) return false;

        // 3. Down to settle onto the ledge; must be a walkable slope strictly higher than pos. The down-sweep RANGE is
        // StepDownSweepRangeSteps * StepHeight (2x, not 1x) to work around Bepu's mesh-sweep far-half under-report - see
        // that const for the full rationale (a one-sided mesh tread sits in the far half of a bare StepHeight range).
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        float downRange = StepDownSweepRangeSteps * step + SkinWidth;
        if (world.SweepCapsule(capsule, Pose.At(fwd), -Vector3.UnitY, downRange, out SweepHit downHit) &&
            downHit.Normal.Y >= cosMaxSlope)                                 // a walkable ledge the full-radius sweep found
        {
            Vector3 landed = fwd; landed.Y -= MathF.Max(0f, downHit.Distance - SkinWidth);
            if (landed.Y > pos.Y + 1e-4f)                                    // and strictly higher than the start
            {
                stepped = landed;
                landedNormalY = downHit.Normal.Y;
                return true;
            }
        }

        // Shallow-tread fallback (near-vertical risers, at the terrain-floor handoff only). When the tread is shallower
        // than the capsule diameter, the footprint STRADDLES it and the full-radius down-sweep above grazes the tread's
        // FRONT edge, returning a steep, rejected normal - so the mount is refused even though a flat tread is right
        // there (the first riser of a placed staircase on rolling terrain, whose effective riser rolls short across the
        // width: the consumer corner-stall). This is the same straddling-footprint miss the SUPPORT probe solves with a
        // radius-less ray fan; do the same here. Read the tread top with WalkableTreadUnderFeet cast at the FORWARD XZ
        // from the ORIGINAL feet level (so the step band [feet, feet+StepHeight] spans the tread), and mount onto it.
        // Two guards keep it tight:
        //   - NEAR-VERTICAL contact only (|n.Y| < StepUpNormalY): the up-tilted LIP band is left to the sweep +
        //     flat-landing gate, which is what keeps a convex prop flank (whose rounded top a ray fan reads as a tread)
        //     from being climbed from its base; and
        //   - near the TERRAIN FLOOR only (the base handoff). Once elevated on a step RUN the support probe's own
        //     tread-find (step 4) already carries the climb, so firing this too would double-seat a fast run FORWARD
        //     into the risers (a deep-penetration steep-run regression). At the base the capsule sits at terrain level
        //     and the support probe is gated off (not yet on a step), so this is the only path that starts the mount.
        // A true wall has no tread in the band, so the fan finds nothing and the caller slides.
        float terrainCentreY = groundHeight(pos.X, pos.Z) + t.CapsuleHalfHeight;
        bool nearFloor = pos.Y <= terrainCentreY + step + SkinWidth;
        if (MathF.Abs(contactNormal.Y) < StepUpNormalY && nearFloor &&
            WalkableTreadUnderFeet(world, capsule, new Vector3(fwd.X, pos.Y, fwd.Z), t, out float treadCentreY) &&
            treadCentreY > pos.Y + 1e-4f && treadCentreY <= pos.Y + step + SkinWidth)
        {
            stepped = new Vector3(fwd.X, treadCentreY, fwd.Z);
            landedNormalY = 1f;   // the ray fan accepts only a walkable tread; the near-vertical band skips the flat-landing gate
            return true;
        }
        return false;
    }
}
