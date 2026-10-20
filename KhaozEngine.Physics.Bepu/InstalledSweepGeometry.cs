using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Private installation topology or query-private triangle scratch.
/// Published installation poses change only under the writer.</summary>
internal sealed class InstalledSweepGeometry
{
    readonly Vector3[] _vertices;
    static readonly int[][] BoxFaces = [[0, 4, 6, 2], [1, 3, 7, 5], [0, 1, 5, 4],
        [2, 6, 7, 3], [0, 2, 3, 1], [4, 5, 7, 6]];
    static readonly int[][] TriangleFaces = [[0, 1, 2]];
    static readonly (int A, int B)[] BoxEdges = [(0, 1), (0, 2), (0, 4), (1, 3), (1, 5), (2, 3),
        (2, 6), (3, 7), (4, 5), (4, 6), (5, 7), (6, 7)];
    static readonly (int A, int B)[] TriangleEdges = [(0, 1), (1, 2), (2, 0)];
    readonly int[][] _faces = BoxFaces;
    readonly (int A, int B)[] _edges;
    Triangle _triangle;
    readonly Vector3[]? _installedPoints;
    readonly HullVertexIndex[]? _installedIndices;
    readonly InstalledSweepGeometry? _native;
    internal InstalledSweepGeometry[]? Children { get; }
    internal int ChildIndex { get; } = -1;
    internal Shapes Owner { get; private set; }
    internal TypedIndex Root { get; private set; }
    internal TypedIndex Shape { get; private set; }
    internal RigidPose LocalPose { get; private set; }
    internal InstalledMeshSweepGeometry? Mesh { get; }
    internal bool IsTriangle { get; }
    internal SupportCurvedShape? Curved { get; }
    internal float Radius { get; }
    internal float HalfLength { get; }
    internal bool IsRound => Curved is SupportCurvedShape.Sphere or SupportCurvedShape.Capsule;
    internal InstalledSweepGeometry? SweepCompanion { get; set; }
    internal bool IsHull => _installedPoints is not null;
    internal Vector3 Half { get; }
    internal int VertexCount => Mesh is { } mesh ? mesh.Count * 3 : IsTriangle ? 3 : _vertices.Length;
    internal int FaceCount => _faces.Length;
    internal int SurfaceCount => _faces.Length;
    internal int EdgeCount => _edges.Length;
    internal int FeatureCount => SurfaceCount + EdgeCount + VertexCount + (IsTriangle || Curved.HasValue ? 0 : 1);
    internal CapsuleSweepPruningBounds PruningMetrics { get; set; }
    internal (Vector3 Min, Vector3 Max)? PruningBounds { get; set; }
    internal RigidPose LeafPose { get; private set; }
    internal RigidPose ParentPose => _pose;
    RigidPose _pose;
    InstalledPoseOperator _operator;
    bool _posed;
    int _registrationId = -1;
    long _installationGeneration;
    internal bool IsInstallationBound => _registrationId >= 0;

    internal void BindInstallation(int registrationId, long generation)
    {
        if (_registrationId != -1 || registrationId < 0 || generation <= 0)
            throw new InvalidOperationException("Sweep topology must bind once to a live installation.");
        _registrationId = registrationId; _installationGeneration = generation;
    }

    internal bool MatchesInstallation(int registrationId, long generation) =>
        _registrationId == registrationId && _installationGeneration == generation && generation > 0;

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, Vector3 half)
    {
        Owner = owner; Root = Shape = root; Half = half;
        LocalPose = new(Vector3.Zero, Quaternion.Identity);
        _vertices = new Vector3[8];
        _edges = BoxEdges;
        for (int i = 0; i < 8; i++)
        {
            _vertices[i] = new((i & 1) == 0 ? -half.X : half.X,
                (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
        }
    }

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, TypedIndex shape, RigidPose local,
        CapsuleFeaturePolyhedron topology, Vector3[] installedPoints, HullVertexIndex[] indices)
    {
        Owner = owner; Root = root; Shape = shape; LocalPose = local;
        _vertices = topology.LocalVertices; _faces = topology.Faces;
        _installedPoints = installedPoints; _installedIndices = indices;
        _edges = new (int, int)[topology.Edges.Length];
        for (int i = 0; i < _edges.Length; i++) _edges[i] = (topology.Edges[i].A, topology.Edges[i].B);
    }

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, InstalledMeshSweepGeometry mesh)
    {
        Owner = owner; Root = Shape = root; Mesh = mesh;
        LocalPose = new(Vector3.Zero, Quaternion.Identity);
        _vertices = []; _faces = []; _edges = [];
    }

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, in Triangle triangle, RigidPose? local = null)
    {
        Owner = owner; Root = Shape = root; IsTriangle = true;
        LocalPose = local ?? new RigidPose(Vector3.Zero, Quaternion.Identity);
        _triangle = triangle; _vertices = [];
        _faces = TriangleFaces; _edges = TriangleEdges;
    }

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, TypedIndex shape, RigidPose local,
        SupportCurvedShape curved, float radius, float halfLength)
    {
        Owner = owner; Root = root; Shape = shape; LocalPose = local;
        Curved = curved; Radius = radius; HalfLength = halfLength;
        _faces = [];
        _vertices = halfLength == 0 ? [Vector3.Zero] : [new(0, -halfLength, 0), new(0, halfLength, 0)];
        _edges = halfLength == 0 ? [] : [(0, 1)];
    }

    internal InstalledSweepGeometry(Shapes owner, TypedIndex root, InstalledSweepGeometry[] children)
    {
        Owner = owner; Root = Shape = root; Children = children;
        LocalPose = new(Vector3.Zero, Quaternion.Identity);
        _vertices = []; _faces = []; _edges = [];
    }

    internal InstalledSweepGeometry(InstalledSweepGeometry native, TypedIndex root, RigidPose local, int childIndex)
    {
        _native = native; Owner = native.Owner; Root = root; Shape = native.Shape;
        LocalPose = local; ChildIndex = childIndex; Half = native.Half;
        Curved = native.Curved; Radius = native.Radius; HalfLength = native.HalfLength;
        _vertices = native._vertices; _faces = native._faces; _edges = native._edges;
        _installedPoints = native._installedPoints; _installedIndices = native._installedIndices;
    }

    internal Vector3 Vertex(int index) => Mesh is { } mesh ? mesh.Vertex(index) : IsTriangle
        ? index switch { 0 => _triangle.A, 1 => _triangle.B, 2 => _triangle.C, _ => throw new IndexOutOfRangeException() }
        : _vertices[index];
    internal void SetTriangle(Shapes owner, TypedIndex root, in Triangle triangle, RigidPose local)
    {
        Owner = owner; Root = Shape = root; _triangle = triangle; LocalPose = local;
    }
    internal int FaceVertex(int face, int index) => _faces[face][index];
    internal int InteriorFaceVertex(int face, int index) => _faces[face][index];
    internal int InteriorFaceSize(int face) => _faces[face].Length;
    internal (int A, int B) Edge(int index) => _edges[index];
    internal void PublishPose(RigidPose pose, RigidPose leafPose, InstalledPoseOperator map)
    {
        _posed = false;
        if (Children is { } children)
            foreach (var child in children)
            {
                if (!child.TryComposePose(pose, out var composed) || !InstalledPoseOperator.TryCreate(composed, out var childMap)) return;
                child.PublishPose(pose, composed, childMap);
            }
        _pose = pose; LeafPose = leafPose; _operator = map; _posed = true;
    }
    internal bool TryComposePose(RigidPose parent, out RigidPose leaf)
    {
        leaf = parent;
        if (Children is not null || Root.Type != default(Compound).TypeId) return true;
        Compound.GetWorldPose(LocalPose, parent, out leaf);
        return InstalledPoseOperations.ProveComposition(LocalPose, parent, leaf);
    }

    internal bool Matches(Shapes shapes, TypedIndex root)
    {
        if (!ReferenceEquals(Owner, shapes) || root.Type != Root.Type || root.Index != Root.Index) return false;
        if (Children is { } children)
        {
            if (root.Type != default(Compound).TypeId) return false;
            ref Compound compound = ref shapes.GetShape<Compound>(root.Index);
            if (!compound.Children.Allocated || compound.Children.Length != children.Length) return false;
            foreach (var child in children) if (!child.Matches(shapes, root)) return false;
            return true;
        }
        if (_native is { } native)
        {
            ref Compound compound = ref shapes.GetShape<Compound>(root.Index);
            if (!compound.Children.Allocated || (uint)ChildIndex >= (uint)compound.Children.Length) return false;
            var child = compound.Children[ChildIndex];
            return child.ShapeIndex == Shape && child.LocalPose.Position == LocalPose.Position &&
                child.LocalPose.Orientation == LocalPose.Orientation && native.Matches(shapes, Shape);
        }
        if (Mesh is { } mesh) return mesh.Matches(shapes, root);
        if (Curved.HasValue) return CapsuleCurvedGeometry.Matches(this, shapes, root);
        if (!IsHull) return CapsuleSweepGeometry.TryReadIdentityBox(shapes, root,
            new RigidPose(Vector3.Zero, Quaternion.Identity), out Box box) &&
            new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength) == Half;
        if (root.Type == default(Compound).TypeId)
        {
            ref Compound compound = ref shapes.GetShape<Compound>(root.Index);
            if (!compound.Children.Allocated || compound.Children.Length != 1) return false;
            var child = compound.Children[0];
            if (child.ShapeIndex.Type != Shape.Type || child.ShapeIndex.Index != Shape.Index ||
                child.LocalPose.Position != LocalPose.Position || child.LocalPose.Orientation != LocalPose.Orientation) return false;
        }
        ref ConvexHull hull = ref shapes.GetShape<ConvexHull>(Shape.Index);
        if (!CapsuleSweepGeometry.HullBuffersValid(hull) ||
            hull.Points.Length * Vector<float>.Count != _installedPoints!.Length ||
            hull.FaceToVertexIndicesStart.Length != FaceCount || hull.FaceVertexIndices.Length != _installedIndices!.Length) return false;
        int offset = 0;
        for (int face = 0; face < FaceCount; face++)
        {
            if (hull.FaceToVertexIndicesStart[face] != offset) return false;
            offset += _faces[face].Length;
        }
        for (int i = 0; i < _installedIndices.Length; i++)
            if (hull.FaceVertexIndices[i].BundleIndex != _installedIndices[i].BundleIndex ||
                hull.FaceVertexIndices[i].InnerIndex != _installedIndices[i].InnerIndex) return false;
        for (int i = 0; i < _installedPoints.Length; i++)
        {
            hull.GetPoint(i, out Vector3 point);
            Vector3 expected = _installedPoints[i];
            if (BitConverter.SingleToInt32Bits(point.X) != BitConverter.SingleToInt32Bits(expected.X) ||
                BitConverter.SingleToInt32Bits(point.Y) != BitConverter.SingleToInt32Bits(expected.Y) ||
                BitConverter.SingleToInt32Bits(point.Z) != BitConverter.SingleToInt32Bits(expected.Z)) return false;
        }
        return true;
    }
    internal bool TryPose(RigidPose pose, out InstalledPoseOperator map)
    {
        map = default;
        if (!_posed || pose.Position != _pose.Position || pose.Orientation != _pose.Orientation) return false;
        map = _operator;
        return map.IsResolved;
    }
}

/// <summary>One registration's immutable topology and current mutation-proved pose.</summary>
internal readonly struct InstalledSweepLeaf(InstalledSweepGeometry geometry, RigidPose pose, InstalledPoseOperator map, bool operational = false)
{
    internal InstalledSweepGeometry Geometry { get; } = geometry;
    internal RigidPose Pose { get; } = pose;
    internal InstalledPoseOperator Map { get; } = map;
    internal bool Operational { get; } = operational;
}
