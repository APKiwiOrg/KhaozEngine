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
        var result = new Vector3(x, groundHeight(x, z) + tuning.CapsuleHalfHeight, z);
        // Defense-in-depth: never return a non-finite position from a finite input. A pathological command is
        // already neutralized by the move gate, but a misbehaving groundHeight/bound could still inject a NaN/Inf
        // that slips past every clamp and replicates to every client in range; hold the last good position instead.
        return IsFinite(result) ? result : position;
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

        // 3. Candidate position: SWEEP from the current pose to the target (collide-and-slide), then settle.
        // The swept move can never cross a face (substepped to a fraction of the capsule radius), so the capsule
        // never begins a tick inside a wall - enforcing the depenetration contract for real. A one-sided building
        // mesh is now both rich (every triangle blocks) and trap-proof (no entry => no inner-face suck-through).
        Vector3 start = s.Position;
        Vector3 target = new(dx, desiredY, dz);
        Vector3 pos;
        bool propGrounded = false;
        if (world is null)
        {
            pos = target;
        }
        else
        {
            pos = SweptMove(world, capsule, start, target, t, s.Grounded);

            // Settle pass: residual-overlap depenetration (rarely fires now; the swept move starts known-outside).
            const int ResolveIterations = 6;
            const float ResolveSlop = 0.01f;
            const float MaxCorrection = 0.5f;
            for (int i = 0; i < ResolveIterations; i++)
            {
                if (!world.ComputePenetration(capsule, Pose.At(pos), out Vector3 mtv)) break;
                float len = mtv.Length();
                if (len <= 1e-6f) break;
                if (mtv.Y > 0.5f * len) propGrounded = true;
                if (len <= ResolveSlop) break;
                float push = MathF.Min(len - ResolveSlop, MaxCorrection);
                pos += mtv / len * push;
            }
            if (clampXz is not null) { Vector2 c = clampXz(pos.X, pos.Z); pos.X = c.X; pos.Z = c.Y; }
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
        //    prop sweep is skipped and the move-and-depenetrate alone blocks it at the base (the perfect
        //    horizontal the prior version established is untouched, and a low dome cannot be walked up its side).
        float terrainGroundY = groundHeight(pos.X, pos.Z) + halfH;
        float groundY = terrainGroundY;
        // A small "standing on a prop" skin (NOT the larger GroundedEpsilon mount band): a capsule whose carried
        // Y is above terrain by more than this is genuinely on a prop, so the sweep keeps following the prop
        // surface (e.g. down the far side of a dome it mounted), while one at terrain level walking into a flank
        // is below it and the sweep stays off (depenetration blocks the base). Too large a skin would snap the
        // capsule off the prop surface onto terrain mid-descent and clip it into the prop.
        const float OnPropSkin = 0.05f;
        bool overProp = !s.Grounded || s.Position.Y > terrainGroundY + OnPropSkin;
        if (world is not null && overProp)
        {
            float probeStart = pos.Y + 2f * halfH;                 // above the head, clear of a standable prop
            float maxProbe = (probeStart - terrainGroundY) + 2f * halfH;
            // Require an UPWARD-facing hit. A tall wall the capsule is beside extends above the head, so the sweep
            // starts already inside the wall side and Bepu reports a zero-distance hit with a degenerate (zero)
            // normal - NOT a floor, and it must not lift the capsule onto the wall top. A real prop top under the
            // capsule gives a positive-distance hit with an up normal.
            if (world.SweepCapsule(capsule, Pose.At(new Vector3(pos.X, probeStart, pos.Z)),
                    -Vector3.UnitY, maxProbe, out SweepHit floorHit) && floorHit.Normal.Y > 0f)
            {
                float propCentreY = probeStart - floorHit.Distance; // capsule centre resting on the prop surface
                if (propCentreY > groundY) groundY = propCentreY;
            }
        }

        bool grounded;
        float tSinceGround;
        // The swept resolver leaves the capsule at SkinWidth above a surface (not flush). Extend the landing
        // threshold by SkinWidth so a swept-settled capsule on a flat prop top counts as grounded on the first
        // contact tick (without this, pos.Y = groundY + SkinWidth > groundY and the capsule falls forever).
        bool onGround = vVel <= 0f && (pos.Y <= groundY + SkinWidth || (s.Grounded && pos.Y <= groundY + t.GroundedEpsilon));
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

    /// <summary>The unconstrained horizontal target the camera-relative move would reach in one step, before the
    /// slope gate, static collision, or play-area clamp deny any of it. The XZ distance from this to the position a
    /// constrained <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?)"/>
    /// actually produced is the authoritative "correction" the server applied this tick - a server-side anti-cheat
    /// signal: a client repeatedly driving into a wall, slope, or boundary keeps this large. Pass
    /// <paramref name="speedScale"/> = the value the step used (1 grounded, <see cref="MoveTuning.AirControl"/>
    /// airborne) so the comparison isolates only the denial, not the air-control scaling. Mirrors the basis +
    /// speed of <see cref="DesiredHorizontal"/> (pre-gate).</summary>
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
    private const float SubstepFraction = 0.5f;
    private const int   SlideIterations = 4;
    private const float SkinWidth       = 0.01f;

    /// <summary>Move the capsule from <paramref name="start"/> toward <paramref name="target"/> by a substepped
    /// swept collide-and-slide over <see cref="IPhysicsWorld.SweepCapsule"/>. The displacement is split into
    /// substeps no longer than <see cref="SubstepFraction"/> * the capsule radius, so even a near-terminal fall or
    /// fast jump never crosses a face. Deterministic (Bepu Sweep is deterministic single-threaded; the substep
    /// count is a deterministic length).</summary>
    private static Vector3 SweptMove(IPhysicsWorld world, CapsuleShape capsule, Vector3 start, Vector3 target,
        in MoveTuning t, bool grounded)
    {
        Vector3 full = target - start;
        float fullLen = full.Length();
        if (fullLen <= 1e-6f) return target;

        float maxStep = MathF.Max(0.01f, t.CapsuleRadius * SubstepFraction);
        int substeps = (int)MathF.Ceiling(fullLen / maxStep);
        if (substeps < 1) substeps = 1;
        Vector3 stepDelta = full / substeps;

        Vector3 pos = start;
        for (int i = 0; i < substeps; i++)
            pos = SlideSubstep(world, capsule, pos, stepDelta, t, grounded);
        return pos;
    }

    /// <summary>Collide-and-slide one substep's displacement: sweep, advance to the contact minus a skin, project
    /// the remainder onto the contact plane, iterate (resolves inner corners). No step-up in this overload.</summary>
    private static Vector3 SlideSubstep(IPhysicsWorld world, CapsuleShape capsule, Vector3 pos, Vector3 delta,
        in MoveTuning t, bool grounded)
    {
        for (int iter = 0; iter < SlideIterations; iter++)
        {
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
            if (n.LengthSquared() > 1e-12f) n = Vector3.Normalize(n);
            else break;           // degenerate contact (deep/zero-distance): let the settle pass handle it
            delta = remaining - Vector3.Dot(remaining, n) * n;   // slide along the contact plane
        }
        return pos;
    }

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
