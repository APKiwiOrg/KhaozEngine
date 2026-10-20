using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Privately owned local topology. Construction copies every array, including face indices.
/// Materialization copies into query scratch before exposing mutable polyhedron storage.</summary>
internal sealed class InstalledSupportLeaf
{
    readonly Vector3[] _vertices;
    readonly int[][] _faces;
    internal int LeafId { get; }
    internal Pose LocalPose { get; }
    internal SupportCurvedShape? CurvedShape { get; }
    internal double Radius { get; }
    internal double HalfLength { get; }
    internal TypedIndex Installation { get; }
    internal ReadOnlySpan<Vector3> Vertices => _vertices;

    internal InstalledSupportLeaf(int leafId, Pose localPose, Vector3[] vertices, int[][] faces,
        SupportCurvedShape? curvedShape, double radius, double halfLength, TypedIndex installation = default)
    {
        LeafId = leafId;
        LocalPose = localPose;
        CurvedShape = curvedShape;
        Radius = radius;
        HalfLength = halfLength;
        Installation = installation;
        _vertices = (Vector3[])vertices.Clone();
        _faces = new int[faces.Length][];
        for (int i = 0; i < faces.Length; i++) _faces[i] = (int[])faces[i].Clone();
    }

    internal CapsuleFeatureStatus Materialize(in RigidPose pose, CapsuleFeatureCaptureScratch scratch,
        List<CapsuleFeaturePolyhedron> leaves, List<SupportCurvedLeaf> curved)
    {
        if (CurvedShape is { } shape)
            return SupportNeighborhoodCurved.Leaf(shape, (float)Radius, (float)HalfLength, pose, LeafId, curved);
        if (!InstalledPoseOperator.TryCreateSupport(pose, out InstalledPoseOperator transform))
            return CapsuleFeatureStatus.Unsupported;
        Vector3[] local = scratch.Locals(_vertices.Length);
        _vertices.CopyTo(local, 0);
        int[][] faces = scratch.Faces(_faces.Length);
        for (int i = 0; i < faces.Length; i++)
        {
            faces[i] = scratch.Indices(_faces[i].Length);
            _faces[i].CopyTo(faces[i], 0);
        }
        FeaturePoint[] world = scratch.Points(local.Length);
        CapsuleFeatureStatus status = CapsuleFeatureGeometry.TransformVertices(local, transform, world);
        if (status != CapsuleFeatureStatus.Complete) return status;
        CapsuleFeaturePolyhedron leaf = scratch.Leaf();
        leaf.Reset(Installation, new RigidPose(LocalPose.Position, LocalPose.Orientation), pose,
            LeafId, local, world, faces, transform);
        status = leaf.ValidateInstalledNormals(scratch);
        if (status == CapsuleFeatureStatus.Complete) leaves.Add(leaf);
        return status == CapsuleFeatureStatus.Ambiguous ? status :
            status == CapsuleFeatureStatus.Complete ? status : CapsuleFeatureStatus.Unsupported;
    }
}

/// <summary>Immutable local topology of one installed static, keyed by owner, installation and static geometry
/// version, with at most one lazily published <see cref="InstalledSupportWorldForm"/>.</summary>
internal sealed class InstalledSupportGeometry
{
    InstalledSupportWorldForm? _worldForm;

    internal IReadOnlyList<InstalledSupportLeaf> Leaves { get; }

    /// <summary>The published world form, or null before the first build publishes one.</summary>
    internal InstalledSupportWorldForm? WorldForm => Volatile.Read(ref _worldForm);

    /// <summary>Serializes unobserved world form builds, so concurrent unobserved misses build once. It follows the
    /// <see cref="InstalledSupportGeometryCache"/> slot rule: a build with an allocation observer installed runs
    /// observer code at its phase boundaries, which may block on another reader or reenter this geometry on its own
    /// thread, so it builds outside this gate, and the first publication wins. Unobserved builds run engine code
    /// alone and are held under it.</summary>
    internal Lock WorldFormGate { get; } = new();

    /// <summary>Publishes <paramref name="built"/> unless a form is already published, and returns the published
    /// form. A form built later, such as an outer build whose observer ran a nested build, is discarded.</summary>
    internal InstalledSupportWorldForm Publish(InstalledSupportWorldForm built) =>
        Interlocked.CompareExchange(ref _worldForm, built, null) ?? built;

    internal InstalledSupportGeometry(IReadOnlyList<InstalledSupportLeaf> leaves)
    {
        var owned = new InstalledSupportLeaf[leaves.Count];
        for (int i = 0; i < owned.Length; i++) owned[i] = leaves[i];
        Leaves = Array.AsReadOnly(owned);
    }

    internal bool Matches(Simulation simulation, TypedIndex installation)
    {
        if (installation.Type != default(Compound).TypeId)
            return Leaves.Count == 1 && Leaves[0].Installation == installation;
        ref Compound compound = ref simulation.Shapes.GetShape<Compound>(installation.Index);
        if (compound.Children.Length != Leaves.Count) return false;
        for (int i = 0; i < Leaves.Count; i++)
        {
            ref var child = ref compound.Children[i];
            InstalledSupportLeaf leaf = Leaves[i];
            if (child.ShapeIndex != leaf.Installation || child.LocalPose.Position != leaf.LocalPose.Position ||
                child.LocalPose.Orientation != leaf.LocalPose.Orientation) return false;
        }
        return true;
    }

    internal CapsuleFeatureStatus Materialize(Simulation simulation, in StaticDescription description,
        CapsuleFeatureCaptureScratch scratch, List<CapsuleFeaturePolyhedron> leaves, List<SupportCurvedLeaf> curved)
    {
        leaves.Clear();
        curved.Clear();
        if (description.Shape.Type != default(Compound).TypeId)
            return Leaves[0].Materialize(description.Pose, scratch, leaves, curved);
        if (!InstalledPoseOperator.TryCreateSupport(description.Pose, out _)) return CapsuleFeatureStatus.Unsupported;
        ref Compound compound = ref simulation.Shapes.GetShape<Compound>(description.Shape.Index);
        for (int i = 0; i < Leaves.Count; i++)
        {
            ref var child = ref compound.Children[i];
            if (!InstalledPoseOperator.TryCreateSupport(child.LocalPose, out _)) return CapsuleFeatureStatus.Unsupported;
            Compound.GetWorldPose(child.LocalPose, description.Pose, out RigidPose pose);
            if (!InstalledPoseOperations.ProveSupportComposition(child.LocalPose, description.Pose, pose))
                return CapsuleFeatureStatus.Unsupported;
            CapsuleFeatureStatus status = Leaves[i].Materialize(pose, scratch, leaves, curved);
            if (status != CapsuleFeatureStatus.Complete) return status;
        }
        return CapsuleFeatureStatus.Complete;
    }
}
