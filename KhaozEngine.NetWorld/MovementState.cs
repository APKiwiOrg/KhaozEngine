using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The replicated vertical movement state of a player entity: the part of <see cref="KhaozEngine.Locomotion.MoveState"/>
/// beyond the <see cref="ReplicatedPosition"/> (which carries the 3D position). It rides alongside
/// <see cref="ReplicatedPosition"/> so the vertical axis survives a sharded cell handoff (handoff transfers
/// registered components) and reaches the client, where it forms the authoritative reconciliation basis. Registered
/// in <see cref="MoveProtocol.CreateRegistry"/> as type id <see cref="MoveProtocol.MovementTypeId"/>; not
/// interpolated (remotes render from <see cref="ReplicatedPosition"/>; only the local owner uses this, exactly).
/// </summary>
public struct MovementState : IComponent
{
    /// <summary>Vertical velocity (m/s, positive up).</summary>
    public float VerticalVelocity;

    /// <summary>True while resting on the ground this tick.</summary>
    public bool Grounded;

    /// <summary>Seconds since last grounded (coyote-time accounting).</summary>
    public float TimeSinceGrounded;

    /// <summary>Seconds of jump-buffer remaining (jump-buffer accounting).</summary>
    public float JumpBufferRemaining;

    /// <summary>True while the player is surface-swimming (mirrors <see cref="KhaozEngine.Locomotion.MoveState.Swimming"/>).
    /// Replicated alongside the vertical axis so the local owner reconciles it AND remote clients read it to drive the
    /// swim animation state (Task 3 derives the remote swim pose from this bit rather than re-querying water). Added on
    /// the wire in generation 3 (<see cref="MoveProtocol.WireProtocolVersion"/>); a mismatched peer is rejected at
    /// connect by the always-on <see cref="WireGenerationAuthenticator"/>.</summary>
    public bool Swimming;

    /// <summary>The vertical part of a full <see cref="PlayerMoveState"/> (the position is in
    /// <see cref="ReplicatedPosition"/>).</summary>
    public static MovementState From(in PlayerMoveState state) => new()
    {
        VerticalVelocity = state.Move.VerticalVelocity,
        Grounded = state.Move.Grounded,
        TimeSinceGrounded = state.Move.TimeSinceGrounded,
        JumpBufferRemaining = state.Move.JumpBufferRemaining,
        Swimming = state.Move.Swimming,
    };
}
