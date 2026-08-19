using System;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Character locomotion: the single movement step run by the local controller, the authoritative server sim,
/// and client-side prediction alike. Every entry point funnels a resolved horizontal move (unit direction + a
/// speed fraction in [0,1]), walk/run speed, and an optional steep-terrain wall slide through ONE collision core, so a
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
/// swept collide-and-slide + step-up, wall slide, and bounds clamp, driven by a world-space steering direction
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

        (Vector2 moveDir, float speedFraction, bool run) = ResolveCameraCommand(cmd, tuning);
        float wade = WadeSpeedScale(position.X, position.Z, position.Y - tuning.CapsuleHalfHeight, tuning, medium);
        // A StepHeight reach, unchanged: this overload has no vertical physics at all (it clamps Y to the ground
        // every tick, so the character is always standing), and therefore no resolved vertical motion for the
        // no-footing allowance to read. Byte-identical to every release before #468.
        // The BARE gate, for the same reason inverted: hysteresis is a memory of a support decision, and this overload
        // takes no support decision to remember - it never grounds and never un-grounds. Handing it the widened gate
        // would not be hysteresis at all, it would be a permanently wider walkable slope, so this path is
        // byte-identical to every release before #475 too.
        (float x, float z, _) = DesiredHorizontalCore(position.X, position.Z, moveDir, speedFraction, run, dt,
            tuning, groundNormal, groundHeight, position.Y - tuning.CapsuleHalfHeight, speedScale: wade,
            reach: tuning.StepHeight, gate: tuning.MaxSlopeRadians);
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
        (Vector2 moveDir, float speedFraction, bool run) = ResolveCameraCommand(cmd, tuning);
        return StepCore(state, moveDir, speedFraction, run, cmd.Jump, dt, groundHeight, tuning,
            groundNormal, world, clampXz, medium, cmd.FaceCamera ? cmd.CameraYaw : null);
    }

    /// <summary>The WORLD-SPACE kinematic movement step for server-authoritative, non-player agents (enemy NPCs):
    /// it drives the SAME collision resolution the player gets - swept collide-and-slide + step-up against the
    /// <paramref name="world"/> (<see cref="IPhysicsWorld.SweepCapsule"/>), the analytic terrain support floor, the
    /// <paramref name="groundNormal"/> wall slide, and the <paramref name="clampXz"/> bounds - but from a world-space
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

    /// <summary>The shared vertical-physics collision core behind both the camera-relative player
    /// <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// and the world-space AI <see cref="StepTowards(in MoveState, Vector2, bool, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>.
    /// Both callers resolve their input to the same shape - a unit <paramref name="moveDir"/> (XZ) plus a
    /// <paramref name="speedFraction"/> in [0,1] - then hand off here, so terrain follow, swept collide-and-slide,
    /// step-up, the wall slide, and the bounds clamp are byte-for-byte the same for player and AI.
    /// <para><paramref name="faceYaw"/> is the one thing the two paths cannot share: the camera yaw the player asked
    /// to FACE (<see cref="MoveCommand.FaceCamera"/>), or null for "no camera target this tick", which is what the
    /// AI path always passes because it has no camera. Everything else the facing update needs is
    /// <paramref name="moveDir"/> and the carried heading, so the rule itself stays one function
    /// (<c>CharacterMovement.Facing.cs</c>) on every path.</para></summary>
    private static MoveState StepCore(in MoveState state, Vector2 moveDir, float speedFraction, bool run, bool jump,
        float dt, Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal, IPhysicsWorld? world,
        Func<float, float, Vector2>? clampXz, Func<float, float, float, MovementMedium>? medium, float? faceYaw = null)
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
            return SwimStep(s, moveDir, speedFraction, jump, dt, t, medNow, groundHeight, clampXz, halfH, faceYaw);

        // 0a. THE TRACTION GATE FOR THIS TICK, resolved ONCE from the footing the tick STARTED with and handed to every
        //    consumer below: the slide contact, the wall contact on all three horizontal paths, the slide resolve, the
        //    support decision, and the wedge. Widened by the hysteresis band while the character HAS footing, bare
        //    while it does not (#475). See CharacterMovement.Traction.cs for the rule and the measurements.
        float tractionGate = TractionGate(s.Grounded, t);

        // 0b. SLIDE CONTACT: is this tick standing against ground too steep to stand on? Read from the START of the
        //    tick (the carried position and the ground under its own column), so it is a pure function of carried
        //    state and a reconcile replay reaches the same answer. See CharacterMovement.Slide.cs for the model. The
        //    short version is that a too-steep surface grants no support, so gravity decomposes against it and the
        //    character rides the fall line instead of walking, jumping, or being refused at it.
        bool sliding = SlideContact(s, halfH, groundHeight, groundNormal, tractionGate, out Vector3 slideNormal);

        // 1. Horizontal desired (UNCHANGED when dry): resolved unit move direction + speed fraction + the terrain
        //    wall slide. The grounded/air scale composes with the wade scale (1 when no provider or out of water) and
        //    with the per-entity haste/slow multiplier (1 by default), so an unmodified dry path is untouched. An
        //    AIRBORNE tick under MoveTuning.AirMomentum takes the momentum resolve instead
        //    (CharacterMovement.Momentum.cs), which flies the carried velocity rather than recomputing the horizontal
        //    from scratch. Grounded ticks and the default (knob off) never reach it, so they are arithmetically
        //    identical to the pre-momentum step. A SLIDING tick takes NEITHER: its horizontal is the surface plane,
        //    not the command, whatever the momentum knob says (the knob governs free flight, which a slide is not),
        //    so the slide is evaluated FIRST and the two discarded resolves are never run at all.
        // `carrySeed` is what CARRIES to the next tick: the commanded velocity everywhere except on a slide, where it
        // is the IN-PLANE part alone (fall line plus contour), so the contour input steer cannot accumulate into the
        // carry tick after tick.
        float wade = WadeSpeedScale(s.Position.X, s.Position.Z, s.Position.Y - halfH, t, medium);
        float speedScale = (s.Grounded ? 1f : t.AirControl) * wade * s.SpeedScale;
        float dx, dz, slideVVel = 0f;
        Vector2 commandedVel, carrySeed;
        if (sliding)
        {
            SlideStep slide = ResolveSlide(s, moveDir, speedFraction, run, dt, t, slideNormal, groundNormal, groundHeight, speedScale, halfH, tractionGate);
            (dx, dz, commandedVel, carrySeed, slideVVel) = (slide.X, slide.Z, slide.Commanded, slide.Carry, slide.VerticalVelocity);
        }
        else
        {
            (dx, dz, float commandedSpeed) = DesiredHorizontalCore(s.Position.X, s.Position.Z, moveDir, speedFraction, run, dt, t, groundNormal, groundHeight, s.Position.Y - halfH, speedScale, NoFootingReach(s, t, dt), tractionGate);
            commandedVel = moveDir * commandedSpeed;
            if (t.AirMomentum && !s.Grounded)
                (dx, dz, commandedVel) = AirborneMomentumMove(s, moveDir, speedFraction, run, dt, t, groundNormal, groundHeight, wade, tractionGate);
            carrySeed = commandedVel;
        }
        if (clampXz is not null) { Vector2 c = clampXz(dx, dz); dx = c.X; dz = c.Y; }

        // 2. Vertical integrate (UNCHANGED math): jump-buffer countdown, gravity, terminal clamp. On a SLIDING tick
        //    the ordinary integrate is replaced wholesale by the fall-line one resolved above, which is the same
        //    gravity decomposed against the surface - so the committed drop is exactly the drop the committed
        //    horizontal travel needs and the ground clamp never has to correct it.
        bool jumpRequested = jump || s.JumpBufferRemaining > 0f;
        float jumpBuffer = jump ? t.JumpBuffer : MathF.Max(0f, s.JumpBufferRemaining - dt);
        float vVel = FallIntegrate(s.VerticalVelocity, t, dt);   // the same call NoFootingReach read above, so the two cannot drift
        if (sliding) vVel = slideVVel;
        float fallSpeed = vVel, desiredY = s.Position.Y + vVel * dt;   // fallSpeed: this tick's vertical BEFORE a landing zeroes it, the impact latch's source

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

            // The settle pass the swept move needs after it commits - residual-overlap depenetration (with the
            // walkable-rest vertical-only rule), the re-applied XZ clamp, and the monotone-forward climb hold that
            // refuses a riser's backward shove - lives in CharacterMovement.Settle.cs. It corrects the candidate
            // position only, and it is the reason propGrounded can be set without the support probe below.
            pos = SettleAfterSweep(world, capsule, pos, s, t, dx, dz, vVel, halfH, restHold, clampXz, groundHeight,
                out propGrounded);
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
        // The PHYSICS-WORLD contribution to the support floor - the prop surface under the capsule (a downward
        // capsule sweep, guarded against side grazes and overhangs) plus the staircase-BASE tread-find fallback -
        // lives in CharacterMovement.Collision.cs beside the probes it drives. It can only ever RAISE the floor
        // above the analytic terrain, and it is skipped entirely on a terrain-only step.
        if (world is not null)
            groundY = PropSupportFloor(world, capsule, pos, s.Position, s.Grounded, t, halfH, terrainGroundY, groundY,
                steppedUp);

        bool grounded;
        float tSinceGround;
        // The swept resolver leaves the capsule at SkinWidth above a surface (not flush). Extend the landing
        // threshold by SkinWidth so a swept-settled capsule on a flat prop top counts as grounded on the first
        // contact tick (without this, pos.Y = groundY + SkinWidth > groundY and the capsule falls forever).
        bool onGround = vVel <= 0f && (pos.Y <= groundY + (world is not null ? SkinWidth : 0f) || (s.Grounded && pos.Y <= groundY + t.GroundedEpsilon));
        if (onGround) pos.Y = groundY;          // snap onto the support surface (generalizes the old terrain clamp)
        if (pos.Y < groundY) pos.Y = groundY;   // and never rest below it, even on a tick that is not "onGround"
        // NO TRACTION ON STEEP GROUND. A terrain surface steeper than THIS TICK'S TRACTION GATE seats the capsule (the
        // clamp above still forbids penetration) but grants it nothing else: no Grounded, so no jump, no coyote
        // refresh, and no landing latch on the face - the landing is at the bottom, and LandingImpactSpeed reports
        // there from the fall the slide accumulated. It is the rule that retired the ascent gate rather than patching
        // it, and since #475 it is HYSTERETIC: the gate resolved above already carries the band a standing character
        // keeps its footing over, while a body arriving WITHOUT footing is judged at the bare gate and slides. The test
        // itself, and the prop support it exempts, is RefusesTraction (CharacterMovement.Traction.cs), because the
        // step-down hold below asks the identical question and a second expression of it is how #470 happened.
        bool noTraction = onGround &&
                          RefusesTraction(pos, propGrounded, groundY, terrainGroundY, groundNormal, tractionGate);
        if ((onGround || propGrounded) && !noTraction)
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

        // 4-wedge. THE ONE EXCEPTION TO "STEEP GROUND GRANTS NOTHING". A capsule whose descent the world is
        //    SWALLOWING is being held up by the world, whatever the normal under its own column says, and the rule
        //    above refusing it support is what made a concave crease a soft-lock: no support, no slide out (the
        //    wall contact removes the whole horizontal), and no jump either, since the character is never grounded
        //    and its coyote window expired long ago. SlideWedged (CharacterMovement.Slide.cs) reads that off this
        //    tick alone - a real accumulated fall, and a committed descent far short of the one the resolved
        //    velocity demanded - and it cannot arm at a jump apex, which is what keeps the #440 ratchet dead. The
        //    crease is its motivating case rather than its condition, so any concave curvature can arm it for a
        //    tick (documented there, and worth no altitude). Support is granted for THIS TICK: the landing latch
        //    below then fires from the swallowed fall, exactly as a landing anywhere else does, because this IS one.
        if (!grounded && sliding && SlideWedged(s.Position.Y, pos.Y, vVel, dt, t, pos, groundNormal, groundHeight, tractionGate))
        {
            grounded = true;
            tSinceGround = 0f;
            if (vVel < 0f) vVel = 0f;
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
        //     AND IT RUNS THE TRACTION TEST ITSELF (#470). Stepping DOWN onto a face too steep to stand on is a slide,
        //     not a step. The support decision's test is guarded by `onGround`, which only reaches drops within
        //     GroundedEpsilon, so across THIS band - which opens exactly where that stick closes - it was vacuously
        //     false and the hold seated the character on whatever the drop landed on, a cliff face included. Same test,
        //     same tick-resolved gate: `s.Grounded` says footing was held at tick start, so the widened gate applies
        //     here as everywhere else. Past it the seat is refused and the body goes over the edge, which is a
        //     walk-off, and the slide takes it from there. Walkable stair treads are under the gate and are untouched.
        if (!grounded && vVel <= 0f && s.Grounded &&
            !RefusesTraction(pos, propGrounded, groundY, terrainGroundY, groundNormal, tractionGate))
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

        // 4b. The paced step-up climb and the climb signal it exports live in CharacterMovement.Climb.cs, beside
        //     the probes they drive: the per-tick rise cap that paces a stair run to a steady walk, the monotone
        //     mount, the tangent co-pace, and the continuous ascent/descent rate the render glide reads. It runs
        //     only when NOT rising ballistically (a jump is never throttled) and only against a physics world with
        //     the pacing enabled, so the gate stays here where the rest of the step can see it.
        if (vVel <= 0f && world is not null && t.MaxStepClimbSpeed > 0f)
        {
            ClimbPacing climb = PaceStepClimb(world, capsule, pos, s, t, dt, dx, dz, halfH, terrainGroundY,
                steppedUp, grounded, tSinceGround, vVel);
            pos = climb.Position;
            grounded = climb.Grounded;
            tSinceGround = climb.TimeSinceGrounded;
            vVel = climb.VerticalVelocity;
            stepUpRose = climb.StepUpRose;
            climbRate = climb.ClimbRate;
            climbEwma = climb.ClimbRateEwma;
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
        float landingImpact = LandingImpact(s.Grounded, grounded, fallSpeed);   // latched BEFORE step 5: a buffered jump on the landing tick must not cancel the impact
        bool supportGranted = grounded;   // and so is the footing grant, for the same reason: step 5 consumes the support it launches from
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
            LandingImpactSpeed = landingImpact, // a per-tick EVENT: the downward speed this tick's landing erased, else 0
            SupportGranted = supportGranted,    // and the fact the jump above just consumed: did this tick resolve footing at all

            // CARRIED heading, turned shortest-arc toward the camera (FaceCamera) or the commanded direction. A pure
            // OUTPUT: nothing above reads it, so the position this step commits is untouched by it.
            FacingYaw = ResolveFacing(s.FacingYaw, moveDir, faceYaw, dt, t),
            SpeedScale = state.SpeedScale,   // a movement INPUT: carried through unchanged, never derived by the step
            CommandedVelocity = commandedVel, // a per-tick OUTPUT: what the step asked for, before anything denied it
            // Carried inertia, stamped on EVERY tick (grounded included) and consumed only when AirMomentum is on:
            // the intended velocity clipped to what survived, so collision can shed it but never inject into it. The
            // clip is measured against commandedVel because that is the vector this tick's advance was computed
            // from. On a slide it carries a contour steer the carry does not, and reading the carry alone against a
            // displacement the steer helped produce would clip the carry for the steer's travel (see there).
            HorizontalVelocity = ClipCarryToAchieved(carrySeed, commandedVel, start, pos, dt),
        };
        // Defense-in-depth: a finite input state must never produce a non-finite result. A pathological command is
        // gated out upstream, but a misbehaving ground/bound/tuning value could inject a NaN/Inf that would slip
        // past every clamp and replicate; hold the last good state instead of propagating a poisoned position.
        // The fallback holds the last good POSE, and a per-tick EVENT is not pose: LandingImpactSpeed is zeroed on the
        // way out, because handing back the previous state wholesale re-emits the landing tick's impact on EVERY
        // poisoned tick, and a consumer reading it from OnAfterTick would apply that one landing's fall damage over
        // and over for as long as the delegate misbehaves. SupportGranted is zeroed with it and for the same reason:
        // a held state did not resolve footing this tick, and re-emitting a grant per poisoned tick would read to an
        // anomaly check as a character standing on nothing forever.
        return IsFinite(result.Position) && float.IsFinite(result.VerticalVelocity) &&
               float.IsFinite(result.ClimbRate) && float.IsFinite(result.ClimbRateEwma) &&
               float.IsFinite(result.StepDeltaY) && IsFinite(result.HorizontalVelocity) && float.IsFinite(result.FacingYaw)
            ? result : state with { LandingImpactSpeed = 0f, SupportGranted = false };
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
}
