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
public static class CharacterMovement
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
        (float x, float z) = DesiredHorizontalCore(position.X, position.Z, moveDir, speedFraction, cmd.Run, dt,
            tuning, groundNormal, speedScale: wade);
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
        //    gate. The grounded/air scale composes with the wade scale (1 when no provider or out of water, so the
        //    dry path is untouched).
        float wade = WadeSpeedScale(s.Position.X, s.Position.Z, s.Position.Y - halfH, t, medium);
        float speedScale = (s.Grounded ? 1f : t.AirControl) * wade;
        (float dx, float dz) = DesiredHorizontalCore(s.Position.X, s.Position.Z, moveDir, speedFraction, run, dt, t, groundNormal, speedScale);
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
            pos = SweptMove(world, capsule, start, target, t, s.Grounded, restHold, out steppedUp, out steppedFloorY);

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
                // TANGENT CO-PACE. The rise is throttled from the support's desired rise (pos.Y - start) down to the
                // climb cap (allowedRise = MaxStepClimbSpeed*dt). Scale the committed HORIZONTAL advance so the capsule
                // travels ALONG the stair surface at ~MaxStepClimbSpeed/grade instead of committing the full (run-speed)
                // horizontal while the Y is held. That mismatch raced the XZ ahead of the paced height, plowing the
                // capsule metres into the risers (sustained penetration) and strobing forward advance between a frozen 0
                // (monotone-hold / no support) and a full-tread catch-up. Co-pacing keeps a runner gliding up smoothly at
                // the honest grade-limited speed; a walk whose per-tick rise fits the climb cap is untouched (block
                // skipped), while a discrete mount tick is paced (and now co-paced) even at walk speed, smoothing walk mounts too.
                //
                // Gated on a CONTINUOUS climb - a MOUNTABLE next riser AHEAD within reach (NextRiserAhead). Anything
                // else is a single-riser seat that MUST keep its full forward commitment: a clear path ahead (a deep
                // tread or the top of the run), OR a steep face ahead that is NOT a climbable riser - a building's tall
                // back WALL a footprint behind the doorstep, or an overhang. Throttling any of those re-embeds the
                // load-bearing seat's footprint and re-creates the slow-walk mount stall (the 10.66 stall on a clear
                // tread; the 10.68 stall on a compound doorstep whose bare steep-face test misread the wall behind it
                // as a riser). NextRiserAhead confirms a mountable, strictly-higher tread with the same step-up probe
                // the mount uses, so it is geometry-robust where a support-under-the-feet probe is not (analytic
                // terrain in front of a placed staircase is invisible to a downward ray) and cannot misread a wall as
                // a riser.
                float desiredRise = pos.Y - s.Position.Y;
                float allowedRise = climbCap - s.Position.Y;
                float hx = pos.X - s.Position.X, hz = pos.Z - s.Position.Z;
                float hLen = MathF.Sqrt(hx * hx + hz * hz);
                if (desiredRise > allowedRise && allowedRise > 0f && hLen > 1e-6f &&
                    NextRiserAhead(world, capsule, pos, new Vector2(hx, hz), t))
                {
                    // The horizontal the co-pace permits: the tangent for the actual grade (allowedRise/grade), but with
                    // a floor of allowedRise/MaxClimbGrade so a DISCRETE whole-riser step-up (desiredRise = a full riser,
                    // hLen a fraction of a tread) cannot inflate the apparent grade and throttle the advance to a crawl
                    // (which stalled the first-riser mount). MaxClimbGrade is the steepest grade paced to the exact
                    // surface tangent; a steeper stair advances a touch faster than the surface (bounded sub-tread lead
                    // the depenetrate/support pass absorbs), which is safe, where under-advancing would float the capsule
                    // off the surface and drop it.
                    float tangent = allowedRise / desiredRise;                 // exact tangent throttle (small if inflated)
                    float floor = allowedRise / (MaxClimbGrade * hLen);        // grade floor: horiz >= allowedRise/MaxClimbGrade
                    float throttle = MathF.Min(1f, MathF.Max(tangent, floor));
                    pos.X = s.Position.X + hx * throttle;
                    pos.Z = s.Position.Z + hz * throttle;
                }
                // Cap the vertical RISE to MaxStepClimbSpeed (the smooth, steady ascent) and hold the movement state
                // grounded through the paced climb: the real support (the tread the step-up mounted) sits ABOVE this
                // committed height, so the capsule is resting on the step run, not airborne - forcing grounded here
                // stops step 4's support probe (which lags the paced feet) from flickering Grounded false and spamming
                // the fall animation. Only reached when NOT rising ballistically (vVel <= 0), so a jump is untouched.
                pos.Y = climbCap;
                grounded = true;
                tSinceGround = 0f;
                if (vVel < 0f) vVel = 0f;
            }
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
        };
        // Defense-in-depth: a finite input state must never produce a non-finite result. A pathological command is
        // gated out upstream, but a misbehaving ground/bound/tuning value could inject a NaN/Inf that would slip
        // past every clamp and replicate; hold the last good state instead of propagating a poisoned position.
        return IsFinite(result.Position) && float.IsFinite(result.VerticalVelocity) ? result : state;
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
    /// pure provider sample.</summary>
    private static MoveState SwimStep(in MoveState state, Vector2 moveDir, float speedFraction, bool jump, float dt,
        in MoveTuning t, in MovementMedium medium, Func<float, float, float> groundHeight,
        Func<float, float, Vector2>? clampXz, float halfH)
    {
        // Horizontal: swim speed (run has no effect while swimming), scaled by the medium's zone multiplier so a
        // swamp/current still composes, clamped >= 0 so a hostile negative zone scale cannot reverse travel. Uses the
        // resolved unit move direction + speed fraction shared with the walk path (so the camera-relative player and
        // the world-space AI swim identically), at SwimSpeed.
        float zoneScale = medium.WadeSpeedScale < 0f ? 0f : medium.WadeSpeedScale;
        float x = state.Position.X, z = state.Position.Z;
        if (speedFraction > 0f)
        {
            float speed = t.SwimSpeed * zoneScale * speedFraction;
            x += moveDir.X * speed * dt;
            z += moveDir.Y * speed * dt;
        }
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
        };
        // Defense-in-depth (as the land path): a finite input must never yield a non-finite result; hold the last
        // good state if a misbehaving provider/ground/tuning injected a NaN/Inf.
        return IsFinite(result.Position) && float.IsFinite(result.VerticalVelocity) ? result : state;
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

    /// <summary>The unconstrained horizontal target the camera-relative move would reach in one step, before the
    /// slope gate, static collision, or play-area clamp deny any of it. The XZ distance from this to the position a
    /// constrained <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// actually produced is the authoritative "correction" the server applied this tick - a server-side anti-cheat
    /// signal: a client repeatedly driving into a wall, slope, or boundary keeps this large. Pass
    /// <paramref name="speedScale"/> = the value the step used (1 grounded, <see cref="MoveTuning.AirControl"/>
    /// airborne) so the comparison isolates only the denial, not the air-control scaling. Mirrors the basis +
    /// speed of <see cref="DesiredHorizontalCore"/> (pre-gate).</summary>
    public static Vector2 IntendedHorizontalTarget(Vector3 position, in MoveCommand cmd, float dt,
        in MoveTuning tuning, float speedScale = 1f)
    {
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);
        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        float x = position.X, z = position.Z;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);
            float speed = (cmd.Run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale;
            x += move.X * speed * dt;
            z += move.Z * speed * dt;
        }
        return new Vector2(x, z);
    }

    /// <summary>The upright capsule for a tuning: radius + cylindrical length so total height = 2*halfHeight.</summary>
    public static CapsuleShape CapsuleFor(in MoveTuning tuning)
        => new(tuning.CapsuleRadius, MathF.Max(0.01f, 2f * tuning.CapsuleHalfHeight - 2f * tuning.CapsuleRadius));

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
    // Contact counts as a wall/riser (step-up candidate) rather than a floor/ceiling when |normal.Y| is small.
    private const float StepUpNormalY = 0.5f;
    // Tangent co-pace grade floor (step 4b): the steepest stair grade (rise/run) paced to the EXACT surface tangent.
    // On a steeper stair the co-paced horizontal advances a touch faster than the surface rises - a bounded, sub-tread
    // lead the depenetrate/support pass absorbs - which is the safe side; under-advancing would float the capsule off
    // the surface between mounts and drop it. It also floors the throttle so a discrete whole-riser step-up cannot
    // inflate the apparent grade and crawl the mount to a stall. ~37 deg; below the 45 deg default slope gate so a
    // normal ramp is never in this regime, and near the 0.30/0.40 dungeon/consumer stair grade (0.75) it wires.
    private const float MaxClimbGrade = 0.72f;
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
        in MoveTuning t, bool grounded, bool restHold, out bool steppedUp, out float steppedFloorY)
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
            pos = SlideSubstep(world, capsule, pos, stepDelta, t, grounded, restHold, out bool stepped, out float floorY);
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
        in MoveTuning t, bool grounded, bool restHold, out bool steppedUp, out float steppedFloorY)
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

            // Step-up: only while grounded, only over a near-vertical contact (a riser/wall), only on the
            // horizontal remainder. Climbs a stair tread; a real wall has no ledge within StepHeight so it slides.
            if (grounded && MathF.Abs(n.Y) < StepUpNormalY &&
                TryStepUp(world, capsule, pos, remaining, n, t, out Vector3 stepped))
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

    // Recovery sweep: pull back this many radii along -dir (a provably clear start, since a tangent capsule touches
    // at the surface) and re-sweep this many radii forward to read the contact normal. The re-contact sits at
    // t = RecoverBackRadii * radius; the forward range is wider because Bepu's mesh sweep does not report a hit that
    // lands in the far portion of the swept range (empirically it must sit within ~half) - so it is swept to
    // RecoverSweepRadii * radius (> 2x the contact distance) to be registered reliably.
    private const float RecoverBackRadii  = 1f;
    private const float RecoverSweepRadii = 3f;

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

    /// <summary>Classic up/forward/down step probe over the horizontal remainder: sweep up by
    /// <see cref="MoveTuning.StepHeight"/> (headroom), sweep forward, sweep down; accept only if it lands on a
    /// walkable-slope ledge strictly higher than the start (a stair tread/curb). A vertical wall has no such ledge
    /// within StepHeight, so this returns false and the caller slides. Returns the stepped capsule centre.</summary>
    private static bool TryStepUp(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 remaining,
        in Vector3 contactNormal, in MoveTuning t, out Vector3 stepped)
    {
        stepped = pos;
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

        // 3. Down by StepHeight to settle onto the ledge; must be walkable slope and strictly higher than pos.
        if (!world.SweepCapsule(capsule, Pose.At(fwd), -Vector3.UnitY, step + SkinWidth, out SweepHit downHit))
            return false;
        if (downHit.Normal.Y < MathF.Cos(t.MaxSlopeRadians)) return false;   // too steep to stand on
        Vector3 landed = fwd; landed.Y -= MathF.Max(0f, downHit.Distance - SkinWidth);
        if (landed.Y <= pos.Y + 1e-4f) return false;                        // did not actually rise
        stepped = landed;
        return true;
    }

    // Desired world XZ position after applying the resolved horizontal move (unit direction + speed fraction) and the
    // slope gate, WITHOUT collision. The direction + speed fraction are resolved upstream from either a
    // camera-relative MoveCommand (ResolveCameraRelative) or a world-space steering direction (ResolveWorldDir), so
    // player and AI share this exact input/slope section; prop collision is resolved separately by the swept
    // collide-and-slide block in StepCore. moveDir is a unit vector when speedFraction > 0.
    private static (float x, float z) DesiredHorizontalCore(float x, float z, Vector2 moveDir, float speedFraction,
        bool run, float dt, in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, float speedScale)
    {
        if (speedFraction > 0f)
        {
            float speed = (run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale * speedFraction;
            float nx = x + moveDir.X * speed * dt;
            float nz = z + moveDir.Y * speed * dt;

            bool blocked = false;
            if (groundNormal is not null)
            {
                float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
                if (MathF.Acos(ny) > tuning.MaxSlopeRadians) blocked = true;
            }
            if (!blocked) { x = nx; z = nz; }
        }
        return (x, z);
    }
}
