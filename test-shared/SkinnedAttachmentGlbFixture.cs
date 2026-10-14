using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Nodes;

namespace KhaozEngine.Tests.Attachments;

internal sealed class SkinnedAttachmentGlbFixture
{
    readonly List<byte> binary = new();

    internal JsonObject Root { get; }
    internal JsonArray Accessors => Root["accessors"]!.AsArray();
    internal JsonArray Views => Root["bufferViews"]!.AsArray();
    internal JsonArray Nodes => Root["nodes"]!.AsArray();
    internal JsonObject Skin => Root["skins"]![0]!.AsObject();
    internal JsonObject Mesh => Root["meshes"]![0]!.AsObject();
    internal JsonArray Primitives => Mesh["primitives"]!.AsArray();
    internal JsonObject MeshNode => Nodes[JointCount]!.AsObject();
    internal int JointCount { get; }
    internal int InfluencesPerVertex => 4;

    internal SkinnedAttachmentGlbFixture(int jointCount = 4, int jointComponentType = 5121,
        int weightComponentType = 5126, bool strided = false)
    {
        JointCount = jointCount;
        Root = JsonNode.Parse("""{"asset":{"version":"2.0"},"buffers":[{}],"bufferViews":[],"accessors":[],"skins":[{}],"nodes":[],"meshes":[{"primitives":[{"attributes":{"POSITION":0,"JOINTS_0":1,"WEIGHTS_0":2}}]}],"scenes":[{"nodes":[]}],"scene":0}""")!.AsObject();
        AddAccessor(3, "VEC3", 5126, false, strided);
        AddAccessor(3, "VEC4", jointComponentType, false, strided);
        AddAccessor(3, "VEC4", weightComponentType, weightComponentType != 5126, strided);
        AddAccessor(jointCount, "MAT4", 5126, false, false);
        var joints = new JsonArray();
        var jointChildren = new JsonArray();
        JsonArray sceneNodes = Root["scenes"]![0]!["nodes"]!.AsArray();
        for (int i = 0; i < jointCount; i++)
        {
            Nodes.Add(new JsonObject { ["name"] = "joint-" + i });
            joints.Add(i);
            if (i > 0) jointChildren.Add(i);
            SetInverseBind(i, Matrix4x4.Identity);
        }
        if (jointCount > 1) Nodes[0]!["children"] = jointChildren;
        if (jointCount > 0) sceneNodes.Add(0);
        Skin["joints"] = joints;
        Skin["inverseBindMatrices"] = 3;
        Nodes.Add(new JsonObject { ["name"] = "garment", ["mesh"] = 0, ["skin"] = 0 });
        sceneNodes.Add(jointCount);
        for (int vertex = 0; vertex < 3; vertex++)
        {
            SetElement(0, vertex, vertex == 1 ? 1 : 0, vertex == 2 ? 1 : 0, 0);
            SetElement(1, vertex, 0, 1, 2, 3);
            SetElement(2, vertex, .25f, .25f, .25f, .25f);
        }
        Accessors[0]!["min"] = new JsonArray(0, 0, 0);
        Accessors[0]!["max"] = new JsonArray(1, 1, 0);
    }

    void AddAccessor(int count, string type, int componentType, bool normalized, bool strided)
    {
        int components = type == "MAT4" ? 16 : type == "VEC3" ? 3 : 4;
        int size = components * ComponentSize(componentType);
        int stride = strided ? ((size + 3) & ~3) + 4 : size;
        int view = AddView(new byte[count * stride], strided ? stride : null);
        var accessor = new JsonObject
        {
            ["bufferView"] = view, ["componentType"] = componentType,
            ["count"] = count, ["type"] = type,
        };
        if (normalized) accessor["normalized"] = true;
        Accessors.Add(accessor);
    }

    internal int AddView(byte[] bytes, int? stride = null)
    {
        while (binary.Count % 4 != 0) binary.Add(0);
        var view = new JsonObject { ["buffer"] = 0, ["byteOffset"] = binary.Count, ["byteLength"] = bytes.Length };
        if (stride.HasValue) view["byteStride"] = stride.Value;
        Views.Add(view);
        binary.AddRange(bytes);
        return Views.Count - 1;
    }

    internal void SetElement(int accessorIndex, int element, params float[] values)
    {
        JsonNode accessor = Accessors[accessorIndex]!;
        JsonNode view = Views[accessor["bufferView"]!.GetValue<int>()]!;
        int componentType = accessor["componentType"]!.GetValue<int>();
        int size = ComponentSize(componentType);
        int offset = view["byteOffset"]!.GetValue<int>() + (accessor["byteOffset"]?.GetValue<int>() ?? 0)
            + element * (view["byteStride"]?.GetValue<int>() ?? values.Length * size);
        bool normalized = accessor["normalized"]?.GetValue<bool>() ?? false;
        byte[] encoded = Encode(values, componentType, normalized);
        for (int i = 0; i < encoded.Length; i++) binary[offset + i] = encoded[i];
    }

    internal void SetInverseBind(int joint, Matrix4x4 matrix) => SetElement(3, joint,
        matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44);

    internal void SetSparse(int accessorIndex, int[] indices, float[][] values, bool omitBase = false)
    {
        JsonNode accessor = Accessors[accessorIndex]!;
        int componentType = accessor["componentType"]!.GetValue<int>();
        bool normalized = accessor["normalized"]?.GetValue<bool>() ?? false;
        byte[] indexBytes = new byte[indices.Length * 2];
        var valueBytes = new List<byte>();
        for (int i = 0; i < indices.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(indexBytes.AsSpan(i * 2), checked((ushort)indices[i]));
            valueBytes.AddRange(Encode(values[i], componentType, normalized));
        }
        accessor["sparse"] = new JsonObject
        {
            ["count"] = indices.Length,
            ["indices"] = new JsonObject { ["bufferView"] = AddView(indexBytes), ["componentType"] = 5123 },
            ["values"] = new JsonObject { ["bufferView"] = AddView(valueBytes.ToArray()) },
        };
        if (omitBase) accessor.AsObject().Remove("bufferView");
    }

    static int ComponentSize(int type) => type is 5120 or 5121 ? 1 : type is 5122 or 5123 ? 2 : 4;

    static byte[] Encode(float[] values, int type, bool normalized)
    {
        int size = ComponentSize(type);
        byte[] bytes = new byte[values.Length * size];
        for (int i = 0; i < values.Length; i++)
        {
            Span<byte> target = bytes.AsSpan(i * size, size);
            if (type == 5126) BinaryPrimitives.WriteSingleLittleEndian(target, values[i]);
            else if (size == 1) target[0] = checked((byte)(normalized ? MathF.Round(values[i] * 255) : values[i]));
            else BinaryPrimitives.WriteUInt16LittleEndian(target, checked((ushort)(normalized ? MathF.Round(values[i] * 65535) : values[i])));
        }
        return bytes;
    }

    internal byte[] Build(Func<string, string>? editJson = null)
    {
        if (Root["buffers"] is JsonArray buffers && buffers.Count > 0 && buffers[0] is JsonObject buffer)
            buffer["byteLength"] = binary.Count;
        string json = Root.ToJsonString();
        return Container(editJson is null ? json : editJson(json), binary.ToArray());
    }

    internal static byte[] Container(string json, byte[]? binary = null)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(json);
        int jsonLength = (utf8.Length + 3) & ~3;
        int binLength = binary is null ? 0 : (binary.Length + 3) & ~3;
        byte[] glb = new byte[20 + jsonLength + (binary is null ? 0 : 8 + binLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(glb, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(16), 0x4e4f534a);
        utf8.CopyTo(glb, 20);
        glb.AsSpan(20 + utf8.Length, jsonLength - utf8.Length).Fill(0x20);
        if (binary is not null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(20 + jsonLength), (uint)binLength);
            BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(24 + jsonLength), 0x004e4942);
            binary.CopyTo(glb, 28 + jsonLength);
        }
        return glb;
    }
}
