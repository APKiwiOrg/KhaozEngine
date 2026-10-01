namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// What a <see cref="TileCollider"/> was built from. The declaration order is the canonical order
/// <see cref="TileWorldColliders.Colliders"/> lists them in, and the byte value goes into
/// <see cref="TileWorldColliders.Hash"/>, so neither may change.
/// </summary>
public enum TileColliderKind : byte
{
    /// <summary>The drawn ground of one region, a triangle mesh.</summary>
    Ground,
    /// <summary>One blocked edge of a placed <c>Wall</c> or <c>WallCorner</c> object, a thin box.</summary>
    Wall,
    /// <summary>A tile with no underlay or marked <see cref="TileSettings.Blocked"/>, a box.</summary>
    Blocked,
    /// <summary>A placed <c>Solid</c> or <c>Diagonal</c> object, a box.</summary>
    Object,
    /// <summary>One walk surface of a placed object, a thin box under its top.</summary>
    WalkSurface,
}
