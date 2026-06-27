using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>Which static shape a <see cref="WorldCollider"/> is.</summary>
public enum ColliderKind
{
    /// <summary>A circle in the XZ plane (vertical cylinder): a tree trunk, a rock, a barrel.</summary>
    Cylinder,
    /// <summary>An oriented rectangle in the XZ plane (a building footprint).</summary>
    Box,
}

/// <summary>
/// One placed static collider in world space (XZ): a <see cref="ColliderKind.Cylinder"/> (centre + radius) or a
/// <see cref="ColliderKind.Box"/> (centre + half-extents + yaw). The unit <see cref="WorldColliders"/> stores
/// and the player capsule's footprint resolves against. Built from a prop's <see cref="ColliderShape"/> via
/// <see cref="ColliderShape.Place"/> or hand-authored for a building. Render-free, plain float.
/// </summary>
public readonly struct WorldCollider
{
    /// <summary>The shape kind.</summary>
    public ColliderKind Kind { get; }
    /// <summary>World-space XZ centre.</summary>
    public Vector2 Center { get; }
    /// <summary>Cylinder radius (world units). Unused for a box.</summary>
    public float Radius { get; }
    /// <summary>Box half-extents (world units, pre-rotation). Unused for a cylinder.</summary>
    public Vector2 HalfExtents { get; }
    /// <summary>Box rotation about its centre (radians). Unused for a cylinder.</summary>
    public float Yaw { get; }

    WorldCollider(ColliderKind kind, Vector2 center, float radius, Vector2 halfExtents, float yaw)
    {
        Kind = kind; Center = center; Radius = radius; HalfExtents = halfExtents; Yaw = yaw;
    }

    /// <summary>A cylinder collider at <paramref name="center"/> with <paramref name="radius"/>.</summary>
    public static WorldCollider Cylinder(Vector2 center, float radius)
        => new(ColliderKind.Cylinder, center, radius, Vector2.Zero, 0f);

    /// <summary>An oriented-box collider at <paramref name="center"/>, <paramref name="halfExtents"/>,
    /// rotated <paramref name="yaw"/> radians.</summary>
    public static WorldCollider Box(Vector2 center, Vector2 halfExtents, float yaw)
        => new(ColliderKind.Box, center, 0f, halfExtents, yaw);

    /// <summary>Conservative broad-phase radius (used to insert into the spatial hash). Cylinder = its radius;
    /// box = its half-diagonal.</summary>
    public float BoundingRadius => Kind == ColliderKind.Cylinder ? Radius : HalfExtents.Length();

    /// <summary>Push-out of a circle (<paramref name="c"/>, <paramref name="r"/>) from this collider. True + the
    /// MTV in <paramref name="push"/> when overlapping; false + zero when clear.</summary>
    public bool Resolve(Vector2 c, float r, out Vector2 push) => Kind == ColliderKind.Cylinder
        ? BoxCollision.ResolveCircleCircle(c, r, Center, Radius, out push)
        : BoxCollision.ResolveCircleOrientedBox(c, r, Center, HalfExtents, Yaw, out push);
}
