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
    /// <summary>The prop's solid top world Y. Height-aware resolution blocks the capsule only while its feet are
    /// below <see cref="Top"/> (the side); at or above it the capsule is standing on top and is not pushed.
    /// Default <see cref="float.PositiveInfinity"/> = always blocks (a thin blocker like a tree trunk).</summary>
    public float Top { get; }

    WorldCollider(ColliderKind kind, Vector2 center, float radius, Vector2 halfExtents, float yaw, float top)
    {
        Kind = kind; Center = center; Radius = radius; HalfExtents = halfExtents; Yaw = yaw; Top = top;
    }

    /// <summary>A cylinder collider at <paramref name="center"/> with <paramref name="radius"/>; optional solid
    /// <paramref name="top"/> world Y for height-aware blocking (default always-blocks).</summary>
    public static WorldCollider Cylinder(Vector2 center, float radius, float top = float.PositiveInfinity)
        => new(ColliderKind.Cylinder, center, radius, Vector2.Zero, 0f, top);

    /// <summary>An oriented-box collider at <paramref name="center"/>, <paramref name="halfExtents"/>,
    /// rotated <paramref name="yaw"/> radians; optional solid <paramref name="top"/> world Y (default always-blocks).</summary>
    public static WorldCollider Box(Vector2 center, Vector2 halfExtents, float yaw, float top = float.PositiveInfinity)
        => new(ColliderKind.Box, center, 0f, halfExtents, yaw, top);

    /// <summary>Conservative broad-phase radius (used to insert into the spatial hash). Cylinder = its radius;
    /// box = its half-diagonal.</summary>
    public float BoundingRadius => Kind == ColliderKind.Cylinder ? Radius : HalfExtents.Length();

    /// <summary>Push-out of a circle (<paramref name="c"/>, <paramref name="r"/>) from this collider. True + the
    /// MTV in <paramref name="push"/> when overlapping; false + zero when clear.</summary>
    public bool Resolve(Vector2 c, float r, out Vector2 push) => Kind == ColliderKind.Cylinder
        ? BoxCollision.ResolveCircleCircle(c, r, Center, Radius, out push)
        : BoxCollision.ResolveCircleOrientedBox(c, r, Center, HalfExtents, Yaw, out push);
}
