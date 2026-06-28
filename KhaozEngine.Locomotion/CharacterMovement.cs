using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Physics;

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

        return new MoveState
        {
            Position = new Vector3(x, y, z),
            VerticalVelocity = vVel,
            Grounded = grounded,
            TimeSinceGrounded = tSinceGround,
            JumpBufferRemaining = jumpBuffer,
        };
    }

    /// <summary>Vertical-physics step resolving collision against a 3D <see cref="IPhysicsWorld"/>: horizontal
    /// motion is collide-and-slid against the world, the support height is the higher of the terrain and a downward
    /// capsule probe (so the capsule rests on a prop's real surface, e.g. a domed rock or building floor), and
    /// step-up uses a support-rise gate. <paramref name="world"/> null = terrain only (unchanged). The same world
    /// + math runs on the authoritative server and in client prediction.</summary>
    /// <param name="state">The carried kinematic state (position + vertical velocity + grounded + feel timers).</param>
    /// <param name="cmd">Movement intent including the jump bit.</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Speed/half-height/slope + gravity/jump/fall/feel constants.</param>
    /// <param name="groundNormal">Optional ground normal; when given, gates a horizontal step by slope.</param>
    /// <param name="world">The physics world to resolve against, or null for terrain-only (no change to existing
    /// behaviour).</param>
    /// <param name="clampXz">Optional XZ clamp (e.g. a play-area bound); applied after move/collide.</param>
    /// <returns>The advanced <see cref="MoveState"/>.</returns>
    public static MoveState Step(in MoveState state, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
        Func<float, float, Vector2>? clampXz = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        // Copy in-params to locals so local functions and lambdas can capture them.
        MoveState stateLocal = state;
        MoveTuning tuningLocal = tuning;

        CapsuleShape capsule = CapsuleFor(tuningLocal);

        // Support height under (x,z): the higher of the terrain and the prop surface a downward capsule probe finds.
        float Support(float sx, float sz)
        {
            float g = groundHeight(sx, sz);
            if (world is null) return g;
            float p = ProbeSupport(world, capsule, sx, sz, stateLocal.Position.Y, g, tuningLocal);
            return p > g ? p : g;
        }

        // 1. Camera-relative desired position from input (slope-gated), without collision yet.
        float speedScale = stateLocal.Grounded ? 1f : tuningLocal.AirControl;
        (float dx, float dz) = DesiredHorizontal(stateLocal.Position.X, stateLocal.Position.Z, cmd, dt, tuningLocal, groundNormal, speedScale);

        // 1b. Collide-and-slide the capsule against the world from current to desired.
        float x = dx, z = dz;
        if (world is not null)
        {
            Vector3 from = stateLocal.Position;
            Vector3 to = new(dx, stateLocal.Position.Y, dz);
            Vector3 slid = SlideHorizontal(world, capsule, from, to, tuningLocal);
            x = slid.X;
            z = slid.Z;
        }

        if (clampXz is not null) { Vector2 c = clampXz(x, z); x = c.X; z = c.Y; }

        // 1c. Step-up gate: while grounded, a support rise taller than StepHeight is a wall (revert the move).
        float supBefore = Support(stateLocal.Position.X, stateLocal.Position.Z);
        if (stateLocal.Grounded && Support(x, z) - supBefore > tuningLocal.StepHeight) { x = stateLocal.Position.X; z = stateLocal.Position.Z; }

        // 2. Jump-buffer countdown: a press arms it for JumpBuffer seconds; otherwise it bleeds down.
        bool jumpRequested = cmd.Jump || stateLocal.JumpBufferRemaining > 0f;
        float jumpBuffer = cmd.Jump ? tuningLocal.JumpBuffer : MathF.Max(0f, stateLocal.JumpBufferRemaining - dt);

        // 3. Gravity integrate (clamp to terminal fall speed).
        float vVel = stateLocal.VerticalVelocity - tuningLocal.Gravity * dt;
        if (vVel < -tuningLocal.MaxFallSpeed) vVel = -tuningLocal.MaxFallSpeed;
        float y = stateLocal.Position.Y + vVel * dt;

        // 4. Ground contact onto the support height (terrain or prop surface from the probe).
        float groundY = Support(x, z) + tuningLocal.CapsuleHalfHeight;
        bool grounded;
        float tSinceGround;
        if (vVel <= 0f && (y <= groundY || (stateLocal.Grounded && y <= groundY + tuningLocal.GroundedEpsilon)))
        {
            y = groundY;
            vVel = 0f;
            grounded = true;
            tSinceGround = 0f;
        }
        else
        {
            grounded = false;
            tSinceGround = stateLocal.TimeSinceGrounded + dt;
        }

        // 5. Jump after contact (buffered jump fires on the landing tick).
        if (jumpRequested && (grounded || tSinceGround <= tuningLocal.CoyoteTime))
        {
            vVel = tuningLocal.JumpSpeed;
            grounded = false;
            tSinceGround = tuningLocal.CoyoteTime + dt;
            jumpBuffer = 0f;
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

    /// <summary>The upright capsule for a tuning: radius + cylindrical length so total height = 2*halfHeight.</summary>
    public static CapsuleShape CapsuleFor(in MoveTuning tuning)
        => new(tuning.CapsuleRadius, MathF.Max(0.01f, 2f * tuning.CapsuleHalfHeight - 2f * tuning.CapsuleRadius));

    // Desired world XZ position after camera-relative input + slope gate, WITHOUT collision.
    // Mirrors the input/slope section of ResolveHorizontal; collision is handled separately by SlideHorizontal.
    private static (float x, float z) DesiredHorizontal(float x, float z, in MoveCommand cmd, float dt,
        in MoveTuning tuning, Func<float, float, Vector3>? groundNormal, float speedScale)
    {
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);

        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);
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
        return (x, z);
    }

    // Depenetrate the capsule at 'from', then iteratively sweep-and-slide toward 'to' (horizontal only, Y = from.Y).
    // Skin width 0.02 m keeps the capsule just off the surface so sweeps do not start inside geometry.
    private static Vector3 SlideHorizontal(IPhysicsWorld world, CapsuleShape capsule,
        Vector3 from, Vector3 to, in MoveTuning tuning)
    {
        const float Skin = 0.02f;
        const int MaxIterations = 4;

        // Depenetrate the start position.
        var current = from;
        if (world.ComputePenetration(capsule, Pose.At(current), out Vector3 mtv))
            current += mtv;

        // Horizontal remaining displacement (keep Y fixed throughout).
        var remaining = new Vector3(to.X - current.X, 0f, to.Z - current.Z);

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            float remainLen = remaining.Length();
            if (remainLen < 1e-5f) break;

            Vector3 dir = remaining / remainLen;
            if (world.SweepCapsule(capsule, Pose.At(current), dir, remainLen + Skin, out SweepHit hit))
            {
                // Advance to just before the contact surface (leave skin gap).
                float advance = MathF.Max(0f, hit.Distance - Skin);
                current += dir * advance;

                // Slide: remove the component of remaining along the hit normal (project onto wall surface).
                // Use the XZ projection of the hit normal for horizontal slide.
                var wallNormal = new Vector3(hit.Normal.X, 0f, hit.Normal.Z);
                float wallNormalLen = wallNormal.Length();
                if (wallNormalLen > 1e-5f)
                {
                    wallNormal /= wallNormalLen;
                    // Correct collide-and-slide order: consume what was already advanced, THEN project
                    // the residue onto the wall plane. The old order projected first and subtracted
                    // second, which double-accounted and under-slid on oblique (non-perpendicular) walls.
                    remaining -= dir * advance;
                    float dot = Vector3.Dot(remaining, wallNormal);
                    remaining -= wallNormal * dot;
                }
                else
                {
                    // Wall is purely vertical (horizontal normal is zero) - blocked.
                    break;
                }
            }
            else
            {
                // No hit: move the full remaining distance.
                current += remaining;
                remaining = Vector3.Zero;
                break;
            }
        }

        return new Vector3(current.X, from.Y, current.Z);
    }

    // Downward capsule probe to find the support surface under (x, z). The probe starts above the
    // expected head height and sweeps down far enough to catch any prop surface the capsule might rest on.
    // Returns float.NegativeInfinity when nothing is found (caller falls back to terrain).
    private static float ProbeSupport(IPhysicsWorld world, CapsuleShape capsule,
        float x, float z, float currentCentreY, float terrainY, in MoveTuning tuning)
    {
        // Start the probe centre well above the expected support surface: at least 1 m above the
        // terrain or the current capsule centre + a margin so the sweep exits any local geometry.
        float probeStart = MathF.Max(
            terrainY + tuning.CapsuleHalfHeight + 1.5f,
            currentCentreY + 0.5f);

        // Sweep far enough to reach any prop the capsule might stand on.
        float maxProbe = probeStart - terrainY + 1f;

        if (world.SweepCapsule(capsule, Pose.At(new Vector3(x, probeStart, z)),
                -Vector3.UnitY, maxProbe, out SweepHit hit))
        {
            // The capsule centre Y when it first contacts the surface.
            float contactCentreY = probeStart - hit.Distance;
            // Support is where the FEET would be (centre - halfHeight).
            return contactCentreY - tuning.CapsuleHalfHeight;
        }

        return float.NegativeInfinity;
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
