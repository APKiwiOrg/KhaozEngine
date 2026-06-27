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
        return new Vector3(x, groundHeight(x, z) + tuning.CapsuleHalfHeight, z);
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
        //    the static-collision push-out height-aware. The side-block compares the feet against the WALKABLE SURFACE
        //    under the player, not the prop's max top, so standing on a domed rock (surface below its peak) is not
        //    mis-read as a side hit and shoved off. No surface here (terrain only) -> +inf -> Top-only gating.
        float footY = state.Position.Y - tuning.CapsuleHalfHeight;
        float surfaceTop = surfaces?.Query(state.Position.X, state.Position.Z) ?? float.PositiveInfinity;
        float supBefore = Support(state.Position.X, state.Position.Z);
        float speedScale = state.Grounded ? 1f : tuning.AirControl;
        (float x, float z) = ResolveHorizontal(state.Position.X, state.Position.Z, cmd, dt, tuning, groundNormal,
            colliders, clampXz, speedScale, footY, surfaceTop);

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

        return new MoveState
        {
            Position = new Vector3(x, y, z),
            VerticalVelocity = vVel,
            Grounded = grounded,
            TimeSinceGrounded = tSinceGround,
            JumpBufferRemaining = jumpBuffer,
        };
    }

    /// <summary>Shared horizontal resolve: camera-relative move (normalized diagonals, walk/run speed scaled by
    /// <paramref name="speedScale"/>), optional slope gate, static-collision push-out, then optional XZ clamp.</summary>
    private static (float x, float z) ResolveHorizontal(float x, float z, in MoveCommand cmd, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, WorldColliders? colliders,
        Func<float, float, Vector2>? clampXz, float speedScale, float footY = float.PositiveInfinity,
        float surfaceTop = float.PositiveInfinity)
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
        // below the surface they stand on (surfaceTop, or the prop's max top when no surface is known), so standing
        // on a rock/roof is not shoved off. Null/empty set leaves the XZ untouched.
        if (colliders is not null && !colliders.IsEmpty)
        {
            Vector2 resolved = float.IsInfinity(footY)
                ? colliders.Resolve(new Vector2(x, z), tuning.CapsuleRadius)
                : colliders.Resolve(new Vector2(x, z), tuning.CapsuleRadius, footY, surfaceTop);
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
