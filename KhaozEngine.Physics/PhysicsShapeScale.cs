using System;
using System.Numerics;

namespace KhaozEngine.Physics;

/// <summary>Returns a new <see cref="PhysicsShape"/> with all geometry scaled uniformly by a single factor.
/// Convex-hull / triangle-mesh vertex positions are pre-multiplied; primitive (sphere/capsule/cylinder/box)
/// length fields are scaled; <see cref="CompoundShape"/> children recurse and each child's local-pose POSITION
/// is scaled (orientation unchanged). A scale of 1 (within 1e-6) returns the original instance unchanged. The
/// single public home for per-placement uniform shape scaling, shared by the Render3D-side chunk-statics loader
/// and any headless consumer (e.g. an authoritative game server) that must scale a baked shape before adding it
/// as a static. Non-uniform (per-axis) scale is intentionally not modelled (the prop scatter emits one uniform
/// scale per placement).</summary>
public static class PhysicsShapeScale
{
    /// <summary>A new shape with every dimension scaled by <paramref name="scale"/>; the original instance when
    /// <paramref name="scale"/> is 1 (within 1e-6).</summary>
    public static PhysicsShape Uniform(PhysicsShape shape, float scale)
    {
        if (shape is null) throw new ArgumentNullException(nameof(shape));
        if (MathF.Abs(scale - 1f) < 1e-6f) return shape;

        return shape switch
        {
            SphereShape s        => new SphereShape(s.Radius * scale),
            CapsuleShape c       => new CapsuleShape(c.Radius * scale, c.Length * scale),
            CylinderShape cy     => new CylinderShape(cy.Radius * scale, cy.Length * scale),
            BoxShape b           => new BoxShape(b.HalfExtents * scale),
            ConvexHullShape h    => ScaleConvexHull(h, scale),
            TriangleMeshShape m  => ScaleTriangleMesh(m, scale),
            CompoundShape co     => ScaleCompound(co, scale),
            _ => throw new NotSupportedException($"PhysicsShapeScale.Uniform: unsupported shape type {shape.GetType().Name}."),
        };
    }

    static ConvexHullShape ScaleConvexHull(ConvexHullShape h, float scale)
    {
        Vector3[] src = h.Points;
        var dst = new Vector3[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
        return new ConvexHullShape(dst);
    }

    static TriangleMeshShape ScaleTriangleMesh(TriangleMeshShape m, float scale)
    {
        Vector3[] src = m.Vertices;
        var dst = new Vector3[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = src[i] * scale;
        return new TriangleMeshShape(dst, m.Indices);
    }

    static CompoundShape ScaleCompound(CompoundShape co, float scale)
    {
        CompoundChild[] src = co.Children;
        var dst = new CompoundChild[src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            CompoundChild child = src[i];
            dst[i] = new CompoundChild(
                Uniform(child.Shape, scale),
                new Pose(child.Local.Position * scale, child.Local.Orientation));
        }
        return new CompoundShape(dst);
    }
}
