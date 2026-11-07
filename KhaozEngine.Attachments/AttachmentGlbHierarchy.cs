using System;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class AttachmentGlbHierarchy
{
    // Public callers resolve joint references before this guard. Inject only the refused table shape.
    internal static void RejectNonArrayNodesForTests(JsonElement nodes) => RequireNodesArray(nodes, "skin 0");

    static void RequireNodesArray(JsonElement nodes, string context)
    {
        if (nodes.ValueKind != JsonValueKind.Array)
            throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.NodesNotArray, $"{context}: nodes must be an array.")
            {
                JsonPath = "$.nodes",
            };
    }

    internal static int[] Validate(JsonElement root, JsonElement joints, string context)
    {
        JsonElement nodes = AttachmentGlbAccessors.Required(root, "nodes", context);
        RequireNodesArray(nodes, context);
        int count = nodes.GetArrayLength();
        var parents = new int[count];
        Array.Fill(parents, -1);
        for (int index = 0; index < count; index++)
        {
            string nodeContext = context + $" node {index}";
            JsonElement node = nodes[index];
            if (node.ValueKind != JsonValueKind.Object)
                throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.NodeNotObject, $"{nodeContext}: node must be an object.")
                {
                    JsonPath = $"$.nodes[{index}]",
                    NodeIndex = index,
                };
            if (!node.TryGetProperty("children", out JsonElement children)) continue;
            if (children.ValueKind != JsonValueKind.Array)
                throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.ChildrenNotArray, $"{nodeContext}: children must be an array.")
                {
                    JsonPath = $"$.nodes[{index}].children",
                    NodeIndex = index,
                };
            int childOrdinal = 0;
            foreach (JsonElement child in children.EnumerateArray())
            {
                int childIndex = AttachmentGlbAccessors.Index(child, nodeContext + " children", $"$.nodes[{index}].children[{childOrdinal++}]");
                if (childIndex >= count)
                    throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.ChildIndexOutsideNodes, $"{nodeContext} children: invalid node reference {childIndex}.")
                    {
                        JsonPath = $"$.nodes[{index}].children",
                        NodeIndex = index,
                        ChildIndex = childIndex,
                        NodeCount = count,
                    };
                if (parents[childIndex] != -1)
                    throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.MultipleParents, $"{context} node {childIndex}: multiple parents or repeated child references are refused.")
                    {
                        JsonPath = $"$.nodes[{childIndex}]",
                        NodeIndex = childIndex,
                        ParentIndex = index,
                    };
                parents[childIndex] = index;
            }
        }

        // Resolve each tree root once, without recursion, before comparing the joints.
        var roots = new int[count];
        var states = new byte[count];
        for (int start = 0; start < count; start++)
        {
            if (states[start] != 0) continue;
            int node = start;
            int last = start;
            while (node >= 0 && states[node] == 0)
            {
                states[node] = 1;
                last = node;
                node = parents[node];
            }
            if (node >= 0 && states[node] == 1)
                throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.Cycle, $"{context} node {node}: node hierarchy contains a cycle.")
                {
                    JsonPath = $"$.nodes[{node}]",
                    NodeIndex = node,
                };
            int treeRoot = node < 0 ? last : roots[node];
            node = start;
            while (node >= 0 && states[node] == 1)
            {
                states[node] = 2;
                roots[node] = treeRoot;
                node = parents[node];
            }
        }

        int commonRoot = -1;
        foreach (JsonElement joint in joints.EnumerateArray())
        {
            int jointRoot = roots[AttachmentGlbAccessors.Index(joint, context + " joint", "$.skins[0].joints")];
            if (commonRoot >= 0 && commonRoot != jointRoot)
                throw new AttachmentHierarchyDataException(AttachmentHierarchyRefusal.JointsHaveDifferentRoots, $"{context}: joints must share a common root.")
                {
                    JsonPath = "$.skins[0].joints",
                    ExpectedRootIndex = commonRoot,
                    ActualRootIndex = jointRoot,
                };
            commonRoot = jointRoot;
        }
        return parents;
    }
}
