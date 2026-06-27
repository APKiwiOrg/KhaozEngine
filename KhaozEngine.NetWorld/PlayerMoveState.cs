using System.Numerics;
using KhaozEngine.Netcode;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The predicted/authoritative movement state of one player: a 3D world position (Y ground-clamped).
/// Implements <see cref="IPredictedState{T}"/> over its XZ plane, so client prediction measures and
/// smooths reconciliation error on the ground plane while Y is a pure function of XZ via the ground
/// delegate (re-derived each step).
/// </summary>
public struct PlayerMoveState : IPredictedState<PlayerMoveState>
{
    /// <summary>Capsule-centre world position.</summary>
    public Vector3 Position;

    Vector2 IPredictedState<PlayerMoveState>.Position => new(Position.X, Position.Z);

    /// <summary>Returns a copy with the planar (XZ) position replaced; Y is kept from this state.</summary>
    public PlayerMoveState WithPosition(Vector2 position) =>
        new() { Position = new Vector3(position.X, Position.Y, position.Y) };
}
