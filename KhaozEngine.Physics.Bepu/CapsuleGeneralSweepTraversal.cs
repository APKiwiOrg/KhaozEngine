using System;
using System.Numerics;
using BepuPhysics.Trees;
using BepuUtilities;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Structural A-first preflight for the general-only aperture. Its paired collector filters
/// identity and unselected leaves before charging its separate general callback budget.</summary>
internal static unsafe class CapsuleGeneralSweepTraversal
{
    internal static bool TryValidate(in Tree tree, Vector3 min, Vector3 max)
    {
        if (!ValidBounds(min, max) || tree.NodeCount < 0 || tree.LeafCount < 0 || tree.NodeCount > tree.Nodes.Length ||
            tree.LeafCount > tree.Leaves.Length) return false;
        if (tree.LeafCount == 0) return true;
        if (tree.NodeCount == 0 || !tree.Nodes.Allocated || !tree.Leaves.Allocated) return false;
        Span<int> stack = stackalloc int[CapsuleTreePreflight.MaximumPending];
        int index = 0, count = 0, nodes = 0;
        while (true)
        {
            if (index < 0)
            {
                int leaf = Tree.Encode(index);
                if ((uint)leaf >= (uint)tree.LeafCount) return false;
                if (count == 0) break;
                index = stack[--count];
                continue;
            }
            if ((uint)index >= (uint)tree.NodeCount || nodes == CapsuleTreePreflight.MaximumNodes) return false;
            nodes++;
            ref readonly Node node = ref tree.Nodes[index];
            if (!Valid(node.A, tree)) return false;
            bool a = BoundingBox.Intersects(min, max, node.A.Min, node.A.Max), b = false;
            if (tree.LeafCount > 1)
            {
                if (!Valid(node.B, tree)) return false;
                b = BoundingBox.Intersects(min, max, node.B.Min, node.B.Max);
            }
            else if (node.A.Index >= 0) return false;
            if (a)
            {
                if (b)
                {
                    if (count == CapsuleTreePreflight.MaximumPending) return false;
                    stack[count++] = node.B.Index;
                }
                index = node.A.Index;
            }
            else if (b) index = node.B.Index;
            else
            {
                if (count == 0) break;
                index = stack[--count];
            }
        }
        return true;
    }

    static bool Valid(in NodeChild child, in Tree tree) => ValidBounds(child.Min, child.Max) &&
        (child.Index < 0 ? (uint)Tree.Encode(child.Index) < (uint)tree.LeafCount : (uint)child.Index < (uint)tree.NodeCount);

    static bool ValidBounds(Vector3 min, Vector3 max) =>
        float.IsFinite(min.X) && float.IsFinite(min.Y) && float.IsFinite(min.Z) &&
        float.IsFinite(max.X) && float.IsFinite(max.Y) && float.IsFinite(max.Z) &&
        min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;
}
