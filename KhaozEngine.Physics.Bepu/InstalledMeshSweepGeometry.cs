using System;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Installation-owned raw triangle identities and mutation-proved inner tree.
/// Capturing or posing may scan this installation. Queries visit only certified tree branches.</summary>
internal sealed class InstalledMeshSweepGeometry
{
    readonly Shapes _owner;
    readonly TypedIndex _root;
    readonly Vector3 _scale;
    readonly Triangle[] _raw;
    readonly InstalledSweepGeometry[] _triangles;
    readonly RigidPose[] _nativeOffsets;
    readonly (Vector3 Min, Vector3 Max)[] _nativeBounds;
    RigidPose _lastPose;
    bool _posed;
    internal (Vector3 Min, Vector3 Max) RootBounds { get; private set; }
    internal (Vector3 Min, Vector3 Max) PruningBounds { get; private set; }
    internal CapsuleSweepPruningBounds PruningMetrics { get; private set; }
    readonly int _triangleBuffer, _nodeBuffer, _leafBuffer;
    readonly Node[] _nodes;
    readonly (Vector3 Min, Vector3 Max)[] _posedBounds;
    readonly bool[] _seenNodes, _seenLeaves;
    readonly int[] _stack, _order, _counts;
    internal int Count => _raw.Length;

    InstalledMeshSweepGeometry(Shapes owner, TypedIndex root, in Mesh mesh)
    {
        _owner = owner; _root = root; _scale = mesh.Scale;
        _raw = new Triangle[mesh.Triangles.Length];
        _triangles = new InstalledSweepGeometry[_raw.Length];
        _nativeOffsets = new RigidPose[_raw.Length];
        _nativeBounds = new (Vector3, Vector3)[_raw.Length];
        _triangleBuffer = mesh.Triangles.Id; _nodeBuffer = mesh.Tree.Nodes.Id; _leafBuffer = mesh.Tree.Leaves.Id;
        _nodes = new Node[mesh.Tree.NodeCount];
        _posedBounds = new (Vector3, Vector3)[_nodes.Length * 2];
        _seenNodes = new bool[_nodes.Length]; _seenLeaves = new bool[_raw.Length];
        _stack = new int[_nodes.Length + _raw.Length]; _order = new int[_nodes.Length]; _counts = new int[_nodes.Length];
        for (int i = 0; i < _raw.Length; i++)
        {
            _raw[i] = mesh.Triangles[i];
            mesh.GetLocalChild(i, out Triangle scaled);
            _triangles[i] = new(owner, root, scaled);
            mesh.GetPosedLocalChild(i, out Triangle native, out _nativeOffsets[i]);
            _triangles[i].SweepCompanion = new(owner, root, native, _nativeOffsets[i]);
            _nativeBounds[i] = (Vector3.Min(native.A, Vector3.Min(native.B, native.C)), Vector3.Max(native.A, Vector3.Max(native.B, native.C)));
        }
    }

    internal static bool TryCapture(Shapes shapes, TypedIndex root, out InstalledSweepGeometry? geometry)
    {
        geometry = null;
        if (shapes is null || !root.Exists || root.Type != default(Mesh).TypeId) return false;
        ref Mesh mesh = ref shapes.GetShape<Mesh>(root.Index);
        if (!Positive(mesh.Scale.X) || !Positive(mesh.Scale.Y) || !Positive(mesh.Scale.Z) ||
            !mesh.Triangles.Allocated || mesh.Triangles.Length < 1 ||
            !mesh.Tree.Nodes.Allocated || !mesh.Tree.Leaves.Allocated || !mesh.Tree.Metanodes.Allocated ||
            mesh.Tree.LeafCount != mesh.Triangles.Length || mesh.Tree.NodeCount != Math.Max(1, mesh.Triangles.Length - 1) ||
            mesh.Tree.NodeCount > mesh.Tree.Nodes.Length || mesh.Tree.NodeCount > mesh.Tree.Metanodes.Length ||
            mesh.Tree.LeafCount > mesh.Tree.Leaves.Length) return false;
        for (int i = 0; i < mesh.Triangles.Length; i++)
        {
            mesh.GetLocalChild(i, out Triangle triangle);
            mesh.GetPosedLocalChild(i, out Triangle native, out RigidPose offset);
            if (!Local(native.A) || !Local(native.B) || !Local(native.C) || !Local(offset.Position) || offset.Orientation != Quaternion.Identity ||
                !Local(triangle.A) || !Local(triangle.B) || !Local(triangle.C) ||
                !Local(mesh.Triangles[i].A) || !Local(mesh.Triangles[i].B) || !Local(mesh.Triangles[i].C)) return false;
        }
        var captured = new InstalledMeshSweepGeometry(shapes, root, mesh);
        if (!captured.ProveTree(ref mesh)) return false;
        geometry = new(shapes, root, captured);
        return true;
    }

    internal bool Matches(Shapes shapes, TypedIndex root)
    {
        if (!ReferenceEquals(shapes, _owner) || root.Type != _root.Type || root.Index != _root.Index) return false;
        ref Mesh mesh = ref shapes.GetShape<Mesh>(root.Index);
        return mesh.Scale == _scale && mesh.Triangles.Allocated && mesh.Tree.Nodes.Allocated && mesh.Tree.Leaves.Allocated &&
            mesh.Triangles.Id == _triangleBuffer && mesh.Tree.Nodes.Id == _nodeBuffer && mesh.Tree.Leaves.Id == _leafBuffer &&
            mesh.Triangles.Length == Count && mesh.Tree.LeafCount == Count && mesh.Tree.NodeCount == _nodes.Length &&
            mesh.Tree.Nodes.Length >= _nodes.Length && mesh.Tree.Leaves.Length >= Count;
    }

    internal Vector3 Vertex(int index) => _triangles[index / 3].Vertex(index % 3);
    internal InstalledSweepGeometry Triangle(int index) => _triangles[index];
    internal bool TriangleMatches(in Mesh mesh, int index) => Same(mesh.Triangles[index], _raw[index]);
    internal bool ReadNode(in Mesh mesh, int index, out Node node)
    {
        node = default;
        if ((uint)index >= _nodes.Length) return false;
        node = mesh.Tree.Nodes[index];
        return Same(node.A, _nodes[index].A) && (Count == 1 || Same(node.B, _nodes[index].B));
    }
    internal (Vector3 Min, Vector3 Max) Bounds(int node, int child) => _posedBounds[node * 2 + child];

    internal bool ProvePose(RigidPose pose, InstalledPoseOperator map)
    {
        if (!Matches(_owner, _root) || !CapsuleSweepPruning.Validate(map, pose.Orientation)) return false;
        ref Mesh mesh = ref _owner.GetShape<Mesh>(_root.Index);
        if (_posed && pose.Position == _lastPose.Position && pose.Orientation == _lastPose.Orientation && MatchesProof(mesh)) return true;
        if (!ProveTree(ref mesh)) return false;
        CapsuleSweepPruningBounds metrics = default;
        bool identity = pose.Orientation == Quaternion.Identity;
        for (int i = 0; i < Count; i++)
        {
            RigidPose nativePose;
            InstalledPoseOperator nativeMap;
            if (identity)
            {
                // Pinned scalar and wide identity rotation leave the offset unchanged.
                // Their only composition operation is the same rounded translation add.
                nativePose = new(pose.Position + _nativeOffsets[i].Position, Quaternion.Identity);
                nativeMap = map.WithTranslation(nativePose.Position);
                if (!nativeMap.IsResolved) return false;
            }
            else
            {
                Compound.GetWorldPose(_nativeOffsets[i], pose, out nativePose);
                if (!InstalledPoseOperations.ProveComposition(_nativeOffsets[i], pose, nativePose) ||
                    !InstalledPoseOperator.TryCreate(nativePose, out nativeMap)) return false;
            }
            _triangles[i].PublishPose(pose, pose, map);
            _triangles[i].SweepCompanion!.PublishPose(pose, nativePose, nativeMap);
            if (!CapsuleSweepPruningBounds.TryCreate(_triangles[i], pose, pose, map, out var rawMetrics) ||
                !CapsuleSweepPruningBounds.TryCreate(_triangles[i].SweepCompanion!, pose, nativePose, nativeMap, out var nativeMetrics)) return false;
            metrics = metrics.Include(rawMetrics).Include(nativeMetrics);
        }
        Vector3 forwardRootMin = new(float.PositiveInfinity), forwardRootMax = new(float.NegativeInfinity);
        for (int i = 0; i < _nodes.Length; i++)
        {
            _nodes[i] = mesh.Tree.Nodes[i];
            for (int child = 0; child < (Count == 1 ? 1 : 2); child++)
            {
                NodeChild source = child == 0 ? _nodes[i].A : _nodes[i].B;
                Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                Vector3 forwardMin = min, forwardMax = max;
                if (identity)
                {
                    if (!Translate(source.Min * _scale, source.Max * _scale, pose.Position, out min, out max)) return false;
                    forwardMin = min; forwardMax = max;
                }
                else for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = new((corner & 1) == 0 ? source.Min.X : source.Max.X,
                        (corner & 2) == 0 ? source.Min.Y : source.Max.Y, (corner & 4) == 0 ? source.Min.Z : source.Max.Z);
                    // Positive binary32 scaling is monotone. Rounded extrema cover every
                    // scaled installed vertex, including points on a closed tree boundary.
                    point *= _scale;
                    if (!Local(point) || !InstalledSweepBounds.TryPoint(map, pose, point, out Vector3 lo, out Vector3 hi)) return false;
                    forwardMin = Vector3.Min(forwardMin, lo); forwardMax = Vector3.Max(forwardMax, hi);
                    if (!InstalledSweepBounds.TryPruningPoint(map, pose, point, out lo, out hi)) return false;
                    min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
                }
                if (source.Index < 0)
                {
                    // ConvexHomogeneousCompoundSweepTask uses GetPosedLocalChild.
                    // Retain its actual rounded recenter and composed pose as a second
                    // geometry, instead of asserting that subtracting the center is exact.
                    int triangle = Tree.Encode(source.Index);
                    InstalledSweepGeometry native = _triangles[triangle].SweepCompanion!;
                    if (!native.TryPose(pose, out var nativeMap)) return false;
                    if (identity)
                    {
                        var local = _nativeBounds[triangle];
                        if (!Translate(local.Min, local.Max, native.LeafPose.Position, out Vector3 lo, out Vector3 hi)) return false;
                        min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
                        forwardMin = Vector3.Min(forwardMin, lo); forwardMax = Vector3.Max(forwardMax, hi);
                    }
                    else for (int vertex = 0; vertex < 3; vertex++)
                    {
                        if (!InstalledSweepBounds.TryPoint(nativeMap, native.LeafPose, native.Vertex(vertex), out Vector3 lo, out Vector3 hi)) return false;
                        forwardMin = Vector3.Min(forwardMin, lo); forwardMax = Vector3.Max(forwardMax, hi);
                        if (!InstalledSweepBounds.TryPruningPoint(nativeMap, native.LeafPose, native.Vertex(vertex), out lo, out hi)) return false;
                        min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
                    }
                }
                forwardRootMin = Vector3.Min(forwardRootMin, forwardMin); forwardRootMax = Vector3.Max(forwardRootMax, forwardMax);
                _posedBounds[i * 2 + child] = (min, max);
            }
        }
        // Union the proved descendant enclosures bottom-up as well as the transformed
        // raw boxes, so the recentered continuation cannot escape an ancestor's bound.
        for (int i = _order.Length - 1; i >= 0; i--)
        {
            int node = _order[i];
            for (int slot = 0; slot < (Count == 1 ? 1 : 2); slot++)
            {
                NodeChild child = slot == 0 ? _nodes[node].A : _nodes[node].B;
                if (child.Index < 0) continue;
                var a = _posedBounds[child.Index * 2]; var b = _posedBounds[child.Index * 2 + 1];
                var current = _posedBounds[node * 2 + slot];
                _posedBounds[node * 2 + slot] = (Vector3.Min(current.Min, Vector3.Min(a.Min, b.Min)),
                    Vector3.Max(current.Max, Vector3.Max(a.Max, b.Max)));
            }
        }
        var first = _posedBounds[0];
        RootBounds = (forwardRootMin, forwardRootMax);
        PruningBounds = Count == 1 ? first : (Vector3.Min(first.Min, _posedBounds[1].Min), Vector3.Max(first.Max, _posedBounds[1].Max));
        PruningMetrics = metrics.WithExcess(RootBounds.Min, RootBounds.Max, PruningBounds.Min, PruningBounds.Max);
        _lastPose = pose; _posed = true;
        return true;
    }

    bool MatchesProof(in Mesh mesh)
    {
        ref readonly Tree tree = ref mesh.Tree;
        if (!tree.Metanodes.Allocated || tree.Metanodes.Length < _nodes.Length || tree.Metanodes[0].Parent != -1) return false;
        // Matching the complete proved graph needs one linear pass, with no reachability
        // rebuild or repair. Raw identities and every backlink remain part of the cache key.
        for (int i = 0; i < _nodes.Length; i++)
        {
            if (!ReadNode(mesh, i, out Node node)) return false;
            for (int slot = 0; slot < (Count == 1 ? 1 : 2); slot++)
            {
                NodeChild child = slot == 0 ? node.A : node.B;
                if (child.Index < 0)
                {
                    int leaf = Tree.Encode(child.Index);
                    if (tree.Leaves[leaf].NodeIndex != i || tree.Leaves[leaf].ChildIndex != slot || !TriangleMatches(mesh, leaf)) return false;
                }
                else if (tree.Metanodes[child.Index].Parent != i || tree.Metanodes[child.Index].IndexInParent != slot) return false;
            }
        }
        return true;
    }

    bool ProveTree(ref Mesh mesh)
    {
        ref Tree tree = ref mesh.Tree;
        if (!tree.Metanodes.Allocated || tree.Metanodes.Length < _nodes.Length) return false;
        Array.Clear(_seenNodes); Array.Clear(_seenLeaves);
        int pending = 1, nodeCount = 0, leafCount = 0;
        _stack[0] = 0;
        while (pending > 0)
        {
            int index = _stack[--pending];
            if (index < 0)
            {
                int leaf = Tree.Encode(index);
                if ((uint)leaf >= Count || _seenLeaves[leaf] || !TriangleMatches(mesh, leaf)) return false;
                _seenLeaves[leaf] = true; leafCount++;
                continue;
            }
            if ((uint)index >= _nodes.Length || _seenNodes[index]) return false;
            _seenNodes[index] = true; _order[nodeCount++] = index;
            for (int slot = 0; slot < (Count == 1 ? 1 : 2); slot++)
            {
                NodeChild child = slot == 0 ? tree.Nodes[index].A : tree.Nodes[index].B;
                if (!Valid(child.Min, child.Max) || pending >= _stack.Length) return false;
                if (child.Index < 0)
                {
                    int leaf = Tree.Encode(child.Index);
                    if ((uint)leaf >= Count || tree.Leaves[leaf].NodeIndex != index || tree.Leaves[leaf].ChildIndex != slot || child.LeafCount != 1) return false;
                }
                else if ((uint)child.Index >= _nodes.Length || tree.Metanodes[child.Index].Parent != index ||
                    tree.Metanodes[child.Index].IndexInParent != slot) return false;
                _stack[pending++] = child.Index;
            }
        }
        if (nodeCount != _nodes.Length || leafCount != Count || tree.Metanodes[0].Parent != -1) return false;
        // A malformed/nonfinite bound has already refused. Repair only finite inward
        // neighbors through Tree.UpdateBounds, whose owning path refits every ancestor.
        for (int i = 0; i < Count; i++)
        {
            Triangle raw = _raw[i];
            Vector3 min = Vector3.Min(raw.A, Vector3.Min(raw.B, raw.C)), max = Vector3.Max(raw.A, Vector3.Max(raw.B, raw.C));
            var link = tree.Leaves[i];
            NodeChild child = link.ChildIndex == 0 ? tree.Nodes[link.NodeIndex].A : tree.Nodes[link.NodeIndex].B;
            if (child.Min.X > min.X || child.Min.Y > min.Y || child.Min.Z > min.Z ||
                child.Max.X < max.X || child.Max.Y < max.Y || child.Max.Z < max.Z)
                tree.UpdateBounds(i, Vector3.Min(min, child.Min), Vector3.Max(max, child.Max));
        }
        for (int i = nodeCount - 1; i >= 0; i--)
        {
            int node = _order[i], total = 0;
            for (int slot = 0; slot < (Count == 1 ? 1 : 2); slot++)
            {
                NodeChild child = slot == 0 ? tree.Nodes[node].A : tree.Nodes[node].B;
                int count = child.Index < 0 ? 1 : _counts[child.Index];
                if (child.LeafCount != count) return false;
                if (child.Index >= 0)
                {
                    Node next = tree.Nodes[child.Index];
                    if (!Contains(child, next.A) || !Contains(child, next.B)) return false;
                }
                total += count;
            }
            _counts[node] = total;
        }
        return _counts[0] == Count;
    }

    static bool Translate(Vector3 localMin, Vector3 localMax, Vector3 position, out Vector3 min, out Vector3 max)
    {
        min = max = default;
        GeometryInterval x = GeometryInterval.Enclose(localMin.X, localMax.X).Add(GeometryInterval.Exact(position.X)).EncloseSingleRounding();
        GeometryInterval y = GeometryInterval.Enclose(localMin.Y, localMax.Y).Add(GeometryInterval.Exact(position.Y)).EncloseSingleRounding();
        GeometryInterval z = GeometryInterval.Enclose(localMin.Z, localMax.Z).Add(GeometryInterval.Exact(position.Z)).EncloseSingleRounding();
        if (!x.IsResolved || !y.IsResolved || !z.IsResolved) return false;
        min = new((float)x.Lower, (float)y.Lower, (float)z.Lower);
        max = new((float)x.Upper, (float)y.Upper, (float)z.Upper);
        return Valid(min, max);
    }

    static bool Local(Vector3 value) => InstalledPoseOperator.Within(value, RepresentedGeometryTransforms.MaximumLocalMagnitude);
    static bool Positive(float value) => float.IsFinite(value) && value > 0;
    static bool Valid(Vector3 min, Vector3 max) => InstalledPoseOperator.Within(min, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) &&
        InstalledPoseOperator.Within(max, CapsuleGeneralSweepPolicy.MaximumStoredCoordinate) && min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
    static bool Contains(NodeChild outer, NodeChild inner) => outer.Min.X <= inner.Min.X && outer.Min.Y <= inner.Min.Y && outer.Min.Z <= inner.Min.Z &&
        outer.Max.X >= inner.Max.X && outer.Max.Y >= inner.Max.Y && outer.Max.Z >= inner.Max.Z;
    static bool Same(in NodeChild a, in NodeChild b) => a.Index == b.Index && a.LeafCount == b.LeafCount && a.Min == b.Min && a.Max == b.Max;
    static bool Same(in Triangle a, in Triangle b) => Bits(a.A, b.A) && Bits(a.B, b.B) && Bits(a.C, b.C);
    static bool Bits(Vector3 a, Vector3 b) => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X) &&
        BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y) && BitConverter.SingleToInt32Bits(a.Z) == BitConverter.SingleToInt32Bits(b.Z);
}
