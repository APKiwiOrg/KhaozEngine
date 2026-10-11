using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
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
        if (!ProveInstalledPose(pose)) return CapsuleFeatureStatus.Unsupported;
        if (shape.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref simulation.Shapes.GetShape<Compound>(shape.Index);
            if (compound.Children.Length > MaximumLeaves) return CapsuleFeatureStatus.CapacityExceeded;
            if (compound.Children.Length == 0) return CapsuleFeatureStatus.Unsupported;
            var scratch = new CapsuleFeaturePolyhedron[compound.Children.Length];
            for (int i = 0; i < scratch.Length; i++)
            {
                ref var child = ref compound.Children[i];
                if (!ProveInstalledPose(child.LocalPose)) return CapsuleFeatureStatus.Unsupported;
                // Exactly the pinned backend's composition, including its represented rounding.
                Compound.GetWorldPose(child.LocalPose, pose, out RigidPose worldPose);
                if (!InstalledPoseOperations.ProveComposition(child.LocalPose, pose, worldPose))
                    return CapsuleFeatureStatus.Unsupported;
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

    internal static CapsuleFeatureStatus CaptureLeaf(Simulation simulation, TypedIndex index, in RigidPose pose,
        in RigidPose localPose, int leafId, out CapsuleFeaturePolyhedron? leaf) =>
        CaptureLeaf(simulation, index, pose, localPose, leafId, null, out leaf);

    /// <summary>Captures and admits one box or hull leaf. With <paramref name="scratch"/> every array, working
    /// collection and the leaf itself are reused from it, and the admitted leaf is valid until its next reset.</summary>
    internal static CapsuleFeatureStatus CaptureLeaf(Simulation simulation, TypedIndex index, in RigidPose pose,
        in RigidPose localPose, int leafId, CapsuleFeatureCaptureScratch? scratch, out CapsuleFeaturePolyhedron? leaf)
    {
        leaf = null;
        // The operator proves the pose here and maps the vertices below. It is a function of the pose alone.
        if (!InstalledPoseOperator.TryCreate(pose, out InstalledPoseOperator transform))
            return CapsuleFeatureStatus.Unsupported;
        Vector3[] local;
        int[][] polygons;
        if (index.Type == default(Box).TypeId)
        {
            ref Box box = ref simulation.Shapes.GetShape<Box>(index.Index);
            if (!(box.HalfWidth > 0 && box.HalfHeight > 0 && box.HalfLength > 0))
                return CapsuleFeatureStatus.Unsupported;
            local = scratch?.Locals(8) ?? new Vector3[8];
            for (int i = 0; i < local.Length; i++)
                local[i] = new((i & 1) == 0 ? -box.HalfWidth : box.HalfWidth,
                    (i & 2) == 0 ? -box.HalfHeight : box.HalfHeight,
                    (i & 4) == 0 ? -box.HalfLength : box.HalfLength);
            // Admission and the consumers only read the faces, so a scratch capture shares one copy.
            polygons = scratch is null ? [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4],
                [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]] : BoxFaces;
        }
        else if (index.Type == default(ConvexHull).TypeId)
        {
            ref ConvexHull hull = ref simulation.Shapes.GetShape<ConvexHull>(index.Index);
            int count = hull.FaceToVertexIndicesStart.Length;
            if (count > MaximumFaces || hull.FaceVertexIndices.Length > MaximumFaceEntries)
                return CapsuleFeatureStatus.CapacityExceeded;
            if (count < 4) return CapsuleFeatureStatus.Unsupported;
            List<Vector3> vertices = scratch?.HullVertices ?? [];
            Dictionary<int, int> ids = scratch?.HullIds ?? [];
            HashSet<Vector3> positions = scratch?.Positions ?? [];
            vertices.Clear();
            ids.Clear();
            positions.Clear();
            polygons = scratch?.Faces(count) ?? new int[count][];
            for (int face = 0; face < count; face++)
            {
                hull.GetVertexIndicesForFace(face, out var indices);
                if (indices.Length < 3) return CapsuleFeatureStatus.Unsupported;
                if (indices.Length > MaximumVertices) return CapsuleFeatureStatus.CapacityExceeded;
                polygons[face] = scratch?.Indices(indices.Length) ?? new int[indices.Length];
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
                    // CreateShape derives outward planes from this stored order. Preserve it for
                    // the same cross-product convention used by the strict face validator.
                    polygons[face][i] = vertex;
                }
            }
            local = scratch?.Locals(vertices.Count) ?? new Vector3[vertices.Count];
            vertices.CopyTo(local);
        }
        else
        {
            // Mesh has its own finite one-sided owner. Nonconvex compound children still refuse.
            return CapsuleFeatureStatus.Unsupported;
        }

        FeaturePoint[] world = scratch?.Points(local.Length) ?? new FeaturePoint[local.Length];
        CapsuleFeatureStatus transformed = TransformVertices(local, pose, transform, world);
        if (transformed != CapsuleFeatureStatus.Complete) return transformed;
        CapsuleFeaturePolyhedron candidate = scratch?.Leaf() ?? new CapsuleFeaturePolyhedron();
        candidate.Reset(index, localPose, pose, leafId, local, world, polygons, transform);
        CapsuleFeatureStatus validation = candidate.Validate(scratch);
        if (validation == CapsuleFeatureStatus.Complete) leaf = candidate;
        return validation;
    }

    // Box corner i has +X when bit 0 is set, +Y for bit 1 and +Z for bit 2. Faces are -X, +X, -Y, +Y, -Z, +Z.
    static readonly int[][] BoxFaces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4], [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];

    internal static CapsuleFeatureStatus TransformVertices(Vector3[] local, in RigidPose pose, out FeaturePoint[] world,
        out InstalledPoseOperator transform)
    {
        world = new FeaturePoint[local.Length];
        if (!InstalledPoseOperator.TryCreate(pose, out transform)) return CapsuleFeatureStatus.Unsupported;
        return TransformVertices(local, pose, transform, world);
    }

    // Every vertex through the operator already created for the pose.
    static CapsuleFeatureStatus TransformVertices(Vector3[] local, in RigidPose pose,
        in InstalledPoseOperator transform, FeaturePoint[] world)
    {
        var seamPose = new Pose(pose.Position, pose.Orientation);
        for (int i = 0; i < local.Length; i++)
        {
            CapsuleFeatureStatus status = TransformVertex(local[i], seamPose, transform, out world[i]);
            if (status != CapsuleFeatureStatus.Complete) return status;
        }
        return CapsuleFeatureStatus.Complete;
    }

    /// <summary>One local vertex through a proved installed pose, with the coordinate bounds and finite checks every
    /// captured vertex passes.</summary>
    internal static CapsuleFeatureStatus TransformVertex(Vector3 point, in Pose seamPose,
        in InstalledPoseOperator transform, out FeaturePoint world)
    {
        world = default;
        if (!Within(point, 64)) return CapsuleFeatureStatus.Unsupported;
        GeometryVector enclosure = RepresentedGeometryTransforms.PosePoint(seamPose, point);
        if (!enclosure.IsResolved) return CapsuleFeatureStatus.Unsupported;
        // PosePoint is an operation correspondence record only. The common certified affine
        // operator below contains real and actual represented coefficients through every
        // witness expression. Independently rounded point outputs never define topology.
        world = transform.Point(point);
        if (!world.IsResolved) return CapsuleFeatureStatus.Unresolved;
        if (!world.Within(2048)) return CapsuleFeatureStatus.Unsupported;
        return CapsuleFeatureStatus.Complete;
    }

    internal static bool ProveInstalledPose(in RigidPose pose) => InstalledPoseOperator.TryCreate(pose, out _);

    static Vector3 Canonical(Vector3 p) => new(p.X == 0 ? 0 : p.X, p.Y == 0 ? 0 : p.Y, p.Z == 0 ? 0 : p.Z);
    static bool Within(Vector3 p, float maximum) => float.IsFinite(p.X) && float.IsFinite(p.Y) &&
        float.IsFinite(p.Z) && MathF.Abs(p.X) <= maximum && MathF.Abs(p.Y) <= maximum && MathF.Abs(p.Z) <= maximum;
}
