using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The owner-only half of a player's replicated movement state: the two feel timers that exist only to make the
/// owning client's reconciliation replay exact. <see cref="MovementState"/> carries everything a remote observer
/// reads (the vertical axis, the grounded, swim and climb signals, the heading, the committed move), and this carries
/// what only the owner's replay reads.
/// <para>Registered in <see cref="MoveProtocol.CreateRegistry"/> as the built-in type id
/// <see cref="MoveProtocol.MovementOwnerTypeId"/> on <c>ReplicationChannels.Default | ReplicationChannels.OwnerOnly</c>.
/// Client AoI serving writes it only on the receiving client's own player, so no other observer pays for its bytes.
/// Cell persistence and cell handoff write it unconditionally, because the server keeps simulating the player after a
/// restore or a handoff and the coyote and jump-buffer windows have to survive both. Border ghosts do not carry it,
/// which is safe because a ghost is never simulated.</para>
/// <para>Split out of <see cref="MovementState"/> in wire generation 12 (<see cref="MoveProtocol.WireProtocolVersion"/>).
/// A stored cell blob from an older generation is brought forward by moving these eight bytes out of its movement
/// frame into a frame of this type (<see cref="BuiltinBlobLayout.MovementOwnerWireGeneration"/>).</para>
/// </summary>
public struct MovementOwnerState : IComponent
{
    /// <summary>Seconds since last grounded (coyote-time accounting,
    /// <see cref="KhaozEngine.Locomotion.MoveState.TimeSinceGrounded"/>).</summary>
    public float TimeSinceGrounded;

    /// <summary>Seconds of jump-buffer remaining (jump-buffer accounting,
    /// <see cref="KhaozEngine.Locomotion.MoveState.JumpBufferRemaining"/>).</summary>
    public float JumpBufferRemaining;

    /// <summary>The owner-only part of a full <see cref="PlayerMoveState"/>. <see cref="MovementState.From"/> is the
    /// observer-visible part.</summary>
    public static MovementOwnerState From(in PlayerMoveState state) => new()
    {
        TimeSinceGrounded = state.Move.TimeSinceGrounded,
        JumpBufferRemaining = state.Move.JumpBufferRemaining,
    };
}
