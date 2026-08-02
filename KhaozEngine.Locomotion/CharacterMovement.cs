using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Character locomotion: the single movement step run by the local controller, the authoritative server sim,
/// and client-side prediction alike. Every entry point funnels a resolved horizontal move (unit direction + a
/// speed fraction in [0,1]), walk/run speed, and an optional slope gate through ONE collision core, so a
/// camera-relative player command and a world-space AI steering direction resolve identically - parity by
/// construction, not by copy:
/// <list type="bullet">
/// <item><see cref="Step(Vector3, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, Func{float, float, float, MovementMedium}?)"/>
/// is the horizontal-only step: Y is a pure function of XZ (ground + half-height), no air.</item>
/// <item><see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
/// is the camera-relative vertical-physics step (player / local prediction): gravity, jump (coyote + jump-buffer),
/// land/clamp, air control, plus 3D swept collide-and-slide prop resolution via an <see cref="IPhysicsWorld"/>
/// (substepped <see cref="IPhysicsWorld.SweepCapsule"/> + step-up probe), over the carried <see cref="MoveState"/>.</item>
/// <item><see cref="StepTowards(in MoveState, Vector2, bool, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
/// is the WORLD-SPACE kinematic step for server-authoritative NPCs (enemy AI): the identical terrain follow,
/// swept collide-and-slide + step-up, slope gate, and bounds clamp, driven by a world-space steering direction
/// instead of a camera yaw. No jump and no client prediction (AI is server-only).</item>
/// </list>
/// No input, render, or netcode dependency.
/// </summary>
public static partial class CharacterMovement
{
    /// <summary>Horizontal-only step (no vertical physics): Y is clamped onto the ground + half-height every tick.</summary>
    /// <param name="position">Current capsule-centre world position.</param>
    /// <param name="cmd">Movement intent (camera-relative axis + run + camera yaw).</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Speed/half-height/slope constants.</param>
    /// <param name="groundNormal">Optional ground normal at (x, z); when given, gates a step by slope.</param>
    /// <param name="medium">Optional fluid-medium provider <c>(x, z, feetY) -> MovementMedium</c>: when a sample is in
    /// water, horizontal speed is scaled by the submersion-depth wade ramp times the sample's own WadeSpeedScale. Null
    /// = dry land everywhere = bit-identical to the pre-medium behaviour. Must be pure and identical on both heads.
    /// This horizontal-only overload only WADES; surface swim (buoyancy + suspended gravity) is a vertical-physics
    /// concept and lives on the <see cref="MoveState"/> overload, since this one clamps Y to the ground every tick.</param>
    /// <returns>The advanced position (Y on the ground + half-height).</returns>
    public static Vector3 Step(Vector3 position, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null,
        Func<float, float, float, MovementMedium>? medium = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        (Vector2 moveDir, float speedFraction) = ResolveCameraRelative(cmd);
        float wade = WadeSpeedScale(position.X, position.Z, position.Y - tuning.CapsuleHalfHeight, tuning, medium);
        (float x, float z, _) = DesiredHorizontalCore(position.X, position.Z, moveDir, speedFraction, cmd.Run, dt,
            tuning, groundNormal, groundHeight, position.Y - tuning.CapsuleHalfHeight, speedScale: wade);
        var result = new Vector3(x, groundHeight(x, z) + tuning.CapsuleHalfHeight, z);
        // Defense-in-depth: never return a non-finite position from a finite input. A pathological command is
        // already neutralized by the move gate, but a misbehaving groundHeight/bound could still inject a NaN/Inf
        // that slips past every clamp and replicates to every client in range; hold the last good position instead.
        return IsFinite(result) ? result : position;
    }

    /// <summary>Vertical-physics step resolving collision against a 3D <see cref="IPhysicsWorld"/> via a
    /// substepped swept collide-and-slide (<see cref="IPhysicsWorld.SweepCapsule"/>): the capsule is swept from
    /// its current pose to the desired target in substeps no larger than a fraction of the capsule radius, so a
    /// fast move (jump / run / terminal fall) can never tunnel through a thin one-sided wall, and the capsule
    /// never enters a closed mesh (no inner-face suck-through). Walkable contacts (slope at or below the slope gate)
    /// are followed (walk across / mount a domed prop top); steep contacts block and redirect the velocity
    /// tangentially (no dead-stop). A step-up probe wires <see cref="MoveTuning.StepHeight"/>: a stair tread or
    /// curb below <c>StepHeight</c> is mounted without a jump, rising toward the ledge at no more than
    /// <see cref="MoveTuning.MaxStepClimbSpeed"/> per tick (a tall stair run ascends as a steady walk, a single low
    /// curb still mounts in one tick). Depenetration via
    /// <see cref="IPhysicsWorld.ComputePenetration"/> is retained as a residual-overlap settle pass (rarely
    /// fires after the swept move). The analytic terrain stays the floor (resolved on the final XZ).
    /// <paramref name="world"/> null = terrain only (byte-identical to pre-8.4.0). The same world + math runs
    /// on the authoritative server and in client prediction (deterministic single-threaded).</summary>
    /// <param name="state">The carried kinematic state (position + vertical velocity + grounded + feel timers).</param>
    /// <param name="cmd">Movement intent including the jump bit.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Speed/half-height/slope + gravity/jump/fall/feel constants.</param>
    /// <param name="groundNormal">Optional ground normal; when given, gates a horizontal step by slope.</param>
    /// <param name="world">The physics world to resolve against, or null for terrain-only (no change to existing
    /// behaviour).</param>
    /// <param name="clampXz">Optional XZ clamp (e.g. a play-area bound); applied after move and re-applied after
    /// depenetration so a prop cannot shove the capsule out of the play area.</param>
    /// <param name="medium">Optional fluid-medium provider <c>(x, z, feetY) -> MovementMedium</c>: in water, horizontal
    /// speed is scaled by the submersion-depth wade ramp times the sample's WadeSpeedScale (composed on top of the
    /// grounded/air-control scale). Null = dry land everywhere = bit-identical to the pre-medium behaviour. Must be
    /// pure and identical on the server and in client prediction.</param>
    /// <returns>The advanced <see cref="MoveState"/>.</returns>
    public static MoveState Step(in MoveState state, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, IPhysicsWorld? world = null,
        Func<float, float, Vector2>? clampXz = null,
        Func<float, float, float, MovementMedium>? medium = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        (Vector2 moveDir, float speedFraction) = ResolveCameraRelative(cmd);
        return StepCore(state, moveDir, speedFraction, cmd.Run, cmd.Jump, dt, groundHeight, tuning,
            groundNormal, world, clampXz, medium);
    }

    /// <summary>The WORLD-SPACE kinematic movement step for server-authoritative, non-player agents (enemy NPCs):
    /// it drives the SAME collision resolution the player gets - swept collide-and-slide + step-up against the
    /// <paramref name="world"/> (<see cref="IPhysicsWorld.SweepCapsule"/>), the analytic terrain support floor, the
    /// <paramref name="groundNormal"/> slope gate, and the <paramref name="clampXz"/> bounds - but from a world-space
    /// steering direction instead of a camera yaw, so an agent moves through the world exactly as a player would.
    /// Per-agent capsule radius / half-height / walk-run speed all come from <paramref name="tuning"/>, so different
    /// creatures get different sizes and speeds with no extra plumbing. There is no jump bit (NPCs do not jump in
    /// v1) and no client prediction path: AI is server-only, so this is called once per tick by the authoritative
    /// sim. Shares <c>StepCore</c> with the camera-relative player
    /// <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>,
    /// so the two can never drift apart.</summary>
    /// <param name="state">The carried kinematic state (position + vertical velocity + grounded + feel timers).</param>
    /// <param name="worldDir">The desired horizontal travel direction in WORLD space (XZ). Its length scales speed in
    /// [0,1] (a unit vector = full speed, a shorter vector = a slower saunter, a longer one is clamped to full); a
    /// near-zero vector is idle. Not camera-relative - the caller supplies the actual world heading (e.g. toward a
    /// target).</param>
    /// <param name="run">True to use <see cref="MoveTuning.RunSpeed"/> instead of <see cref="MoveTuning.WalkSpeed"/>.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Per-agent speed/half-height/radius/slope + gravity/fall constants.</param>
    /// <param name="groundNormal">Optional ground normal; when given, gates a horizontal step by slope exactly as the
    /// player path does.</param>
    /// <param name="world">The physics world to resolve against, or null for terrain-only.</param>
    /// <param name="clampXz">Optional XZ clamp (a play-area bound), applied after the move and re-applied after
    /// depenetration.</param>
    /// <param name="medium">Optional fluid-medium provider; wades/swims identically to the player path. Must be pure.</param>
    /// <returns>The advanced <see cref="MoveState"/>.</returns>
    public static MoveState StepTowards(in MoveState state, Vector2 worldDir, bool run, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, IPhysicsWorld? world = null,
        Func<float, float, Vector2>? clampXz = null,
        Func<float, float, float, MovementMedium>? medium = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));
        (Vector2 moveDir, float speedFraction) = ResolveWorldDir(worldDir);
        return StepCore(state, moveDir, speedFraction, run, jump: false, dt, groundHeight, tuning,
            groundNormal, world, clampXz, medium);
    }

    /// <summary>Resolve a camera-relative <see cref="MoveCommand"/> into the unit world-space move direction (XZ) and
    /// a speed fraction the shared core consumes. The player always moves at full speed (the axis is normalized), so
    /// the fraction is exactly 1 when there is input and 0 when idle - preserving the pre-refactor behaviour
    /// bit-for-bit (the same <see cref="Vector3.Normalize(Vector3)"/> over the same camera basis, gated by the same
    /// 1e-6 length-squared threshold).</summary>
    private static (Vector2 dir, float fraction) ResolveCameraRelative(in MoveCommand cmd)
    {
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);
        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        if (move.LengthSquared() > 1e-6f)
        {
            Vector3 n = Vector3.Normalize(move);
            return (new Vector2(n.X, n.Z), 1f);
        }
        return (Vector2.Zero, 0f);
    }

    /// <summary>Resolve a world-space steering direction into the unit move direction (XZ) and a speed fraction: the
    /// vector's length scales speed in [0,1] (unit = full speed, shorter = slower, longer clamped to 1), and a
    /// length below the same 1e-6 length-squared dead-zone the player path uses is treated as idle. This is the only
    /// difference between the AI and player entry points - once resolved, both drive the identical
    /// <c>StepCore</c>.</summary>
    private static (Vector2 dir, float fraction) ResolveWorldDir(Vector2 worldDir)
    {
        float lenSq = worldDir.LengthSquared();
        if (lenSq > 1e-6f)
        {
            float len = MathF.Sqrt(lenSq);
            float fraction = len > 1f ? 1f : len;
            return (worldDir / len, fraction);
        }
        return (Vector2.Zero, 0f);
    }

    /// <summary>The shared vertical-physics collision core behind both the camera-relative player
    /// <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// and the world-space AI <see cref="StepTowards(in MoveState, Vector2, bool, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>.
    /// Both callers resolve their input to the same shape - a unit <paramref name="moveDir"/> (XZ) plus a
    /// <paramref name="speedFraction"/> in [0,1] - then hand off here, so terrain follow, swept collide-and-slide,
    /// step-up, the slope gate, and the bounds clamp are byte-for-byte the same for player and AI.</summary>
    private static MoveState StepCore(in MoveState state, Vector2 moveDir, float speedFraction, bool run, bool jump,
        float dt, Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium)
    {
        MoveState s = state;
        MoveTuning t = tuning;

        CapsuleShape capsule = CapsuleFor(t);
        float halfH = t.CapsuleHalfHeight;

        // Signed step-climb signal (E1/E4): the vertical rate the presentation smoother reads instead of estimating
        // climb from position deltas. On a detected continuous paced run it is stamped from `climbEwma` (the smoothed
        // ACHIEVED rise rate, below); on a discrete step-down grounded-hold it is a clamped negative; 0 otherwise.
        // Default 0 = not on a step climb.
        float climbRate = 0f;
        // EWMA of the actually-applied per-tick climb RATE over a run (carried on MoveState.ClimbRateEwma). Default 0 =
        // reset (not on a run), so a fall / jump / flat / single-riser never accumulates into it. Updated ONLY on a
        // continuous-run tick (step 4b), where ClimbRate is then stamped from it, so the exported signal converges to
        // the sim's true emergent rise rate and the render glide's hover/crest offset settles to ~0.
        float climbEwma = 0f;

        // DISCRETE-STEP mesh-offset impulse (E5, MoveState.StepDeltaY): the signed vertical delta an ISOLATED step commits
        // this tick - a step the CONTINUOUS climb signal declines (a one-off riser leaves climbRate 0). Captured at the two
        // discrete commit sites (the step-DOWN grounded-hold below records stepDownDeltaY; the step-UP seat is read from the
        // committed rise) and stamped at the END of step 4b, MUTUALLY EXCLUSIVE with climbRate: exported only when this tick
        // is NOT part of a continuous run (climbRate == 0), so the glide and the mesh offset never double-apply. Default 0 =
        // not a discrete step this tick.
        float stepDeltaY = 0f;
        bool stepDownSeated = false;   // the step-down grounded-hold (step 4a-down) seated a riser down this tick
        float stepDownDeltaY = 0f;     // its signed drop (pos.Y - startY, negative), the mesh-offset feed for a descent
        bool stepUpRose = false;       // the step-up mechanism raised the capsule this tick (a discrete mount OR a paced
                                       // step-rise tick above terrain) - the whole committed rise feeds the mesh offset,
                                       // not only the initial steppedUp mount (a doorstep paces up over a couple ticks)

        // A grounded capsule with NO horizontal command this tick is AT REST: its walkable-contact depenetration must
        // resolve vertically only, so it cannot creep down a tilted walkable prop surface (see the depenetration
        // passes). Gated on the command bit, not just grounded, because a fast run-climb is also grounded but needs
        // full-MTV horizontal depenetration to extract itself from the risers it embeds into.
        bool restHold = s.Grounded && speedFraction <= 0f;

        // 0. Fluid medium: sampled ONCE per step at the step-start feet position (identical to how the wade ramp
        //    reads it below), so the swim decision, the wade scale, and the buoyancy target all see one consistent
        //    medium this tick. Null provider => Dry everywhere => no swim can ever engage and the dry/wade path is
        //    byte-identical to the pre-swim behaviour (the swim block is gated on medium being non-null).
        MovementMedium medNow = medium is null ? MovementMedium.Dry : medium(s.Position.X, s.Position.Z, s.Position.Y - halfH);

        // Swim enter/exit with hysteresis (carried in s.Swimming so the band works across ticks): once submersion
        // crosses the higher enter fraction the character swims; it only drops back out below the lower exit fraction
        // (or on leaving the water). A gentle slope walked into the lake therefore flips exactly once at each
        // threshold, never flickering at the boundary. Only meaningful with a provider; dry land never swims.
        bool swimming = ResolveSwimming(s.Swimming, medNow, s.Position.Y - halfH, t);
        if (swimming)
            return SwimStep(s, moveDir, speedFraction, jump, dt, t, medNow, groundHeight, clampXz, halfH);

        // 1. Horizontal desired (UNCHANGED when dry): resolved unit move direction + speed fraction + terrain slope
        //    gate. The grounded/air scale composes with the wade scale (1 when no provider or out of water) and with
        //    the per-entity haste/slow multiplier (1 by default), so an unmodified dry path is untouched. An AIRBORNE
        //    tick under MoveTuning.AirMomentum takes the momentum resolve instead (CharacterMovement.Momentum.cs),
        //    which flies the carried velocity rather than recomputing the horizontal from scratch. Grounded ticks and
        //    the default (knob off) never reach it, so they are arithmetically identical to the pre-momentum step.
        float wade = WadeSpeedScale(s.Position.X, s.Position.Z, s.Position.Y - halfH, t, medium);
        float speedScale = (s.Grounded ? 1f : t.AirControl) * wade * s.SpeedScale;
        (float dx, float dz, float commandedSpeed) = DesiredHorizontalCore(s.Position.X, s.Position.Z, moveDir, speedFraction, run, dt, t, groundNormal, groundHeight, s.Position.Y - halfH, speedScale);
        Vector2 commandedVel = moveDir * commandedSpeed;
        if (t.AirMomentum && !s.Grounded)
            (dx, dz, commandedVel) = AirborneMomentumMove(s, moveDir, speedFraction, run, dt, t, groundNormal, groundHeight, wade);
        if (clampXz is not null) { Vector2 c = clampXz(dx, dz); dx = c.X; dz = c.Y; }

        // 2. Vertical integrate (UNCHANGED math): jump-buffer countdown, gravity, terminal clamp.
        bool jumpRequested = jump || s.JumpBufferRemaining > 0f;
        float jumpBuffer = jump ? t.JumpBuffer : MathF.Max(0f, s.JumpBufferRemaining - dt);
        float vVel = s.VerticalVelocity - t.Gravity * dt;
        if (vVel < -t.MaxFallSpeed) vVel = -t.MaxFallSpeed;
        float desiredY = s.Position.Y + vVel * dt;

        // 3. Candidate position: SWEEP from the current pose to the target (collide-and-slide), then settle.
        // The swept move can never cross a face (substepped to a fraction of the capsule radius), so the capsule
        // never begins a tick inside a wall - enforcing the depenetration contract for real. A one-sided building
        // mesh is now both rich (every triangle blocks) and trap-proof (no entry => no inner-face suck-through).
        Vector3 start = s.Position;
        Vector3 target = new(dx, desiredY, dz);
        Vector3 pos;
        bool propGrounded = false;
        bool steppedUp = false;
        float steppedFloorY = 0f;
        if (world is null)
        {
            pos = target;
        }
        else
        {
            pos = SweptMove(world, capsule, start, target, t, s.Grounded, restHold, groundHeight, out steppedUp, out steppedFloorY);

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
                // push-out is unchanged. (Future seam: a steep-face slide policy, if ever wanted, would branch here on
                // the non-walkable normal; today static-at-rest below the gate is the design, matching analytic terrain.)
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
        }

        // 4. Support floor (analytic terrain + a downward prop sweep). The physics world holds only props
        //    (terrain is analytic), so the floor is the HIGHER of the terrain height and the prop surface
        //    directly under the capsule. The prop surface comes from a downward capsule sweep from ABOVE the
        //    head, which stays accurate even when a fast jump-landing has plunged the capsule deep into a hull
        //    (the landing-tick depenetration can under-report a one-tick deep penetration and leave the capsule
        //    sunk; the sweep does not). The capsule never rests below this floor.
        //
        //    The prop sweep only CONTRIBUTES the floor when the capsule is airborne (landing/jumping onto a prop)
        //    or already standing on a prop (its carried Y is above the terrain slope-stick band). A capsule
        //    walking horizontally into a rising prop flank while grounded on terrain stays in the band, so the
        //    prop sweep is skipped and the depenetrate settle pass alone blocks it at the base (the perfect
        //    horizontal the prior version established is untouched, and a low dome cannot be walked up its side).
        float terrainGroundY = groundHeight(pos.X, pos.Z) + halfH;
        float groundY = terrainGroundY;
        if (steppedUp)
        {
            if (steppedFloorY > groundY) groundY = steppedFloorY;
            propGrounded = true;   // standing on the stepped ledge
        }
        // A small "standing on a prop" skin (NOT the larger GroundedEpsilon mount band): a capsule whose carried
        // Y is above terrain by more than this is genuinely on a prop, so the sweep keeps following the prop
        // surface (e.g. down the far side of a dome it mounted), while one at terrain level walking into a flank
        // is below it and the sweep stays off (depenetration blocks the base). Too large a skin would snap the
        // capsule off the prop surface onto terrain mid-descent and clip it into the prop.
        const float OnPropSkin = 0.05f;
        bool overProp = !s.Grounded || s.Position.Y > terrainGroundY + OnPropSkin || steppedUp;
        if (world is not null && overProp)
        {
            float probeStart = pos.Y + 2f * halfH;                 // above the head, clear of a standable prop
            float maxProbe = (probeStart - terrainGroundY) + 2f * halfH;
            // The hit must be a FLOOR genuinely under the capsule, not a graze on the SIDE of a prop the capsule is
            // pressed against. A swept-down capsule beside a tall prop (trunk / rock / building wall) grazes that
            // prop ~one radius off the axis; treating that as floor used to HAUL the capsule up the prop's side, or
            // hang it there mid-air, instead of letting it fall - the "float up trees/rocks/walls" bug. Two guards:
            // (a) the contact is walkable-up (n.Y >= cos(maxSlope)), so a steep wall face / degenerate zero-normal
            // side contact is rejected; and (b) its XZ point sits under the capsule footprint (near the axis), not
            // out at the side. A real prop top under the feet passes both; a sideways graze fails both.
            float cosMaxSlope = MathF.Cos(t.MaxSlopeRadians);
            if (world.SweepCapsule(capsule, Pose.At(new Vector3(pos.X, probeStart, pos.Z)),
                    -Vector3.UnitY, maxProbe, out SweepHit floorHit) &&
                floorHit.Normal.Y >= cosMaxSlope &&
                UnderFootprint(floorHit.Point, pos, capsule.Radius))
            {
                float propCentreY = probeStart - floorHit.Distance; // capsule centre resting on the prop surface
                // Accept only a surface the capsule rests ON / lands ON / mounts within a step - NOT a downward-
                // facing overhang (eave / awning / soffit) the capsule is BELOW. Bepu's downward sweep registers a
                // thin overhang quad from above with an up-pointing contact normal that passes the walkable-up guard,
                // so without this height cap a capsule jumping up under an eave has its feet snapped onto it - the
                // "float up onto the awning/roof" bug. A real floor/dome-top under the feet sits at or just above the
                // capsule centre (the swept move stops the capsule there); an overhang sits well above it.
                if (propCentreY > groundY && propCentreY <= pos.Y + t.StepHeight) groundY = propCentreY;
            }
        }

        // 4-tread-find. The downward capsule sweep above MISSES the tread at a staircase BASE when the footprint
        // STRADDLES a tread shallower than the capsule diameter: the sweep grazes the vertical riser front face, returns
        // a steep, off-footprint normal both of its guards (walkable-up + under-footprint) reject, and groundY stays at
        // terrainGroundY. A single step below, that terrain sits within GroundedEpsilon of the partially-mounted capsule,
        // so the onGround snap DROPS it a whole riser back onto the flat - the sticky / collapse-bob bottom stair (mid
        // climb the terrain is metres below, so the same miss collapses to nothing reachable and never shows; only the
        // base misbehaves). When the sweep contributed NO support (groundY still at terrain) yet the capsule was grounded
        // and elevated on a step, drop a RADIUS-LESS ray fan over the footprint: a ray finds the tread top through the
        // clear air above it where the full-radius sweep cannot. If a walkable tread sits in the step band (at or above
        // the feet, within StepHeight), set groundY to it and the normal onGround snap seats the capsule UP onto the
        // tread instead of collapsing; the paced step-up (4b) still caps the rise, so the mount stays smooth. The fan
        // sources only real raycast hits at/above the feet, so it cannot invent support in open air: a genuine ledge
        // walk-off (open air below the feet) finds no tread, groundY stays at terrain, and gravity still releases the
        // capsule - the ledge-release invariant holds. Guarded behind the sweep miss + grounded + elevated, so it runs
        // only on the rare base-handoff tick, never on a normal climb or flat tick.
        // Gate on the PREVIOUS tick's elevation (s.Position.Y), not the current pos.Y: the collapse tick has already
        // depenetrated the deeply-embedded footprint back down toward terrain by the time this runs, so the current Y
        // reads at the flat and would skip the fix - it is precisely that collapse we are catching. The same reason the
        // overProp sweep above gates on s.Position.Y. treadCentreY is bounded into the step band by the fan itself, so
        // the upper guard only defends against a marginal skin overshoot.
        //
        // Scoped to the BASE by pos.Y <= terrainGroundY + GroundedEpsilon: the collapse ONLY fires where the onGround
        // snap below can reach the terrain, i.e. within one GroundedEpsilon of it - the first riser. A step or more up,
        // the capsule sits well above that band, so onGround never snaps it to terrain and there is nothing to fix; MID
        // CLIMB the same sweep miss is harmless (the terrain is metres below, out of the snap band), so the fan MUST NOT
        // run there or it would re-seat the height on a mid-climb miss and add penetration on a fast small-radius run.
        // This fires the fan on exactly the ticks the collapse would fire the snap-down, and nowhere else.
        if (world is not null && s.Grounded && groundY <= terrainGroundY + 1e-4f &&
            s.Position.Y > terrainGroundY + OnPropSkin && pos.Y <= terrainGroundY + t.GroundedEpsilon &&
            WalkableTreadUnderFeet(world, capsule, pos, t, out float treadCentreY) &&
            treadCentreY > groundY && treadCentreY <= pos.Y + t.StepHeight + SkinWidth)
        {
            groundY = treadCentreY;
        }

        bool grounded;
        float tSinceGround;
        // The swept resolver leaves the capsule at SkinWidth above a surface (not flush). Extend the landing
        // threshold by SkinWidth so a swept-settled capsule on a flat prop top counts as grounded on the first
        // contact tick (without this, pos.Y = groundY + SkinWidth > groundY and the capsule falls forever).
        bool onGround = vVel <= 0f && (pos.Y <= groundY + (world is not null ? SkinWidth : 0f) || (s.Grounded && pos.Y <= groundY + t.GroundedEpsilon));
        if (onGround) pos.Y = groundY;          // snap onto the support surface (generalizes the old terrain clamp)
        if (pos.Y < groundY) pos.Y = groundY;   // and never rest below it, even on a tick that is not "onGround"
        if (onGround || propGrounded)
        {
            grounded = true;
            tSinceGround = 0f;
            if (vVel < 0f) vVel = 0f;            // landed on terrain or a prop -> stop falling
        }
        else
        {
            grounded = false;
            tSinceGround = s.TimeSinceGrounded + dt;
        }

        // 4a. Stair-climb ground-stick (with a step-contact hysteresis). The paced step-up (4b) commits the capsule
        //     BELOW the tread it is mounting, so on the ticks BETWEEN mounts (no step-up fired, the paced feet drifted
        //     more than a StepHeight below the tread ahead) step 4's downward capsule probe - which returns only the
        //     HIGHEST surface under the footprint and rejects it as too high - reports no support, and `grounded` flips
        //     false with a one-tick gravity dip. On a stair that reads as the character stuttering airborne between
        //     steps and spamming the fall animation. But the capsule is still resting on the LOWER treads its footprint
        //     spans: if it was grounded last tick, is above the terrain floor (genuinely on a step run, not on flat
        //     ground), is not rising ballistically (a jump owns vVel > 0), and a walkable surface still sits within
        //     reach beneath its feet, it has not left the ground - hold it grounded. Uses the same feet-down ray fan
        //     the wall slide trusts for "am I supported" (rays never hit the zero-normal degeneracy a capsule sweep
        //     can).
        //
        //     STEP-CONTACT HYSTERESIS (the terrain/prop handoff): mounting the FIRST riser at an angle, the freshly
        //     lifted footprint can sit a hair IN FRONT of / straddling the riser edge, so no tread is under the feet
        //     (the ray fan misses) yet the body is still EMBEDDED in the riser it is climbing - a step contact plainly
        //     remains. The bare ray fan flipped grounded false there for a tick, and the avatar's snap-to-physics
        //     branch (which fires on !Grounded) popped the model + camera: root cause C's angled first-riser grounded
        //     flicker. So also hold grounded when the capsule OVERLAPS a static (ComputePenetration) - the honest "a
        //     step contact remains" signal that the feet-ray fan cannot see through the riser. This holds only the
        //     grounded FLAG (not pos.Y or the support height), so it cannot stall or float the paced climb - the
        //     step-up/pacing still drive the rise; it only stops the spurious airborne pop. Ledge-safe: a genuine walk
        //     off a ledge leaves the capsule in OPEN AIR (no overlap AND no floor under the feet), so both signals are
        //     false and it releases and falls (the grounded-stick ledge-release pin holds). The penetration query is
        //     guarded behind the ray-fan miss, so it runs only on the rare handoff tick, never on a normal climb tick.
        if (!grounded && vVel <= 0f && s.Grounded && world is not null &&
            pos.Y > terrainGroundY + OnPropSkin &&
            (WalkableFloorUnderFeet(world, capsule, pos, t) || world.ComputePenetration(capsule, Pose.At(pos), out _)))
        {
            grounded = true;
            tSinceGround = 0f;
            if (vVel < 0f) vVel = 0f;
        }

        // 4a-down. Step-DOWN grounded-hold. Walking OFF a step whose DROP is within StepHeight is a step, not a fall:
        //     it must stay grounded and seat onto the support one riser below, NOT go airborne. The onGround stick above
        //     only reaches GroundedEpsilon (0.30) below the feet, so a drop between GroundedEpsilon and StepHeight - e.g.
        //     a ~0.4 m door step - slips past it: `grounded` flips false, gravity spikes vVel a few m/s over the next
        //     few ticks, and the render-height smoother reads that as ballistic and hard-cuts (the "very glitchy going
        //     down" flap on those steps). Extend the stick to StepHeight for the descent, scoped tightly to the
        //     step-down transition:
        //       - `s.Grounded` (grounded LAST tick): this fires ONLY on the FIRST tick of leaving the ground, so a real
        //         FALL (airborne, hence !s.Grounded, on every tick after the first) is never caught mid-air and landed
        //         early - only the tick you actually step off a ledge is a candidate;
        //       - the drop from the last grounded height to the resolved support is strictly positive and at MOST
        //         StepHeight (`0 < s.Position.Y - groundY <= StepHeight`): a DESCENT (ascent has support at/above the
        //         feet, so this is <= 0 and skips), and a genuine ledge walk-off beyond StepHeight (the ledge-release
        //         pin's 3 m box) exceeds the band and FALLS as before; and
        //       - `groundY` is the resolved walkable support (terrain + walkable-normal props), so there is really a
        //         surface within a step below - open air past the drop leaves groundY at the far floor and it falls.
        //     Seat pos.Y onto that support this tick (a one-tick step-down snap, exactly what a within-GroundedEpsilon
        //     step-down already does); the render-height smoother glides the grounded drop instead of hard-cutting a
        //     ballistic one.
        if (!grounded && vVel <= 0f && s.Grounded)
        {
            float stepDrop = s.Position.Y - groundY;
            if (stepDrop > 0f && stepDrop <= t.StepHeight)
            {
                pos.Y = groundY;
                grounded = true;
                tSinceGround = 0f;
                if (vVel < 0f) vVel = 0f;
                // Discrete step-DOWN (E5): this is an ISOLATED step-down - a doorstep-sized drop between GroundedEpsilon
                // (0.30) and StepHeight (0.40) the onGround stick did not catch. Record its seated drop as the DISCRETE-STEP
                // impulse, NOT a continuous ClimbRate. An isolated one-tick drop is a step the CONTINUOUS glide cannot
                // smooth: its signal-gated glide renders raw the very next tick (ClimbRate back to 0), snapping the drop -
                // the "very glitchy going down" pop - whereas the mesh-offset layer, decaying in render time, carries it.
                // So climbRate stays 0 here and the drop rides StepDeltaY. A CONTINUOUS run descent instead reports its
                // rate through step 4b's descent branch (which sets climbRate != 0 and so, via the discrete stamp's gate
                // below, suppresses this candidate), keeping a real descent staircase on its continuous signal. A drop
                // within GroundedEpsilon never reaches here (the onGround stick catches it), so a sub-perceptible
                // micro-step-down leaves BOTH signals 0 - the honest dead-zone.
                stepDownSeated = true;
                stepDownDeltaY = pos.Y - s.Position.Y;   // = -stepDrop (negative): the seated drop the mesh offset eases
            }
        }

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
        if (vVel <= 0f && world is not null && t.MaxStepClimbSpeed > 0f)
        {
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
        }

        // DISCRETE-STEP mesh-offset stamp (E5), MUTUALLY EXCLUSIVE with the continuous climb signal. When this tick is NOT
        // part of a continuous run - climbRate is still 0 (neither the ascent EWMA nor the descent-grade branch of step 4b
        // fired) - export the committed vertical delta of a DISCRETE step so a client-side mesh smoother can ease the
        // isolated riser the continuous glide declines. A continuous run leaves climbRate != 0, so nothing is stamped here
        // (the glide already owns that smoothing - no double-apply). Two discrete sources:
        //   - step-DOWN: the grounded-hold (step 4a-down) seated a riser down this tick (stepDownSeated) -> its signed drop.
        //   - step-UP: a discrete riser was mounted this tick (steppedUp) while grounded and not rising ballistically ->
        //     the committed rise (MathF.Max(0, .) so a fall-onto-a-step, a net drop, cannot masquerade as a step-up). This
        //     ALSO fires on the FIRST riser of a run, before the continuous signal engages (climbRate still 0 there), so a
        //     run ENTRY is softened too and then hands off to the glide as the signal comes up and this offset decays out.
        // A fall / jump never reaches here as a step (a jump rises ballistically so steppedUp+grounded+vVel<=0 is false, and
        // a plain landing has s.Grounded == false so the step-down hold never armed), so a landing is not a step event and
        // the fall-sink cannot recur through this layer.
        if (climbRate == 0f)
        {
            if (stepDownSeated) stepDeltaY = stepDownDeltaY;
            else if (stepUpRose && grounded && vVel <= 0f) stepDeltaY = MathF.Max(0f, pos.Y - s.Position.Y);
        }

        // 5. Jump after contact (UNCHANGED): grounded or within coyote-time, consume both windows.
        if (jumpRequested && (grounded || tSinceGround <= t.CoyoteTime))
        {
            vVel = t.JumpSpeed;
            grounded = false;
            tSinceGround = t.CoyoteTime + dt;
            jumpBuffer = 0f;
        }

        var result = new MoveState
        {
            Position = pos,
            VerticalVelocity = vVel,
            Grounded = grounded,
            TimeSinceGrounded = tSinceGround,
            JumpBufferRemaining = jumpBuffer,
            ClimbRate = climbRate,
            ClimbRateEwma = climbEwma,
            StepDeltaY = stepDeltaY,
            SpeedScale = state.SpeedScale,   // a movement INPUT: carried through unchanged, never derived by the step
            CommandedVelocity = commandedVel, // a per-tick OUTPUT: what the step asked for, before anything denied it
            // Carried inertia, stamped on EVERY tick (grounded included) and consumed only when AirMomentum is on:
            // the intended velocity clipped to what survived, so collision can shed it but never inject into it.
            HorizontalVelocity = ClipToAchieved(commandedVel, start, pos, dt),
        };
        // Defense-in-depth: a finite input state must never produce a non-finite result. A pathological command is
        // gated out upstream, but a misbehaving ground/bound/tuning value could inject a NaN/Inf that would slip
        // past every clamp and replicate; hold the last good state instead of propagating a poisoned position.
        return IsFinite(result.Position) && float.IsFinite(result.VerticalVelocity) &&
               float.IsFinite(result.ClimbRate) && float.IsFinite(result.ClimbRateEwma) &&
               float.IsFinite(result.StepDeltaY) && IsFinite(result.HorizontalVelocity)
            ? result : state;
    }

    /// <summary>True when every component of <paramref name="v"/> is finite (neither NaN nor infinite).</summary>
    private static bool IsFinite(in Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>True when an XZ contact point sits under the capsule's footprint (a floor the capsule rests ON),
    /// as opposed to a graze ~one radius off the axis on the SIDE of an adjacent prop the capsule is pressed
    /// against. The steepest walkable contact sits at about radius*sin(maxSlope) off-axis (~0.8 r for a 45-50 deg
    /// gate); a vertical-side graze sits at the full radius. The 0.9 r cutoff separates them.</summary>
    private static bool UnderFootprint(in Vector3 point, in Vector3 capsuleCentre, float radius)
    {
        float dx = point.X - capsuleCentre.X, dz = point.Z - capsuleCentre.Z;
        float lim = 0.9f * radius;
        return dx * dx + dz * dz <= lim * lim;
    }

    /// <summary>The upright capsule for a tuning: radius + cylindrical length so total height = 2*halfHeight.</summary>
    public static CapsuleShape CapsuleFor(in MoveTuning tuning)
        => new(tuning.CapsuleRadius, MathF.Max(0.01f, 2f * tuning.CapsuleHalfHeight - 2f * tuning.CapsuleRadius));

    // Desired world XZ position after applying the resolved horizontal move (unit direction + speed fraction) and the slope gate,
    // WITHOUT collision. The direction + speed fraction are resolved upstream from either a camera-relative MoveCommand
    // (ResolveCameraRelative) or a world-space steering direction (ResolveWorldDir), so player and AI share this exact input/slope
    // section. Prop collision is resolved separately by the swept collide-and-slide block in StepCore. moveDir is a unit vector when
    // speedFraction > 0. The advance + gate itself is AdvanceSlopeGated (CharacterMovement.Momentum.cs), shared with the airborne
    // momentum path, and it is DIRECTION-AWARE: a too-steep destination refuses the move only while its ground stands above feetY
    // (the capsule centre minus the half-height), so walking off a cliff falls through to gravity while climbing one is refused.
    // Returns the resolved speed alongside the position so StepCore can export it as MoveState.CommandedVelocity: it is reported
    // UNCONDITIONALLY, including on a slope-gate block, because the anomaly check needs the ask, not what survived. 0 when idle.
    private static (float x, float z, float speed) DesiredHorizontalCore(float x, float z, Vector2 moveDir,
        float speedFraction, bool run, float dt, in MoveTuning tuning, Func<float, float, Vector3>? groundNormal,
        Func<float, float, float> groundHeight, float feetY, float speedScale)
    {
        bool moving = speedFraction > 0f;
        float speed = moving ? (run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale * speedFraction : 0f;
        (x, z) = AdvanceSlopeGated(x, z, moveDir * speed, moving, dt, tuning, groundNormal, groundHeight, feetY);
        return (x, z, speed);
    }
}
