using KhaozEngine.Ecs;

namespace KhaozEngine.Sharding;

/// <summary>
/// Marks an entity mid <b>authority handoff</b>: its owner cell has serialized it and sent a
/// <see cref="CellMessageKind.Migrate"/> to the destination, and is holding it frozen (relinquished, not
/// simulated, not counted as an owner) until the destination acks. On the ack the owner releases (despawns) it.
/// Game systems must treat <c>Migrating</c> entities as frozen, like <see cref="Ghost"/>s. <see cref="Destination"/>
/// is the cell taking ownership.
/// </summary>
public struct Migrating : IComponent
{
    /// <summary>The cell taking authoritative ownership of the entity.</summary>
    public CellCoord Destination;
}
