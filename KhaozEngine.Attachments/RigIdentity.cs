using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace KhaozEngine.Attachments;

internal static class RigIdentity
{
    internal static string Compute(JsonElement root, ReadOnlyMemory<byte> binary)
    {
        JsonElement buffers = AttachmentGlbAccessors.Required(root, "buffers", "rig");
        JsonElement skins = AttachmentGlbAccessors.Required(root, "skins", "rig");
        if (buffers.ValueKind != JsonValueKind.Array || buffers.GetArrayLength() != 1)
            throw new RigIdentityDataException(RigIdentityRefusal.EmbeddedBufferCountMismatch, "Rig identity requires exactly one embedded buffer.")
            {
                BufferCount = buffers.ValueKind == JsonValueKind.Array ? buffers.GetArrayLength() : null,
            };
        if (skins.ValueKind != JsonValueKind.Array || skins.GetArrayLength() != 1 || skins[0].ValueKind != JsonValueKind.Object)
            throw new RigIdentityDataException(RigIdentityRefusal.SkinCountMismatch, "Rig identity requires exactly one skin.")
            {
                SkinCount = skins.ValueKind == JsonValueKind.Array ? skins.GetArrayLength() : null,
            };
        var accessors = new AttachmentGlbAccessors(root, binary, refuseCompression: true);
        JsonElement skin = skins[0];
        int jointCount = AttachmentGlb.ValidateSkin(root, skin, accessors, int.MaxValue);
        JsonElement joints = skin.GetProperty("joints");
        JsonElement nodes = root.GetProperty("nodes");
        int[] parents = AttachmentGlbHierarchy.Validate(root, joints, "rig");
        var graph = new List<int>();
        var ids = new Dictionary<int, int>();
        foreach (JsonElement joint in joints.EnumerateArray()) Include(joint.GetInt32(), parents, graph, ids);
        var meshes = new List<int>();
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            JsonElement node = nodes[i];
            if (!node.TryGetProperty("skin", out JsonElement reference)) continue;
            if (AttachmentGlbAccessors.Index(reference, "node skin", $"$.nodes[{i}].skin") != 0)
                throw new RigIdentityDataException(RigIdentityRefusal.MeshNodeSkinMismatch, "Skinned nodes must reference skin 0.")
                {
                    NodeIndex = i,
                    SkinIndex = reference.GetInt32(),
                };
            if (!node.TryGetProperty("mesh", out _))
                throw new RigIdentityDataException(RigIdentityRefusal.MeshNodeHasNoMesh, "A skinned node must reference a mesh.") { NodeIndex = i };
            if (node.TryGetProperty("weights", out _))
                throw new RigIdentityDataException(RigIdentityRefusal.MorphWeightsUnsupported, "Morph weights are not supported by rig-v1.")
                {
                    JsonPath = AttachmentJsonPath.Property($"$.nodes[{i}]", "weights"),
                    ElementIndex = i,
                };
            Include(i, parents, graph, ids);
            meshes.Add(i);
        }
        if (meshes.Count == 0) throw new RigIdentityDataException(RigIdentityRefusal.SkinnedGeometryMissing, "Rig identity requires skinned mesh geometry.");

        using var writer = new RigIdentityWriter();
        writer.Text("KhaozEngine.Attachments/rig-v1");
        writer.Integer(jointCount);
        foreach (JsonElement joint in joints.EnumerateArray())
        {
            int index = joint.GetInt32();
            writer.Text(nodes[index].GetProperty("name").GetString()!);
            writer.Integer(ids[index]);
        }
        writer.Integer(graph.Count);
        foreach (int index in graph)
        {
            writer.Integer(parents[index] < 0 ? -1 : ids[parents[index]]);
            writer.Transform(nodes[index], index);
        }
        if (skin.TryGetProperty("inverseBindMatrices", out JsonElement binds))
            writer.Accessor(accessors.Read(binds, "inverseBindMatrices", "MAT4", "$.skins[0].inverseBindMatrices"), "MAT4");
        else writer.DefaultBinds(jointCount);
        writer.Integer(meshes.Count);
        foreach (int index in meshes)
        {
            writer.Integer(ids[index]);
            int meshIndex = AttachmentGlbAccessors.Index(nodes[index].GetProperty("mesh"), "mesh", $"$.nodes[{index}].mesh");
            JsonElement mesh = AttachmentGlbAccessors.Entry(root, "meshes", meshIndex, "rig");
            string meshPath = $"$.meshes[{meshIndex}]";
            if (mesh.TryGetProperty("weights", out _))
                throw new RigIdentityDataException(RigIdentityRefusal.MorphWeightsUnsupported, "Morph weights are not supported by rig-v1.")
                {
                    JsonPath = AttachmentJsonPath.Property(meshPath, "weights"),
                    ElementIndex = meshIndex,
                };
            JsonElement primitives = AttachmentGlbAccessors.Required(mesh, "primitives", "mesh", meshPath);
            if (primitives.ValueKind != JsonValueKind.Array || primitives.GetArrayLength() == 0)
                throw new RigIdentityDataException(RigIdentityRefusal.PrimitivesEmpty, "A skinned mesh requires primitives.")
                {
                    MeshIndex = meshIndex,
                    PrimitiveCount = primitives.ValueKind == JsonValueKind.Array ? primitives.GetArrayLength() : null,
                };
            writer.Integer(primitives.GetArrayLength());
            int primitiveIndex = 0;
            foreach (JsonElement primitive in primitives.EnumerateArray())
            {
                WritePrimitive(primitive, accessors, jointCount, writer, meshPath + $".primitives[{primitiveIndex}]", meshIndex, primitiveIndex);
                primitiveIndex++;
            }
        }
        return writer.Finish();
    }

    static void Include(int index, int[] parents, List<int> graph, Dictionary<int, int> ids)
    {
        var path = new List<int>();
        while (index >= 0 && !ids.ContainsKey(index))
        {
            path.Add(index);
            index = parents[index];
        }
        for (int i = path.Count - 1; i >= 0; i--)
        {
            ids.Add(path[i], graph.Count);
            graph.Add(path[i]);
        }
    }

    static void WritePrimitive(JsonElement primitive, AttachmentGlbAccessors accessors, int jointCount, RigIdentityWriter writer, string jsonPath, int meshIndex, int primitiveIndex)
    {
        if (primitive.ValueKind != JsonValueKind.Object || primitive.TryGetProperty("targets", out _))
            throw new RigIdentityDataException(RigIdentityRefusal.PrimitiveShapeOrMorphUnsupported, "Morph targets or malformed primitives are not supported by rig-v1.")
            {
                MeshIndex = meshIndex,
                PrimitiveIndex = primitiveIndex,
            };
        if (primitive.TryGetProperty("extensions", out JsonElement extensions) && extensions.TryGetProperty("KHR_draco_mesh_compression", out _))
            throw new RigIdentityDataException(RigIdentityRefusal.CompressionUnsupported, "Compressed geometry is not supported by rig-v1.")
            {
                PrimitiveIndex = primitiveIndex,
                ExtensionName = "KHR_draco_mesh_compression",
            };
        int mode = AttachmentGlbAccessors.Integer(primitive, "mode", "primitive", 4, jsonPath);
        if (mode > 6) throw new RigIdentityDataException(RigIdentityRefusal.PrimitiveModeInvalid, "Invalid primitive mode.")
        {
            PrimitiveIndex = primitiveIndex,
            Mode = mode,
        };
        writer.Integer(mode);
        JsonElement attributes = AttachmentGlbAccessors.Object(primitive, "attributes", "primitive", jsonPath);
        var positions = accessors.Read(AttachmentGlbAccessors.Required(attributes, "POSITION", "primitive", jsonPath + ".attributes"), "POSITION", "VEC3", jsonPath + ".attributes.POSITION");
        Finite(positions, 3, AttachmentJsonPath.Property(jsonPath + ".attributes", "POSITION"));
        writer.Accessor(positions, "VEC3");
        bool hasNormals = attributes.TryGetProperty("NORMAL", out JsonElement normal);
        writer.Integer(hasNormals ? 1 : 0);
        if (hasNormals)
        {
            var normals = accessors.Read(normal, "NORMAL", "VEC3", jsonPath + ".attributes.NORMAL");
            SameCount(normals, positions.Count);
            Finite(normals, 3, AttachmentJsonPath.Property(jsonPath + ".attributes", "NORMAL"));
            writer.Accessor(normals, "VEC3");
        }
        var sets = new SortedSet<int>();
        foreach (JsonProperty attribute in attributes.EnumerateObject())
        {
            string name = attribute.Name;
            if (!name.StartsWith("JOINTS_", StringComparison.Ordinal) && !name.StartsWith("WEIGHTS_", StringComparison.Ordinal)) continue;
            string suffix = name[(name.IndexOf('_') + 1)..];
            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out int set) || suffix != set.ToString(CultureInfo.InvariantCulture))
                throw new RigIdentityDataException(RigIdentityRefusal.InfluenceAttributeMalformed, "Malformed skin influence attribute.")
                {
                    PrimitiveIndex = primitiveIndex,
                    AttributeName = name,
                };
            sets.Add(set);
        }
        if (!sets.Contains(0)) throw new RigIdentityDataException(RigIdentityRefusal.FirstInfluenceSetMissing, "Skinned primitives require JOINTS_0 and WEIGHTS_0.")
        {
            PrimitiveIndex = primitiveIndex,
        };
        writer.Integer(sets.Count);
        var totalWeights = new double[positions.Count];
        foreach (int set in sets)
        {
            string suffix = set.ToString(CultureInfo.InvariantCulture);
            var joints = accessors.Read(AttachmentGlbAccessors.Required(attributes, "JOINTS_" + suffix, "primitive", jsonPath + ".attributes"), "JOINTS_" + suffix, "VEC4", jsonPath + ".attributes.JOINTS_" + suffix, joints: true);
            var weights = accessors.Read(AttachmentGlbAccessors.Required(attributes, "WEIGHTS_" + suffix, "primitive", jsonPath + ".attributes"), "WEIGHTS_" + suffix, "VEC4", jsonPath + ".attributes.WEIGHTS_" + suffix, weights: true);
            SameCount(joints, positions.Count);
            SameCount(weights, positions.Count);
            ValidateInfluences(joints, weights, jointCount, totalWeights);
            writer.Integer(set);
            writer.Accessor(joints, "VEC4");
            writer.Accessor(weights, "VEC4");
        }
        for (int vertex = 0; vertex < totalWeights.Length; vertex++)
            if (totalWeights[vertex] == 0) throw new RigIdentityDataException(RigIdentityRefusal.WeightsAllZero, "All-zero skin weights are refused.")
            {
                PrimitiveIndex = primitiveIndex,
                VertexIndex = vertex,
            };
        bool hasIndices = primitive.TryGetProperty("indices", out JsonElement indices);
        writer.Integer(hasIndices ? 1 : 0);
        if (hasIndices)
        {
            var values = accessors.Read(indices, "indices", "SCALAR", jsonPath + ".indices", scalarIndices: true);
            for (int i = 0; i < values.Count; i++)
                if (values.UnsignedElement(i) >= positions.Count) throw new RigIdentityDataException(RigIdentityRefusal.VertexIndexOutsidePositionCount, "Vertex index is outside POSITION count.")
                {
                    PrimitiveIndex = primitiveIndex,
                    Index = values.UnsignedElement(i),
                    VertexCount = positions.Count,
                };
            writer.Accessor(values, "SCALAR");
        }
    }

    static void ValidateInfluences(AttachmentGlbAccessors.Accessor joints, AttachmentGlbAccessors.Accessor weights, int jointCount, double[] totals)
    {
        Span<float> joint = stackalloc float[4];
        Span<float> weight = stackalloc float[4];
        for (int i = 0; i < totals.Length; i++)
        {
            joints.ReadElement(i, joint);
            weights.ReadElement(i, weight);
            for (int lane = 0; lane < 4; lane++)
            {
                if (joint[lane] >= jointCount || !float.IsFinite(weight[lane]) || weight[lane] < 0)
                    throw new RigIdentityDataException(RigIdentityRefusal.JointIndexOrWeightInvalid, "Invalid skin joint index or weight.")
                    {
                        VertexIndex = i,
                        Lane = lane,
                        JointIndex = joint[lane],
                        Weight = weight[lane],
                        JointCount = jointCount,
                    };
                totals[i] += weight[lane];
            }
        }
    }

    static void SameCount(AttachmentGlbAccessors.Accessor accessor, int count)
    {
        if (accessor.Count != count) throw new RigIdentityDataException(RigIdentityRefusal.VertexAccessorCountMismatch, "Skin vertex accessor counts must match POSITION.")
        {
            ActualCount = accessor.Count,
            PositionCount = count,
        };
    }

    static void Finite(AttachmentGlbAccessors.Accessor accessor, int components, string jsonPath)
    {
        Span<float> values = stackalloc float[16];
        for (int i = 0; i < accessor.Count; i++)
        {
            accessor.ReadElement(i, values);
            for (int lane = 0; lane < components; lane++)
                if (!float.IsFinite(values[lane])) throw new RigIdentityDataException(RigIdentityRefusal.FitValueNonFinite, "Fit accessor values must be finite.")
                {
                    JsonPath = jsonPath,
                    VertexIndex = i,
                    ComponentIndex = lane,
                };
        }
    }

}
