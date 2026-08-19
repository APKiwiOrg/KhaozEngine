using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

// What the SWEPT move leaves behind, and nothing else: the residual-overlap settle pass (depenetration, including
// the walkable-rest vertical-only rule) and the monotone-forward climb hold that undoes its backward shove on a
// riser. One concern - correcting the candidate position after the sweep has committed it - split out of
// CharacterMovement.cs so that file stays about the STEP itself, the same partial-file precedent as
// CharacterMovement.Collision.cs and CharacterMovement.Climb.cs. It is a sibling file rather than more of
// Collision.cs because this is post-resolution correction, not resolution. Same partial type, so StepCore calls
// straight in with no seam. Never reached on a terrain-only step (the caller has already established a world).
public static partial class CharacterMovement
{
    /// <summary>Settles the swept candidate position: the residual-overlap depenetration iterations, the XZ clamp
    /// re-application, and the monotone-forward hold that keeps a climbing capsule from being shoved back off the
    /// riser it is mounting. Returns the corrected position, and reports through <paramref name="propGrounded"/>
    /// whether a depenetration separated the capsule along a mostly-upward direction (standing on a prop).</summary>
    private static Vector3 SettleAfterSweep(IPhysicsWorld world, in CapsuleShape capsule, Vector3 pos,
        in MoveState s, in MoveTuning t, float dx, float dz, float vVel, float halfH, bool restHold,
        Func<float, float, Vector2>? clampXz, Func<float, float, float> groundHeight, out bool propGrounded)
    {
        propGrounded = false;

        // Settle pass: residual-overlap depenetration (rarely fires now; the swept move starts known-outside).
        const int ResolveIterations = 6;
        const float ResolveSlop = 0.01f;
        const float MaxCorrection = 0.5f;
        float cosMaxSlopeSettle = MathF.Cos(t.MaxSlopeRadians);
        for (int i = 0; i < ResolveIterations; i++)
        {
            if (!world.ComputePenetration(capsule, Pose.At(pos), out Vector3 mtv)) break;
            float len = mtv.Length();
            if (len <= 1e-6f) break;
            if (mtv.Y > 0.5f * len) propGrounded = true;
            if (len <= ResolveSlop) break;
            float push = MathF.Min(len - ResolveSlop, MaxCorrection);
            Vector3 correction = mtv / len * push;
            // WALKABLE-CONTACT REST DEPENETRATION IS VERTICAL. When the capsule is grounded and the separation
            // direction is walkable (its normal passes the slope gate), resolve ONLY the vertical component and
            // drop the horizontal. Pushing a resting capsule out along the FULL MTV of a tilted walkable surface
            // slides it down-slope by ResolveSlop*sin(slope) EVERY tick - a steady creep the analytic terrain
            // (not in the physics world) never shows. Vertical-only lifts it clear without the sideways shove;
            // step 4 clamps the resting height on the surface. A steep (wall/riser) contact is NOT walkable, so it
            // keeps the full MTV: a capsule walking INTO a wall still depenetrates horizontally, and a riser
            // push-out is unchanged. (The ANALYTIC path does slide on a too-steep surface since 17.28.0. This is the
            // PROP path, which is still static-at-rest against a steep prop face: extending the slide to props is
            // #438's contact-classification rebuild, and it would branch here on the non-walkable normal.)
            // len > 1e-6 above, so mtv.Y >= cosMaxSlope*len is the divide-free normal.Y >= cosMaxSlope test.
            if (restHold && mtv.Y >= cosMaxSlopeSettle * len) correction = new Vector3(0f, correction.Y, 0f);
            pos += correction;
        }
        if (clampXz is not null) { Vector2 c = clampXz(pos.X, pos.Z); pos.X = c.X; pos.Z = c.Y; }

        // MONOTONE-FORWARD climb. The paced step-up caps the per-tick horizontal advance to the walk step while the
        // rise is throttled, so between mounts the footprint sits slightly behind the tread it is climbing, embedded
        // in the riser. This same swept pass's depenetrate-to-clearance (its pre-sweep push-out) then shoves the
        // capsule BACKWARD off that riser next tick - a real per-riser fore-aft ripple (~-0.06 m at walk, ~-0.07 m at
        // slow walk, then a catch-up) felt on the body AND on the camera, which tracks the physics XZ un-smoothed.
        // The backward push is spurious while climbing: the capsule is SUPPOSED to press the riser it is mounting,
        // and the step-up remounts it the very next tick. So when it was grounded up on the step run last tick (its
        // carried Y sits above the analytic terrain floor, i.e. genuinely on a step, not on flat ground) and this
        // tick's resolved move points net BACKWARD along the intended direction, HOLD the horizontal at last tick's
        // position for this tick (do not commit the shove). This runs BEFORE the support floor (step 4) is read, so
        // when the backward shove would also have dropped the height (its shoved-back footprint losing the riser and
        // collapsing to the flat floor), sampling the floor at the held (still-on-the-riser) XZ keeps the capsule up
        // on the step and the ascent stays smooth in Y too. A separate Y hold is deliberately NOT added: holding the
        // height instead of the XZ freezes the step-up (the brief drop-to-flat is part of how a deep-footprint mount
        // re-engages the next riser). It cannot push the capsule into new geometry (it only removes a regression,
        // never adds reach) and never lowers the mount's forward cap (which the swept step-up needs to clear the
        // pushback - lowering it re-creates the first-riser stall). The hold is the WHOLE displacement, not just its
        // backward component: on an ANGLED climb, projecting out only the along-move part would spill that removed
        // length onto the climb-perpendicular axis and amplify the sideways drift on shove ticks (breaking the
        // no-lateral-amplification pin); holding the full XZ leaves the sideways position exactly where it was, so a
        // shove tick contributes zero lateral. A real descent keeps the along-move component >= 0 (forward-and-down)
        // and is untouched; flat-ground knockback fails the on-the-run gate (its carried Y is at the floor). Gated
        // on the PREVIOUS state, not the post-shove pos, because a deep shove has already dropped pos by now,
        // and excludes jump-takeoff ticks when rising ballistically (vVel > 0).
        float floorAtStart = groundHeight(s.Position.X, s.Position.Z) + halfH;
        if (s.Grounded && t.MaxStepClimbSpeed > 0f && vVel <= 0f && s.Position.Y > floorAtStart + 0.05f)
        {
            float mvx = dx - s.Position.X, mvz = dz - s.Position.Z;
            float mvLen = MathF.Sqrt(mvx * mvx + mvz * mvz);
            if (mvLen > 1e-6f)
            {
                float inv = 1f / mvLen; mvx *= inv; mvz *= inv;
                float along = (pos.X - s.Position.X) * mvx + (pos.Z - s.Position.Z) * mvz;
                if (along < 0f) { pos.X = s.Position.X; pos.Z = s.Position.Z; }   // net backward: hold last tick's XZ
            }
            else
            {
                // ZERO-INPUT HOLD. No horizontal command this tick, yet the swept + depenetrate passes still
                // resolved a net XZ displacement (the riser the capsule was mounting pushes its embedded footprint
                // BACKWARD off the step - a steep riser normal, so it is not the walkable-rest vertical-only case
                // above). With no command that shove is pure artifact: a capsule that stops mid-climb should settle
                // vertically onto the tread it is on, not be knocked back a riser. Same arming as the forward hold
                // (grounded, elevated on a step, not rising ballistically), so a released climb holds its XZ and
                // the support/pacing below seats it on the current tread. Flat ground fails the elevated gate and
                // is untouched; a commanded move (mvLen > 1e-6) takes the forward-hold branch, so walking into a
                // wall still slides normally.
                pos.X = s.Position.X; pos.Z = s.Position.Z;
            }
        }

        return pos;
    }
}
