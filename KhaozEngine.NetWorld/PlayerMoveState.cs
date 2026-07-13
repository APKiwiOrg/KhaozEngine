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
        },
        TeleportEpoch = movement.TeleportEpoch,
    };
}
