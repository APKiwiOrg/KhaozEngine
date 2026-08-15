using System;

namespace KhaozEngine.TileWorld;

/// <summary>Derived per-tile collision. Movement bits are 0..8, <see cref="ProjectileBlocked"/> and
/// <see cref="Decoration"/> are reserved for ranged line of sight and ground decor and are never set by this
/// program's baker. A wall is one edge shared by two tiles: the baker sets the edge bit on both, so a
/// movement check never needs to look at objects.</summary>
[Flags]
public enum TileCollisionFlags : ushort
{
    /// <summary>Nothing blocks this tile.</summary>
    None = 0,
    /// <summary>The whole tile is impassable, no edge test needed.</summary>
    Blocked = 1,
    /// <summary>The tile's north edge is walled.</summary>
    WallN = 2,
    /// <summary>The tile's east edge is walled.</summary>
    WallE = 4,
    /// <summary>The tile's south edge is walled.</summary>
    WallS = 8,
    /// <summary>The tile's west edge is walled.</summary>
    WallW = 16,
    /// <summary>The tile's north-east corner is walled, blocking that diagonal step.</summary>
    CornerNE = 32,
    /// <summary>The tile's north-west corner is walled, blocking that diagonal step.</summary>
    CornerNW = 64,
    /// <summary>The tile's south-east corner is walled, blocking that diagonal step.</summary>
    CornerSE = 128,
    /// <summary>The tile's south-west corner is walled, blocking that diagonal step.</summary>
    CornerSW = 256,
    /// <summary>Reserved: blocks projectiles and line of sight without blocking movement.</summary>
    ProjectileBlocked = 512,
    /// <summary>Reserved: ground decor that a mover walks over, carried for the systems that care.</summary>
    Decoration = 1024,
}
