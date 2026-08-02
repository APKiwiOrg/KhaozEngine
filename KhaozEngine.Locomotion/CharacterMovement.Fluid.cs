using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The FLUID half of the movement step: the surface-swim enter/exit decision, the swim tick itself, and the
// wade speed scale the land path composes. One concern (what water does to a character), split out of the
// main CharacterMovement.cs so that file - already the engine's largest - does not grow, exactly as
// CharacterMovement.CameraRelativeDir.cs did. Same partial type, same shared private core: StepCore calls
// ResolveSwimming and hands off to SwimStep, and the dry path multiplies in WadeSpeedScale.
public static partial class CharacterMovement
{
    /// <summary>The surface-swim enter/exit decision with hysteresis, a pure function of the current medium sample and
    /// the carried swim flag. A land/wading character (<paramref name="wasSwimming"/> false) begins swimming only when
    /// it is <see cref="MovementMedium.InWater"/> and its submersion depth (<c>WaterSurfaceY - feetY</c>, as a fraction
    /// of body height) reaches <see cref="MoveTuning.SwimEnterDepthFraction"/> (chest). A swimming character
    /// (<paramref name="wasSwimming"/> true) keeps swimming until it either leaves the water or its submersion falls
    /// below the LOWER <see cref="MoveTuning.SwimExitDepthFraction"/>. The gap between the two thresholds is the
    /// hysteresis band that stops the state flickering when the feet sit right at the chest line (pin: walking a gentle
    /// slope into a lake flips exactly once). Dry land / a null-provider Dry sample never swims.</summary>
    /// <param name="wasSwimming">The swim flag carried from the previous tick's <see cref="MoveState"/>.</param>
    /// <param name="medium">The medium sampled at the feet this tick.</param>
    /// <param name="feetY">Capsule-bottom world Y (centre minus half-height).</param>
    /// <param name="tuning">Carries the enter/exit depth fractions and the capsule half-height.</param>
    /// <returns>True to surface-swim this tick.</returns>
    public static bool ResolveSwimming(bool wasSwimming, in MovementMedium medium, float feetY, in MoveTuning tuning)
    {
        if (!medium.InWater) return false;                      // out of the water: never swimming (also the null-Dry path)
        float bodyHeight = 2f * tuning.CapsuleHalfHeight;
        if (bodyHeight <= 1e-6f) return wasSwimming;            // degenerate body has no depth axis: hold the state (no flip)
        float depthFraction = (medium.WaterSurfaceY - feetY) / bodyHeight;
        // Hysteresis: the exit threshold applies while already swimming, the (higher) enter threshold while not. A
        // character between the two lines keeps whatever it was, so the boundary cannot chatter.
        return wasSwimming
            ? depthFraction >= tuning.SwimExitDepthFraction
            : depthFraction >= tuning.SwimEnterDepthFraction;
    }

    /// <summary>One surface-swim tick: gravity and ground-snap are suspended, the capsule settles toward its buoyancy
    /// waterline via the EXACT analytic critically-damped spring (unconditionally stable, no oscillation), horizontal
    /// travel is <see cref="MoveTuning.SwimSpeed"/> scaled by the medium's own <c>WadeSpeedScale</c> (a swamp can drag
    /// a swim), and a jump is honoured ONLY as a hop-out in near-shore shallows (submersion within the exit band of
    /// leaving the water); in deep water the jump bit is ignored. <see cref="MoveState.VerticalVelocity"/> is reused as
    /// the buoyancy settle velocity while swimming (gravity does not run), so a leftover fall/jump velocity eases out
    /// through the same damped settle rather than snapping. The terrain floor is still respected (the capsule never
    /// sinks below ground + half-height, e.g. in shallow water at the edge). Deterministic: pure float math over the
    /// pure provider sample.
    /// <para>It is its OWN facing site. This method returns early out of <c>StepCore</c>, so the heading update has to
    /// run here too or a swimmer would be the one path on which the character silently cannot turn - the rule itself
    /// is the same <c>ResolveFacing</c> every other path calls, with the same camera-or-command target.</para></summary>
    private static MoveState SwimStep(in MoveState state, Vector2 moveDir, float speedFraction, bool jump, float dt,
        in MoveTuning t, in MovementMedium medium, Func<float, float, float> groundHeight,
        Func<float, float, Vector2>? clampXz, float halfH, float? faceYaw = null)
    {
        // Horizontal: swim speed (run has no effect while swimming), scaled by the medium's zone multiplier so a
        // swamp/current still composes, clamped >= 0 so a hostile negative zone scale cannot reverse travel. Uses the
        // resolved unit move direction + speed fraction shared with the walk path (so the camera-relative player and
        // the world-space AI swim identically), at SwimSpeed.
        float zoneScale = medium.WadeSpeedScale < 0f ? 0f : medium.WadeSpeedScale;
        float x = state.Position.X, z = state.Position.Z;
        float commandedSpeed = 0f;
        if (speedFraction > 0f)
        {
            // The per-entity haste/slow multiplier applies here too (deliberate): a player who dives mid-boost keeps
            // it rather than silently losing the buff at the waterline.
            commandedSpeed = t.SwimSpeed * zoneScale * speedFraction * state.SpeedScale;
            x += moveDir.X * commandedSpeed * dt;
            z += moveDir.Y * commandedSpeed * dt;
        }
        Vector2 commandedVel = moveDir * commandedSpeed;   // (0,0) when idle: moveDir is zero and so is the speed
        if (clampXz is not null) { Vector2 c = clampXz(x, z); x = c.X; z = c.Y; }

        // Buoyancy target: the capsule Y at which the body sits at its resting waterline, i.e. feet submerged by
        // SwimSurfaceSubmersionFraction of body height below the surface. targetFeetY = surface - fraction*bodyHeight,
        // targetY (capsule centre) = targetFeetY + halfH.
        float bodyHeight = 2f * halfH;
        float targetFeetY = medium.WaterSurfaceY - t.SwimSurfaceSubmersionFraction * bodyHeight;
        float targetY = targetFeetY + halfH;

        // Critically-damped settle to targetY. EXACT analytic solution over dt (no oscillation, never blows up for
        // any dt/stiffness; from rest it is monotone, and an adverse entry velocity yields at most a single bounded
        // settle dip past the target): y(dt) = target + (A + B*dt) e^{-w dt}, with A = y0 - target, B = v0 + w*A. VerticalVelocity
        // is repurposed as the settle velocity (gravity is off while swimming), so an entry fall/jump velocity bleeds
        // out through the same damping instead of a snap.
        float w = t.SwimBuoyancyStiffness;
        float y = state.Position.Y;
        float v = state.VerticalVelocity;
        float dy = y - targetY;
        float e = MathF.Exp(-w * dt);
        float a = dy;
        float b = v + w * dy;
        y = targetY + (a + b * dt) * e;
        v = (b - w * (a + b * dt)) * e;

        // Terrain floor still holds while swimming (never sink through the lakebed in shallow water at the edge): the
        // capsule centre never rests below ground + half-height. If the floor clamps, kill any residual downward
        // settle velocity so it does not fight the clamp next tick.
        float floorY = groundHeight(x, z) + halfH;
        if (y < floorY) { y = floorY; if (v < 0f) v = 0f; }

        // Jump = hop-out, near-shore only. "Near-shore shallows" is defined by the exit band: the hop-out fires when
        // the feet are shallow enough that submersion is within the exit threshold (i.e. one hop clears the water). In
        // deeper water the jump bit is ignored (you cannot leap out of open water). The hop-out launches the ordinary
        // jump velocity and DROPS swim: the next land tick reads a jumping, airborne, non-swimming state.
        bool swimmingNext = true;
        float vVel = v;
        if (jump)
        {
            // Deliberately reads the POST-settle feet (y - halfH from the settled y above), not the step-start feetY the
            // enter/exit hysteresis uses: near-shore reflects where the body ended up resting this tick, not where it began.
            float depthFraction = bodyHeight > 1e-6f ? (medium.WaterSurfaceY - (y - halfH)) / bodyHeight : t.SwimEnterDepthFraction;
            if (depthFraction <= t.SwimExitDepthFraction)
            {
                vVel = t.JumpSpeed;      // hop out with the ordinary jump launch
                swimmingNext = false;    // leave swim: the land path takes over next tick (airborne)
            }
        }

        var result = new MoveState
        {
            Position = new Vector3(x, y, z),
            VerticalVelocity = vVel,
            Grounded = false,               // swimming is never grounded (gravity/ground-snap suspended)
            TimeSinceGrounded = state.TimeSinceGrounded + dt,
            JumpBufferRemaining = 0f,       // no jump-buffer while swimming (a hop-out is instant or ignored)
            Swimming = swimmingNext,
            SpeedScale = state.SpeedScale,  // carried through the swim exactly as the land path carries it
            // Exported here as well as on the land path, at the SWIM speed: an anomaly check that assumed walk/run
            // for a swimmer is exactly the bug this export exists to make impossible.
            CommandedVelocity = commandedVel,
            // Water KILLS an airborne arc. The carried inertia is REPLACED by the swim's own commanded velocity
            // rather than clipped from it, so a character who flies into a lake drops the arc at the waterline
            // instead of skating across the surface at its takeoff speed for the next second.
            HorizontalVelocity = commandedVel,
            // The heading turns while swimming exactly as it does on land: the water changes what the body does, not
            // which way it points. Carried, so a mid-turn survives the swim rather than restarting at the shoreline.
            FacingYaw = ResolveFacing(state.FacingYaw, moveDir, faceYaw, dt, t),
        };
        // Defense-in-depth (as the land path): a finite input must never yield a non-finite result; hold the last
        // good state if a misbehaving provider/ground/tuning injected a NaN/Inf.
        return IsFinite(result.Position) && float.IsFinite(result.VerticalVelocity) &&
               float.IsFinite(result.FacingYaw) ? result : state;
    }

    /// <summary>The horizontal-speed multiplier the fluid medium imposes at a sample: 1 (no penalty) on dry land or
    /// with a null provider, otherwise a linear wade ramp from full speed at ankle depth
    /// (<see cref="MoveTuning.WadeStartDepthFraction"/> of body height) down to <see cref="MoveTuning.WadeMinSpeedScale"/>
    /// at chest depth (<see cref="MoveTuning.WadeEndDepthFraction"/>), the whole ramp then multiplied by the sample's
    /// own <see cref="MovementMedium.WadeSpeedScale"/>. Submersion depth is <c>WaterSurfaceY - feetY</c>. Pure and
    /// deterministic given a pure provider (a bare arithmetic ramp over the provider's read), so the server and client
    /// prediction produce the identical scale. Exposed for callers that predict/echo the wade factor (Task 2's swim
    /// mode reads the same submersion the ramp is built from).</summary>
    /// <param name="x">Sample X (world).</param>
    /// <param name="z">Sample Z (world).</param>
    /// <param name="feetY">Capsule-bottom world Y (capsule centre minus half-height).</param>
    /// <param name="tuning">Carries the wade ramp depths + floor.</param>
    /// <param name="medium">The medium provider, or null for dry land everywhere (returns 1).</param>
    /// <returns>The depth ramp (in <c>[<see cref="MoveTuning.WadeMinSpeedScale"/>, 1]</c>) times the sample's own
    /// <see cref="MovementMedium.WadeSpeedScale"/> zone scale, clamped to <c>&gt;= 0</c> and UNCAPPED above: a zone
    /// scale greater than 1 (a current/aid zone, which is allowed) lifts the result past 1. Never negative. A null
    /// provider or a dry sample returns exactly 1.</returns>
    public static float WadeSpeedScale(float x, float z, float feetY, in MoveTuning tuning,
        Func<float, float, float, MovementMedium>? medium)
    {
        if (medium is null) return 1f;                 // dry land everywhere: bit-identical to the pre-medium path
        MovementMedium m = medium(x, z, feetY);
        if (!m.InWater) return 1f;                     // out of water: the ramp contributes nothing

        float bodyHeight = 2f * tuning.CapsuleHalfHeight;
        float ramp;
        if (bodyHeight <= 1e-6f)
        {
            // A degenerate zero-height body has no depth axis to ramp over: in water it is simply at/past the floor.
            ramp = tuning.WadeMinSpeedScale;
        }
        else
        {
            float depthFraction = (m.WaterSurfaceY - feetY) / bodyHeight;
            float start = tuning.WadeStartDepthFraction;
            float end = tuning.WadeEndDepthFraction;
            if (depthFraction <= start) ramp = 1f;                       // ankle-deep or shallower: full speed
            else if (depthFraction >= end) ramp = tuning.WadeMinSpeedScale;  // chest-deep or deeper: the floor
            else
            {
                // Linear lerp from full speed (at start) down to the floor (at end). end > start by the tuning
                // contract; guard the denominator anyway so a mis-set tuning cannot divide by zero.
                float span = end - start;
                float tNorm = span > 1e-6f ? (depthFraction - start) / span : 1f;
                ramp = 1f + (tuning.WadeMinSpeedScale - 1f) * tNorm;
            }
        }

        float scale = ramp * m.WadeSpeedScale;
        return scale < 0f ? 0f : scale;               // a hostile/mis-set negative zone scale can never reverse travel
    }
}
