using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>A collision shape. Convex primitives plus a triangle mesh (for non-convex buildings/interiors)
/// and a compound (disjoint child shapes). A backend converts these to its own representation.</summary>
public abstract class PhysicsShape { }

/// <summary>A sphere of the given radius, centred on the body pose.</summary>
public sealed class SphereShape(float radius) : PhysicsShape
{
    public float Radius { get; } = radius;
}

/// <summary>An upright capsule (axis = local Y). <paramref name="length"/> is the cylindrical segment
/// length, so the total height is <c>Length + 2*Radius</c>.</summary>
public sealed class CapsuleShape(float radius, float length) : PhysicsShape
{
    public float Radius { get; } = radius;
    public float Length { get; } = length;
}

/// <summary>A box with the given half-extents in local space.</summary>
public sealed class BoxShape(Vector3 halfExtents) : PhysicsShape
{
    public Vector3 HalfExtents { get; } = halfExtents;
}

/// <summary>A cylinder (axis = local Y) of the given radius and length.</summary>
public sealed class CylinderShape(float radius, float length) : PhysicsShape
{
    public float Radius { get; } = radius;
    public float Length { get; } = length;
}

/// <summary>A convex hull of the given local-space points (solid props: rocks, trunks).</summary>
public sealed class ConvexHullShape(Vector3[] points) : PhysicsShape
{
    public Vector3[] Points { get; } = points;
}

/// <summary>A static triangle mesh (non-convex: buildings, interiors). Indices are triples.</summary>
public sealed class TriangleMeshShape(Vector3[] vertices, int[] indices) : PhysicsShape
{
    public Vector3[] Vertices { get; } = vertices;
    public int[] Indices { get; } = indices;
}

/// <summary>One child of a <see cref="CompoundShape"/>, placed at a local pose.</summary>
public readonly record struct CompoundChild(PhysicsShape Shape, Pose Local);

/// <summary>Several disjoint child shapes treated as one static body.</summary>
public sealed class CompoundShape(CompoundChild[] children) : PhysicsShape
{
    public CompoundChild[] Children { get; } = children;
}
