using System;
using System.Numerics;
using KhaozEngine.Collision;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Character locomotion: the single movement step run by the local controller, the authoritative server sim,
/// and client-side prediction alike. Two overloads share one horizontal core (camera-relative move, normalized
/// diagonals, walk/run speed, optional slope gate + static-collision resolve + bounds clamp):
/// <list type="bullet">
/// <item><see cref="Step(Vector3, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, WorldColliders?)"/>
/// is the original horizontal-only step: Y is a pure function of XZ (ground + half-height), no air.</item>
/// <item><see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, WorldColliders?, Func{float, float, Vector2}?)"/>
/// is the vertical-physics step: gravity, jump (coyote + jump-buffer), land/clamp, and air control over the
/// carried <see cref="MoveState"/>.</item>
/// </list>
/// No input, render, physics, or netcode dependency.
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
    /// <param name="colliders">Optional static-world colliders; when given, the capsule footprint
    /// (<see cref="MoveTuning.CapsuleRadius"/>) is pushed out of any prop/building it overlaps (slide along
    /// surfaces). Null or empty leaves the XZ untouched. The same set + math runs on server and client.</param>
    /// <returns>The advanced position (Y on the ground + half-height).</returns>
    public static Vector3 Step(Vector3 position, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldColliders? colliders = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        (float x, float z) = ResolveHorizontal(position.X, position.Z, cmd, dt, tuning, groundNormal, colliders,
            clampXz: null, speedScale: 1f);
        var result = new Vector3(x, groundHeight(x, z) + tuning.CapsuleHalfHeight, z);
        // Defense-in-depth: never return a non-finite position from a finite input. A pathological command is
        // already neutralized by the move gate, but a misbehaving groundHeight/bound could still inject a NaN/Inf
        // that slips past every clamp and replicates to every client in range; hold the last good position instead.
        return IsFinite(result) ? result : position;
    }

    /// <summary>Vertical-physics step: horizontal move (air-controlled while airborne) plus gravity, ground
    /// contact, and jump (coyote-time + jump-buffer), evolving the carried <see cref="MoveState"/>. The same step
    /// runs authoritatively on the server and in client prediction, so state must round-trip identically.</summary>
    /// <param name="state">The carried kinematic state (position + vertical velocity + grounded + feel timers).</param>
    /// <param name="cmd">Movement intent including the jump bit.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Speed/half-height/slope + gravity/jump/fall/feel constants.</param>
    /// <param name="groundNormal">Optional ground normal; when given, gates a horizontal step by slope.</param>
    /// <param name="colliders">Optional static-world colliders (capsule footprint push-out).</param>
    /// <param name="clampXz">Optional XZ clamp (e.g. a play-area bound); applied after the move/collide so the
    /// vertical axis is then resolved at the clamped position. The same delegate runs on server and client.</param>
    /// <param name="surfaces">Optional walkable prop/building surfaces; when given, the support height the capsule
    /// lands/rests on is <c>max(terrain, surface)</c> (stand on / jump onto rocks + roofs), the static-collision
    /// push-out becomes height-aware (a prop's side blocks only while the feet are below the walkable surface under
    /// the player, so standing on a domed rock - whose surface sits below its peak - is not shoved off), and a
    /// support rise no greater than <see cref="MoveTuning.StepHeight"/> is auto-mounted (step-up). Null = terrain
    /// only, unchanged. The same set + math runs on server and client.</param>
    /// <returns>The advanced <see cref="MoveState"/>.</returns>
    public static MoveState Step(in MoveState state, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldColliders? colliders = null,
        Func<float, float, Vector2>? clampXz = null, WorldSurfaces? surfaces = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        // Support height = the higher of the terrain and any walkable prop surface under (x, z).
        float Support(float sx, float sz)
        {
            float g = groundHeight(sx, sz);
            float? s = surfaces?.Query(sx, sz);
            return s.HasValue && s.Value > g ? s.Value : g;
        }

        // 1. Horizontal: full control while grounded, scaled by AirControl while airborne. The capsule foot Y makes
        //    the static-collision push-out height-aware: a prop's side blocks only while the feet are below the
        //    WALKABLE SURFACE where you would step onto it (the rim toward you), so standing on a domed rock is not
        //    shoved off AND the dome is mountable by walking/jumping up its side. No surfaces -> Top-only gating.
        float footY = state.Position.Y - tuning.CapsuleHalfHeight;
        float supBefore = Support(state.Position.X, state.Position.Z);
        float speedScale = state.Grounded ? 1f : tuning.AirControl;
        (float x, float z) = ResolveHorizontal(state.Position.X, state.Position.Z, cmd, dt, tuning, groundNormal,
            colliders, clampXz, speedScale, footY, surfaces);

        // 1b. Step-up gate: while grounded, a support rise taller than the step height is a wall (revert the move).
        if (state.Grounded && Support(x, z) - supBefore > tuning.StepHeight) { x = state.Position.X; z = state.Position.Z; }

        // 2. Jump-buffer countdown: a press arms it for JumpBuffer seconds; otherwise it bleeds down.
        //    A jump is requested if pressed this tick or still within a buffered window from a recent press.
        bool jumpRequested = cmd.Jump || state.JumpBufferRemaining > 0f;
        float jumpBuffer = cmd.Jump ? tuning.JumpBuffer : MathF.Max(0f, state.JumpBufferRemaining - dt);

        // 3. Gravity integrate (clamp to terminal fall speed).
        float vVel = state.VerticalVelocity - tuning.Gravity * dt;
        if (vVel < -tuning.MaxFallSpeed) vVel = -tuning.MaxFallSpeed;
        float y = state.Position.Y + vVel * dt;

        // 4. Ground contact onto the support height (terrain or a prop top), with a grounded skin so a downhill
        //    run does not jitter grounded/airborne.
        float groundY = Support(x, z) + tuning.CapsuleHalfHeight;
        bool grounded;
        float tSinceGround;
        if (vVel <= 0f && (y <= groundY || (state.Grounded && y <= groundY + tuning.GroundedEpsilon)))
        {
            y = groundY;
            vVel = 0f;
            grounded = true;
            tSinceGround = 0f;
        }
        else
        {
            grounded = false;
            tSinceGround = state.TimeSinceGrounded + dt;
        }

        // 5. Jump after contact (so a buffered jump fires on the landing tick): grounded or within coyote-time,
        //    and a jump is requested. Consume both windows so there is no double-jump at the apex.
        if (jumpRequested && (grounded || tSinceGround <= tuning.CoyoteTime))
        {
            vVel = tuning.JumpSpeed;
            grounded = false;
            tSinceGround = tuning.CoyoteTime + dt;   // out of coyote: no second jump at the apex
            jumpBuffer = 0f;                         // consume the buffered request
        }

        var result = new MoveState
        {
            Position = new Vector3(x, y, z),
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

    /// <summary>The unconstrained horizontal target the camera-relative move would reach in one step, before the
    /// slope gate, static collision, or play-area clamp deny any of it. The XZ distance from this to the position a
    /// constrained <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, WorldColliders?, Func{float, float, Vector2}?, WorldSurfaces?)"/>
    /// actually produced is the authoritative "correction" the server applied this tick - a server-side anti-cheat
    /// signal: a client repeatedly driving into a wall, slope, or boundary keeps this large. Pass
    /// <paramref name="speedScale"/> = the value the step used (1 grounded, <see cref="MoveTuning.AirControl"/>
    /// airborne) so the comparison isolates only the denial, not the air-control scaling. Mirrors the basis +
    /// speed of <see cref="ResolveHorizontal"/> (pre-gate).</summary>
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

    /// <summary>Shared horizontal resolve: camera-relative move (normalized diagonals, walk/run speed scaled by
    /// <paramref name="speedScale"/>), optional slope gate, static-collision push-out, then optional XZ clamp.</summary>
    private static (float x, float z) ResolveHorizontal(float x, float z, in MoveCommand cmd, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, WorldColliders? colliders,
        Func<float, float, Vector2>? clampXz, float speedScale, float footY = float.PositiveInfinity,
        WorldSurfaces? surfaces = null)
    {
        // Camera-relative ground basis (matches FollowCamera3D's yaw convention).
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);

        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);   // normalized diagonals
            float speed = (cmd.Run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale;
            float nx = x + move.X * speed * dt;
            float nz = z + move.Z * speed * dt;

            bool blocked = false;
            if (groundNormal is not null)
            {
                float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
                if (MathF.Acos(ny) > tuning.MaxSlopeRadians) blocked = true;
            }
            if (!blocked) { x = nx; z = nz; }
        }

        // Static-world collision: push the capsule footprint out of any prop/building it now overlaps, sliding
        // along surfaces. Height-aware when a finite footY is supplied: a prop's side blocks only while the feet are
        // below the walkable surface where the capsule would step onto it (the rim toward the player), so standing on
        // a rock/roof is not shoved off AND a domed prop is mountable from its side. Null/empty set leaves XZ untouched.
        if (colliders is not null && !colliders.IsEmpty)
        {
            Vector2 resolved = float.IsInfinity(footY)
                ? colliders.Resolve(new Vector2(x, z), tuning.CapsuleRadius)
                : colliders.Resolve(new Vector2(x, z), tuning.CapsuleRadius, footY, surfaces);
            x = resolved.X;
            z = resolved.Y;
        }

        // Optional play-area clamp (clamp-and-slide), applied after the move/collide.
        if (clampXz is not null)
        {
            Vector2 c = clampXz(x, z);
            x = c.X;
            z = c.Y;
        }

        return (x, z);
    }
}
