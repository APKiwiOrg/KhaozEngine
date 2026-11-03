using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static partial class AttachmentGlb
{
    internal const int MaxSkinnedJoints = 128;

    public static void ValidateSkinnedEmbedded(ReadOnlyMemory<byte> glb, string sourceName) => Validate(glb, sourceName, container =>
    {
        JsonElement root = container.Root;
        JsonElement buffers = AttachmentGlbAccessors.Required(root, "buffers", "skinned buffer");
        if (buffers.ValueKind != JsonValueKind.Array || buffers.GetArrayLength() != 1)
            throw new InvalidDataException("Skinned geometry requires exactly one embedded buffer.");
        JsonElement skins = AttachmentGlbAccessors.Required(root, "skins", "skinned skins");
        if (skins.ValueKind != JsonValueKind.Array || skins.GetArrayLength() != 1 || skins[0].ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Skinned geometry requires exactly one entry in skins.");
        foreach (JsonElement _ in Array(root, "animations"))
            throw new InvalidDataException("Skinned attachment animations are refused.");

        var accessors = new AttachmentGlbAccessors(root, container.Binary);
        int jointCount = ValidateSkin(root, skins[0], accessors);
        int nodeIndex = 0;
        foreach (JsonElement node in Array(root, "nodes"))
        {
            string context = $"node {nodeIndex++}";
            if (node.TryGetProperty("weights", out _))
                throw new InvalidDataException($"{context}: morph weights are refused.");
            if (!node.TryGetProperty("mesh", out JsonElement mesh)) continue;
            int meshIndex = AttachmentGlbAccessors.Index(mesh, context + " mesh");
            AttachmentGlbAccessors.Entry(root, "meshes", meshIndex, context);
            if (!node.TryGetProperty("skin", out JsonElement skin) || AttachmentGlbAccessors.Index(skin, context + " skin") != 0)
                throw new InvalidDataException($"{context}: each mesh node must reference skin 0.");
        }

        int meshOrdinal = 0;
        foreach (JsonElement mesh in Array(root, "meshes"))
        {
            string context = $"mesh {meshOrdinal++}";
            if (mesh.TryGetProperty("weights", out _))
                throw new InvalidDataException($"{context}: morph weights are refused.");
            int primitiveIndex = 0;
            foreach (JsonElement primitive in Array(mesh, "primitives"))
                ValidatePrimitive(primitive, context + $" primitive {primitiveIndex++}", accessors, jointCount);
        }
    });

    internal static int ValidateSkin(JsonElement root, JsonElement skin, AttachmentGlbAccessors accessors, int maxJoints = MaxSkinnedJoints)
    {
        const string context = "skin 0";
        JsonElement joints = AttachmentGlbAccessors.Required(skin, "joints", context);
        if (joints.ValueKind != JsonValueKind.Array || joints.GetArrayLength() < 1 || joints.GetArrayLength() > maxJoints)
            throw new InvalidDataException($"{context}: joints must contain 1 to {maxJoints} entries.");
        var indices = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int ordinal = 0;
        foreach (JsonElement joint in joints.EnumerateArray())
        {
            string jointContext = context + $" joint {ordinal++}";
            int index = AttachmentGlbAccessors.Index(joint, jointContext);
            if (!indices.Add(index))
                throw new InvalidDataException($"{jointContext}: repeated node index {index}.");
            JsonElement node = AttachmentGlbAccessors.Entry(root, "nodes", index, jointContext);
            if (!node.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidDataException($"{jointContext}: a non-empty joint name is required.");
            if (!names.Add(name.GetString()!))
                throw new InvalidDataException($"{jointContext}: repeated joint name '{name.GetString()}'.");
        }
        AttachmentGlbHierarchy.Validate(root, joints, context);
        int count = joints.GetArrayLength();
        if (skin.TryGetProperty("inverseBindMatrices", out JsonElement reference))
        {
            var binds = accessors.Read(reference, context + " inverseBindMatrices", "MAT4");
            if (binds.Count != count)
                throw new InvalidDataException($"{context}: inverseBindMatrices count must match joints.");
            Span<float> matrix = stackalloc float[16];
            for (int joint = 0; joint < count; joint++)
            {
                binds.ReadElement(joint, matrix);
                foreach (float element in matrix)
                    if (!float.IsFinite(element))
                        throw new InvalidDataException($"{context} inverseBindMatrices joint {joint}: matrix must be finite.");
            }
        }
        return count;
    }

    static void ValidatePrimitive(JsonElement primitive, string context, AttachmentGlbAccessors accessors, int jointCount)
    {
        if (primitive.TryGetProperty("targets", out _))
            throw new InvalidDataException($"{context}: morph targets are refused.");
        JsonElement attributes = AttachmentGlbAccessors.Object(primitive, "attributes", context);
        foreach (JsonProperty attribute in attributes.EnumerateObject())
            if ((attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) && attribute.Name != "JOINTS_0")
                || (attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal) && attribute.Name != "WEIGHTS_0"))
                throw new InvalidDataException($"{context}: extra influence attribute {attribute.Name} is refused.");
        var positions = accessors.Read(AttachmentGlbAccessors.Required(attributes, "POSITION", context), context + " POSITION", "VEC3");
        var joints = accessors.Read(AttachmentGlbAccessors.Required(attributes, "JOINTS_0", context), context + " JOINTS_0", "VEC4", joints: true);
        var weights = accessors.Read(AttachmentGlbAccessors.Required(attributes, "WEIGHTS_0", context), context + " WEIGHTS_0", "VEC4", weights: true);
        if (positions.Count != joints.Count || positions.Count != weights.Count)
            throw new InvalidDataException($"{context}: POSITION, JOINTS_0 and WEIGHTS_0 accessor counts must match.");
        Span<float> jointValues = stackalloc float[4];
        Span<float> weightValues = stackalloc float[4];
        for (int vertex = 0; vertex < positions.Count; vertex++)
        {
            joints.ReadElement(vertex, jointValues);
            weights.ReadElement(vertex, weightValues);
            double totalWeight = 0;
            for (int lane = 0; lane < 4; lane++)
            {
                if (jointValues[lane] >= jointCount)
                    throw new InvalidDataException($"{context} JOINTS_0 vertex {vertex} lane {lane}: index is outside skin 0 joint count {jointCount}.");
                float weight = weightValues[lane];
                if (!float.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException($"{context} WEIGHTS_0 vertex {vertex} lane {lane}: weight must be finite and non-negative.");
                totalWeight += weight;
            }
            if (totalWeight == 0)
                throw new InvalidDataException($"{context} WEIGHTS_0 vertex {vertex}: all-zero weights are refused.");
        }
    }
}
