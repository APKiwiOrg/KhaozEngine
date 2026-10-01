namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// How a tile world becomes static colliders. Every length is in world metres.
/// </summary>
public sealed record TileColliderOptions
{
    /// <summary>The thickness of the box built for an edge wall. Defaults to 0.1 m.</summary>
    public float WallThickness { get; init; } = 0.1f;

    /// <summary>
    /// How far a blocked tile's box rises above the highest ground corner under it. Its bottom is the lowest corner.
    /// Null means the document's plane height.
    /// </summary>
    public float? BlockedHeight { get; init; }

    /// <summary>The thickness of the box built under a walk surface. Defaults to 0.1 m.</summary>
    public float WalkSurfaceThickness { get; init; } = 0.1f;
}
