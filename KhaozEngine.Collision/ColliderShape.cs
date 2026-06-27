using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// A prop-local (unplaced, unit-scale) collider declaration: the optional collision footprint carried on an
/// asset entry. A <see cref="ColliderKind.Cylinder"/> stores a <see cref="Radius"/>; a
/// <see cref="ColliderKind.Box"/> stores <see cref="HalfW"/> / <see cref="HalfD"/>. <c>Place</c> turns
/// it into a world-space <see cref="WorldCollider"/> at a scatter placement (centre, scale, yaw).
/// </summary>
public readonly struct ColliderShape
{
    /// <summary>The shape kind.</summary>
    public ColliderKind Kind { get; }
    /// <summary>Cylinder radius at unit scale. Unused for a box.</summary>
    public float Radius { get; }
    /// <summary>Box half-width (local X) at unit scale. Unused for a cylinder.</summary>
    public float HalfW { get; }
    /// <summary>Box half-depth (local Z) at unit scale. Unused for a cylinder.</summary>
    public float HalfD { get; }

    ColliderShape(ColliderKind kind, float radius, float halfW, float halfD)
    {
        Kind = kind; Radius = radius; HalfW = halfW; HalfD = halfD;
    }

    /// <summary>A cylinder footprint of the given unit-scale <paramref name="radius"/>.</summary>
    public static ColliderShape Cylinder(float radius) => new(ColliderKind.Cylinder, radius, 0f, 0f);

    /// <summary>A box footprint of the given unit-scale half-width / half-depth.</summary>
    public static ColliderShape Box(float halfW, float halfD) => new(ColliderKind.Box, 0f, halfW, halfD);

    /// <summary>Place this shape at <paramref name="center"/> scaled by <paramref name="scale"/> and rotated by
    /// <paramref name="yaw"/> (radians); a cylinder ignores yaw. The collider always-blocks (top = +inf).</summary>
    public WorldCollider Place(Vector2 center, float scale, float yaw) => Place(center, scale, yaw, float.PositiveInfinity);

    /// <summary>Place this shape with a solid <paramref name="top"/> world Y for height-aware blocking (the side
    /// blocks only while the capsule's feet are below <paramref name="top"/>).</summary>
    public WorldCollider Place(Vector2 center, float scale, float yaw, float top) => Kind == ColliderKind.Cylinder
        ? WorldCollider.Cylinder(center, Radius * scale, top)
        : WorldCollider.Box(center, new Vector2(HalfW * scale, HalfD * scale), yaw, top);
}
