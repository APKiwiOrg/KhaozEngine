using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Bounded, uncached snapshots of actual installed shapes. No descriptor or SIMD padding is
/// geometry authority. These managed copies are private to one authenticated owner read interval.</summary>
internal static class CapsuleFeatureGeometry
{
    internal const int MaximumLeaves = 64;
    internal const int MaximumFaces = 256;
    // Euler and degree >= 3 give V <= 2F-4, E <= 3F-6 and 2E <= 6F-12 for a strict convex solid.
    internal const int MaximumVertices = 508;
    internal const int MaximumFaceEntries = 1524;

    internal static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex shape, in RigidPose pose,
        out CapsuleFeaturePolyhedron[] leaves)
    {
        leaves = [];
        if (!ProveRigidPose(pose)) return CapsuleFeatureStatus.Unsupported;
        if (shape.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref simulation.Shapes.GetShape<Compound>(shape.Index);
            if (compound.Children.Length > MaximumLeaves) return CapsuleFeatureStatus.CapacityExceeded;
            if (compound.Children.Length == 0) return CapsuleFeatureStatus.Unsupported;
            var scratch = new CapsuleFeaturePolyhedron[compound.Children.Length];
            for (int i = 0; i < scratch.Length; i++)
            {
                ref var child = ref compound.Children[i];
                if (!ProveRigidPose(child.LocalPose)) return CapsuleFeatureStatus.Unsupported;
                // Exactly the pinned backend's composition, including its represented rounding.
                Compound.GetWorldPose(child.LocalPose, pose, out RigidPose worldPose);
                CapsuleFeatureStatus status = CaptureLeaf(simulation, child.ShapeIndex, worldPose,
                    child.LocalPose, i, out CapsuleFeaturePolyhedron? leaf);
                if (status != CapsuleFeatureStatus.Complete) return status;
                scratch[i] = leaf!;
            }
            leaves = scratch;
            return CapsuleFeatureStatus.Complete;
        }
        CapsuleFeatureStatus single = CaptureLeaf(simulation, shape, pose, new RigidPose(Vector3.Zero, Quaternion.Identity), 0,
            out CapsuleFeaturePolyhedron? only);
        if (single == CapsuleFeatureStatus.Complete) leaves = [only!];
        return single;
    }

    static CapsuleFeatureStatus CaptureLeaf(Simulation simulation, TypedIndex index, in RigidPose pose,
        in RigidPose localPose, int leafId, out CapsuleFeaturePolyhedron? leaf)
    {
        leaf = null;
        if (!ProveRigidPose(pose)) return CapsuleFeatureStatus.Unsupported;
        Vector3[] local;
        int[][] polygons;
        if (index.Type == default(Box).TypeId)
        {
            ref Box box = ref simulation.Shapes.GetShape<Box>(index.Index);
            if (!(box.HalfWidth > 0 && box.HalfHeight > 0 && box.HalfLength > 0))
                return CapsuleFeatureStatus.Unsupported;
            local = new Vector3[8];
            for (int i = 0; i < local.Length; i++)
                local[i] = new((i & 1) == 0 ? -box.HalfWidth : box.HalfWidth,
                    (i & 2) == 0 ? -box.HalfHeight : box.HalfHeight,
                    (i & 4) == 0 ? -box.HalfLength : box.HalfLength);
            polygons = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4],
                [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
        }
        else if (index.Type == default(ConvexHull).TypeId)
        {
            ref ConvexHull hull = ref simulation.Shapes.GetShape<ConvexHull>(index.Index);
            int count = hull.FaceToVertexIndicesStart.Length;
            if (count > MaximumFaces || hull.FaceVertexIndices.Length > MaximumFaceEntries)
                return CapsuleFeatureStatus.CapacityExceeded;
            if (count < 4) return CapsuleFeatureStatus.Unsupported;
            var vertices = new List<Vector3>();
            var ids = new Dictionary<int, int>();
            var positions = new HashSet<Vector3>();
            polygons = new int[count][];
            for (int face = 0; face < count; face++)
            {
                hull.GetVertexIndicesForFace(face, out var indices);
                if (indices.Length < 3) return CapsuleFeatureStatus.Unsupported;
                if (indices.Length > MaximumVertices) return CapsuleFeatureStatus.CapacityExceeded;
                polygons[face] = new int[indices.Length];
                for (int i = 0; i < indices.Length; i++)
                {
                    HullVertexIndex source = indices[i];
                    int key = (source.BundleIndex << 16) | source.InnerIndex;
                    if (!ids.TryGetValue(key, out int vertex))
                    {
                        if (vertices.Count == MaximumVertices) return CapsuleFeatureStatus.CapacityExceeded;
                        hull.GetPoint(source, out Vector3 point);
                        point = Canonical(point);
                        if (!positions.Add(point)) return CapsuleFeatureStatus.Ambiguous;
                        vertex = vertices.Count;
                        ids.Add(key, vertex);
                        vertices.Add(point);
                    }
                    // ConvexHullTriangleSource in 2.4 explicitly flips this inward source convention.
                    polygons[face][indices.Length - 1 - i] = vertex;
                }
            }
            local = vertices.ToArray();
        }
        else
        {
            // Mesh is Task 5. Curves, BigCompound and unknown/nonconvex child types also refuse.
            return CapsuleFeatureStatus.Unsupported;
        }

        Matrix3x3.CreateFromQuaternion(pose.Orientation, out Matrix3x3 matrix);
        var world = new FeaturePoint[local.Length];
        var seamPose = new Pose(pose.Position, pose.Orientation);
        for (int i = 0; i < local.Length; i++)
        {
            Vector3 point = local[i];
            if (!Within(point, 64)) return CapsuleFeatureStatus.Unsupported;
            GeometryVector enclosure = RepresentedGeometryTransforms.PosePoint(seamPose, point);
            if (!enclosure.IsResolved) return CapsuleFeatureStatus.Unsupported;
            // Retain the real represented-matrix affine expression. Binary32 evaluation is not
            // the finite geometry, and its rounding belongs only in bounded output publication.
            FeaturePoint p = FeaturePoint.Exact(point);
            FeatureNumber x = FeaturePoint.Dot(p, FeaturePoint.Exact(new(matrix.X.X, matrix.Y.X, matrix.Z.X)))
                .Add(FeatureNumber.Exact(pose.Position.X));
            FeatureNumber y = FeaturePoint.Dot(p, FeaturePoint.Exact(new(matrix.X.Y, matrix.Y.Y, matrix.Z.Y)))
                .Add(FeatureNumber.Exact(pose.Position.Y));
            FeatureNumber z = FeaturePoint.Dot(p, FeaturePoint.Exact(new(matrix.X.Z, matrix.Y.Z, matrix.Z.Z)))
                .Add(FeatureNumber.Exact(pose.Position.Z));
            world[i] = new(x, y, z);
            if (!world[i].IsResolved) return CapsuleFeatureStatus.Unresolved;
            if (!world[i].Within(2048)) return CapsuleFeatureStatus.Unsupported;
        }
        var candidate = new CapsuleFeaturePolyhedron(index, localPose, pose, leafId, local, world, polygons);
        CapsuleFeatureStatus validation = candidate.Validate();
        if (validation == CapsuleFeatureStatus.Complete) leaf = candidate;
        return validation;
    }

    static bool ProveRigidPose(in RigidPose pose)
    {
        if (!Within(pose.Position, 2048)) return false;
        var seamPose = new Pose(pose.Position, pose.Orientation);
        if (!RepresentedGeometryTransforms.PosePoint(seamPose, Vector3.Zero).IsResolved) return false;
        Quaternion q = pose.Orientation;
        FeatureNumber norm = FeatureNumber.Exact(q.X).Multiply(FeatureNumber.Exact(q.X))
            .Add(FeatureNumber.Exact(q.Y).Multiply(FeatureNumber.Exact(q.Y)))
            .Add(FeatureNumber.Exact(q.Z).Multiply(FeatureNumber.Exact(q.Z)))
            .Add(FeatureNumber.Exact(q.W).Multiply(FeatureNumber.Exact(q.W)));
        if (!norm.IsExact || norm.Value != 1) return false;
        Matrix3x3.CreateFromQuaternion(q, out Matrix3x3 matrix);
        FeaturePoint x = FeaturePoint.Exact(matrix.X), y = FeaturePoint.Exact(matrix.Y), z = FeaturePoint.Exact(matrix.Z);
        return EqualsExact(FeaturePoint.Dot(x, x), 1) && EqualsExact(FeaturePoint.Dot(y, y), 1) &&
            EqualsExact(FeaturePoint.Dot(z, z), 1) && EqualsExact(FeaturePoint.Dot(x, y), 0) &&
            EqualsExact(FeaturePoint.Dot(x, z), 0) && EqualsExact(FeaturePoint.Dot(y, z), 0) &&
            EqualsExact(FeaturePoint.Dot(FeaturePoint.Cross(x, y), z), 1);
    }

    static bool EqualsExact(FeatureNumber value, double expected) => value.IsExact && value.Value == expected;
    static Vector3 Canonical(Vector3 p) => new(p.X == 0 ? 0 : p.X, p.Y == 0 ? 0 : p.Y, p.Z == 0 ? 0 : p.Z);
    static bool Within(Vector3 p, float maximum) => float.IsFinite(p.X) && float.IsFinite(p.Y) &&
        float.IsFinite(p.Z) && MathF.Abs(p.X) <= maximum && MathF.Abs(p.Y) <= maximum && MathF.Abs(p.Z) <= maximum;
}
