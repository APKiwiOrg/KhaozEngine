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
/// duplicate triangles and inconsistent winding certify triangle by triangle. Coplanar triangles are not merged.</summary>
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
    /// mesh. An unproved pose, a scaled mesh or a candidate triangle outside that domain is Unsupported.</summary>
    internal static CapsuleFeatureStatus Polygons(Simulation simulation, TypedIndex shape, in RigidPose pose,
        StaticHandle owner, FeaturePoint lower, FeaturePoint upper, GeometryInterval reach, List<int> candidates,
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
            // Edges and normal come from the common installed operator. Bepu 2.4 Triangle.RayTest defines the front
            // as Cross(ac, ab), as CapsuleFeatureMesh does. Wound A, C, B, the polygon's inside convention carries it.
            FeaturePoint ac = transform.Edge(a, c), ab = transform.Edge(a, b);
            FeaturePoint normal = FeaturePoint.Cross(ac, ab);
            // A triangle with no certain area has no front.
            if (FeaturePoint.Dot(normal, normal).Sign != GeometrySign.Positive) return CapsuleFeatureStatus.Unsupported;
            output.Add(new SupportPolygon(owner, id, [worldA, worldC, worldB],
                [ac, transform.Edge(c, b), transform.Edge(b, a)], normal));
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
        FeaturePoint[] rows = [transform.InverseTransposeX, transform.InverseTransposeY, transform.InverseTransposeZ];
        for (int axis = 0; axis < 3; axis++)
        {
            GeometryInterval first = FeaturePoint.Dot(rows[axis], FeaturePoint.Subtract(lower, transform.Translation)).Bounds;
            GeometryInterval second = FeaturePoint.Dot(rows[axis], FeaturePoint.Subtract(upper, transform.Translation)).Bounds;
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

    struct TriangleOverlaps(List<int> found) : IBreakableForEach<int>
    {
        public bool LoopBody(int triangle)
        {
            found.Add(triangle);
            return true;
        }
    }
}
