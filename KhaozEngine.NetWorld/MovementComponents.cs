using System.Numerics;
using KhaozEngine.Ecs;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Reads and writes a player's movement as the two replicated components it is split across:
/// <see cref="MovementState"/> (every observer) and <see cref="MovementOwnerState"/> (the owner only). Every server
/// site that authors or rebuilds a <see cref="PlayerMoveState"/> goes through here, so none of them can write one half
/// and forget the other.
/// </summary>
internal static class MovementComponents
{
    /// <summary>Writes both movement components of <paramref name="state"/> onto <paramref name="entity"/>.</summary>
    internal static void Set(World world, Entity entity, in PlayerMoveState state)
    {
        world.Set(entity, MovementState.From(state));
        world.Set(entity, MovementOwnerState.From(state));
    }

    /// <summary>Rebuilds the full state of <paramref name="entity"/> around <paramref name="position"/>. A missing
    /// component reads as its <c>default</c>.</summary>
    internal static PlayerMoveState Read(World world, Entity entity, Vector3 position)
    {
        world.TryGet(entity, out MovementState movement);
        world.TryGet(entity, out MovementOwnerState owner);
        return PlayerMoveState.From(position, movement, owner);
    }
}
