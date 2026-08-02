using System;
using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-cell authoritative movement step. Added to every <see cref="KhaozEngine.Sharding.CellSim"/>'s
/// <see cref="World"/> by <see cref="ShardedWorldServer"/>, so <see cref="KhaozEngine.Sharding.ShardHost.Tick"/>
/// runs it for every cell (fanned across the opt-in scheduler - cells are disjoint worlds, so the result is
/// scheduler-independent). For each owned entity carrying a <see cref="PendingMove"/> it advances the
/// <see cref="ReplicatedPosition"/> + <see cref="MovementState"/> (the vertical axis) via the shared
/// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
/// (the same step the single-<see cref="World"/> <see cref="WorldServer"/> and the client's prediction run, so
/// they stay in lockstep). <see cref="MovementState"/> is required on every movable entity (added at spawn,
/// carried across handoff because it is replicated). Read-only <see cref="Ghost"/>s and in-flight
/// <see cref="Migrating"/> entities are skipped: the owning cell is the sole simulator.
/// <para>
/// ONE INSTANCE PER CELL, holding that cell's physics world and that cell's island <see cref="Frame"/>. All of its
/// fields are readonly for the instance's life and it keeps no per-TICK mutable state, so the scheduler fan-out and
/// its scheduler-independence claim are unchanged. The per-cell shape is what makes the frame safe: a single shared
/// instance with a settable frame would be a write-then-read on state shared across parallel cell ticks, and the
/// symptom of losing that race is a player in the wrong cell's coordinates for one tick, which is a 128 m teleport.
/// </para>
/// </summary>
public sealed class PlayerMovementSystem : ISystem
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly IPhysicsWorld? physics;
    private readonly Func<float, float, Vector2>? clampXz;
    private readonly Func<float, float, float, MovementMedium>? medium;
    private readonly WorldBounds? bounds;
    private readonly WorldFrame frame;
    private readonly bool adaptSamplers;

    /// <summary>
    /// Builds one cell's movement step. <c>frame</c> is the island frame it steps in - the owning cell's, matching
    /// that cell's physics world's <c>Origin</c>. <see cref="WorldFrame.Origin"/> (the default) is absolute world
    /// coordinates and is byte-identical to the pre-frame system. <c>samplerSpace</c> says which space the ground /
    /// normal / medium delegates read. See <see cref="NetWorld.SamplerSpace"/> for why
    /// <see cref="SamplerSpace.World"/> is WRONG rather than merely imprecise for a sampler backed by the cell's own
    /// physics world.
    /// </summary>
    public PlayerMovementSystem(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        Func<float, float, float, MovementMedium>? medium = null, WorldFrame frame = default,
        SamplerSpace samplerSpace = SamplerSpace.World)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.physics = physics;
        this.bounds = bounds;
        this.frame = frame;
        // A sampler that reads absolute coordinates gets the anchor added back before the call, and any coordinate it
        // returns gets it subtracted again. Both are exact adds under the frame lemma, and both are skipped entirely
        // at the world origin, so an unframed cell is byte-identical to the pre-frame system.
        adaptSamplers = samplerSpace == SamplerSpace.World && frame != WorldFrame.Origin;
        // Play-area bound folded into the step (XZ only). WorldBounds is the one sampler SamplerSpace does not
        // govern: Clamp(x, z) carries no frame and the bounds are authored ABSOLUTE, so the step converts in both
        // directions in both sampler spaces, or a framed cell yanks the player to the play-area boundary every tick.
        clampXz = bounds is null ? null : (frame == WorldFrame.Origin ? bounds.Clamp : ClampXzIn);
        // Optional fluid-medium provider, mirrored from the authoritative server so every cell wades identically to
        // the client's prediction. Null = dry land everywhere = bit-identical to the pre-medium system.
        this.medium = medium;
    }

    /// <summary>The island frame this system steps in - the owning cell's, fixed at construction.</summary>
    public WorldFrame Frame => frame;

    private float GroundHeightIn(float x, float z)
    {
        if (!adaptSamplers) return groundHeight(x, z);
        Vector2 w = frame.ToWorldXz(x, z);
        return groundHeight(w.X, w.Y);
    }

    private Vector3 GroundNormalIn(float x, float z)
    {
        if (!adaptSamplers) return groundNormal!(x, z);
        Vector2 w = frame.ToWorldXz(x, z);
        return groundNormal!(w.X, w.Y);   // a normal is a direction, so nothing comes back to convert
    }

    private MovementMedium MediumIn(float x, float z, float feetY)
    {
        if (!adaptSamplers) return medium!(x, z, feetY);
        Vector2 w = frame.ToWorldXz(x, z);
        return medium!(w.X, w.Y, feetY);  // feetY is absolute world height on both sides: Y is never framed
    }

    private Vector2 ClampXzIn(float x, float z)
    {
        Vector2 w = frame.ToWorldXz(x, z);
        Vector2 clamped = bounds!.Clamp(w.X, w.Y);
        return frame.ToLocalXz(clamped.X, clamped.Y);
    }

    public void Update(World world, float dt)
    {
        // Built once per Update rather than per entity, and skipped entirely on an unframed cell, so the framed path
        // costs one delegate allocation per cell per tick and the unframed path costs nothing.
        Func<float, float, float> ground = adaptSamplers ? GroundHeightIn : groundHeight;
        Func<float, float, Vector3>? normal = groundNormal is null ? null : (adaptSamplers ? GroundNormalIn : groundNormal);
        Func<float, float, float, MovementMedium>? fluid = medium is null ? null : (adaptSamplers ? MediumIn : medium);
        WorldFrame cellFrame = frame;
        world.ForEach<NetId, ReplicatedPosition, PendingMove, MovementState>(
            (Entity e, ref NetId _, ref ReplicatedPosition pos, ref PendingMove move, ref MovementState ms) =>
        {
            if (world.Has<Ghost>(e) || world.Has<Migrating>(e))
            {
                // Owner is the only simulator. Zero the per-tick step OUTPUTS on the way out: this entity did not step,
                // so leaving last tick's velocity behind would have the post-tick anomaly check measure a motionless
                // entity against a full stride of intended travel and read it as a denial, and leaving last tick's
                // landing impact behind would read as a second landing on a tick where nothing moved at all.
                ms.CommandedVelocity = Vector2.Zero;
                ms.LandingImpactSpeed = 0f;
                return;
            }

            // Self-healing invariant, scoped to what this query actually reaches: every entity this cell owns that
            // ALSO carries PendingMove + MovementState (i.e. every entity this system steps) is stamped with this
            // cell's frame by the time it steps. Every door an entity enters by already converts (spawn, handoff,
            // restore, teleport, ghost mirror), so this normally never fires. What it buys is that a miss at any of
            // them is corrected EXACTLY here, on the next tick, instead of becoming a 128 m step.
            // A consumer-written entity that never gains PendingMove/MovementState (a static prop, an NPC driven by
            // its own movement logic) is NOT covered by this loop at all - it never runs through here. That is
            // harmless on its own: Value = Frame.Anchor + Local stays the exact absolute position whatever Frame it
            // carries (a consumer setting Local to an absolute value with Frame left at its Origin default is simply
            // a valid, non-canonical stamp), and it IS healed the moment it crosses a door this system does not own
            // (a border ghost mirror or a cell handoff both convert through CellSim.AdaptFrame). One comparison per
            // covered entity per tick.
            if (pos.Frame != cellFrame) pos = pos.ToFrame(cellFrame);

            var state = new MoveState
            {
                Position = pos.Local,
                VerticalVelocity = ms.VerticalVelocity,
                Grounded = ms.Grounded,
                TimeSinceGrounded = ms.TimeSinceGrounded,
                JumpBufferRemaining = ms.JumpBufferRemaining,
                Swimming = ms.Swimming,   // carry the swim flag IN so the enter/exit hysteresis band works across ticks
                ClimbRateEwma = ms.ClimbRateEwma,   // carry the sim-local ascent EWMA IN so the exported signal converges
                // Carry the server-authored haste/slow multiplier IN. It is a movement INPUT, so unlike the fields
                // below it is never written back OUT: the step does not derive it, SetSpeedScale is its only author,
                // and re-quantizing an already-quantized value every tick would only invite drift.
                SpeedScale = MovementState.DecodeSpeedScale(ms.SpeedScaleQ),
                // Carry the airborne arc IN. Unlike SpeedScale this one is a step OUTPUT as well, so it round-trips
                // through the wire quantum every tick on this head where the single-World WorldServer keeps it at full
                // float precision in its own PlayerMoveState. That costs at most half a quantum (0.002 m/s) of rounding
                // per tick, which is the same resolution the client already reconciles against, and it buys the arc
                // surviving a cell handoff: the component is what migrates, so a full-precision copy parked beside it
                // would be a second source of truth that silently loses to this one at the border.
                HorizontalVelocity = new Vector2(
                    MovementState.DecodeHorizontalVelocity(ms.HorizontalVelocityXQ),
                    MovementState.DecodeHorizontalVelocity(ms.HorizontalVelocityZQ)),
            };
            state = CharacterMovement.Step(state, move.Command, dt, ground, tuning, normal, physics, clampXz, fluid);

            pos = pos.WithLocal(state.Position);   // frame preserved by construction, never re-derived
            ms.VerticalVelocity = state.VerticalVelocity;
            ms.Grounded = state.Grounded;
            ms.TimeSinceGrounded = state.TimeSinceGrounded;
            ms.JumpBufferRemaining = state.JumpBufferRemaining;
            ms.Swimming = state.Swimming;   // write the swim flag back OUT so it replicates (TryGetPlayerState + remotes)
            ms.ClimbRateEwma = state.ClimbRateEwma;   // persist the sim-local ascent EWMA tick-to-tick (rides no wire)
            // Write the quantized step-climb rate OUT so it replicates to remotes (the glide signal). The single-World
            // WorldServer does this via MovementState.From per tick; the sharded per-cell step must do it here or a remote
            // on a sharded server never sees a climb (ClimbRateQ stays at its spawn value of 0).
            ms.ClimbRateQ = MovementState.QuantizeClimbRate(state.ClimbRate);
            // Persist the step's commanded velocity so ShardedWorldServer's post-tick anomaly check can read it back
            // (rides no wire, and the single-World head reads the step output directly, never this).
            ms.CommandedVelocity = state.CommandedVelocity;
            // Persist the step's landing impact for the same reason, and note it is NOT carried back IN above: it is a
            // per-tick EVENT, so a fresh MoveState reading 0 is exactly right and re-seeding it would double-report the
            // landing on the following tick. Written unconditionally, so a non-landing tick clears the previous one.
            ms.LandingImpactSpeed = state.LandingImpactSpeed;
            // Write the carried arc back OUT so it both survives to the next tick and replicates. Both halves matter
            // here: without the write-back the sharded head re-reads its spawn value every tick and no player on a
            // sharded server ever carries momentum at all, and without it reaching the wire the client's reconcile
            // basis resets the arc on every correction. The single-World WorldServer covers both through
            // MovementState.From per tick, so the per-cell step has to do it here.
            ms.HorizontalVelocityXQ = MovementState.QuantizeHorizontalVelocity(state.HorizontalVelocity.X);
            ms.HorizontalVelocityZQ = MovementState.QuantizeHorizontalVelocity(state.HorizontalVelocity.Y);
        });
    }
}
