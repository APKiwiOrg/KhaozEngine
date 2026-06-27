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

    /// <summary>Capsule-centre world position (Y is ground-clamped while grounded, free while airborne).</summary>
    public Vector3 Position { readonly get => Move.Position; set => Move.Position = value; }

    /// <summary>Vertical velocity (m/s, positive up).</summary>
    public float VerticalVelocity { readonly get => Move.VerticalVelocity; set => Move.VerticalVelocity = value; }

    /// <summary>True while resting on the ground this tick.</summary>
    public bool Grounded { readonly get => Move.Grounded; set => Move.Grounded = value; }

    readonly Vector2 IPredictedState<PlayerMoveState>.Position => new(Move.Position.X, Move.Position.Z);

    /// <summary>Returns a copy with the planar (XZ) position replaced; Y and the vertical state are preserved.</summary>
    public readonly PlayerMoveState WithPosition(Vector2 position)
    {
        MoveState m = Move;
        m.Position = new Vector3(position.X, Move.Position.Y, position.Y);
        return new PlayerMoveState { Move = m };
    }

    /// <summary>Rebuilds a full state from the two replicated components: the 3D <paramref name="position"/>
    /// (<see cref="ReplicatedPosition"/>) plus the vertical <paramref name="movement"/> (<see cref="MovementState"/>).</summary>
    public static PlayerMoveState From(Vector3 position, in MovementState movement) => new()
    {
        Move = new MoveState
        {
            Position = position,
            VerticalVelocity = movement.VerticalVelocity,
            Grounded = movement.Grounded,
            TimeSinceGrounded = movement.TimeSinceGrounded,
            JumpBufferRemaining = movement.JumpBufferRemaining,
        },
    };
}
