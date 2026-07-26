using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The predicted/authoritative movement state of one player: the full kinematic <see cref="Locomotion.MoveState"/>
/// (3D position + vertical velocity + grounded + feel timers). Implements <see cref="IPredictedState{T}"/> over its
/// XZ plane (so client prediction measures and smooths reconciliation error on the ground plane), while the whole
/// state - including the vertical axis - is carried through prediction replay and corrected by the authoritative
/// basis, so jumping and falling reconcile alongside horizontal movement.
/// </summary>
public struct PlayerMoveState : IPredictedState<PlayerMoveState>
{
    /// <summary>The carried kinematic state (position + vertical velocity + grounded + coyote/buffer timers).</summary>
    public MoveState Move;

    /// <summary>
    /// Monotonic teleport epoch (implements <see cref="IPredictedState{T}.TeleportEpoch"/>). The authoritative server
    /// bumps it ONLY at teleport sites (join/reconnect placement, admin/self-rescue, future fast-travel); normal
    /// movement leaves it unchanged. An advance tells client prediction to hard-CUT rather than glide, regardless of
    /// distance. It is a networking marker, not a movement quantity - the simulator carries it through a step
    /// unchanged, and it rides the wire on <see cref="MovementState.TeleportEpoch"/>.
    /// </summary>
    public uint TeleportEpoch { get; set; }

    /// <summary>Capsule-centre world position (Y is ground-clamped while grounded, free while airborne).</summary>
    public Vector3 Position { readonly get => Move.Position; set => Move.Position = value; }

    /// <summary>Vertical velocity (m/s, positive up).</summary>
    public float VerticalVelocity { readonly get => Move.VerticalVelocity; set => Move.VerticalVelocity = value; }

    /// <summary>True while resting on the ground this tick.</summary>
    public bool Grounded { readonly get => Move.Grounded; set => Move.Grounded = value; }

    /// <summary>True while surface-swimming this tick (mirrors <see cref="MoveState.Swimming"/>). Replicated via
    /// <see cref="MovementState.Swimming"/> so the local owner reconciles it and remotes animate the swim clips.</summary>
    public bool Swimming { readonly get => Move.Swimming; set => Move.Swimming = value; }

    readonly Vector2 IPredictedState<PlayerMoveState>.Position => new(Move.Position.X, Move.Position.Z);

    /// <summary>The vertical axis (height) carried through render smoothing, so a jump/fall eases on screen.</summary>
    readonly float IPredictedState<PlayerMoveState>.Vertical => Move.Position.Y;

    /// <summary>The discrete-step impulse this predicted tick committed (<see cref="MoveState.StepDeltaY"/>): the signed
    /// vertical delta of an isolated step the continuous glide declines, read once per Predict into the client mesh
    /// smoother. Sim-local (rides no wire); 0 on every non-step tick.</summary>
    readonly float IPredictedState<PlayerMoveState>.StepDeltaY => Move.StepDeltaY;

    /// <summary>Returns a copy with the planar (XZ) position replaced; Y and the vertical state are preserved.</summary>
    public readonly PlayerMoveState WithPosition(Vector2 position)
    {
        MoveState m = Move;
        m.Position = new Vector3(position.X, Move.Position.Y, position.Y);
        return new PlayerMoveState { Move = m, TeleportEpoch = TeleportEpoch };
    }

    /// <summary>Returns a copy with the smoothed planar (XZ) AND vertical (Y) render position applied; the rest of
    /// the kinematic state (velocity, grounded, timers) is preserved. Builds the rendered presentation state so the
    /// height eases alongside the ground plane instead of stair-stepping or popping.</summary>
    readonly PlayerMoveState IPredictedState<PlayerMoveState>.WithRenderState(Vector2 position, float vertical)
    {
        MoveState m = Move;
        m.Position = new Vector3(position.X, vertical, position.Y);
        return new PlayerMoveState { Move = m, TeleportEpoch = TeleportEpoch };
    }

    /// <summary>Rebuilds a full state from the two replicated components: the 3D <paramref name="position"/>
    /// (<see cref="ReplicatedPosition"/>) plus the vertical <paramref name="movement"/> (<see cref="MovementState"/>,
    /// which also carries the <see cref="TeleportEpoch"/>).</summary>
    public static PlayerMoveState From(Vector3 position, in MovementState movement) => new()
    {
        Move = new MoveState
        {
            Position = position,
            VerticalVelocity = movement.VerticalVelocity,
            Grounded = movement.Grounded,
            TimeSinceGrounded = movement.TimeSinceGrounded,
            JumpBufferRemaining = movement.JumpBufferRemaining,
            Swimming = movement.Swimming,
            ClimbRate = MovementState.DecodeClimbRate(movement.ClimbRateQ),
            // Seed the sim-local ascent EWMA (MoveState.ClimbRateEwma, which does NOT ride the wire) from the wire's
            // decoded rate, so client-prediction replay CONTINUES the average from the authoritative value instead of
            // restarting it from 0. Without this the reconcile rebuilds the basis here, the EWMA restarts every
            // reconcile, and over a pending-command window SHORTER than the EWMA tau (6 ticks) the exported ClimbRate
            // reads below the achieved rise - the render feed-forward/damp equilibrium then sinks below the true feet
            // (tens of mm at walk to ~100 mm at run on short RTT windows), plus a per-reconcile ripple.
            // ASCENT-ONLY via Max(0, ...): the EWMA is ascent state (only the ascent branch reads/writes it; descent's
            // signal is STATELESS - re-derived from the current tick's geometry, never from the EWMA). A descent basis
            // carries a NEGATIVE decoded rate; seeding the ascent EWMA negative would suppress the next ascent's seed
            // sentinel (== 0) and hold ClimbRate = Max(0, ewma) at 0 for several ticks into that ascent (a fresh sink).
            // Clamping to 0 for a descent basis re-arms the ascent seed path and leaves the stateless descent untouched.
            // At a genuine run START the decoded rate is 0 (sub-quantum), so the seed still lands at 0 and the first-tick
            // seed fraction applies exactly as in a lag-free run.
            ClimbRateEwma = MathF.Max(0f, MovementState.DecodeClimbRate(movement.ClimbRateQ)),
            // Restore the server-authored haste/slow multiplier into the reconcile basis. This is the reason the scale
            // rides the wire at all: this method rebuilds the basis from the replicated components ALONE, so a scale
            // living only on the sim-local MoveState would reset here on every correction and the pending command
            // window would replay at base speed while the server ran it boosted - a permanent rubber-band for the whole
            // duration of the buff.
            SpeedScale = MovementState.DecodeSpeedScale(movement.SpeedScaleQ),
        },
        TeleportEpoch = movement.TeleportEpoch,
    };
}
