using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The triangles of one installed mesh static near the probe, read through the mesh's own bounding tree.
/// Each triangle is its own polygon, with element id equal to its triangle index. Membership, front facing and
/// joins are decided per triangle by geometry, so the mesh's topology is never validated: non-manifold edges,
/// duplicate triangles and inconsistent winding certify triangle by triangle. Coplanar triangles are not
/// merged.</summary>
internal static class SupportNeighborhoodMesh
{
    // The local query box is the interval preimage of the probe segment under the installed operator, inflated by
    // the reach and this margin. The operator departs from a rigid map by about 1e-6 relative and the reach is at
    // most about 2 m, so the margin is far beyond the stretch a rigid inflation can miss. Bepu's leaf bounds
    // contain their triangles' float vertices.
    const double Margin = 0.01;

    internal static bool IsMesh(TypedIndex shape) => shape.Type == default(Mesh).TypeId;

    /// <summary>Every triangle whose tree bounds may reach the probe segment within <paramref name="reach"/>, in
    /// triangle order. Only those triangles are read, each with the vertex bounds and finite checks of a captured
    /// mesh. An unproved pose, a scaled mesh or a candidate triangle outside that domain is Unsupported. A triangle
    /// whose world bounds are beyond the reach of the probe bounds <paramref name="probeLow"/> to
    /// <paramref name="probeHigh"/> is skipped before its area is examined. An exactly degenerate triangle, or one
    /// whose normal cannot be bounded away from zero, has no certifiable surface and is skipped too.</summary>
    internal static CapsuleFeatureStatus Polygons(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, FeaturePoint lower, FeaturePoint upper, ReadOnlySpan<double> probeLow,
        ReadOnlySpan<double> probeHigh, GeometryInterval reach, List<int> candidates, SupportPolygonPool pool,
        List<SupportPolygon> output)
    {
        if (!InstalledPoseOperator.TryCreate(pose, out InstalledPoseOperator transform))
            return CapsuleFeatureStatus.Unsupported;
        ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(shape.Index);
        if (mesh.Scale != Vector3.One) return CapsuleFeatureStatus.Unsupported;
        if (!LocalBounds(transform, lower, upper, reach, out Vector3 min, out Vector3 max))
            return CapsuleFeatureStatus.Unsupported;
        candidates.Clear();
        var overlaps = new TriangleOverlaps(candidates);
        mesh.Tree.GetOverlaps(min, max, ref overlaps);
        candidates.Sort();
        var seamPose = new Pose(pose.Position, pose.Orientation);
        Span<double> low = stackalloc double[3], high = stackalloc double[3];
        foreach (int id in candidates)
        {
            ref Triangle triangle = ref mesh.Triangles[id];
            Vector3 a = triangle.A, b = triangle.B, c = triangle.C;
            if (CapsuleFeatureGeometry.TransformVertex(a, seamPose, transform, out FeaturePoint worldA) !=
                    CapsuleFeatureStatus.Complete ||
                CapsuleFeatureGeometry.TransformVertex(b, seamPose, transform, out FeaturePoint worldB) !=
                    CapsuleFeatureStatus.Complete ||
                CapsuleFeatureGeometry.TransformVertex(c, seamPose, transform, out FeaturePoint worldC) !=
                    CapsuleFeatureStatus.Complete)
                return CapsuleFeatureStatus.Unsupported;
            for (int axis = 0; axis < 3; axis++)
            {
                GeometryInterval x = SupportGeometry.Component(worldA, axis);
                GeometryInterval y = SupportGeometry.Component(worldB, axis);
                GeometryInterval z = SupportGeometry.Component(worldC, axis);
                low[axis] = Math.Min(x.Lower, Math.Min(y.Lower, z.Lower));
                high[axis] = Math.Max(x.Upper, Math.Max(y.Upper, z.Upper));
            }
            if (SupportGeometry.BoxGap(probeLow, probeHigh, low, high) > reach.Upper || Degenerate(a, b, c)) continue;
            // Edges and normal come from the common installed operator. Bepu 2.4 Triangle.RayTest defines the front
            // as Cross(ac, ab), as CapsuleFeatureMesh does. Wound A, C, B, the polygon's inside convention carries it.
            FeaturePoint ac = transform.Edge(a, c), ab = transform.Edge(a, b);
            FeaturePoint normal = FeaturePoint.Cross(ac, ab);
            SupportPolygon polygon = pool.Rent();
            polygon.Begin(owner, id, 3);
            polygon.Set(0, worldA, ac);
            polygon.Set(1, worldC, transform.Edge(c, b));
            polygon.Set(2, worldB, transform.Edge(b, a));
            polygon.Finish(normal);
            // A sliver keeps its bounded normal. A triangle whose normal cannot be bounded away from zero has no
            // certifiable surface and no area to stand on, and its edges belong to its neighbours, so like an exactly
            // degenerate one it is not a member.
            if (!polygon.NormalLength.IsResolved || !(polygon.NormalLength.Lower > 0))
            {
                pool.ReturnLast();
                continue;
            }
            output.Add(polygon);
        }
        return CapsuleFeatureStatus.Complete;
    }

    // Local coordinates are M^-1 (world - translation). The rows of M^-1 are the columns of its inverse transpose,
    // and interval evaluation encloses the preimage for every operator the certified coefficients contain.
    static bool LocalBounds(in InstalledPoseOperator transform, FeaturePoint lower, FeaturePoint upper,
        GeometryInterval reach, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        GeometryInterval inflation = reach.Add(GeometryInterval.Exact(Margin));
        if (!inflation.IsResolved) return false;
        Span<float> low = stackalloc float[3], high = stackalloc float[3];
        for (int axis = 0; axis < 3; axis++)
        {
            FeaturePoint row = axis == 0 ? transform.InverseTransposeX
                : axis == 1 ? transform.InverseTransposeY : transform.InverseTransposeZ;
            GeometryInterval first = FeaturePoint.Dot(row, FeaturePoint.Subtract(lower, transform.Translation)).Bounds;
            GeometryInterval second = FeaturePoint.Dot(row, FeaturePoint.Subtract(upper, transform.Translation)).Bounds;
            GeometryInterval below = GeometryInterval.Exact(Math.Min(first.Lower, second.Lower)).Subtract(inflation);
            GeometryInterval above = GeometryInterval.Exact(Math.Max(first.Upper, second.Upper)).Add(inflation);
            if (!first.IsResolved || !second.IsResolved || !below.IsResolved || !above.IsResolved) return false;
            low[axis] = MathF.BitDecrement((float)below.Lower);
            high[axis] = MathF.BitIncrement((float)above.Upper);
            if (!float.IsFinite(low[axis]) || !float.IsFinite(high[axis])) return false;
        }
        min = new Vector3(low[0], low[1], low[2]);
        max = new Vector3(high[0], high[1], high[2]);
        return true;
    }

    // The local cross product is zero exactly when the triangle's orientation is zero in all three coordinate
    // projections, decided by the exact predicate on the float vertices. An undecided projection is not degenerate.
    // The installed operator is invertible, so the world triangle is degenerate exactly when the local one is.
    static bool Degenerate(Vector3 a, Vector3 b, Vector3 c) =>
        BoundedGeometryArithmetic.Orient2D(a.X, a.Y, b.X, b.Y, c.X, c.Y) == GeometrySign.Zero &&
        BoundedGeometryArithmetic.Orient2D(a.Y, a.Z, b.Y, b.Z, c.Y, c.Z) == GeometrySign.Zero &&
        BoundedGeometryArithmetic.Orient2D(a.Z, a.X, b.Z, b.X, c.Z, c.X) == GeometrySign.Zero;

    struct TriangleOverlaps(List<int> found) : IBreakableForEach<int>
    {
        public bool LoopBody(int triangle)
        {
            found.Add(triangle);
            return true;
        }
    }
}
