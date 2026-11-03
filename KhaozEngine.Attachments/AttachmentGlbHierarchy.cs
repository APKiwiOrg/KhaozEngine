using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class AttachmentGlbHierarchy
{
    internal static int[] Validate(JsonElement root, JsonElement joints, string context)
    {
        JsonElement nodes = AttachmentGlbAccessors.Required(root, "nodes", context);
        if (nodes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context}: nodes must be an array.");
        int count = nodes.GetArrayLength();
        var parents = new int[count];
        Array.Fill(parents, -1);
        for (int index = 0; index < count; index++)
        {
            string nodeContext = context + $" node {index}";
            JsonElement node = nodes[index];
            if (node.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{nodeContext}: node must be an object.");
            if (!node.TryGetProperty("children", out JsonElement children)) continue;
            if (children.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"{nodeContext}: children must be an array.");
            foreach (JsonElement child in children.EnumerateArray())
            {
                int childIndex = AttachmentGlbAccessors.Index(child, nodeContext + " children");
                if (childIndex >= count)
                    throw new InvalidDataException($"{nodeContext} children: invalid node reference {childIndex}.");
                if (parents[childIndex] != -1)
                    throw new InvalidDataException($"{context} node {childIndex}: multiple parents or repeated child references are refused.");
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
                throw new InvalidDataException($"{context} node {node}: node hierarchy contains a cycle.");
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
            int jointRoot = roots[AttachmentGlbAccessors.Index(joint, context + " joint")];
            if (commonRoot >= 0 && commonRoot != jointRoot)
                throw new InvalidDataException($"{context}: joints must share a common root.");
            commonRoot = jointRoot;
        }
        return parents;
    }
}
