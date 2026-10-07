using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

// The swept move's step-up: which contacts are step candidates, the up/forward/down probe that mounts a riser, and the
// landing gate that keeps a lip-band step-up off a convex prop flank.
public static partial class CharacterMovement
{
    // Step-up down-sweep range, as a multiple of StepHeight (sibling to RecoverSweepRadii, same Bepu half-range
    // rationale). TryStepUp raises the pose a full StepHeight then sweeps back DOWN to settle onto the ledge, so a
    // SHORT step's tread sits up to StepHeight below - right at HALF of a bare StepHeight range, the far portion where
    // Bepu's triangle-mesh sweep under-reports a hit (only solid convex risers, whose sweeps report reliably, mounted;
    // the identical one-sided mesh tread was silently dropped). Doubling the range puts every in-band ledge in the near
    // half so the mesh tread registers; the strictly-higher-than-pos.Y guard still rejects any step-DOWN the longer
    // reach can now touch, so the ACCEPTED band is unchanged [pos.Y, pos.Y + StepHeight] - only Bepu's reliability improves.
    private const float StepDownSweepRangeSteps = 2f;

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

        // 3. Down to settle onto the ledge; must be a walkable slope strictly higher than pos (TryLandStep).
        float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
        float downRange = StepDownSweepRangeSteps * step + SkinWidth;
        if (TryLandStep(world, capsule, fwd, pos.Y, downRange, cosMaxSlope, out stepped, out landedNormalY))
            return true;

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

        // Nearer landing, for a run of treads shallower than the capsule. One radius forward, the footprint reaches
        // past the tread being mounted onto the NEXT nosing, whose edge is too steep to stand on, so the landing above
        // is refused on every riser of the run. Before this, a walk only rose when the support sweep happened to read
        // that steep nosing first, which depended on millimetres of approach. The riser being mounted is still a step:
        // land on it from a shorter advance along the same clear forward sweep. Each candidate passes the same capsule
        // landing test, walkable and strictly higher than the start, so it finds only a stand the full advance missed.
        foreach (float fraction in NearerLandingFractions)
        {
            if (TryLandStep(world, capsule, up + (fwd - up) * fraction, pos.Y, downRange, cosMaxSlope,
                    out Vector3 nearer, out float nearerNormalY))
            {
                stepped = nearer;
                landedNormalY = nearerNormalY;
                return true;
            }
        }
        stepped = pos;
        landedNormalY = 0f;
        return false;
    }

    // Fractions of the forward advance TryStepUp retries when the full advance lands on a face it cannot stand on.
    private static readonly float[] NearerLandingFractions = { 0.5f, 0.25f };

    /// <summary>Sweeps down from the raised, advanced pose <paramref name="from"/> and lands on a walkable surface strictly
    /// higher than <paramref name="startY"/>. The down-sweep range is StepDownSweepRangeSteps * StepHeight to work around
    /// Bepu's mesh-sweep far-half under-report (see that const).</summary>
    private static bool TryLandStep(IPhysicsWorld world, CapsuleShape capsule, Vector3 from, float startY,
        float downRange, float cosMaxSlope, out Vector3 landed, out float landedNormalY)
    {
        landed = from; landedNormalY = 0f;
        if (!world.SweepCapsule(capsule, Pose.At(from), -Vector3.UnitY, downRange, out SweepHit downHit) ||
            !(downHit.Normal.Y >= cosMaxSlope))
            return false;
        landed.Y -= MathF.Max(0f, downHit.Distance - SkinWidth);
        if (!(landed.Y > startY + 1e-4f)) return false;
        landedNormalY = downHit.Normal.Y;
        return true;
    }
}
