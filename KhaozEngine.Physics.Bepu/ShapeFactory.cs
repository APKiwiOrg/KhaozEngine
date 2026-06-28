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
            CylinderShape cy  => sim.Shapes.Add(new Cylinder(cy.Radius, cy.Length)),
            ConvexHullShape ch => AddConvexHull(sim, pool, ch),
            TriangleMeshShape tm => AddTriangleMesh(sim, pool, tm),
            CompoundShape co  => AddCompound(sim, pool, co),
            _ => throw new NotSupportedException($"PhysicsShape type '{shape.GetType().Name}' is not supported by the Bepu backend.")
        };
    }

    private static TypedIndex AddConvexHull(BepuSim sim, BufferPool pool, ConvexHullShape ch)
    {
        var span = ch.Points.AsSpan();
        ConvexHullHelper.CreateShape(span, pool, out _, out var hull);
        return sim.Shapes.Add(hull);
    }

    private static TypedIndex AddTriangleMesh(BepuSim sim, BufferPool pool, TriangleMeshShape tm)
    {
        int triCount = tm.Indices.Length / 3;
        pool.Take<Triangle>(triCount, out var triangles);
        try
        {
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
        finally
        {
            pool.Return(ref triangles);
        }
    }

    private static TypedIndex AddCompound(BepuSim sim, BufferPool pool, CompoundShape co)
    {
        var builder = new CompoundBuilder(pool, sim.Shapes, co.Children.Length);
        try
        {
            foreach (var child in co.Children)
            {
                var childIndex = Add(sim, pool, child.Shape);
                var localPose = new RigidPose(child.Local.Position, child.Local.Orientation);
                builder.AddForKinematic(childIndex, in localPose, 1f);
            }
            builder.BuildKinematicCompound(out var compoundChildren);
            var compound = new Compound(compoundChildren);
            return sim.Shapes.Add(compound);
        }
        finally
        {
            builder.Dispose();
        }
    }
}
