using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Character locomotion: the single movement step run by the local controller, the authoritative server sim,
/// and client-side prediction alike. Two overloads share one horizontal core (camera-relative move, normalized
/// diagonals, walk/run speed, optional slope gate):
/// <list type="bullet">
/// <item><see cref="Step(Vector3, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?)"/>
/// is the horizontal-only step: Y is a pure function of XZ (ground + half-height), no air.</item>
/// <item><see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?)"/>
/// is the vertical-physics step: gravity, jump (coyote + jump-buffer), land/clamp, air control, plus 3D
/// move-and-depenetrate prop resolution via an <see cref="IPhysicsWorld"/>, over the carried
/// <see cref="MoveState"/>.</item>
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
    /// <returns>The advanced position (Y on the ground + half-height).</returns>
    public static Vector3 Step(Vector3 position, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        (float x, float z) = DesiredHorizontal(position.X, position.Z, cmd, dt, tuning, groundNormal, speedScale: 1f);
        return new Vector3(x, groundHeight(x, z) + tuning.CapsuleHalfHeight, z);
    }

    /// <summary>Vertical-physics step resolving collision against a 3D <see cref="IPhysicsWorld"/>: the capsule
    /// moves freely to its desired position, then is depenetrated against the props in full 3D
    /// (<see cref="IPhysicsWorld.ComputePenetration"/> push-out, iterated up to <c>ResolveIterations</c> times).
    /// An upward-dominant push-out means the capsule is resting on a prop top/slope, so it counts as grounded
    /// support; a horizontal push-out is a wall, and moving in then being pushed out perpendicular yields net
    /// tangential progress (sliding, no dead-stop). The analytic terrain stays the floor (resolved on the final
    /// XZ). <paramref name="world"/> null = terrain only (unchanged). The same world + math runs on the
    /// authoritative server and in client prediction (deterministic single-threaded depenetration).</summary>
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
    /// <returns>The advanced <see cref="MoveState"/>.</returns>
    public static MoveState Step(in MoveState state, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, IPhysicsWorld? world = null,
        Func<float, float, Vector2>? clampXz = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        MoveState s = state;
        MoveTuning t = tuning;

        CapsuleShape capsule = CapsuleFor(t);
        float halfH = t.CapsuleHalfHeight;

        // 1. Horizontal desired (UNCHANGED): camera-relative move + terrain slope gate.
        float speedScale = s.Grounded ? 1f : t.AirControl;
        (float dx, float dz) = DesiredHorizontal(s.Position.X, s.Position.Z, cmd, dt, t, groundNormal, speedScale);
        if (clampXz is not null) { Vector2 c = clampXz(dx, dz); dx = c.X; dz = c.Y; }

        // 2. Vertical integrate (UNCHANGED math): jump-buffer countdown, gravity, terminal clamp.
        bool jumpRequested = cmd.Jump || s.JumpBufferRemaining > 0f;
        float jumpBuffer = cmd.Jump ? t.JumpBuffer : MathF.Max(0f, s.JumpBufferRemaining - dt);
        float vVel = s.VerticalVelocity - t.Gravity * dt;
        if (vVel < -t.MaxFallSpeed) vVel = -t.MaxFallSpeed;
        float desiredY = s.Position.Y + vVel * dt;

        // 3. Candidate position: move freely, then resolve against PROPS in full 3D.
        // Move-then-depenetrate can tunnel only if one tick's motion exceeds ~the capsule radius (0.4 m).
        // Walk/run is ~0.05-0.1 m/tick and jump rise ~0.13 m/tick, all well under; only a near-terminal fall
        // (~0.8 m/tick) onto a thin prop top could skip it, and the analytic terrain below still catches the
        // capsule (it never falls through the world). A vertical anti-tunnel sweep can be added later if needed.
        Vector3 pos = new(dx, desiredY, dz);
        bool propGrounded = false;
        if (world is not null)
        {
            const int ResolveIterations = 6;
            for (int i = 0; i < ResolveIterations; i++)
            {
                if (!world.ComputePenetration(capsule, Pose.At(pos), out Vector3 mtv)) break;
                pos += mtv;
                // An upward-dominant push = resting on a prop top/slope -> grounded support from the prop.
                float len = mtv.Length();
                if (len > 1e-6f && mtv.Y > 0.5f * len) propGrounded = true;
            }
            // Re-clamp XZ after depenetration so a prop cannot shove the capsule out of the play area.
            if (clampXz is not null) { Vector2 c = clampXz(pos.X, pos.Z); pos.X = c.X; pos.Z = c.Y; }
        }

        // 4. Terrain ground contact (UNCHANGED analytic floor) on the RESOLVED xz, with the grounded skin.
        float terrainGroundY = groundHeight(pos.X, pos.Z) + halfH;
        bool grounded;
        float tSinceGround;
        bool onTerrain = vVel <= 0f && (pos.Y <= terrainGroundY ||
                         (s.Grounded && pos.Y <= terrainGroundY + t.GroundedEpsilon));
        if (onTerrain) pos.Y = terrainGroundY;
        if (onTerrain || propGrounded)
        {
            grounded = true;
            tSinceGround = 0f;
            if (vVel < 0f) vVel = 0f;   // landed on terrain or a prop -> stop falling
        }
        else
        {
            grounded = false;
            tSinceGround = s.TimeSinceGrounded + dt;
        }

        // 5. Jump after contact (UNCHANGED): grounded or within coyote-time, consume both windows.
        if (jumpRequested && (grounded || tSinceGround <= t.CoyoteTime))
        {
            vVel = t.JumpSpeed;
            grounded = false;
            tSinceGround = t.CoyoteTime + dt;
            jumpBuffer = 0f;
        }

        return new MoveState
        {
            Position = pos,
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
    // Handles the input/slope section only; prop collision is resolved separately by the 3D
    // move-and-depenetrate block in the vertical-physics Step overload.
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
}
