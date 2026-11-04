using System;
using System.Collections.Generic;
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
            throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.EmbeddedBufferCountMismatch, "Skinned geometry requires exactly one embedded buffer.")
            {
                BufferCount = buffers.ValueKind == JsonValueKind.Array ? buffers.GetArrayLength() : null,
            };
        JsonElement skins = AttachmentGlbAccessors.Required(root, "skins", "skinned skins");
        if (skins.ValueKind != JsonValueKind.Array || skins.GetArrayLength() != 1 || skins[0].ValueKind != JsonValueKind.Object)
            throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.SkinCountMismatch, "Skinned geometry requires exactly one entry in skins.")
            {
                SkinCount = skins.ValueKind == JsonValueKind.Array ? skins.GetArrayLength() : null,
            };
        foreach (JsonElement _ in Array(root, "animations"))
            throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.AnimationPresent, "Skinned attachment animations are refused.");

        var accessors = new AttachmentGlbAccessors(root, container.Binary);
        int jointCount = ValidateSkin(root, skins[0], accessors);
        int nodeIndex = 0;
        foreach (JsonElement node in Array(root, "nodes"))
        {
            string context = $"node {nodeIndex++}";
            if (node.TryGetProperty("weights", out _))
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.MorphWeightsPresent, $"{context}: morph weights are refused.")
                {
                    JsonPath = AttachmentJsonPath.Property($"$.nodes[{nodeIndex - 1}]", "weights"),
                    ElementIndex = nodeIndex - 1,
                };
            if (!node.TryGetProperty("mesh", out JsonElement mesh)) continue;
            int meshIndex = AttachmentGlbAccessors.Index(mesh, context + " mesh", $"$.nodes[{nodeIndex - 1}].mesh");
            AttachmentGlbAccessors.Entry(root, "meshes", meshIndex, context);
            if (!node.TryGetProperty("skin", out JsonElement skin) || AttachmentGlbAccessors.Index(skin, context + " skin", $"$.nodes[{nodeIndex - 1}].skin") != 0)
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.MeshNodeSkinMismatch, $"{context}: each mesh node must reference skin 0.")
                {
                    NodeIndex = nodeIndex - 1,
                    SkinIndex = skin.ValueKind == JsonValueKind.Undefined ? null : skin.GetInt32(),
                };
        }

        int meshOrdinal = 0;
        foreach (JsonElement mesh in Array(root, "meshes"))
        {
            string context = $"mesh {meshOrdinal++}";
            if (mesh.TryGetProperty("weights", out _))
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.MorphWeightsPresent, $"{context}: morph weights are refused.")
                {
                    JsonPath = AttachmentJsonPath.Property($"$.meshes[{meshOrdinal - 1}]", "weights"),
                    ElementIndex = meshOrdinal - 1,
                };
            int primitiveIndex = 0;
            foreach (JsonElement primitive in Array(mesh, "primitives"))
            {
                int index = primitiveIndex++;
                ValidatePrimitive(primitive, context + $" primitive {index}", accessors, jointCount, $"$.meshes[{meshOrdinal - 1}].primitives[{index}]");
            }
        }
    });

    internal static int ValidateSkin(JsonElement root, JsonElement skin, AttachmentGlbAccessors accessors, int maxJoints = MaxSkinnedJoints)
    {
        const string context = "skin 0";
        JsonElement joints = AttachmentGlbAccessors.Required(skin, "joints", context, "$.skins[0]");
        if (joints.ValueKind != JsonValueKind.Array || joints.GetArrayLength() < 1 || joints.GetArrayLength() > maxJoints)
            throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.JointCountOutsideRange, $"{context}: joints must contain 1 to {maxJoints} entries.")
            {
                JsonPath = "$.skins[0].joints",
                JointCount = joints.ValueKind == JsonValueKind.Array ? joints.GetArrayLength() : null,
                MaximumJointCount = maxJoints,
            };
        var indices = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        int ordinal = 0;
        foreach (JsonElement joint in joints.EnumerateArray())
        {
            string jointContext = context + $" joint {ordinal++}";
            int index = AttachmentGlbAccessors.Index(joint, jointContext, $"$.skins[0].joints[{ordinal - 1}]");
            if (!indices.Add(index))
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.JointNodeDuplicate, $"{jointContext}: repeated node index {index}.")
                {
                    JointOrdinal = ordinal - 1,
                    NodeIndex = index,
                };
            JsonElement node = AttachmentGlbAccessors.Entry(root, "nodes", index, jointContext);
            if (!node.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.JointNameEmpty, $"{jointContext}: a non-empty joint name is required.")
                {
                    JointOrdinal = ordinal - 1,
                    NodeIndex = index,
                };
            if (!names.Add(name.GetString()!))
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.JointNameDuplicate, $"{jointContext}: repeated joint name '{name.GetString()}'.")
                {
                    JointOrdinal = ordinal - 1,
                    JointName = name.GetString(),
                };
        }
        AttachmentGlbHierarchy.Validate(root, joints, context);
        int count = joints.GetArrayLength();
        if (skin.TryGetProperty("inverseBindMatrices", out JsonElement reference))
        {
            var binds = accessors.Read(reference, context + " inverseBindMatrices", "MAT4", "$.skins[0].inverseBindMatrices");
            if (binds.Count != count)
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.InverseBindCountMismatch, $"{context}: inverseBindMatrices count must match joints.")
                {
                    BindCount = binds.Count,
                    JointCount = count,
                };
            Span<float> matrix = stackalloc float[16];
            for (int joint = 0; joint < count; joint++)
            {
                binds.ReadElement(joint, matrix);
                for (int component = 0; component < matrix.Length; component++)
                    if (!float.IsFinite(matrix[component]))
                        throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.InverseBindNonFinite, $"{context} inverseBindMatrices joint {joint}: matrix must be finite.")
                        {
                            JointOrdinal = joint,
                            ComponentIndex = component,
                        };
            }
        }
        return count;
    }

    static void ValidatePrimitive(JsonElement primitive, string context, AttachmentGlbAccessors accessors, int jointCount, string jsonPath)
    {
        if (primitive.TryGetProperty("targets", out _))
            throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.MorphTargetsPresent, $"{context}: morph targets are refused.")
            {
                JsonPath = AttachmentJsonPath.Property(jsonPath, "targets"),
            };
        JsonElement attributes = AttachmentGlbAccessors.Object(primitive, "attributes", context, jsonPath);
        foreach (JsonProperty attribute in attributes.EnumerateObject())
            if ((attribute.Name.StartsWith("JOINTS_", StringComparison.Ordinal) && attribute.Name != "JOINTS_0")
                || (attribute.Name.StartsWith("WEIGHTS_", StringComparison.Ordinal) && attribute.Name != "WEIGHTS_0"))
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.ExtraInfluenceAttribute, $"{context}: extra influence attribute {attribute.Name} is refused.")
                {
                    JsonPath = AttachmentJsonPath.Property(AttachmentJsonPath.Property(jsonPath, "attributes"), attribute.Name),
                    AttributeName = attribute.Name,
                };
        var positions = accessors.Read(AttachmentGlbAccessors.Required(attributes, "POSITION", context, jsonPath + ".attributes"), context + " POSITION", "VEC3", jsonPath + ".attributes.POSITION");
        var joints = accessors.Read(AttachmentGlbAccessors.Required(attributes, "JOINTS_0", context, jsonPath + ".attributes"), context + " JOINTS_0", "VEC4", jsonPath + ".attributes.JOINTS_0", joints: true);
        var weights = accessors.Read(AttachmentGlbAccessors.Required(attributes, "WEIGHTS_0", context, jsonPath + ".attributes"), context + " WEIGHTS_0", "VEC4", jsonPath + ".attributes.WEIGHTS_0", weights: true);
        if (positions.Count != joints.Count || positions.Count != weights.Count)
            throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.VertexAttributeCountMismatch, $"{context}: POSITION, JOINTS_0 and WEIGHTS_0 accessor counts must match.")
            {
                JsonPath = jsonPath,
                PositionCount = positions.Count,
                JointCount = joints.Count,
                WeightCount = weights.Count,
            };
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
                    throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.JointIndexOutsideSkin, $"{context} JOINTS_0 vertex {vertex} lane {lane}: index is outside skin 0 joint count {jointCount}.")
                    {
                        JsonPath = AttachmentJsonPath.Property(AttachmentJsonPath.Property(jsonPath, "attributes"), "JOINTS_0"),
                        VertexIndex = vertex,
                        Lane = lane,
                        Index = jointValues[lane],
                        JointCount = jointCount,
                    };
                float weight = weightValues[lane];
                if (!float.IsFinite(weight) || weight < 0)
                    throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.WeightInvalid, $"{context} WEIGHTS_0 vertex {vertex} lane {lane}: weight must be finite and non-negative.")
                    {
                        JsonPath = AttachmentJsonPath.Property(AttachmentJsonPath.Property(jsonPath, "attributes"), "WEIGHTS_0"),
                        VertexIndex = vertex,
                        Lane = lane,
                        Weight = weight,
                    };
                totalWeight += weight;
            }
            if (totalWeight == 0)
                throw new AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal.WeightsAllZero, $"{context} WEIGHTS_0 vertex {vertex}: all-zero weights are refused.")
                {
                    JsonPath = AttachmentJsonPath.Property(AttachmentJsonPath.Property(jsonPath, "attributes"), "WEIGHTS_0"),
                    VertexIndex = vertex,
                };
        }
    }
}
