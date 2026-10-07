using System;
using System.Numerics;
using BepuPhysics.Trees;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Bounds the pinned A-first/B-pending volume traversal for one immutable tree/aperture.
/// It does not establish geometry bounds, canonical topology or selected-world completeness.</summary>
internal static unsafe class CapsuleTreePreflight
{
    internal const uint PolicyVersion = 1;
    internal const int MaximumNodes = 8192;
    internal const int MaximumLeaves = 4096;
    internal const int MaximumPending = 128;

    internal static bool TryValidate(in Tree tree, Vector3 min, Vector3 max, int maximumNodes,
        int maximumLeaves, int maximumPending, out int nodes, out int leaves, out int pending)
    {
        nodes = leaves = pending = 0;
        if (!ValidBounds(min, max) || maximumNodes <= 0 || maximumNodes > MaximumNodes ||
            maximumLeaves <= 0 || maximumLeaves > MaximumLeaves ||
            maximumPending <= 0 || maximumPending > MaximumPending ||
            tree.NodeCount < 0 || tree.LeafCount < 0 || tree.NodeCount > tree.Nodes.Length ||
            tree.LeafCount > tree.Leaves.Length) return false;
        if (tree.LeafCount == 0) return true;
        if (tree.NodeCount == 0 || !tree.Nodes.Allocated || !tree.Leaves.Allocated) return false;
        if (tree.LeafCount == 1)
        {
            ref readonly NodeChild child = ref tree.Nodes[0].A;
            if (child.Index >= 0 || !ValidChild(child, tree)) return false;
            nodes = 1;
            leaves = BoundingBox.Intersects(min, max, child.Min, child.Max) ? 1 : 0;
            return true;
        }

        Span<int> stack = stackalloc int[MaximumPending];
        int index = 0, count = 0, peak = 0, visitedNodes = 0, visitedLeaves = 0;
        while (true)
        {
            if (index < 0)
            {
                if ((uint)Tree.Encode(index) >= (uint)tree.LeafCount || visitedLeaves == maximumLeaves)
                    return false;
                visitedLeaves++;
                if (count == 0) break;
                index = stack[--count];
                continue;
            }
            if ((uint)index >= (uint)tree.NodeCount || visitedNodes == maximumNodes) return false;
            visitedNodes++;
            ref readonly Node node = ref tree.Nodes[index];
            if (!ValidChild(node.A, tree) || !ValidChild(node.B, tree)) return false;
            bool a = BoundingBox.Intersects(node.A.Min, node.A.Max, min, max);
            bool b = BoundingBox.Intersects(node.B.Min, node.B.Max, min, max);
            if (a)
            {
                if (b)
                {
                    if (count == maximumPending) return false;
                    stack[count++] = node.B.Index;
                    peak = Math.Max(peak, count);
                }
                // This exact order matches Bepu 2.4 Tree_VolumeQuery. A different DFS order can
                // require a different pending stack even when it visits the same leaves.
                index = node.A.Index;
            }
            else if (b) index = node.B.Index;
            else
            {
                if (count == 0) break;
                index = stack[--count];
            }
        }
        nodes = visitedNodes;
        leaves = visitedLeaves;
        pending = peak;
        return true;
    }

    static bool ValidChild(in NodeChild child, in Tree tree) =>
        ValidBounds(child.Min, child.Max) && (child.Index < 0
            ? (uint)Tree.Encode(child.Index) < (uint)tree.LeafCount
            : (uint)child.Index < (uint)tree.NodeCount);

    static bool ValidBounds(Vector3 min, Vector3 max) =>
        float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(min.Z) &&
        float.IsFinite(max.X) && float.IsFinite(max.Y) && float.IsFinite(max.Z) &&
        min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
}
