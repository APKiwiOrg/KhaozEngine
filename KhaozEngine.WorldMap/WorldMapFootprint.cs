using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.WorldMap;

/// <summary>The collision role of a surface footprint.</summary>
public enum WorldMapCollision
{
    /// <summary>The footprint does not block movement.</summary>
    None,
    /// <summary>The footprint is a solid obstacle.</summary>
    Solid,
    /// <summary>The footprint forms a wall.</summary>
    Wall
}

/// <summary>An oriented rectangular footprint already filtered by the source to the mapped surface.</summary>
/// <param name="Id">The source's stable placement identifier.</param>
/// <param name="ArchetypeKey">The source archetype key used by symbol rules.</param>
/// <param name="Center">The rectangle centre in map metres.</param>
/// <param name="HalfExtents">The half extents along the local axes before rotation, in metres.</param>
/// <param name="Rotation">Radians rotating local +x clockwise on the north-up map.</param>
/// <param name="Tags">The authored tags used by symbol and icon rules.</param>
/// <param name="IsRoof">Whether this footprint depicts a roof.</param>
/// <param name="Collision">The footprint's collision role.</param>
public sealed record WorldMapFootprint(
    long Id, string ArchetypeKey, Vector2 Center, Vector2 HalfExtents, float Rotation,
    IReadOnlyList<string> Tags, bool IsRoof, WorldMapCollision Collision)
{
    /// <summary>Whether the footprint blocks movement.</summary>
    public bool Blocking => Collision != WorldMapCollision.None;
}
