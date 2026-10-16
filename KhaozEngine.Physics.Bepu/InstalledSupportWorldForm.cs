using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>The pose-applied world form of one <see cref="InstalledSupportGeometry"/> at one exact static pose and,
/// for a compound, its exact child local poses. It is snapshotted from the uncached materialization and polygon
/// derivation of one query, so it holds exactly their results, and is then shared read-only by concurrent readers.
/// No query operand, result or refusal enters it. Readers bind their own pooled polygons from it.</summary>
internal sealed class InstalledSupportWorldForm
{
    readonly RigidPose _pose;
    readonly RigidPose[] _childPoses;
    readonly InstalledSupportWorldLeaf[] _leaves;
    readonly SupportCurvedLeaf[] _curved;

    /// <summary>Box and hull leaves in materialization order.</summary>
    internal ReadOnlySpan<InstalledSupportWorldLeaf> Leaves => _leaves;

    /// <summary>Curved leaves in materialization order.</summary>
    internal ReadOnlySpan<SupportCurvedLeaf> Curved => _curved;

    /// <summary>Snapshots one static's materialized <paramref name="leaves"/>, the footprint
    /// <paramref name="polygons"/> derived from them in order and its <paramref name="curved"/> leaves, keyed by
    /// the exact live poses of <paramref name="description"/>.</summary>
    internal InstalledSupportWorldForm(Simulation simulation, in StaticDescription description,
        List<CapsuleFeaturePolyhedron> leaves, List<SupportPolygon> polygons, List<SupportCurvedLeaf> curved)
    {
        _pose = description.Pose;
        _childPoses = [];
        if (description.Shape.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref simulation.Shapes.GetShape<Compound>(description.Shape.Index);
            _childPoses = new RigidPose[compound.Children.Length];
            for (int i = 0; i < _childPoses.Length; i++) _childPoses[i] = compound.Children[i].LocalPose;
        }
        _leaves = new InstalledSupportWorldLeaf[leaves.Count];
        int polygon = 0;
        for (int i = 0; i < _leaves.Length; i++)
        {
            CapsuleFeaturePolyhedron leaf = leaves[i];
            _leaves[i] = new(leaf, polygons, polygon);
            polygon += leaf.Faces.Length;
        }
        if (polygon != polygons.Count)
            throw new InvalidOperationException("Every footprint polygon of a world form belongs to one of its leaves.");
        _curved = [.. curved];
    }

    /// <summary>Whether <paramref name="description"/> carries exactly the pose bits, and for a compound exactly
    /// the child local pose bits, this form was built at.</summary>
    internal bool Matches(Simulation simulation, in StaticDescription description)
    {
        if (!Same(description.Pose, _pose)) return false;
        if (description.Shape.Type != default(Compound).TypeId) return _childPoses.Length == 0;
        ref Compound compound = ref simulation.Shapes.GetShape<Compound>(description.Shape.Index);
        if (compound.Children.Length != _childPoses.Length) return false;
        for (int i = 0; i < _childPoses.Length; i++)
            if (!Same(compound.Children[i].LocalPose, _childPoses[i])) return false;
        return true;
    }

    /// <summary>Binds one pooled polygon per face, in the order the uncached derivation emits them, and appends
    /// every curved leaf.</summary>
    internal void Bind(StaticHandle owner, SupportPolygonPool pool, List<SupportPolygon> polygons,
        List<SupportCurvedLeaf> curved)
    {
        foreach (InstalledSupportWorldLeaf leaf in _leaves)
            foreach (InstalledSupportWorldFace face in leaf.Faces)
            {
                SupportPolygon polygon = pool.Rent();
                polygon.Bind(owner, face);
                polygons.Add(polygon);
            }
        foreach (SupportCurvedLeaf leaf in _curved) curved.Add(leaf);
    }

    static bool Same(in RigidPose a, in RigidPose b) =>
        Same(a.Position.X, b.Position.X) && Same(a.Position.Y, b.Position.Y) && Same(a.Position.Z, b.Position.Z) &&
        Same(a.Orientation.X, b.Orientation.X) && Same(a.Orientation.Y, b.Orientation.Y) &&
        Same(a.Orientation.Z, b.Orientation.Z) && Same(a.Orientation.W, b.Orientation.W);

    static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}

/// <summary>One box or hull leaf of a world form: its certified installed operator, the certified world vertices,
/// the validated face normals and its faces' footprint polygon facts.</summary>
internal sealed class InstalledSupportWorldLeaf
{
    readonly FeaturePoint[] _vertices;
    readonly FeaturePoint[] _normals;
    readonly InstalledSupportWorldFace[] _faces;

    internal int LeafId { get; }
    internal InstalledPoseOperator Operator { get; }
    internal ReadOnlySpan<FeaturePoint> Vertices => _vertices;
    internal ReadOnlySpan<FeaturePoint> Normals => _normals;
    internal ReadOnlySpan<InstalledSupportWorldFace> Faces => _faces;

    internal InstalledSupportWorldLeaf(CapsuleFeaturePolyhedron leaf, List<SupportPolygon> polygons, int first)
    {
        LeafId = leaf.LeafId;
        Operator = leaf.SourceMap;
        _vertices = [.. leaf.Vertices];
        _normals = [.. leaf.Normals];
        _faces = new InstalledSupportWorldFace[leaf.Faces.Length];
        for (int i = 0; i < _faces.Length; i++) _faces[i] = new(polygons[first + i]);
    }
}

/// <summary>The query-independent facts of one footprint face polygon, copied from a finished polygon.</summary>
internal sealed class InstalledSupportWorldFace
{
    readonly FeaturePoint[] _vertices;
    readonly FeaturePoint[] _edges;
    readonly Vector3[] _sourceVertices;
    readonly double[] _low;
    readonly double[] _high;

    internal int ElementId { get; }
    internal ReadOnlySpan<FeaturePoint> Vertices => _vertices;
    internal ReadOnlySpan<FeaturePoint> Edges => _edges;
    internal ReadOnlySpan<Vector3> SourceVertices => _sourceVertices;
    internal SupportPolygonSourceMap SourceMap { get; }
    internal bool HasSource { get; }
    internal FeaturePoint Normal { get; }
    internal GeometryInterval NormalLength { get; }
    internal ReadOnlySpan<double> Low => _low;
    internal ReadOnlySpan<double> High => _high;
    internal bool IsResolved { get; }
    internal bool HasJoinIncidenceCertificate { get; }

    internal InstalledSupportWorldFace(SupportPolygon polygon)
    {
        ElementId = polygon.ElementId;
        _vertices = polygon.Vertices.ToArray();
        _edges = polygon.Edges.ToArray();
        _sourceVertices = polygon.SourceVertices.ToArray();
        SourceMap = polygon.SourceMap;
        HasSource = polygon.HasSource;
        Normal = polygon.Normal;
        NormalLength = polygon.NormalLength;
        _low = [.. polygon.Low];
        _high = [.. polygon.High];
        IsResolved = polygon.IsResolved;
        HasJoinIncidenceCertificate = polygon.HasJoinIncidenceCertificate;
    }
}
