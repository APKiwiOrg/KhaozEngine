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
    readonly InstalledMeshSweepBoundsCache _bounds = new();
    readonly InstalledMeshSweepTriangle _proofTriangle = new();
    InstalledPoseOperator _lastMap;
    RigidPose _lastPose;
    bool _posed;
    internal (Vector3 Min, Vector3 Max) RootBounds { get; private set; }
    internal (Vector3 Min, Vector3 Max) PruningBounds { get; private set; }
    internal CapsuleSweepPruningBounds PruningMetrics { get; private set; }
    readonly int _triangleBuffer, _nodeBuffer, _leafBuffer;
    readonly Node[] _nodes;
    readonly int[] _order;
    internal int Count => _raw.Length;

    InstalledMeshSweepGeometry(Shapes owner, TypedIndex root, in Mesh mesh)
    {
        _owner = owner; _root = root; _scale = mesh.Scale;
        _raw = new Triangle[mesh.Triangles.Length];
        _triangleBuffer = mesh.Triangles.Id; _nodeBuffer = mesh.Tree.Nodes.Id; _leafBuffer = mesh.Tree.Leaves.Id;
        _nodes = new Node[mesh.Tree.NodeCount];
        _order = new int[_nodes.Length];
        // Exact raw identities and the proved nodes are the minimal stale-capture evidence.
        // A buffer id alone cannot detect in-place edits before the first certified query.
        for (int i = 0; i < _raw.Length; i++) _raw[i] = mesh.Triangles[i];
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
        using var proof = new InstalledMeshSweepProofScratch(mesh.Tree.NodeCount, mesh.Triangles.Length);
        if (!captured.ProveTree(ref mesh, proof)) return false;
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

    internal Vector3 Vertex(int index)
    {
        Triangle raw = _raw[index / 3];
        return ((index % 3) switch { 0 => raw.A, 1 => raw.B, _ => raw.C }) * _scale;
    }
    internal InstalledSweepGeometry Triangle(int index)
    {
        var triangle = new InstalledMeshSweepTriangle();
        if (!TryTriangle(index, triangle)) throw new InvalidOperationException("Mesh triangle has no proved pose.");
        return triangle.Scaled;
    }
    internal bool TryTriangle(int index, InstalledMeshSweepTriangle triangle) => _posed &&
        triangle.TryPose(_owner, _root, _raw[index], _scale, _lastPose, _lastMap);
    internal (Vector3 Min, Vector3 Max)[]? SweepBounds() => _bounds.Get(this);
    internal (Vector3 Min, Vector3 Max)[]? CreateSweepBounds()
    {
        if (!_posed) return null;
        var bounds = new (Vector3, Vector3)[_nodes.Length * 2];
        return BuildPoseBounds(_lastPose, _lastMap, bounds, out _, out _, out _) ? bounds : null;
    }
    internal bool TriangleMatches(in Mesh mesh, int index) => Same(mesh.Triangles[index], _raw[index]);
    internal bool ReadNode(in Mesh mesh, int index, out Node node)
    {
        node = default;
        if ((uint)index >= _nodes.Length) return false;
        node = mesh.Tree.Nodes[index];
        return Same(node.A, _nodes[index].A) && (Count == 1 || Same(node.B, _nodes[index].B));
    }
    internal (Vector3 Min, Vector3 Max) Bounds(int node, int child) =>
        (SweepBounds() ?? throw new InvalidOperationException("Mesh has no proved traversal bounds."))[node * 2 + child];

    internal bool ProvePose(RigidPose pose, InstalledPoseOperator map)
    {
        if (!Matches(_owner, _root) || !CapsuleSweepPruning.Validate(map, pose.Orientation)) return false;
        ref Mesh mesh = ref _owner.GetShape<Mesh>(_root.Index);
        if (_posed && pose.Position == _lastPose.Position && pose.Orientation == _lastPose.Orientation && MatchesProof(mesh)) return true;
        using var proof = new InstalledMeshSweepProofScratch(_nodes.Length, Count);
        if (!ProveTree(ref mesh, proof)) return false;
        _posed = false;
        var retained = _bounds.TakeForWrite();
        for (int i = 0; i < _nodes.Length; i++) _nodes[i] = mesh.Tree.Nodes[i];
        // Bound repair and publication must stay with the writer. Only the retained
        // traversal array is deferred, never the proof used to select broad-phase candidates.
        if (!BuildPoseBounds(pose, map, retained ?? proof.Bounds, out var rootBounds, out var pruningBounds, out var metrics)) return false;
        RootBounds = rootBounds; PruningBounds = pruningBounds; PruningMetrics = metrics;
        _lastPose = pose; _lastMap = map; _posed = true;
        if (retained is not null) _bounds.Publish(retained);
        return true;
    }

    bool BuildPoseBounds(RigidPose pose, InstalledPoseOperator map, (Vector3 Min, Vector3 Max)[] bounds,
        out (Vector3 Min, Vector3 Max) rootBounds, out (Vector3 Min, Vector3 Max) pruningBounds,
        out CapsuleSweepPruningBounds pruningMetrics)
    {
        rootBounds = pruningBounds = default; pruningMetrics = default;
        // Writer proofs and first-use cache construction cannot overlap: the world gate
        // excludes writers, and the mesh-local cache gate admits one constructing reader.
        var pair = _proofTriangle;
        CapsuleSweepPruningBounds metrics = default;
        bool identity = pose.Orientation == Quaternion.Identity;
        for (int i = 0; i < Count; i++)
        {
            if (!pair.TryPose(_owner, _root, _raw[i], _scale, pose, map)) return false;
            InstalledSweepGeometry native = pair.Native;
            if (!native.TryPose(pose, out var nativeMap) ||
                !CapsuleSweepPruningBounds.TryCreate(pair.Scaled, pose, pose, map, out var rawMetrics) ||
                !CapsuleSweepPruningBounds.TryCreate(native, pose, native.LeafPose, nativeMap, out var nativeMetrics)) return false;
            metrics = metrics.Include(rawMetrics).Include(nativeMetrics);
        }
        Vector3 forwardRootMin = new(float.PositiveInfinity), forwardRootMax = new(float.NegativeInfinity);
        for (int i = 0; i < _nodes.Length; i++)
        {
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
                    if (!pair.TryPose(_owner, _root, _raw[triangle], _scale, pose, map)) return false;
                    InstalledSweepGeometry native = pair.Native;
                    if (!native.TryPose(pose, out var nativeMap)) return false;
                    if (identity)
                    {
                        Vector3 a = native.Vertex(0), b = native.Vertex(1), c = native.Vertex(2);
                        var local = (Min: Vector3.Min(a, Vector3.Min(b, c)), Max: Vector3.Max(a, Vector3.Max(b, c)));
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
                bounds[i * 2 + child] = (min, max);
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
                var a = bounds[child.Index * 2]; var b = bounds[child.Index * 2 + 1];
                var current = bounds[node * 2 + slot];
                bounds[node * 2 + slot] = (Vector3.Min(current.Min, Vector3.Min(a.Min, b.Min)),
                    Vector3.Max(current.Max, Vector3.Max(a.Max, b.Max)));
            }
        }
        var first = bounds[0];
        rootBounds = (forwardRootMin, forwardRootMax);
        pruningBounds = Count == 1 ? first : (Vector3.Min(first.Min, bounds[1].Min), Vector3.Max(first.Max, bounds[1].Max));
        pruningMetrics = metrics.WithExcess(rootBounds.Min, rootBounds.Max, pruningBounds.Min, pruningBounds.Max);
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

    bool ProveTree(ref Mesh mesh, InstalledMeshSweepProofScratch proof)
    {
        bool[] _seenNodes = proof.SeenNodes, _seenLeaves = proof.SeenLeaves;
        int[] _stack = proof.Stack, _counts = proof.Counts;
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
                // Pooled scratch can be longer than requested, so the proof keeps its exact logical bound.
                if (!Valid(child.Min, child.Max) || pending >= _nodes.Length + Count) return false;
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
