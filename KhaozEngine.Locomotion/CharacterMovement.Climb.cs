using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

// The paced step-up CLIMB and the climb signal it exports (E1/E4): the per-tick rise cap that turns a stair run
// into a steady walk instead of a snap per riser, the monotone mount that keeps a deep single riser from stalling,
// the tangent co-pace that stops the XZ racing the paced height, and the continuous ascent/descent rate the render
// glide reads. One concern (how a step is climbed and what that climb reports), split out of CharacterMovement.cs
// so that file stays about the STEP itself, the same partial-file precedent as CharacterMovement.Collision.cs and
// CharacterMovement.Settle.cs. Same partial type, so StepCore calls straight in with no seam. No behaviour of its
// own: the single entry point here is called only from StepCore, under the gate documented on it.
public static partial class CharacterMovement
{
    /// <summary>What one paced-climb tick commits: the position and the grounded/velocity state it resolved, plus
    /// the climb signal it exports. Everything StepCore hands in that this can change comes back here, so the
    /// pacing owns no state of its own between ticks (the EWMA is carried on <see cref="MoveState"/>).</summary>
    private readonly record struct ClimbPacing(Vector3 Position, bool Grounded, float TimeSinceGrounded,
        float VerticalVelocity, bool StepUpRose, float ClimbRate, float ClimbRateEwma);

        // 4b. Smooth step-up climb: cap the per-tick RISE onto step/prop support above the terrain floor to
        //     MaxStepClimbSpeed. The step-up mounts a whole riser in one tick, so a dungeon stair run (12-18 risers
        //     of ~0.33 m) otherwise snaps up ~0.33 m per mounting tick - it reads as shooting/jerking up. This paces
        //     that rise to a steady walk. Scoped tightly so nothing else changes:
        //       - only when NOT rising ballistically (vVel <= 0): a JUMP (vVel > 0, applied in step 5 below) is
        //         never throttled, so jump height/arc is untouched; this covers both the grounded climb ticks and
        //         the brief airborne transition at a stair's emergence, where the support still lifts the capsule a
        //         step;
        //       - only the UPWARD delta (a fall/descent, pos.Y < prev, is below the cap and passes through); and
        //       - only the portion of support ABOVE the analytic terrain floor - a terrain slope is never throttled
        //         (its height passes through via the terrainGroundY floor of the cap), so horizontal walk speed,
        //         coyote, and landing stay untouched and only the discrete static step geometry is paced.
        //     The capped capsule lags the tread it is mounting by at most the few frames it takes to catch up (it
        //     stays on the step run, resting on the lower treads its footprint still spans); the next tick
        //     re-resolves the support and rises another budget's worth, so the climb still reliably reaches the top.
        //     A low curb whose rise is within one tick's budget is unaffected (mounted in one tick as before), and
        //     MaxStepClimbSpeed <= 0 disables the pacing entirely (the pre-smoothing instant snap).
        //
        //     Grounded-through-the-climb: when the cap clamps pos.Y, the real support (the tread/prop the step-up
        //     mounted) sits ABOVE the paced height, so the capsule is genuinely resting on the step run (its
        //     footprint still spans the lower treads) while it rises toward the tread ahead - it is NOT airborne.
        //     Left alone, the paced lag makes step 4's support probe miss the tread once it drifts more than a
        //     StepHeight above the lagging feet, so `grounded` flips false and vVel goes negative for a few ticks
        //     between steps - the capsule reads as briefly FALLING mid-climb, which spams the fall animation (the
        //     10.61 climb jank). So while the cap is actively holding the capsule below its support, force it
        //     grounded and kill any residual downward velocity: the committed height still lags smoothly for the
        //     visible rise, but the movement state reports a steady grounded climb. Only reached when NOT rising
        //     ballistically (vVel <= 0), so a jump is never grounded-forced here.
    private static ClimbPacing PaceStepClimb(IPhysicsWorld world, in CapsuleShape capsule, Vector3 pos,
        in MoveState s, in MoveTuning t, float dt, float dx, float dz, float halfH, float terrainGroundY,
        bool steppedUp, bool grounded, float tSinceGround, float vVel)
    {
        // The three signals this tick can raise, all defaulting to "no climb" exactly as they do in StepCore: the
        // caller assigns them back unconditionally, and they were 0/false before the call.
        float climbRate = 0f;
        float climbEwma = 0f;
        bool stepUpRose = false;

        float climbCap = MathF.Max(terrainGroundY, s.Position.Y + t.MaxStepClimbSpeed * dt);
        // `paced` = the resolved support sits ABOVE this tick's paced ceiling, i.e. a genuine mid-climb tick
        // where the vertical rise is being throttled (the capsule's feet have NOT yet reached the tread). It is
        // the same test the old code gated the whole block on; hoisting it lets the horizontal cap stay active
        // across the final seating tick too (see below), so that tick cannot lurch the capsule the full probe
        // advance forward.
        bool paced = pos.Y > climbCap;
        // The step-up mechanism is raising the capsule this tick when a discrete riser mounted (steppedUp) OR the
        // paced cap is actively throttling a step/prop rise (paced). Records the WHOLE step-up (a doorstep paces up
        // over a couple ticks, steppedUp only on the first), for the discrete-step mesh-offset stamp below. On a
        // continuous run this is also true, but the stamp there is suppressed by the climbRate != 0 gate.
        bool stepRoseNow = steppedUp || paced;
        if (stepRoseNow) stepUpRose = true;

        // CONTINUOUS-CLIMB detection (§E4), for the CLIMB SIGNAL only (no authoritative-motion effect). Grounded and
        // ELEVATED on a step, with a commanded move and a genuine stair GRADE ahead (SurfaceGradeAhead, measured
        // surface-to-surface so it is radius-independent). This holds every tick of a run - between mounts too, not
        // only on the paced mount tick E1 stamps - so the exported climb signal is CONTINUOUS across the run and the
        // render glide can flatten the per-riser Y bob instead of chattering on an intermittent signal. Scoped to the
        // ELEVATED case (above the terrain floor) so the base of a stair reads a signal only once genuinely on it.
        float intX = dx - s.Position.X, intZ = dz - s.Position.Z;
        float intLen = MathF.Sqrt(intX * intX + intZ * intZ);
        bool elevated = s.Position.Y > terrainGroundY + OnPropSkin;
        var intDir = new Vector2(intX, intZ);
        float runGrade = 0f;
        bool continuousRun = intLen > 1e-6f && elevated &&
                             SurfaceGradeAhead(world, capsule, s.Position, intDir, t, s.Position.Y - halfH, out runGrade) &&
                             MathF.Abs(runGrade) >= MinPacedGrade;

        if (steppedUp)
        {
            // MONOTONE step-up mount. The step-up probe teleports the capsule up to a capsule radius forward to
            // seat its footprint on the tread; committed raw that pulses a fore-aft LURCH (a ~0.4 m jump then a
            // wait), so the advance is CAPPED to the intended walk step. That cap is what a stair run needs: its
            // treads are shallower than the footprint, so the capped pose still rests on the treads it spans and
            // the climb is smooth. But the SAME cap is wrong for a single discrete riser onto a deep tread: there
            // the capped footprint falls SHORT, embedded in the solid riser below the tread top, so next tick's
            // depenetration shoves it straight back OFF the step, the support drops to the flat floor, and the
            // capsule buzzes at flat height forever - the slow-walk first-riser stall (worst on a one-sided
            // building-entrance riser, whose depenetration pushback runs well past the capped advance). A fixed
            // StepMountClearance floor was the old bug: any constant is wrong for some geometry (too small still
            // stalls, too large re-lurches).
            //
            // So DERIVE the choice from the actual collision instead of a constant, and keep the mount monotone:
            // cap to the walk step, but VALIDATE the capped pose at the paced height - if it still overlaps the
            // riser (i.e. next tick's depenetration WOULD shove it back off the step and cancel the mount), commit
            // the probe's LANDED horizontal instead. The landed pose is the non-cancelling seat: the probe already
            // proved it lands ON the tread, so its footprint is OVER the tread and the depenetration there lifts
            // the capsule UP onto the step (monotone), never back. A tight stair keeps the cap (its capped pose is
            // clear -> no lurch, the smoothness pins hold); only a deep single riser, where the cap cancels, takes
            // the full seat - and the single riser has no lurch budget to protect. Direction stays TryStepUp's
            // (perpendicular to the riser), so an angled approach gains no lateral drift.
            float intendedH = MathF.Sqrt((dx - s.Position.X) * (dx - s.Position.X) + (dz - s.Position.Z) * (dz - s.Position.Z));
            float hx = pos.X - s.Position.X, hz = pos.Z - s.Position.Z;
            float hLen = MathF.Sqrt(hx * hx + hz * hz);
            if (hLen > intendedH && hLen > 1e-6f)
            {
                float k = intendedH / hLen;
                var capped = new Vector3(s.Position.X + hx * k, climbCap, s.Position.Z + hz * k);
                // Keep the capped advance only while the capped pose stays SUPPORTED - the true discriminator.
                // (Depenetration alone does not tell the two geometries apart: a paced climb sits slightly
                // embedded in the tread it is mounting on BOTH, and on a real stair the capsule is also pressed
                // against the next riser, so the overlap and even its MTV direction look the same as the stall
                // case.) What actually differs is whether the capsule is still carried: a STAIR-RUN tick rests
                // its footprint on the LOWER treads it spans, so the feet-down feel-fan under the capped pose
                // still finds walkable floor -> hold the smooth cap and the smoothness pins are untouched. A deep
                // single riser's capped pose falls SHORT, into the gap in FRONT of the tread with nothing under
                // it, so the mount would be shoved back off the step and stall -> commit the probe's LANDED seat
                // instead. That seat is already OVER the tread (the non-cancelling forward position the design
                // calls for), so the depenetration there lifts the capsule UP onto the step, never back, and the
                // paced Y keeps the rise smooth. Uses the same downward ray fan (radius-less, so it never hits the
                // one-sided-mesh zero-normal degeneracy) the wall-slide trusts for "am I supported".
                if (WalkableFloorUnderFeet(world, capsule, capped, t))
                {
                    pos.X = capped.X;
                    pos.Z = capped.Z;
                }
            }
        }
        if (paced)
        {
            // TANGENT CO-PACE (unchanged authoritative motion). The rise is throttled from the support's desired rise
            // down to the climb cap; scale the committed HORIZONTAL advance so the capsule travels ALONG the stair
            // surface at ~MaxStepClimbSpeed/grade instead of racing the XZ ahead of the paced height (which plowed the
            // risers and strobed forward advance). Gated on a MOUNTABLE next riser ahead (NextRiserAhead), so a
            // single-riser seat or a clear/blocked path keeps its full forward commitment. This is the 10.74.1 forward
            // behaviour exactly - the E4 signal above does not alter it.
            float desiredRise = pos.Y - s.Position.Y;
            float allowedRise = climbCap - s.Position.Y;
            float hx = pos.X - s.Position.X, hz = pos.Z - s.Position.Z;
            float hLen = MathF.Sqrt(hx * hx + hz * hz);
            if (desiredRise > allowedRise && allowedRise > 0f && hLen > 1e-6f &&
                NextRiserAhead(world, capsule, pos, new Vector2(hx, hz), t))
            {
                float tangent = allowedRise / desiredRise;                 // exact tangent throttle (small if inflated)
                float floor = allowedRise / (MaxClimbGrade * hLen);        // grade floor: horiz >= allowedRise/MaxClimbGrade
                float throttle = MathF.Min(1f, MathF.Max(tangent, floor));
                pos.X = s.Position.X + hx * throttle;
                pos.Z = s.Position.Z + hz * throttle;
            }
            // Cap the vertical RISE to MaxStepClimbSpeed and hold the movement state grounded through the paced climb:
            // the real support (the tread the step-up mounted) sits ABOVE this committed height, so the capsule is
            // resting on the step run, not airborne - forcing grounded stops step 4's support probe from flickering
            // Grounded false and spamming the fall animation. Only reached when NOT rising ballistically (vVel <= 0).
            pos.Y = climbCap;
            grounded = true;
            tSinceGround = 0f;
            if (vVel < 0f) vVel = 0f;
        }

        // CONTINUOUS CLIMB SIGNAL (§E4), stamped AFTER the co-pace above has COMMITTED pos.Y for this tick. The two
        // directions are NOT symmetric, because the authoritative FORWARD is not:
        //   - ASCENT forward is CO-PACED (throttled along the stair tangent, step 4b), so commanded-forward * grade
        //     OVERSTATES the footprint-limited emergent rise (a sub-cap walk commands ~2.2 m/s but climbs ~1.34; a
        //     run commands the 3.5 cap but climbs ~2.94). Feeding that overstated rate to the render glide (§E3)
        //     parked the drawn feet at the feed-forward/damp equilibrium (signal - achieved) / SlopeGlideRate ABOVE
        //     the true feet - a persistent half-riser hover, plus a crest snap when it cut to 0. So the ASCENT signal
        //     is instead an EWMA of the rate the capsule ACTUALLY rose ((pos.Y - s.Position.Y) / dt): it converges to
        //     the true rate BY CONSTRUCTION (the signal IS what the feet did), so the equilibrium offset -> ~0 at
        //     both walk and run. The co-pace caps every in-run tick's rise at MaxStepClimbSpeed * dt, so this
        //     applied-rate source is bounded and smooth-able, and the EWMA is <= the cap (no clamp needed).
        //   - DESCENT forward is NOT throttled (the step-down-hold does not co-pace), so commanded-forward * grade
        //     already EQUALS the achieved descent rate - descent never had the hover bug. It KEEPS commanded * grade,
        //     which is smooth by construction; the applied-rise EWMA does NOT fit a descent (the un-paced step-down
        //     delivers the drop in full-riser bursts the EWMA cannot flatten without a long lag). Bounded by the
        //     single MaxDescentSignalRate authority so it round-trips the wire.
        // SIGNAL ONLY: neither branch touches the authoritative position (pos.Y is byte-identical to 10.74.1). The
        // EWMA resets to 0 off a run (climbEwma default) and updates only here, so a fall / jump / flat / single
        // riser never accumulates into it - the fall-sink stays correct by construction. Both heads run this
        // identical deterministic update, so their signals agree.
        if (continuousRun && runGrade >= 0f)
        {
            float appliedRate = dt > 0f ? (pos.Y - s.Position.Y) / dt : 0f;
            // SEED the EWMA on the FIRST tick of a run (to a fraction of the first sample) instead of warming it from
            // 0. A run engages at the flat -> stairs handoff where the render height is exactly the true feet
            // (ClimbRate was 0, rendered raw); the true feet then START rising, so a 0-warmed signal fed forward lags
            // the render ~(achieved / SlopeGlideRate) BELOW the feet for a full time-constant - a run-start foot-sink
            // into the steps. Seeding lifts the feed-forward to match the rise from tick one; the FRACTION keeps a
            // walk (whose first tick is often a full mount at the cap) from over-floating. The reset writes
            // ClimbRateEwma to exactly 0, and an in-run ASCENT EWMA never returns to exactly 0, so `== 0` uniquely
            // marks the run's first tick - deterministic on both heads. Fall-purity is untouched: this only runs on a
            // continuous-run tick, so a fall never seeds or accumulates.
            float alpha = 1f - MathF.Exp(-ClimbSignalSmoothingRate * dt);
            climbEwma = s.ClimbRateEwma == 0f
                ? ClimbSignalSeedFraction * appliedRate
                : s.ClimbRateEwma + alpha * (appliedRate - s.ClimbRateEwma);
            climbRate = MathF.Max(0f, climbEwma);   // ascent is non-negative
        }
        else if (continuousRun)
        {
            // DESCENT: unchanged commanded-forward * grade (climbEwma stays 0, so the EWMA resets across an
            // ascent -> descent transition).
            float absGrade = Math.Clamp(-runGrade, MinPacedGrade, MaxPacedGrade);
            float rate = intLen / dt * absGrade;
            climbRate = -MathF.Min(rate, MaxDescentSignalRate);
        }

        return new ClimbPacing(pos, grounded, tSinceGround, vVel, stepUpRose, climbRate, climbEwma);
    }
}
