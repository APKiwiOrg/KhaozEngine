using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The per-tick player movement step, plugged into the shipped prediction/reconciliation seam. The same
/// instance configuration (ground delegate + tuning) drives the authoritative server tick and the client's
/// prediction replay, so they stay in lockstep. Wraps the vertical <see cref="CharacterMovement"/> step.
/// <para>
/// It can step in a frame. <see cref="Frame"/> is the island frame this simulator's caller is stepping in, and
/// every state <see cref="Step"/> returns is stamped with it. The step function itself needs no change to support
/// this: it reaches the world only through planar sampler delegates and an <see cref="IPhysicsWorld"/>, so it is
/// translation-invariant by construction - feed it a position in a frame and samplers that read the same frame and
/// it produces results identical to the origin-local case, because it is the same arithmetic on the same operands.
/// What this class adds is the adaptation that puts the samplers in that frame.
/// </para>
/// </summary>
public sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>
{
    private readonly Func<float, float, float> groundHeight;
    private readonly Func<float, float, Vector3>? groundNormal;
    private readonly MoveTuning tuning;
    private readonly IPhysicsWorld? physics;
    private readonly WorldBounds? bounds;
    private readonly Func<float, float, float, MovementMedium>? medium;
    private readonly SamplerSpace samplerSpace;

    // The delegates actually handed to the step. Built ONCE (never per tick, so a framed head allocates nothing
    // per step) and they read the current Frame at call time. An optional one stays null when the caller supplied
    // no delegate, because null means "no such sampler" to the step and a non-null wrapper would change behaviour.
    private readonly Func<float, float, float> groundHeightAdapter;
    private readonly Func<float, float, Vector3>? groundNormalAdapter;
    private readonly Func<float, float, Vector2>? clampXzAdapter;
    private readonly Func<float, float, float, MovementMedium>? mediumAdapter;

    public PlayerMoveSimulator(Func<float, float, float> groundHeight, MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null, WorldBounds? bounds = null, IPhysicsWorld? physics = null,
        Func<float, float, float, MovementMedium>? medium = null, SamplerSpace samplerSpace = SamplerSpace.World)
    {
        this.groundHeight = groundHeight ?? throw new ArgumentNullException(nameof(groundHeight));
        this.tuning = tuning;
        this.groundNormal = groundNormal;
        this.physics = physics;
        // Fold the play-area bound into the step as an XZ clamp, so the vertical axis is resolved at the clamped
        // position (an airborne player is not snapped to the ground at the wall) and the server/client stay identical.
        this.bounds = bounds;
        // Optional fluid-medium provider (x, z, feetY) -> MovementMedium. The GAME supplies the SAME pure delegate on
        // the server and the client so wading (and, on Task 2, swimming) predicts in lockstep. Null = dry land
        // everywhere = bit-identical to the pre-medium simulator.
        this.medium = medium;
        this.samplerSpace = samplerSpace;

        groundHeightAdapter = GroundHeightIn;
        groundNormalAdapter = groundNormal is null ? null : GroundNormalIn;
        clampXzAdapter = bounds is null ? null : ClampXzIn;
        mediumAdapter = medium is null ? null : MediumIn;
    }

    /// <summary>
    /// The island frame this simulator steps in: the position carried in and out of <see cref="Step"/> is expressed
    /// against it, and every state <see cref="Step"/> returns is stamped with its anchor. <see cref="WorldFrame.Origin"/>
    /// (the default) is absolute world coordinates and is byte-identical to the pre-frame simulator.
    /// <para>Set by the ISLAND that owns this simulator, between steps, together with the rebase of the island's
    /// physics world - never mid-step and never per entity. A physics world is a coordinate space and cannot be in
    /// two, so the frame and that world's <c>Origin</c> move as one or the step queries colliders in a space its
    /// state is not in.</para>
    /// </summary>
    public WorldFrame Frame { get; set; } = WorldFrame.Origin;

    /// <summary>The coordinate space this simulator's sampler delegates read. <see cref="SamplerSpace.World"/> (the
    /// default) means they take absolute coordinates and the step converts for them.</summary>
    public SamplerSpace SamplerSpace => samplerSpace;

    /// <summary>The play-area bound folded into the step, or null when movement is unbounded. Always ABSOLUTE:
    /// bounds are authored content, so the step converts for them in both sampler spaces.</summary>
    public WorldBounds? Bounds => bounds;

    // --- sampler adaptation ------------------------------------------------------------------------------------
    // A sampler that reads absolute coordinates gets the anchor added back before the call; one that reads frame
    // coordinates is called straight through. Both are exact adds under the frame lemma, and both are no-ops at
    // WorldFrame.Origin, so an unframed head pays one forwarding call and nothing else.

    private bool Adapting => samplerSpace == SamplerSpace.World && Frame != WorldFrame.Origin;

    private float GroundHeightIn(float x, float z)
    {
        if (!Adapting) return groundHeight(x, z);
        Vector2 w = Frame.ToWorldXz(x, z);
        return groundHeight(w.X, w.Y);
    }

    private Vector3 GroundNormalIn(float x, float z)
    {
        if (!Adapting) return groundNormal!(x, z);
        Vector2 w = Frame.ToWorldXz(x, z);
        return groundNormal!(w.X, w.Y);   // a normal is a direction, so nothing comes back to convert
    }

    private MovementMedium MediumIn(float x, float z, float feetY)
    {
        if (!Adapting) return medium!(x, z, feetY);
        Vector2 w = Frame.ToWorldXz(x, z);
        return medium!(w.X, w.Y, feetY);  // feetY is absolute world height on both sides: Y is never framed
    }

    // WorldBounds is the one sampler SamplerSpace does not govern. Clamp(x, z) carries no frame and the bounds are
    // authored absolute, so a consumer-authored subclass has no more information than the engine's own: the STEP
    // converts, in both directions, in both sampler spaces.
    private Vector2 ClampXzIn(float x, float z)
    {
        if (Frame == WorldFrame.Origin) return bounds!.Clamp(x, z);
        Vector2 w = Frame.ToWorldXz(x, z);
        Vector2 clamped = bounds!.Clamp(w.X, w.Y);
        return Frame.ToLocalXz(clamped.X, clamped.Y);
    }

    /// <summary>Advances one player by one command over <paramref name="dt"/> seconds: the shared vertical
    /// <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// (gravity + jump + ground contact), resolved against the optional <see cref="IPhysicsWorld"/> (props/buildings),
    /// and clamped into the play area when a <see cref="WorldBounds"/> is set.
    /// <para>The returned state is stamped with <see cref="Frame"/>'s anchor, so a state that came out of a step
    /// always says which space its position is in.</para></summary>
    public PlayerMoveState Step(in PlayerMoveState state, in MoveCommand command, float dt)
    {
        MoveState m = CharacterMovement.Step(state.Move, command, dt, groundHeightAdapter, tuning, groundNormalAdapter,
            physics, clampXzAdapter, mediumAdapter);
        // Carry the teleport epoch through unchanged: it is a networking marker, not a movement quantity, so a step
        // only advances position/vertical. This keeps a teleport marker alive across the single-World server's next
        // per-tick step (the sharded head preserves it in-place via PlayerMovementSystem's ref-component write).
        return new PlayerMoveState
        {
            Move = m,
            TeleportEpoch = state.TeleportEpoch,
            FrameAnchor = new Vector2(Frame.Anchor.X, Frame.Anchor.Z),
        };
    }
}
