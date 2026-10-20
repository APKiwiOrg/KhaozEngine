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
        List<SupportPolygon> output, bool exactZeroProducts = false)
    {
        if (!InstalledPoseOperator.TryCreateSupport(pose, out InstalledPoseOperator transform))
            return CapsuleFeatureStatus.Unsupported;
        ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(shape.Index);
        if (mesh.Scale != Vector3.One) return CapsuleFeatureStatus.Unsupported;
        if (!LocalBounds(transform, lower, upper, reach, out Vector3 min, out Vector3 max))
            return CapsuleFeatureStatus.Unsupported;
        return Capture(simulation, shape, pose, owner, transform, min, max, probeLow, probeHigh, reach,
            candidates, pool, output, exactZeroProducts);
    }

    /// <summary>Traverses the installed tree over the complete volume. Interval inverse rows evaluate the
    /// whole world AABB, enclosing all eight transformed corners even for a rotated mesh.</summary>
    internal static CapsuleFeatureStatus Polygons(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, in SupportFootprintBounds bounds, List<int> candidates, SupportPolygonPool pool,
        List<SupportPolygon> output, InstalledSupportMeshWorldCache? cache = null, long version = 0)
    {
        if (!InstalledPoseOperator.TryCreateSupport(pose, out InstalledPoseOperator transform))
            return CapsuleFeatureStatus.Unsupported;
        ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(shape.Index);
        if (mesh.Scale != Vector3.One) return CapsuleFeatureStatus.Unsupported;
        var world = new GeometryVector(GeometryInterval.Enclose(bounds.Minimum.X, bounds.Maximum.X),
            GeometryInterval.Enclose(bounds.Minimum.Y, bounds.Maximum.Y),
            GeometryInterval.Enclose(bounds.Minimum.Z, bounds.Maximum.Z));
        GeometryVector translation = transform.Translation.Bounds;
        GeometryVector offset = new(world.X.Subtract(translation.X), world.Y.Subtract(translation.Y),
            world.Z.Subtract(translation.Z));
        GeometryInterval x = GeometryVectorOperations.Dot(transform.InverseTransposeX.Bounds, offset);
        GeometryInterval y = GeometryVectorOperations.Dot(transform.InverseTransposeY.Bounds, offset);
        GeometryInterval z = GeometryVectorOperations.Dot(transform.InverseTransposeZ.Bounds, offset);
        if (!x.IsResolved || !y.IsResolved || !z.IsResolved) return CapsuleFeatureStatus.Unsupported;
        Vector3 min = new(MathF.BitDecrement((float)x.Lower), MathF.BitDecrement((float)y.Lower),
            MathF.BitDecrement((float)z.Lower));
        Vector3 max = new(MathF.BitIncrement((float)x.Upper), MathF.BitIncrement((float)y.Upper),
            MathF.BitIncrement((float)z.Upper));
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z))
            return CapsuleFeatureStatus.Unsupported;
        return Capture(simulation, shape, pose, owner, transform, min, max,
            [bounds.Minimum.X, bounds.Minimum.Y, bounds.Minimum.Z],
            [bounds.Maximum.X, bounds.Maximum.Y, bounds.Maximum.Z], GeometryInterval.Exact(0), candidates, pool, output, exactZeroProducts: true, cache, version);
    }

    static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, in InstalledPoseOperator transform, Vector3 min, Vector3 max,
        ReadOnlySpan<double> probeLow, ReadOnlySpan<double> probeHigh, GeometryInterval reach,
        List<int> candidates, SupportPolygonPool pool, List<SupportPolygon> output, bool exactZeroProducts,
        InstalledSupportMeshWorldCache? cache = null, long version = 0)
    {
        ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(shape.Index);
        candidates.Clear();
        var overlaps = new TriangleOverlaps(candidates);
        mesh.Tree.GetOverlaps(min, max, ref overlaps);
        SupportQueryDiagnostics.Record(SupportQueryCounter.MeshLeaves, candidates.Count);
        candidates.Sort();
        // A query larger than a static's entire cache cannot become warm. Keep it in reused scratch instead of
        // allocating and evicting every triangle on every call, or displacing the smaller neighbourhoods.
        if (SupportReferenceEvaluation.Active || candidates.Count > InstalledSupportMeshWorldCache.PerStaticTriangleCapacity ||
            cache is not null && !cache.Matches(owner, shape, version, pose))
            cache = null;
        Span<double> low = stackalloc double[3], high = stackalloc double[3];
        foreach (int id in candidates)
        {
            SupportPolygon? polygon;
            CapsuleFeatureStatus status;
            if (cache is not null && !SupportReferenceEvaluation.Active)
            {
                status = cache.Resolve(simulation, shape, pose, owner, version, id, transform, pool, out var form);
                if (status != CapsuleFeatureStatus.Complete) return status;
                if (form!.Face is not { } face ||
                    SupportGeometry.BoxGap(probeLow, probeHigh, face.Low, face.High) > reach.Upper) continue;
                polygon = pool.Rent();
                polygon.Bind(owner, face);
            }
            else
            {
                SupportQueryDiagnostics.Record(SupportQueryCounter.MeshTriangleCaptures);
                status = CaptureTriangle(simulation, shape, pose, owner, id, transform, pool, exactZeroProducts,
                    out polygon);
                if (status != CapsuleFeatureStatus.Complete) return status;
                if (polygon is null) continue;
                polygon.Low.CopyTo(low);
                polygon.High.CopyTo(high);
                if (SupportGeometry.BoxGap(probeLow, probeHigh, low, high) > reach.Upper)
                {
                    pool.ReturnLast();
                    continue;
                }
            }
            output.Add(polygon);
        }
        return CapsuleFeatureStatus.Complete;
    }

    // The same operator, transform order, winding and normal validation as uncached mesh materialization.
    internal static CapsuleFeatureStatus CaptureTriangle(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, int id, in InstalledPoseOperator transform, SupportPolygonPool pool,
        bool exactZeroProducts, out SupportPolygon? polygon)
    {
        polygon = null;
        ref Mesh mesh = ref simulation.Shapes.GetShape<Mesh>(shape.Index);
        ref Triangle triangle = ref mesh.Triangles[id];
        Vector3 a = triangle.A, b = triangle.B, c = triangle.C;
        if (CapsuleFeatureGeometry.TransformVertex(a, transform, out FeaturePoint worldA, exactZeroProducts) !=
                CapsuleFeatureStatus.Complete ||
            CapsuleFeatureGeometry.TransformVertex(b, transform, out FeaturePoint worldB, exactZeroProducts) !=
                CapsuleFeatureStatus.Complete ||
            CapsuleFeatureGeometry.TransformVertex(c, transform, out FeaturePoint worldC, exactZeroProducts) !=
                CapsuleFeatureStatus.Complete)
            return CapsuleFeatureStatus.Unsupported;
        if (Degenerate(a, b, c)) return CapsuleFeatureStatus.Complete;
        // Edges and normal come from the common installed operator. Bepu 2.4 Triangle.RayTest defines the front
        // as Cross(ac, ab), as CapsuleFeatureMesh does. Wound A, C, B, the polygon's inside convention carries it.
        FeaturePoint ac = transform.Edge(a, c, exactZeroProducts), ab = transform.Edge(a, b, exactZeroProducts);
        FeaturePoint normal = FeaturePoint.Cross(ac, ab, exactZeroProducts);
        polygon = pool.Rent();
        polygon.Begin(owner, id, 3);
        polygon.BeginSource(transform);
        polygon.SetSource(0, a);
        polygon.SetSource(1, c);
        polygon.SetSource(2, b);
        polygon.Set(0, worldA, ac);
        polygon.Set(1, worldC, transform.Edge(c, b, exactZeroProducts));
        polygon.Set(2, worldB, transform.Edge(b, a, exactZeroProducts));
        polygon.Finish(normal);
        // A sliver keeps its bounded normal. A triangle whose normal cannot be bounded away from zero has no
        // certifiable surface and no area to stand on, and its edges belong to its neighbours, so like an exactly
        // degenerate one it is not a member.
        if (!polygon.NormalLength.IsResolved || !(polygon.NormalLength.Lower > 0))
        {
            pool.ReturnLast();
            polygon = null;
            return CapsuleFeatureStatus.Complete;
        }
        return CapsuleFeatureStatus.Complete;
    }

    // Local coordinates are M^-1 (world - translation). The rows of M^-1 are the columns of its inverse transpose,
    // and interval evaluation encloses the preimage for the represented point map, without assuming transpose is inverse.
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
