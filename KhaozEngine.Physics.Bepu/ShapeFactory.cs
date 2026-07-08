using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using KhaozEngine.Physics;

using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Converts seam <see cref="PhysicsShape"/> instances to Bepu shape indices.
/// Keeps BepuPhysics types confined to this package.</summary>
internal static class ShapeFactory
{
    /// <summary>Adds a seam shape to the Bepu simulation and returns its <see cref="TypedIndex"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static TypedIndex Add(BepuSim sim, BufferPool pool, PhysicsShape shape)
    {
        return shape switch
        {
            SphereShape s     => sim.Shapes.Add(new Sphere(s.Radius)),
            CapsuleShape c    => sim.Shapes.Add(new Capsule(c.Radius, c.Length)),
            // BoxShape HalfExtents -> Bepu Box full width/height/depth
            BoxShape b        => sim.Shapes.Add(new Box(b.HalfExtents.X * 2f, b.HalfExtents.Y * 2f, b.HalfExtents.Z * 2f)),
            CylinderShape cy  => AddBaseAlignedCylinder(sim, pool, cy),
            ConvexHullShape ch => AddConvexHull(sim, pool, ch),
            TriangleMeshShape tm => AddTriangleMesh(sim, pool, tm),
            CompoundShape co  => AddCompound(sim, pool, co),
            _ => throw new NotSupportedException($"PhysicsShape type '{shape.GetType().Name}' is not supported by the Bepu backend.")
        };
    }

    /// <summary>Adds a seam shape for a DYNAMIC body and returns its <see cref="TypedIndex"/> together with the
    /// <see cref="BodyInertia"/> for the given <paramref name="mass"/>. Convex primitives derive their inertia
    /// analytically; base-aligned cylinder/hull shapes and multi-child compounds are built as DYNAMIC compounds
    /// (<c>CompoundBuilder.BuildDynamicCompound</c>) so the same base-alignment wrapping used for statics
    /// carries a correct inertia tensor. A triangle mesh cannot be a dynamic body (it has no volume) and throws.</summary>
    internal static TypedIndex AddDynamic(BepuSim sim, BufferPool pool, PhysicsShape shape, float mass, out BodyInertia inertia)
    {
        switch (shape)
        {
            case SphereShape s:
            {
                var sphere = new Sphere(s.Radius);
                inertia = sphere.ComputeInertia(mass);
                return sim.Shapes.Add(sphere);
            }
            case CapsuleShape c:
            {
                var capsule = new Capsule(c.Radius, c.Length);
                inertia = capsule.ComputeInertia(mass);
                return sim.Shapes.Add(capsule);
            }
            case BoxShape b:
            {
                var box = new Box(b.HalfExtents.X * 2f, b.HalfExtents.Y * 2f, b.HalfExtents.Z * 2f);
                inertia = box.ComputeInertia(mass);
                return sim.Shapes.Add(box);
            }
            case CylinderShape cy:
                return AddBaseAlignedCylinderDynamic(sim, pool, cy, mass, out inertia);
            case ConvexHullShape ch:
                return AddConvexHullDynamic(sim, pool, ch, mass, out inertia);
            case CompoundShape co:
                return AddCompoundDynamic(sim, pool, co, mass, out inertia);
            case TriangleMeshShape:
                throw new NotSupportedException(
                    "A TriangleMeshShape cannot be a dynamic body (a mesh has no closed volume, so it has no " +
                    "well-defined mass/inertia). Use a convex primitive, a convex hull, or a compound of convex leaves.");
            default:
                throw new NotSupportedException($"PhysicsShape type '{shape.GetType().Name}' is not supported by the Bepu backend.");
        }
    }

    // Dynamic mirror of AddBaseAlignedCylinder: a single-child compound lifted +Length/2 so the cylinder spans
    // base -> base+Length from the body pose, and BuildDynamicCompound emits the matching inertia.
    private static TypedIndex AddBaseAlignedCylinderDynamic(BepuSim sim, BufferPool pool, CylinderShape cy, float mass, out BodyInertia inertia)
    {
        var builder = new CompoundBuilder(pool, sim.Shapes, 1);
        try
        {
            var cylinder = new Cylinder(cy.Radius, cy.Length);
            var localPose = new RigidPose(new Vector3(0f, cy.Length * 0.5f, 0f), Quaternion.Identity);
            // The generic Add<TShape>(shape, pose, mass) registers the shape AND derives its inertia
            // analytically, then BuildDynamicCompound accumulates the child inertia at the shifted pose.
            builder.Add(cylinder, in localPose, mass);
            builder.BuildDynamicCompound(out var children, out inertia);
            return sim.Shapes.Add(new Compound(children));
        }
        finally
        {
            builder.Dispose();
        }
    }

    // Dynamic mirror of AddConvexHull: the hull recenters on its centre of mass, so the leaf is placed at
    // +centre to keep the mesh-local origin at the body pose; BuildDynamicCompound emits the inertia.
    private static TypedIndex AddConvexHullDynamic(BepuSim sim, BufferPool pool, ConvexHullShape ch, float mass, out BodyInertia inertia)
    {
        ConvexHullHelper.CreateShape(ch.Points.AsSpan(), pool, out var centre, out var hull);
        var builder = new CompoundBuilder(pool, sim.Shapes, 1);
        try
        {
            var hullPose = new RigidPose(centre, Quaternion.Identity);
            builder.Add(hull, in hullPose, mass);
            builder.BuildDynamicCompound(out var children, out inertia);
            return sim.Shapes.Add(new Compound(children));
        }
        finally
        {
            builder.Dispose();
        }
    }

    // Dynamic mirror of AddCompound: flatten children into one level of convex leaves (never a compound-of-
    // compounds, which breaks the sweep bounds) then BuildDynamicCompound. Mass is split evenly across the
    // flattened leaves so the total equals the requested body mass (a coarse but stable distribution; a
    // proxy's leaves are of comparable scale, so an even split is a reasonable inertia approximation).
    private static TypedIndex AddCompoundDynamic(BepuSim sim, BufferPool pool, CompoundShape co, float mass, out BodyInertia inertia)
    {
        var builder = new CompoundBuilder(pool, sim.Shapes, co.Children.Length);
        try
        {
            int leafCount = CountFlattenedLeaves(co);
            float perLeafMass = leafCount > 0 ? mass / leafCount : mass;
            foreach (var child in co.Children)
                AddFlattenedChildDynamic(sim, pool, ref builder, child.Shape, child.Local, perLeafMass);
            builder.BuildDynamicCompound(out var compoundChildren, out inertia);
            return sim.Shapes.Add(new Compound(compoundChildren));
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static int CountFlattenedLeaves(CompoundShape co)
    {
        int n = 0;
        foreach (var child in co.Children)
            n += child.Shape is CompoundShape nested ? CountFlattenedLeaves(nested) : 1;
        return n;
    }

    // Bepu's Cylinder is CENTRED on the body pose, but the rest of the seam's shapes (convex hulls and
    // triangle meshes baked by PropCollisionBake) carry their Y range in their geometry with the base at
    // y=0, and the runtime places every prop static at the prop BASE (the terrain height at scatter time;
    // see ChunkStatics.AddAll). A bare Cylinder placed at the base would sit half-buried (its centre at
    // the base), blocking only the top half at the wrong height. Wrap it in a single-child compound whose
    // child is lifted +Length/2 in Y, so a CylinderShape behaves base-aligned like a hull/mesh: it spans
    // base -> base+Length when the static pose is at the base. The trunk-cylinder bake (Fix B) depends on
    // this so a tree blocks at trunk-radius height instead of half-buried.
    private static TypedIndex AddBaseAlignedCylinder(BepuSim sim, BufferPool pool, CylinderShape cy)
    {
        var builder = new CompoundBuilder(pool, sim.Shapes, 1);
        try
        {
            var cylIndex = sim.Shapes.Add(new Cylinder(cy.Radius, cy.Length));
            var localPose = new RigidPose(new Vector3(0f, cy.Length * 0.5f, 0f), Quaternion.Identity);
            builder.AddForKinematic(cylIndex, in localPose, 1f);
            builder.BuildKinematicCompound(out var children);
            return sim.Shapes.Add(new Compound(children));
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static TypedIndex AddConvexHull(BepuSim sim, BufferPool pool, ConvexHullShape ch)
    {
        // Bepu RECENTERS a ConvexHull on its computed centre of mass (the second out param). The prop is
        // placed at its mesh-local origin (base, XZ-centred on the placement), so a bare hull sits about its
        // centre-of-mass height BELOW the visual mesh and the character sinks into rocks. Wrap it in a
        // single-child compound offset by +centre so the hull's mesh-local frame lines up with the placement
        // (mirrors AddBaseAlignedCylinder; without this a 1.8 m rock collider is ~0.9 m too low).
        var span = ch.Points.AsSpan();
        ConvexHullHelper.CreateShape(span, pool, out var centre, out var hull);
        var builder = new CompoundBuilder(pool, sim.Shapes, 1);
        try
        {
            var hullIndex = sim.Shapes.Add(hull);
            builder.AddForKinematic(hullIndex, new RigidPose(centre, Quaternion.Identity), 1f);
            builder.BuildKinematicCompound(out var children);
            return sim.Shapes.Add(new Compound(children));
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static TypedIndex AddTriangleMesh(BepuSim sim, BufferPool pool, TriangleMeshShape tm)
    {
        // Mesh takes OWNERSHIP of the triangle buffer (its Triangles field; Mesh.Dispose(pool) returns it).
        // Do NOT return the buffer here - the mesh owns it and RecursivelyRemoveAndDispose will return it
        // when the shape is later removed. Returning it here would cause a double-return and use-after-free.
        int triCount = tm.Indices.Length / 3;
        pool.Take<Triangle>(triCount, out var triangles);
        for (int i = 0; i < triCount; i++)
        {
            triangles[i] = new Triangle(
                tm.Vertices[tm.Indices[i * 3]],
                tm.Vertices[tm.Indices[i * 3 + 1]],
                tm.Vertices[tm.Indices[i * 3 + 2]]);
        }
        var mesh = new Mesh(triangles.Slice(triCount), Vector3.One, pool);
        return sim.Shapes.Add(mesh);
    }

    private static TypedIndex AddCompound(BepuSim sim, BufferPool pool, CompoundShape co)
    {
        var builder = new CompoundBuilder(pool, sim.Shapes, co.Children.Length);
        try
        {
            foreach (var child in co.Children)
                AddFlattenedChild(sim, pool, ref builder, child.Shape, child.Local);
            builder.BuildKinematicCompound(out var compoundChildren);
            var compound = new Compound(compoundChildren);
            return sim.Shapes.Add(compound);
        }
        finally
        {
            builder.Dispose();
        }
    }

    // A Bepu compound's children MUST be convex leaves. Adding a child via the top-level Add() would wrap a
    // ConvexHull or a base-aligned Cylinder in its OWN single-child compound (the centroid / base-offset wrappers
    // above), giving a compound-of-compounds; Bepu's broadphase sweep then calls ComputeBounds on that
    // nonconvex child and throws ("This should only ever be called on convexes"). So flatten here: add each
    // convex shape as a DIRECT leaf and fold its internal recentering (ConvexHull centre-of-mass, Cylinder base
    // lift) into the child's local pose, so a building proxy (a compound of convex hulls baked by
    // PropCollisionBake.BakeProxy) is swept correctly. A nested CompoundShape is recursed into the SAME builder
    // (pose-composed), so the final compound is always one flat level of convex leaves.
    private static void AddFlattenedChild(BepuSim sim, BufferPool pool, ref CompoundBuilder builder, PhysicsShape shape, Pose local)
    {
        switch (shape)
        {
            case SphereShape s:
            {
                var p = new RigidPose(local.Position, local.Orientation);
                builder.AddForKinematic(sim.Shapes.Add(new Sphere(s.Radius)), in p, 1f);
                break;
            }
            case CapsuleShape c:
            {
                var p = new RigidPose(local.Position, local.Orientation);
                builder.AddForKinematic(sim.Shapes.Add(new Capsule(c.Radius, c.Length)), in p, 1f);
                break;
            }
            case BoxShape b:
            {
                var p = new RigidPose(local.Position, local.Orientation);
                builder.AddForKinematic(
                    sim.Shapes.Add(new Box(b.HalfExtents.X * 2f, b.HalfExtents.Y * 2f, b.HalfExtents.Z * 2f)), in p, 1f);
                break;
            }
            case CylinderShape cy:
            {
                // Base-aligned: lift +Length/2 along the child's local Y (mirrors AddBaseAlignedCylinder).
                Vector3 off = Vector3.Transform(new Vector3(0f, cy.Length * 0.5f, 0f), local.Orientation);
                var p = new RigidPose(local.Position + off, local.Orientation);
                builder.AddForKinematic(sim.Shapes.Add(new Cylinder(cy.Radius, cy.Length)), in p, 1f);
                break;
            }
            case ConvexHullShape ch:
            {
                // Bepu recenters the hull on its centre of mass; place the leaf at +centre (in the child frame)
                // so its mesh-local origin lands at child.Local (mirrors AddConvexHull, but as a direct leaf).
                ConvexHullHelper.CreateShape(ch.Points.AsSpan(), pool, out Vector3 centre, out var hull);
                Vector3 off = Vector3.Transform(centre, local.Orientation);
                var p = new RigidPose(local.Position + off, local.Orientation);
                builder.AddForKinematic(sim.Shapes.Add(hull), in p, 1f);
                break;
            }
            case CompoundShape nested:
                foreach (var c in nested.Children)
                {
                    Quaternion rot = Quaternion.Concatenate(c.Local.Orientation, local.Orientation);
                    Vector3 pos = local.Position + Vector3.Transform(c.Local.Position, local.Orientation);
                    AddFlattenedChild(sim, pool, ref builder, c.Shape, new Pose(pos, rot));
                }
                break;
            case TriangleMeshShape:
                throw new NotSupportedException(
                    "A TriangleMeshShape inside a CompoundShape is not supported: a Bepu compound child must be " +
                    "convex, and a mesh child breaks the broadphase sweep bounds. Use convex pieces in a proxy compound.");
            default:
                throw new NotSupportedException(
                    $"PhysicsShape type '{shape.GetType().Name}' is not supported inside a CompoundShape.");
        }
    }

    // Dynamic mirror of AddFlattenedChild: identical geometry/flattening rules, but each convex leaf is added
    // with builder.Add(shape, pose, MASS) instead of AddForKinematic(shape, pose, weight) so BuildDynamicCompound
    // computes a real inertia tensor. Nested compounds recurse into the SAME builder (pose-composed), staying one
    // flat level of convex leaves.
    private static void AddFlattenedChildDynamic(BepuSim sim, BufferPool pool, ref CompoundBuilder builder, PhysicsShape shape, Pose local, float mass)
    {
        switch (shape)
        {
            case SphereShape s:
            {
                var p = new RigidPose(local.Position, local.Orientation);
                builder.Add(new Sphere(s.Radius), in p, mass);
                break;
            }
            case CapsuleShape c:
            {
                var p = new RigidPose(local.Position, local.Orientation);
                builder.Add(new Capsule(c.Radius, c.Length), in p, mass);
                break;
            }
            case BoxShape b:
            {
                var p = new RigidPose(local.Position, local.Orientation);
                builder.Add(new Box(b.HalfExtents.X * 2f, b.HalfExtents.Y * 2f, b.HalfExtents.Z * 2f), in p, mass);
                break;
            }
            case CylinderShape cy:
            {
                Vector3 off = Vector3.Transform(new Vector3(0f, cy.Length * 0.5f, 0f), local.Orientation);
                var p = new RigidPose(local.Position + off, local.Orientation);
                builder.Add(new Cylinder(cy.Radius, cy.Length), in p, mass);
                break;
            }
            case ConvexHullShape ch:
            {
                ConvexHullHelper.CreateShape(ch.Points.AsSpan(), pool, out Vector3 centre, out var hull);
                Vector3 off = Vector3.Transform(centre, local.Orientation);
                var p = new RigidPose(local.Position + off, local.Orientation);
                builder.Add(hull, in p, mass);
                break;
            }
            case CompoundShape nested:
                foreach (var c in nested.Children)
                {
                    Quaternion rot = Quaternion.Concatenate(c.Local.Orientation, local.Orientation);
                    Vector3 pos = local.Position + Vector3.Transform(c.Local.Position, local.Orientation);
                    AddFlattenedChildDynamic(sim, pool, ref builder, c.Shape, new Pose(pos, rot), mass);
                }
                break;
            case TriangleMeshShape:
                throw new NotSupportedException(
                    "A TriangleMeshShape inside a CompoundShape is not supported: a Bepu compound child must be " +
                    "convex, and a mesh child breaks the broadphase sweep bounds. Use convex pieces in a proxy compound.");
            default:
                throw new NotSupportedException(
                    $"PhysicsShape type '{shape.GetType().Name}' is not supported inside a CompoundShape.");
        }
    }
}
