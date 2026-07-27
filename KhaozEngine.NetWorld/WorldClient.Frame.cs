using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The client's ISLAND FRAME: the space its prediction steps in, ADOPTED from the server rather than derived.
/// <para>
/// Deriving it would fail at exactly the moment it matters. The client's prediction routinely sits a tick past a
/// re-anchor boundary the server has not crossed yet, so for that tick the two heads would pick different anchors
/// and every downstream comparison would be a frame-width out. So the frame is authoritative state, exactly like
/// position, and it rides the same wire field: <see cref="ReplicatedPosition.Frame"/>. The client reads it and
/// applies it, and that is the only place the client's frame is ever written.
/// </para>
/// <para>
/// The other half of the rule is the presentation boundary: <b>every position this client exposes to a consumer is
/// absolute world metres, without exception.</b> The frame is an internal representation of the prediction pipeline.
/// Without that rule the local avatar would come out of prediction frame-local while every remote came out of
/// <see cref="ReplicatedPosition.Value"/> absolute, both as a <c>Vector3</c>, both in the same
/// <see cref="EntityRenderState"/> list, with no compile error and no exception anywhere - the avatar would simply
/// render an anchor delta away from the world it is standing in.
/// </para>
/// </summary>
public sealed partial class WorldClient
{
    private readonly PlayerMoveSimulator simulator;
    // The island's physics world, held (not just handed to the simulator) because adopting a new frame rebases it in
    // the same gap between two steps the prediction state moves in. Null when the game supplied none.
    private readonly IPhysicsWorld? islandPhysics;
    // Mirrors WorldClientConfig.FrameAnchoring: gates both the ctor's rebasable-physics guard and AdoptIslandFrame's
    // runtime Rebase call. Set once at construction (WorldClientConfig is read-only after that).
    private readonly bool frameAnchoring;
    private WorldFrame islandFrame = WorldFrame.Origin;

    /// <summary>The frame the client's prediction currently steps in, adopted from the authoritative server.
    /// <see cref="WorldFrame.Origin"/> against an unframed server, and until the first snapshot arrives. Read it to
    /// convert something the engine handed you in island space - though nothing on the public surface is in it.</summary>
    public WorldFrame IslandFrame => islandFrame;

    /// <summary>
    /// Raised when the local player's authoritative frame changes, BEFORE the reconciliation replay and before the
    /// next predicted step. The argument is <c>(from, to, delta)</c>, where the delta is the EXACT translation
    /// carrying a coordinate from the old frame into the new one.
    /// <para>Everything the engine owns is already converted by the time this fires, INCLUDING the island's own
    /// <c>IPhysicsWorld</c> (the island rebases it, not the consumer). It is for the tail of state a consumer holds
    /// in the old frame itself: collider poses it registered outside the engine's own sink, its own spatial indices,
    /// debug overlays. A consumer that only reads the absolute positions this client hands it needs no handler at
    /// all.</para>
    /// <para><b>Against a sharded server this can fire at tick rate.</b> A cell handoff has no hysteresis band (it
    /// triggers purely on which cell the player's position falls in, unlike the flat head's re-anchor, which
    /// requires <c>WorldFrame.ReanchorRadius</c> of further travel before it fires again): a player standing exactly
    /// on a cell border can ping-pong across it tick after tick, each crossing re-stamping the frame and firing this
    /// event again. Keep a handler cheap for that reason alone, independent of how rare a re-anchor is on the flat
    /// head.</para>
    /// </summary>
    public event Action<WorldFrame, WorldFrame, Vector3>? FrameChanged;

    /// <summary>The local player's full predicted/reconciled render state (position + vertical velocity + grounded),
    /// in ABSOLUTE world metres. Exact movement the client already knows for its own avatar - use it to fill the
    /// local entity's <c>KhaozEngine.Game.CharacterSample</c> exact-movement fields (so a replicated-animator bridge
    /// reads true air state instead of finite-differencing position). Defaults (grounded false, zero velocity) until
    /// the first snapshot seeds prediction.
    /// <para>This and the local entry of <see cref="Snapshot"/> are the two places the frame is converted away, and
    /// they must agree: fixing only one of them is the natural half-fix, and its symptom is the camera target
    /// detaching from the terrain rather than anything that looks like a coordinate bug.</para></summary>
    public PlayerMoveState LocalRenderState => prediction.RenderedState.Absolute;

    /// <summary>The local player's predicted grounded flag (shorthand for <see cref="LocalRenderState"/>.Grounded).
    /// Frame-invariant.</summary>
    public bool LocalGrounded => prediction.RenderedState.Grounded;

    /// <summary>The local player's predicted vertical velocity, m/s positive up (shorthand for
    /// <see cref="LocalRenderState"/>.VerticalVelocity). Frame-invariant: Y is never framed.</summary>
    public float LocalVerticalVelocity => prediction.RenderedState.VerticalVelocity;

    /// <summary>The local player's predicted horizontal (planar ground-plane) speed in m/s, taken from the latest
    /// prediction tick (the commanded, collision-clamped move). Use it to drive a speed HUD, footstep audio, or a
    /// locomotion blend: it is the clean source that stays steady under lag, unlike differencing
    /// <see cref="LocalRenderState"/>.Position, which carries the decaying reconciliation render offset and so wobbles
    /// during a steady run. A speed is a difference, so it is frame-invariant. Zero until the first snapshot seeds
    /// prediction.</summary>
    public float LocalHorizontalSpeed => prediction.PredictedHorizontalSpeed;

    /// <summary>
    /// Adopts the authoritative frame carried on the local player's replicated position, and moves the island into it
    /// as ONE operation: the physics world is rebased and the simulator re-pointed together, so no step ever observes
    /// a half-moved island. Runs BEFORE the reconciliation replay, which is what makes the replayed commands step in
    /// the same space as the basis they start from.
    /// </summary>
    private void AdoptIslandFrame(WorldFrame frame)
    {
        if (frame == islandFrame) return;
        WorldFrame previous = islandFrame;
        // Gated on WorldClientConfig.FrameAnchoring: with it off the ctor never required a rebasable world, so
        // calling Rebase here would be reaching for a capability the physics world was never guaranteed to have.
        // In steady state this frame is unreachable anyway (a server with framing off never stamps off Origin), but
        // the gate matches the ctor guard rather than relying on that alone.
        if (frameAnchoring) islandPhysics?.Rebase(frame.Anchor);
        simulator.Frame = frame;
        islandFrame = frame;
        FrameChanged?.Invoke(previous, frame, previous.DeltaTo(frame));
    }

    // A framed prediction queries the island's physics world in the island's space, so that world has to be able to
    // follow the frame. Refuse it at construction rather than serve queries from another space: a client that could
    // not rebase would predict against colliders a frame-width from where it is standing, which is a character
    // walking through walls, not a rounding artifact. Both server heads frame by default, so a client that cannot
    // rebase is a client that will be wrong the moment it connects to one - the mirror of WorldServer's own guard.
    // Called only when WorldClientConfig.FrameAnchoring is on (see the ctor).
    private static void RequireRebasablePhysics(IPhysicsWorld? physics)
    {
        if (physics is not null && !physics.CanRebase)
            throw new ArgumentException(
                "WorldClient needs an IPhysicsWorld that can rebase (CanRebase), or no physics world at all: the "
              + "client adopts the server's island frame and its physics world's Origin must move with it, or "
              + "prediction queries colliders in a space its state is not in.", nameof(physics));
    }
}
