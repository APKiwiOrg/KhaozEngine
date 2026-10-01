using KhaozEngine.Physics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>One static collider a tile world is described as.</summary>
/// <param name="Kind">What it was built from.</param>
/// <param name="Shape">Its shape, in its own local space.</param>
/// <param name="Pose">Where the shape sits, absolute in world metres, before any floating origin.</param>
public readonly record struct TileCollider(TileColliderKind Kind, PhysicsShape Shape, Pose Pose);
