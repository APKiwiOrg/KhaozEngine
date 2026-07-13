using System;
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

    /// <summary>The authoritative teleport epoch (see <see cref="PlayerMoveState.TeleportEpoch"/>): a monotonic
    /// counter the server bumps only at teleport sites, replicated to the local owner alongside the vertical axis so
    /// its prediction cuts on an advance. Added on the wire in generation 4
    /// (<see cref="MoveProtocol.WireProtocolVersion"/>); a mismatched peer is rejected at connect by the always-on
    /// <see cref="WireGenerationAuthenticator"/>.</summary>
    public uint TeleportEpoch;

    /// <summary>The signed step-climb rate (<see cref="KhaozEngine.Locomotion.MoveState.ClimbRate"/>) quantized to a
    /// single byte at the FIXED wire scale <see cref="ClimbRateQuantum"/> (0.05 m/s per unit, range +/-6.35 m/s), so
    /// the climb signal reaches remote observers, not just the local owner. Decoded rate =
    /// <c>ClimbRateQ * ClimbRateQuantum</c>. <b>0 means "not climbing" - the climb FLAG is implicit in the rate</b>: a
    /// sub-0.05 m/s climb quantizes to 0 (its per-frame bob is sub-millimetre, below perception - the honest dead-zone).
    /// The fixed scale is deliberately NOT <see cref="KhaozEngine.Locomotion.MoveTuning.MaxStepClimbSpeed"/> (per-consumer tuning the codec
    /// cannot see), which keeps the codec consumer-agnostic. Added on the wire in generation 5
    /// (<see cref="MoveProtocol.WireProtocolVersion"/>); a mismatched peer is rejected at connect by the always-on
    /// <see cref="WireGenerationAuthenticator"/>.</summary>
    public sbyte ClimbRateQ;

    /// <summary>Fixed wire scale for <see cref="ClimbRateQ"/> (m/s per quantum unit): 0.05, giving +/-6.35 m/s over an
    /// <see cref="sbyte"/> at 0.05 m/s resolution. Consumer-agnostic (independent of any consumer's
    /// <see cref="KhaozEngine.Locomotion.MoveTuning.MaxStepClimbSpeed"/>), so the codec round-trips the same for every game.</summary>
    public const float ClimbRateQuantum = 0.05f;

    /// <summary>Quantizes a signed climb rate (m/s) to the wire <see cref="sbyte"/>: rounded to the nearest
    /// <see cref="ClimbRateQuantum"/> and clamped to the symmetric +/-127 range (leaving -128 unused). A sub-quantum
    /// rate rounds to 0 (the implicit not-climbing dead-zone).</summary>
    public static sbyte QuantizeClimbRate(float rate) =>
        (sbyte)Math.Clamp((int)MathF.Round(rate / ClimbRateQuantum), -127, 127);

    /// <summary>Decodes a wire <see cref="ClimbRateQ"/> back to a signed climb rate (m/s): <c>q * ClimbRateQuantum</c>.
    /// 0 decodes to exactly 0 (not climbing).</summary>
    public static float DecodeClimbRate(sbyte q) => q * ClimbRateQuantum;

    /// <summary>The vertical part of a full <see cref="PlayerMoveState"/> (the position is in
    /// <see cref="ReplicatedPosition"/>).</summary>
    public static MovementState From(in PlayerMoveState state) => new()
    {
        VerticalVelocity = state.Move.VerticalVelocity,
        Grounded = state.Move.Grounded,
        TimeSinceGrounded = state.Move.TimeSinceGrounded,
        JumpBufferRemaining = state.Move.JumpBufferRemaining,
        Swimming = state.Move.Swimming,
        TeleportEpoch = state.TeleportEpoch,
        ClimbRateQ = QuantizeClimbRate(state.Move.ClimbRate),
    };
}
