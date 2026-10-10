using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Box and convex hull leaves of one installed static, captured and admitted exactly as the feature
/// query captures them, with each face published as a polygon. Mesh statics and curved leaves have their own
/// owners. They are skipped here, and a curved leaf never refuses the polyhedral leaves beside it.</summary>
internal static class SupportNeighborhoodPolyhedra
{
    /// <summary>Element ids are leaf * <see cref="FaceStride"/> + face.</summary>
    internal const int FaceStride = CapsuleFeatureGeometry.MaximumFaces;

    /// <summary>Captures every box and hull leaf of one static into <paramref name="leaves"/>, reusing
    /// <paramref name="scratch"/>. The leaves stay valid until the scratch is reset.</summary>
    internal static CapsuleFeatureStatus Capture(Simulation simulation, TypedIndex shape, in RigidPose pose,
        CapsuleFeatureCaptureScratch scratch, List<CapsuleFeaturePolyhedron> leaves)
    {
        leaves.Clear();
        bool compound = shape.Type == default(Compound).TypeId;
        if (!compound && !Polyhedral(shape)) return CapsuleFeatureStatus.Complete;
        CapsuleFeaturePolyhedron? leaf;
        CapsuleFeatureStatus status;
        if (compound)
        {
            if (!CapsuleFeatureGeometry.ProveInstalledPose(pose)) return CapsuleFeatureStatus.Unsupported;
            ref Compound installed = ref simulation.Shapes.GetShape<Compound>(shape.Index);
            if (installed.Children.Length is 0 or > CapsuleFeatureGeometry.MaximumLeaves)
                return CapsuleFeatureStatus.Unsupported;
            for (int i = 0; i < installed.Children.Length; i++)
            {
                ref var child = ref installed.Children[i];
                if (!Polyhedral(child.ShapeIndex)) continue;
                if (!CapsuleFeatureGeometry.ProveInstalledPose(child.LocalPose)) return CapsuleFeatureStatus.Unsupported;
                // Exactly the pinned backend's composition, including its represented rounding.
                Compound.GetWorldPose(child.LocalPose, pose, out RigidPose worldPose);
                if (!InstalledPoseOperations.ProveComposition(child.LocalPose, pose, worldPose))
                    return CapsuleFeatureStatus.Unsupported;
                status = CapsuleFeatureGeometry.CaptureLeaf(simulation, child.ShapeIndex, worldPose, child.LocalPose,
                    i, scratch, out leaf);
                if (status != CapsuleFeatureStatus.Complete) return Admission(status);
                leaves.Add(leaf!);
            }
            return CapsuleFeatureStatus.Complete;
        }
        // CaptureLeaf proves this same pose first and refuses it as Unsupported, which Admission keeps.
        status = CapsuleFeatureGeometry.CaptureLeaf(simulation, shape, pose,
            new RigidPose(Vector3.Zero, Quaternion.Identity), 0, scratch, out leaf);
        if (status != CapsuleFeatureStatus.Complete) return Admission(status);
        leaves.Add(leaf!);
        return CapsuleFeatureStatus.Complete;
    }

    /// <summary>Every face of an admitted leaf, with edges and the outward normal from the leaf's common
    /// installed operator rather than from independently rounded world vertices.</summary>
    internal static void Polygons(StaticHandle owner, CapsuleFeaturePolyhedron leaf, SupportPolygonPool pool,
        List<SupportPolygon> output)
    {
        for (int face = 0; face < leaf.Faces.Length; face++)
        {
            int[] indices = leaf.Faces[face];
            SupportPolygon polygon = pool.Rent();
            polygon.Begin(owner, leaf.LeafId * FaceStride + face, indices.Length);
            for (int i = 0; i < indices.Length; i++)
                polygon.Set(i, leaf.Vertices[indices[i]], leaf.EdgeDirection(indices[i], indices[(i + 1) % indices.Length]));
            polygon.Finish(leaf.Normals[face]);
            output.Add(polygon);
        }
    }

    static bool Polyhedral(TypedIndex shape) =>
        shape.Type == default(Box).TypeId || shape.Type == default(ConvexHull).TypeId;

    // A non-manifold or nonconvex capture is ambiguous. Every other admission failure lies outside the proven
    // pose, size or numerical domain.
    static CapsuleFeatureStatus Admission(CapsuleFeatureStatus status) =>
        status == CapsuleFeatureStatus.Ambiguous ? CapsuleFeatureStatus.Ambiguous : CapsuleFeatureStatus.Unsupported;
}
