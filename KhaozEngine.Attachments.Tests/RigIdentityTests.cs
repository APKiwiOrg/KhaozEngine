using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class RigIdentityTests
{
    [Fact]
    public void Identity_has_a_versioned_lowercase_digest()
    {
        string identity = Identity(new());
        Assert.Matches("^rig-v1:[0-9a-f]{64}$", identity);
        // Derived independently from the documented 896-byte v1 stream.
        Assert.Equal("rig-v1:c39911ee25aff59ef5cf102208354e8b53c6431f0d50aefb6b9f395e8403ccfa", identity);
        Assert.Equal(identity, Identity(new()));
    }

    [Fact]
    public void Adding_removing_and_retiming_animation_does_not_change_fit()
    {
        var body = new SkinnedAttachmentGlbFixture();
        string expected = Identity(body);
        int times = AddAccessor(body, "SCALAR", 5126, 2, Floats(0, 1));
        int values = AddAccessor(body, "VEC3", 5126, 2, Floats(0, 0, 0, 0, 2, 0));
        body.Root["animations"] = new JsonArray(new JsonObject
        {
            ["samplers"] = new JsonArray(new JsonObject { ["input"] = times, ["output"] = values }),
            ["channels"] = new JsonArray(new JsonObject
            {
                ["sampler"] = 0,
                ["target"] = new JsonObject { ["node"] = 1, ["path"] = "translation" },
            }),
        });
        Assert.Equal(expected, Identity(body));
        body.SetElement(times, 1, 9);
        body.SetElement(values, 1, 20, 30, 40);
        body.Root["animations"]![0]!["samplers"]![0]!["interpolation"] = "STEP";
        Assert.Equal(expected, Identity(body));
        Assert.Equal(expected, AttachmentGlb.ComputeRigIdentity(Relocate(body.Build()), "relocated.glb"));
        body.Root.Remove("animations");
        Assert.Equal(expected, Identity(body));
    }

    [Fact]
    public void Appearance_unused_geometry_and_unrelated_nodes_are_ignored()
    {
        var body = new SkinnedAttachmentGlbFixture();
        string expected = Identity(body);
        body.Root["images"] = new JsonArray(new JsonObject { ["uri"] = "never-read.png" });
        body.Root["materials"] = new JsonArray(new JsonObject { ["doubleSided"] = true });
        body.Primitives[0]!["material"] = 0;
        Attributes(body)["TEXCOORD_0"] = 1000;
        Attributes(body)["COLOR_0"] = 1001;
        body.Accessors[0]!["min"] = new JsonArray(-100, -100, -100);
        body.Nodes.Add(new JsonObject { ["name"] = "decoration", ["translation"] = new JsonArray(99, 99, 99) });
        body.Root["meshes"]!.AsArray().Add(new JsonObject());
        body.Root["extras"] = new JsonObject { ["note"] = "not fit data" };
        body.AddView(new byte[] { 99, 98, 97, 96 });
        Assert.Equal(expected, Identity(body));
    }

    [Fact]
    public void Required_quantization_of_only_texture_coordinates_does_not_change_fit()
    {
        var body = new SkinnedAttachmentGlbFixture();
        string before = Identity(body);
        int uv = AddAccessor(body, "VEC2", 5122, 3, new byte[] { 0, 0, 0, 0, 255, 127, 0, 0, 0, 0, 255, 127 });
        body.Accessors[uv]!["normalized"] = true;
        Attributes(body)["TEXCOORD_0"] = uv;
        body.Root["extensionsUsed"] = new JsonArray("KHR_mesh_quantization");
        body.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        Assert.Equal(before, Identity(body));
    }

    [Fact]
    public void Quantized_fit_positions_are_still_refused()
    {
        var body = new SkinnedAttachmentGlbFixture();
        body.Accessors[0]!["componentType"] = 5122;
        body.Root["extensionsUsed"] = new JsonArray("KHR_mesh_quantization");
        body.Root["extensionsRequired"] = new JsonArray("KHR_mesh_quantization");
        Assert.Throws<InvalidDataException>(() => Identity(body));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("order")]
    [InlineData("parent")]
    [InlineData("translation")]
    [InlineData("rotation")]
    [InlineData("scale")]
    [InlineData("matrix")]
    [InlineData("ancestor")]
    [InlineData("mesh-frame")]
    [InlineData("bind")]
    [InlineData("position")]
    [InlineData("normal")]
    [InlineData("joints")]
    [InlineData("weights")]
    [InlineData("second-joints")]
    [InlineData("second-weights")]
    [InlineData("indices")]
    [InlineData("topology")]
    [InlineData("later-primitive")]
    [InlineData("later-mesh")]
    public void Each_fit_input_changes_identity(string change)
    {
        var body = DetailedBody();
        string before = Identity(body);
        switch (change)
        {
            case "name": body.Nodes[1]!["name"] = "renamed"; break;
            case "order": body.SetSkinJoints(0, 2, 1, 3); break;
            case "parent":
                body.Nodes[0]!["children"] = new JsonArray(1, 3);
                body.Nodes[1]!["children"] = new JsonArray(2);
                break;
            case "translation": body.Nodes[1]!["translation"] = new JsonArray(1, 2, 3); break;
            case "rotation": body.Nodes[1]!["rotation"] = new JsonArray(0, 1, 0, 0); break;
            case "scale": body.Nodes[1]!["scale"] = new JsonArray(1, 2, 1); break;
            case "matrix": body.Nodes[1]!["matrix"] = Matrix(2); break;
            case "ancestor":
                body.Nodes.Add(new JsonObject { ["children"] = new JsonArray(0), ["translation"] = new JsonArray(0, 2, 0) });
                break;
            case "mesh-frame": body.MeshNode["translation"] = new JsonArray(0, 2, 0); break;
            case "bind": body.SetInverseBind(1, Matrix4x4.CreateTranslation(0, 2, 0)); break;
            case "position": body.SetElement(0, 1, 2, 0, 0); break;
            case "normal": body.SetElement(4, 1, 1, 0, 0); break;
            case "joints": body.SetElement(1, 1, 1, 1, 2, 3); break;
            case "weights": body.SetElement(2, 1, .5f, .25f, .125f, .125f); break;
            case "second-joints": body.SetElement(6, 1, 1, 2, 3, 0); break;
            case "second-weights": body.SetElement(7, 1, .5f, .25f, .125f, .125f); break;
            case "indices": SetIndex(body, 5, new byte[] { 0, 0, 2, 0, 1, 0 }); break;
            case "topology": body.Primitives[0]!["mode"] = 5; break;
            case "later-primitive": body.Primitives.Add(body.Primitives[0]!.DeepClone()); break;
            case "later-mesh":
                body.Root["meshes"]!.AsArray().Add(body.Mesh.DeepClone());
                body.Nodes.Add(new JsonObject { ["skin"] = 0, ["mesh"] = 1 });
                break;
        }
        Assert.NotEqual(before, Identity(body));
    }

    [Fact]
    public void Layout_padding_sparse_overrides_and_storage_relocation_do_not_change_fit()
    {
        var dense = new SkinnedAttachmentGlbFixture();
        string expected = Identity(dense);
        Assert.Equal(expected, Identity(new(strided: true)));
        dense.SetSparse(0, new[] { 1 }, new[] { new float[] { 1, 0, 0 } });
        Assert.Equal(expected, Identity(dense));
        dense.SetSparse(0, new[] { 0, 1, 2 }, new[]
        {
            new float[] { 0, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 0, 1, 0 },
        }, omitBase: true);
        Assert.Equal(expected, Identity(dense));
        Assert.Equal(expected, AttachmentGlb.ComputeRigIdentity(Relocate(dense.Build()), "offset.glb"));
    }

    [Fact]
    public void Raw_float_bits_are_not_rounded_or_reserialized()
    {
        var body = new SkinnedAttachmentGlbFixture();
        string before = Identity(body);
        body.SetElement(0, 0, BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)), 0, 0);
        Assert.NotEqual(before, Identity(body));
        body.SetElement(0, 0, BitConverter.Int32BitsToSingle(1), 0, 0);
        Assert.NotEqual(before, Identity(body));
    }

    [Fact]
    public void Json_spelling_order_escaping_and_default_transforms_are_canonical()
    {
        var body = new SkinnedAttachmentGlbFixture();
        body.Nodes[1]!["name"] = "joint-\U0001f412";
        string before = Identity(body);
        body.Nodes[1]!["translation"] = new JsonArray(0, 0, 0);
        body.Nodes[1]!["rotation"] = new JsonArray(0, 0, 0, 1);
        body.Nodes[1]!["scale"] = new JsonArray(1, 1, 1);
        Assert.Equal(before, Identity(body));
        body.Nodes[1]!["translation"] = new JsonArray(1.25, 0, 0);
        before = Identity(body);
        byte[] alternate = body.Build(json => json.Replace("1.25", "12500e-4", StringComparison.Ordinal)
            .Replace("[1,1,1]", "[1.0,1e0,100e-2]", StringComparison.Ordinal)
            .Replace("[12500e-4,0,0]", "[12500e-4,-0.0,0e100]", StringComparison.Ordinal)
            .Replace("\"asset\":{\"version\":\"2.0\"}", "\"asset\": { \"version\" : \"2.0\" }", StringComparison.Ordinal));
        Assert.Equal(before, AttachmentGlb.ComputeRigIdentity(alternate, "alternate.glb"));
    }

    [Fact]
    public void Omitted_inverse_binds_agree_with_explicit_identity_matrices()
    {
        var body = new SkinnedAttachmentGlbFixture();
        string before = Identity(body);
        body.Skin.Remove("inverseBindMatrices");
        Assert.Equal(before, Identity(body));
    }

    [Fact]
    public void Identity_reader_does_not_apply_the_attachment_renderer_joint_limit()
    {
        Assert.StartsWith("rig-v1:", Identity(new(129)));
    }

    [Fact]
    public void Existing_non_joint_ancestor_rest_frame_is_covered()
    {
        var body = new SkinnedAttachmentGlbFixture();
        body.Nodes.Add(new JsonObject { ["children"] = new JsonArray(0) });
        string before = Identity(body);
        body.Nodes[5]!["translation"] = new JsonArray(0, 2, 0);
        Assert.NotEqual(before, Identity(body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Later_primitive_or_mesh_data_is_covered(bool secondMesh)
    {
        var body = new SkinnedAttachmentGlbFixture();
        JsonNode later = body.Primitives[0]!.DeepClone();
        if (secondMesh)
        {
            body.Root["meshes"]!.AsArray().Add(new JsonObject { ["primitives"] = new JsonArray(later) });
            body.Nodes.Add(new JsonObject { ["skin"] = 0, ["mesh"] = 1 });
        }
        else body.Primitives.Add(later);
        string before = Identity(body);
        later["attributes"]!["POSITION"] = AddAccessor(body, "VEC3", 5126, 3, Floats(0, 0, 0, 2, 0, 0, 0, 1, 0));
        Assert.NotEqual(before, Identity(body));
    }

    [Fact]
    public void Incidental_node_and_accessor_indices_are_not_part_of_identity()
    {
        var body = DetailedBody();
        string before = Identity(body);
        body.Accessors.Insert(0, new JsonObject());
        body.Skin["inverseBindMatrices"] = 4;
        foreach (JsonNode? primitive in body.Primitives)
        {
            foreach (var attribute in primitive!["attributes"]!.AsObject().ToArray())
                primitive["attributes"]![attribute.Key] = attribute.Value!.GetValue<int>() + 1;
            primitive["indices"] = primitive["indices"]!.GetValue<int>() + 1;
        }
        foreach (JsonNode? node in body.Nodes)
            if (node!["children"] is JsonArray children)
                for (int i = 0; i < children.Count; i++) children[i] = children[i]!.GetValue<int>() + 1;
        JsonArray joints = body.Skin["joints"]!.AsArray();
        for (int i = 0; i < joints.Count; i++) joints[i] = joints[i]!.GetValue<int>() + 1;
        body.Nodes.Insert(0, new JsonObject { ["name"] = "clip helper" });
        Assert.Equal(before, Identity(body));
    }

    [Theory]
    [InlineData(5121)]
    [InlineData(5123)]
    public void Normalized_integer_weights_and_sparse_raw_overrides_are_supported(int component)
    {
        var body = new SkinnedAttachmentGlbFixture(weightComponentType: component);
        string before = Identity(body);
        body.SetSparse(2, new[] { 1 }, new[] { new float[] { .25f, .25f, .25f, .25f } });
        Assert.Equal(before, Identity(body));
        body.SetSparse(2, new[] { 1 }, new[] { new float[] { 1, 0, 0, 0 } });
        Assert.NotEqual(before, Identity(body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compressed_buffer_views_are_refused_even_when_extension_is_optional(bool required)
    {
        var body = new SkinnedAttachmentGlbFixture();
        body.Views[0]!["extensions"] = new JsonObject { ["EXT_meshopt_compression"] = new JsonObject() };
        if (required) body.Root["extensionsRequired"] = new JsonArray("EXT_meshopt_compression");
        Assert.Throws<InvalidDataException>(() => Identity(body));
    }

    [Theory]
    [InlineData("no-skin")]
    [InlineData("two-skins")]
    [InlineData("no-mesh")]
    [InlineData("bad-parent")]
    [InlineData("cycle")]
    [InlineData("duplicate-name")]
    [InlineData("missing-name")]
    [InlineData("joint-range")]
    [InlineData("weight-nan")]
    [InlineData("position-nan")]
    [InlineData("short-view")]
    [InlineData("bad-index")]
    [InlineData("unpaired-set")]
    [InlineData("morph")]
    [InlineData("compression")]
    [InlineData("bad-transform")]
    [InlineData("matrix-and-trs")]
    public void Unsafe_fit_input_is_refused_with_source_context(string defect)
    {
        var body = DetailedBody();
        switch (defect)
        {
            case "no-skin": body.Root.Remove("skins"); break;
            case "two-skins": body.Root["skins"]!.AsArray().Add(body.Skin.DeepClone()); break;
            case "no-mesh": body.MeshNode.Remove("skin"); break;
            case "bad-parent": body.Nodes[2]!["children"] = new JsonArray(1); break;
            case "cycle": body.Nodes[1]!["children"] = new JsonArray(0); break;
            case "duplicate-name": body.Nodes[1]!["name"] = "joint-0"; break;
            case "missing-name": body.Nodes[1]!.AsObject().Remove("name"); break;
            case "joint-range": body.SetElement(1, 1, 4, 0, 0, 0); break;
            case "weight-nan": body.SetElement(2, 1, float.NaN, 0, 0, 0); break;
            case "position-nan": body.SetElement(0, 1, float.NaN, 0, 0); break;
            case "short-view": body.Views[0]!["byteLength"] = 1; break;
            case "bad-index": SetIndex(body, 5, new byte[] { 0, 0, 3, 0, 1, 0 }); break;
            case "unpaired-set": Attributes(body).Remove("WEIGHTS_1"); break;
            case "morph": body.Primitives[0]!["targets"] = new JsonArray(new JsonObject()); break;
            case "compression": body.Primitives[0]!["extensions"] = new JsonObject { ["KHR_draco_mesh_compression"] = new JsonObject() }; break;
            case "bad-transform": body.Nodes[1]!["translation"] = new JsonArray(1, 2); break;
            case "matrix-and-trs":
                body.Nodes[1]!["matrix"] = Matrix(0);
                body.Nodes[1]!["translation"] = new JsonArray(0, 0, 0);
                break;
        }
        var error = Assert.Throws<InvalidDataException>(() => Identity(body));
        Assert.Contains("body.glb", error.Message);
    }

    static string Identity(SkinnedAttachmentGlbFixture body) => AttachmentGlb.ComputeRigIdentity(body.Build(), "body.glb");
    static JsonObject Attributes(SkinnedAttachmentGlbFixture body) => body.Primitives[0]!["attributes"]!.AsObject();
    static JsonArray Matrix(int y) => new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, y, 0, 1);

    static SkinnedAttachmentGlbFixture DetailedBody()
    {
        var body = new SkinnedAttachmentGlbFixture();
        Attributes(body)["NORMAL"] = AddAccessor(body, "VEC3", 5126, 3, Floats(0, 0, 1, 0, 0, 1, 0, 0, 1));
        body.Primitives[0]!["indices"] = AddAccessor(body, "SCALAR", 5123, 3, new byte[] { 0, 0, 1, 0, 2, 0 });
        Attributes(body)["JOINTS_1"] = AddAccessor(body, "VEC4", 5121, 3, new byte[] { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 });
        Attributes(body)["WEIGHTS_1"] = AddAccessor(body, "VEC4", 5126, 3, Floats(.25f, .25f, .25f, .25f, .25f, .25f, .25f, .25f, .25f, .25f, .25f, .25f));
        return body;
    }

    static int AddAccessor(SkinnedAttachmentGlbFixture body, string type, int component, int count, byte[] bytes)
    {
        int index = body.Accessors.Count;
        body.Accessors.Add(new JsonObject
        {
            ["bufferView"] = body.AddView(bytes),
            ["type"] = type,
            ["componentType"] = component,
            ["count"] = count,
        });
        return index;
    }

    static void SetIndex(SkinnedAttachmentGlbFixture body, int accessor, byte[] bytes) =>
        body.Accessors[accessor]!["bufferView"] = body.AddView(bytes);

    static byte[] Floats(params float[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    static byte[] Relocate(byte[] glb)
    {
        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        JsonObject root = JsonNode.Parse(glb.AsSpan(20, jsonLength))!.AsObject();
        byte[] binary = glb.AsSpan(28 + jsonLength).ToArray();
        var shifted = new byte[binary.Length + 16];
        binary.CopyTo(shifted, 16);
        shifted.AsSpan(0, 16).Fill(0xa5);
        foreach (JsonNode? view in root["bufferViews"]!.AsArray())
            view!["byteOffset"] = (view["byteOffset"]?.GetValue<int>() ?? 0) + 16;
        root["buffers"]![0]!["byteLength"] = shifted.Length;
        return SkinnedAttachmentGlbFixture.Container(root.ToJsonString(), shifted);
    }
}
