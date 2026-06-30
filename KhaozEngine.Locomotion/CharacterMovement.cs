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
/// swept collide-and-slide prop resolution via an <see cref="IPhysicsWorld"/> (substepped
/// <see cref="IPhysicsWorld.SweepCapsule"/> + step-up probe), over the carried <see cref="MoveState"/>.</item>
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

    /// <summary>Vertical-physics step resolving collision against a 3D <see cref="IPhysicsWorld"/> via a
    /// substepped swept collide-and-slide (<see cref="IPhysicsWorld.SweepCapsule"/>): the capsule is swept from
    /// its current pose to the desired target in substeps no larger than a fraction of the capsule radius, so a
    /// fast move (jump / run / terminal fall) can never tunnel through a thin one-sided wall, and the capsule
    /// never enters a closed mesh (no inner-face suck-through). Walkable contacts (slope at or below the slope gate)
    /// are followed (walk across / mount a domed prop top); steep contacts block and redirect the velocity
    /// tangentially (no dead-stop). A step-up probe wires <see cref="MoveTuning.StepHeight"/>: a stair tread or
    /// curb below <c>StepHeight</c> is mounted without a jump. Depenetration via
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
        bool steppedUp = false;
        float steppedFloorY = 0f;
        if (world is null)
        {
            pos = target;
        }
        else
        {
            pos = SweptMove(world, capsule, start, target, t, s.Grounded, out steppedUp, out steppedFloorY);

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
                if (propCentreY > groundY) groundY = propCentreY;
            }
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
    // The walkable-contact pass-through advances the remainder UNSWEPT, so this must stay below ~1.0 (each substep
    // under one radius) or such an advance could mask and tunnel a wall in the same substep.
    private const float SubstepFraction = 0.5f;
    private const int   SlideIterations = 4;
    private const float SkinWidth       = 0.01f;
    // Contact counts as a wall/riser (step-up candidate) rather than a floor/ceiling when |normal.Y| is small.
    private const float StepUpNormalY = 0.5f;
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
        in MoveTuning t, bool grounded, out bool steppedUp, out float steppedFloorY)
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
            pos = SlideSubstep(world, capsule, pos, stepDelta, t, grounded, out bool stepped, out float floorY);
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
        in MoveTuning t, bool grounded, out bool steppedUp, out float steppedFloorY)
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
                // Degenerate contact: the sweep started TANGENT to a one-sided mesh (a building wall) so Bepu
                // returns t=0 with a ZERO normal - no slide plane. This is the case 8.5.3's depenetrate-to-clearance
                // cannot prevent: ComputePenetration reports NO overlap for a one-sided face (depth 0 at a tangent),
                // so the capsule CAN reach the touching state - e.g. a jump that arrives airborne pressing in lands
                // flush on the wall via a graze the sweep does not count as a hit. With a bare stop here the capsule
                // freezes: it can neither fall nor strafe, pinned mid-wall (the tester's "stuck up the building").
                // Recover the real contact normal by re-sweeping from a start pulled back along -dir (provably clear,
                // so Bepu yields a real normal), then slide the remainder along it - the into-surface component is
                // blocked while the along component (gravity, strafe, the rise of a jump) proceeds. If the move is
                // genuinely parallel to the face (no normal recoverable) stop this substep; the next tick re-tries.
                if (TryContactNormal(world, capsule, pos, dir, out Vector3 recovered))
                {
                    pos += recovered * SkinWidth;                                       // step off so the next sweep is clean
                    if (recovered.Y >= cosMaxSlope) { pos += remaining; break; }        // degenerate floor: pass the remainder through
                    delta = remaining - Vector3.Dot(remaining, recovered) * recovered;  // slide along the recovered wall plane
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
                TryStepUp(world, capsule, pos, remaining, t, out Vector3 stepped))
            {
                steppedUp = true; steppedFloorY = stepped.Y;
                pos = stepped;
                break;
            }

            delta = remaining - Vector3.Dot(remaining, n) * n;   // wall: slide along the contact plane
        }
        return pos;
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
        in MoveTuning t, out Vector3 stepped)
    {
        stepped = pos;
        Vector3 horiz = new(remaining.X, 0f, remaining.Z);
        float horizLen = horiz.Length();
        if (horizLen <= 1e-6f) return false;
        Vector3 horizDir = horiz / horizLen;
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

    // Desired world XZ position after camera-relative input + slope gate, WITHOUT collision.
    // Handles the input/slope section only; prop collision is resolved separately by the swept
    // collide-and-slide block in the vertical-physics Step overload.
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
