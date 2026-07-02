using KhaozEngine.Physics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Debug;

public enum CollisionShapeKind { Box, Sphere, Capsule, Cylinder, ConvexHull, TriangleMesh }

public readonly record struct CollisionStatic(PhysicsShape Shape, Pose Pose);

/// <summary>Per-kind color + display name for the collision overlay. Colors are translucent
/// and overridable by the game.</summary>
public sealed class CollisionOverlayPalette
{
    // Distinct hues at low alpha (Blender-proxy feel).
    readonly Color[] _colors =
    {
        new(0.90f, 0.20f, 0.20f, 0.35f), // Box       red
        new(0.20f, 0.55f, 0.95f, 0.35f), // Sphere    blue
        new(0.25f, 0.85f, 0.35f, 0.35f), // Capsule   green
        new(0.95f, 0.75f, 0.15f, 0.35f), // Cylinder  amber
        new(0.75f, 0.35f, 0.90f, 0.35f), // ConvexHull violet
        new(0.30f, 0.85f, 0.85f, 0.35f), // TriangleMesh cyan
    };

    static readonly string[] Names = { "Box", "Sphere", "Capsule", "Cylinder", "Convex hull", "Triangle mesh" };

    public Color For(CollisionShapeKind kind) => _colors[(int)kind];
    public Color this[CollisionShapeKind kind] { get => _colors[(int)kind]; set => _colors[(int)kind] = value; }
    public string NameFor(CollisionShapeKind kind) => Names[(int)kind];

    public static CollisionShapeKind KindOf(PhysicsShape shape) => shape switch
    {
        BoxShape => CollisionShapeKind.Box,
        SphereShape => CollisionShapeKind.Sphere,
        CapsuleShape => CollisionShapeKind.Capsule,
        CylinderShape => CollisionShapeKind.Cylinder,
        ConvexHullShape => CollisionShapeKind.ConvexHull,
        TriangleMeshShape => CollisionShapeKind.TriangleMesh,
        _ => throw new System.NotSupportedException($"Unsupported shape for overlay: {shape.GetType().Name}"),
    };
}
