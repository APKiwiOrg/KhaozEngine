using KhaozEngine.Ecs;

namespace KhaozEngine.Sharding;

/// <summary>
/// Marks an entity in a cell's <see cref="KhaozEngine.Ecs.World"/> as a read-only <b>ghost</b>: a mirror of an
/// entity owned (authoritatively simulated) by a neighboring cell, present so this cell's systems can see it for
/// collision / visibility / targeting across the border. A cell's world is its owned entities plus these ghosts.
/// Game systems must treat <c>Ghost</c>-tagged entities as read-only (the owner cell is the only simulator);
/// exclude them from authoritative mutation. <see cref="Source"/> is the cell that owns the real entity.
/// </summary>
public struct Ghost : IComponent
{
    /// <summary>The cell that authoritatively owns (and simulates) the real entity this ghost mirrors.</summary>
    public CellCoord Source;
}
