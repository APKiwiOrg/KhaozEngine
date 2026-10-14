using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public class SkinnedGlbPolicyTests
{
    [Fact]
    public void Valid_skin_at_128_joints_and_four_influences_is_accepted()
    {
        var fixture = new SkinnedAttachmentGlbFixture(128);
        byte[] valid128 = fixture.Build();
        int fixtureJointCount = fixture.JointCount;
        int fixtureInfluencesPerVertex = fixture.InfluencesPerVertex;
        AttachmentGlb.ValidateSkinnedEmbedded(valid128, "valid.glb");
        Assert.Equal(128, fixtureJointCount);
        Assert.Equal(4, fixtureInfluencesPerVertex);
        Assert.Equal(127, fixture.Nodes[0]!["children"]!.AsArray().Count);
        JsonArray sceneRoots = fixture.Root["scenes"]![0]!["nodes"]!.AsArray();
        Assert.Equal(2, sceneRoots.Count);
        Assert.Equal(0, sceneRoots[0]!.GetValue<int>());
        Assert.Equal(128, sceneRoots[1]!.GetValue<int>());
    }

    [Theory]
    [InlineData("joint-chain")]
    [InlineData("non-joint-root")]
    [InlineData("non-joint-intermediate")]
    [InlineData("reordered-joints")]
    [InlineData("single-joint")]
    public void Legal_joint_hierarchies_are_accepted(string name)
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        switch (name)
        {
            case "joint-chain":
                for (int joint = 0; joint < 3; joint++)
                    fixture.Nodes[joint]!["children"] = new JsonArray(joint + 1);
                break;
            case "non-joint-root":
                fixture.Nodes[0]!.AsObject().Remove("children");
                fixture.Nodes.Add(new JsonObject { ["children"] = new JsonArray(0, 1, 2, 3) });
                fixture.Root["scenes"]![0]!["nodes"] = new JsonArray(5, 4);
                break;
            case "non-joint-intermediate":
                fixture.Nodes[0]!["children"] = new JsonArray(5, 2, 3);
                fixture.Nodes.Add(new JsonObject { ["children"] = new JsonArray(1) });
                break;
            case "reordered-joints": fixture.Skin["joints"] = new JsonArray(3, 2, 1, 0); break;
            case "single-joint":
                fixture.Skin["joints"] = new JsonArray(0);
                fixture.Accessors[3]!["count"] = 1;
                for (int vertex = 0; vertex < 3; vertex++)
                {
                    fixture.SetElement(1, vertex, 0, 0, 0, 0);
                    fixture.SetElement(2, vertex, 1, 0, 0, 0);
                }
                break;
        }
        AttachmentGlb.ValidateSkinnedEmbedded(fixture.Build(), "hierarchy.glb");
    }

    [Fact]
    public void Valid_skin_with_deep_parent_chain_is_accepted()
    {
        var fixture = new SkinnedAttachmentGlbFixture();
        int root = 0;
        for (int depth = 0; depth < 8192; depth++)
        {
            fixture.Nodes.Add(new JsonObject { ["children"] = new JsonArray(root) });
            root = fixture.Nodes.Count - 1;
        }
        fixture.Root["scenes"]![0]!["nodes"] = new JsonArray(root, 4);
        AttachmentGlb.ValidateSkinnedEmbedded(fixture.Build(), "deep-hierarchy.glb");
    }

    [Theory]
    [InlineData("strided")]
    [InlineData("ubyte-weights")]
    [InlineData("ushort-weights")]
    [InlineData("ushort-joints")]
    [InlineData("sparse")]
    [InlineData("sparse-only")]
    [InlineData("sparse-normalized")]
    [InlineData("offset")]
    [InlineData("no-inverse-binds")]
    [InlineData("extensions")]
    public void Legal_accessor_encodings_are_accepted(string name)
    {
        var fixture = new SkinnedAttachmentGlbFixture(jointComponentType: name == "ushort-joints" ? 5123 : 5121,
            weightComponentType: name is "ubyte-weights" or "sparse-normalized" ? 5121 : name == "ushort-weights" ? 5123 : 5126,
            strided: name == "strided");
        switch (name)
        {
            case "sparse":
            case "sparse-normalized":
                fixture.SetSparse(1, new[] { 1 }, new[] { new float[] { 3, 2, 1, 0 } });
                fixture.SetSparse(2, new[] { 1 }, new[] { new float[] { 1, 0, 0, 0 } });
                break;
            case "sparse-only":
                fixture.SetSparse(2, new[] { 0, 1, 2 }, new[]
                {
                    new float[] { 1, 0, 0, 0 }, new float[] { 0, 1, 0, 0 }, new float[] { 0, 0, 1, 0 },
                }, omitBase: true);
                break;
            case "offset":
                int view = fixture.AddView(new byte[52]);
                fixture.Accessors[2]!["bufferView"] = view;
                fixture.Accessors[2]!["byteOffset"] = 4;
                for (int i = 0; i < 3; i++) fixture.SetElement(2, i, 1, 0, 0, 0);
                break;
            case "no-inverse-binds": fixture.Skin.Remove("inverseBindMatrices"); break;
            case "extensions":
                fixture.Root["extensions"] = JsonNode.Parse("""{"VENDOR_ordinary":{"future":{"value":1}}}""");
                break;
        }
        AttachmentGlb.ValidateSkinnedEmbedded(fixture.Build(), "accepted.glb");
    }

    [Theory]
    [InlineData("zero-skins", "skins")]
    [InlineData("two-skins", "skins")]
    [InlineData("missing-skin", "node 4")]
    [InlineData("different-skin", "node 4")]
    [InlineData("later-node", "node 5")]
    [InlineData("later-joints", "primitive 1")]
    [InlineData("later-weights", "primitive 1")]
    [InlineData("joints-1", "JOINTS_1")]
    [InlineData("weights-1", "WEIGHTS_1")]
    [InlineData("joints-2", "JOINTS_2")]
    [InlineData("weights-2", "WEIGHTS_2")]
    [InlineData("zero-weights", "vertex 1")]
    [InlineData("negative-weight", "vertex 1")]
    [InlineData("nan-weight", "vertex 1")]
    [InlineData("infinite-weight", "vertex 1")]
    [InlineData("joint-at-count", "vertex 1")]
    [InlineData("joint-at-count-zero-lane", "vertex 1")]
    [InlineData("129-joints", "skin 0")]
    [InlineData("repeated-index", "skin 0")]
    [InlineData("repeated-name", "skin 0")]
    [InlineData("whitespace-name", "joint 1")]
    [InlineData("empty-name", "joint 1")]
    [InlineData("missing-name", "joint 1")]
    [InlineData("joint-node-range", "skin 0")]
    [InlineData("empty-joints", "skin 0")]
    [InlineData("no-common-root", "skin 0: joints must share a common root")]
    [InlineData("separate-joint-root", "skin 0: joints must share a common root")]
    [InlineData("hierarchy-cycle", "skin 0 node 0: node hierarchy contains a cycle")]
    [InlineData("self-cycle", "skin 0 node 4: node hierarchy contains a cycle")]
    [InlineData("detached-cycle", "skin 0 node 5: node hierarchy contains a cycle")]
    [InlineData("two-parents", "skin 0 node 1: multiple parents")]
    [InlineData("repeated-child", "skin 0 node 1: multiple parents")]
    [InlineData("children-object", "skin 0 node 0: children must be an array")]
    [InlineData("child-range", "skin 0 node 0 children: invalid node reference")]
    [InlineData("child-negative", "skin 0 node 0 children: expected a non-negative integer")]
    [InlineData("child-fractional", "skin 0 node 0 children: expected a non-negative integer")]
    [InlineData("child-string", "skin 0 node 0 children: expected a non-negative integer")]
    [InlineData("child-null", "skin 0 node 0 children: expected a non-negative integer")]
    [InlineData("child-overflow", "skin 0 node 0 children: expected a non-negative integer")]
    [InlineData("non-object-node", "skin 0 node 4: node must be an object")]
    [InlineData("animation", "animations")]
    [InlineData("targets", "primitive 0")]
    [InlineData("node-weights", "node 4")]
    [InlineData("mesh-weights", "mesh 0")]
    [InlineData("negative-joint", "JOINTS_0")]
    [InlineData("fractional-joint", "JOINTS_0")]
    [InlineData("joint-type", "JOINTS_0")]
    [InlineData("joint-normalized", "JOINTS_0")]
    [InlineData("weight-type", "WEIGHTS_0")]
    [InlineData("weight-unnormalized", "WEIGHTS_0")]
    [InlineData("float-normalized", "WEIGHTS_0")]
    [InlineData("weight-shape", "WEIGHTS_0")]
    [InlineData("joint-shape", "JOINTS_0")]
    [InlineData("joint-count", "primitive 0")]
    [InlineData("weight-count", "primitive 0")]
    [InlineData("missing-position", "POSITION")]
    [InlineData("accessor-reference", "JOINTS_0")]
    [InlineData("fractional-reference", "JOINTS_0")]
    [InlineData("negative-reference", "WEIGHTS_0")]
    [InlineData("missing-accessors", "accessor")]
    [InlineData("short-view", "accessor 2")]
    [InlineData("overflow-view", "bufferView")]
    [InlineData("overflow-view-length", "bufferView")]
    [InlineData("view-buffer", "bufferView")]
    [InlineData("view-reference", "accessor 2")]
    [InlineData("accessor-offset", "accessor 2")]
    [InlineData("overflow-count", "accessor 2")]
    [InlineData("stride-short", "accessor 2")]
    [InlineData("stride-overflow", "accessor 2")]
    [InlineData("bind-count", "inverseBindMatrices")]
    [InlineData("bind-reference", "inverseBindMatrices")]
    [InlineData("bind-short", "accessor 3")]
    [InlineData("sparse-zero", "vertex 1")]
    [InlineData("sparse-joint-range", "vertex 1")]
    [InlineData("sparse-short-values", "sparse values")]
    [InlineData("sparse-short-indices", "sparse indices")]
    [InlineData("sparse-offset", "sparse values")]
    [InlineData("sparse-overflow", "sparse indices")]
    [InlineData("sparse-index-range", "sparse indices")]
    [InlineData("sparse-index-order", "sparse indices")]
    [InlineData("sparse-index-type", "sparse indices")]
    [InlineData("sparse-count", "sparse")]
    [InlineData("sparse-reference", "sparse values")]
    [InlineData("multiple-buffers", "buffer")]
    [InlineData("missing-buffer", "buffer")]
    [InlineData("external-buffer", "buffer URI")]
    [InlineData("data-buffer", "buffer URI")]
    [InlineData("external-image", "image URI")]
    [InlineData("data-image", "image URI")]
    [InlineData("nested-duplicate", "duplicate")]
    public void Skinned_policy_refuses_lossy_or_unsafe_data(string name, string expectedContext)
    {
        var fixture = new SkinnedAttachmentGlbFixture(name == "129-joints" ? 129 : 4,
            jointComponentType: name is "negative-joint" or "fractional-joint" ? 5126 : 5121);
        JsonObject attributes = fixture.Primitives[0]!["attributes"]!.AsObject();
        switch (name)
        {
            case "zero-skins": fixture.Root["skins"] = new JsonArray(); break;
            case "two-skins": fixture.Root["skins"]!.AsArray().Add(fixture.Skin.DeepClone()); break;
            case "missing-skin": fixture.MeshNode.Remove("skin"); break;
            case "different-skin": fixture.MeshNode["skin"] = 1; break;
            case "later-node": fixture.Nodes.Add(new JsonObject { ["mesh"] = 0 }); break;
            case "later-joints":
            case "later-weights":
                JsonNode later = fixture.Primitives[0]!.DeepClone();
                later["attributes"]!.AsObject().Remove(name == "later-joints" ? "JOINTS_0" : "WEIGHTS_0");
                fixture.Primitives.Add(later);
                break;
            case "joints-1": attributes["JOINTS_1"] = 1; break;
            case "weights-1": attributes["WEIGHTS_1"] = 2; break;
            case "joints-2": attributes["JOINTS_2"] = 1; break;
            case "weights-2": attributes["WEIGHTS_2"] = 2; break;
            case "zero-weights": fixture.SetElement(2, 1, 0, 0, 0, 0); break;
            case "negative-weight": fixture.SetElement(2, 1, -.25f, .25f, .25f, .25f); break;
            case "nan-weight": fixture.SetElement(2, 1, float.NaN, 0, 0, 0); break;
            case "infinite-weight": fixture.SetElement(2, 1, float.PositiveInfinity, 0, 0, 0); break;
            case "joint-at-count": fixture.SetElement(1, 1, 0, 1, 2, 4); break;
            case "joint-at-count-zero-lane":
                fixture.SetElement(1, 1, 0, 1, 2, 4);
                fixture.SetElement(2, 1, 1, 0, 0, 0);
                break;
            case "repeated-index": fixture.Skin["joints"]![1] = 0; break;
            case "repeated-name": fixture.Nodes[1]!["name"] = "joint-0"; break;
            case "whitespace-name": fixture.Nodes[1]!["name"] = " \t"; break;
            case "empty-name": fixture.Nodes[1]!["name"] = ""; break;
            case "missing-name": fixture.Nodes[1]!.AsObject().Remove("name"); break;
            case "joint-node-range": fixture.Skin["joints"]![1] = 99; break;
            case "empty-joints": fixture.Skin["joints"] = new JsonArray(); break;
            case "no-common-root":
                fixture.Nodes[0]!.AsObject().Remove("children");
                fixture.Root["scenes"]![0]!["nodes"] = new JsonArray(0, 1, 2, 3, 4);
                break;
            case "separate-joint-root":
                fixture.Nodes[0]!["children"] = new JsonArray(1, 2);
                fixture.Root["scenes"]![0]!["nodes"] = new JsonArray(0, 3, 4);
                break;
            case "hierarchy-cycle": fixture.Nodes[1]!["children"] = new JsonArray(0); break;
            case "self-cycle": fixture.MeshNode["children"] = new JsonArray(4); break;
            case "detached-cycle":
                fixture.Nodes.Add(new JsonObject { ["children"] = new JsonArray(6) });
                fixture.Nodes.Add(new JsonObject { ["children"] = new JsonArray(5) });
                break;
            case "two-parents": fixture.MeshNode["children"] = new JsonArray(1); break;
            case "repeated-child": fixture.Nodes[0]!["children"]!.AsArray().Add(1); break;
            case "children-object": fixture.Nodes[0]!["children"] = new JsonObject(); break;
            case "child-range": fixture.Nodes[0]!["children"] = new JsonArray(99); break;
            case "child-negative": fixture.Nodes[0]!["children"] = new JsonArray(-1); break;
            case "child-fractional": fixture.Nodes[0]!["children"] = new JsonArray(.5); break;
            case "child-string": fixture.Nodes[0]!["children"] = new JsonArray("one"); break;
            case "child-null": fixture.Nodes[0]!["children"] = new JsonArray((JsonNode?)null); break;
            case "child-overflow": fixture.Nodes[0]!["children"] = new JsonArray(uint.MaxValue); break;
            case "non-object-node": fixture.Nodes[4] = null; break;
            case "animation": fixture.Root["animations"] = new JsonArray(new JsonObject()); break;
            case "targets": fixture.Primitives[0]!["targets"] = new JsonArray(); break;
            case "node-weights": fixture.MeshNode["weights"] = new JsonArray(); break;
            case "mesh-weights": fixture.Mesh["weights"] = new JsonArray(); break;
            case "negative-joint": fixture.SetElement(1, 1, -1, 0, 0, 0); break;
            case "fractional-joint": fixture.SetElement(1, 1, .5f, 0, 0, 0); break;
            case "joint-type": fixture.Accessors[1]!["componentType"] = 5122; break;
            case "joint-normalized": fixture.Accessors[1]!["normalized"] = true; break;
            case "weight-type": fixture.Accessors[2]!["componentType"] = 5125; break;
            case "weight-unnormalized": fixture.Accessors[2]!["componentType"] = 5121; break;
            case "float-normalized": fixture.Accessors[2]!["normalized"] = true; break;
            case "weight-shape": fixture.Accessors[2]!["type"] = "VEC3"; break;
            case "joint-shape": fixture.Accessors[1]!["type"] = "VEC3"; break;
            case "joint-count": fixture.Accessors[1]!["count"] = 2; break;
            case "weight-count": fixture.Accessors[2]!["count"] = 2; break;
            case "missing-position": attributes.Remove("POSITION"); break;
            case "accessor-reference": attributes["JOINTS_0"] = "one"; break;
            case "fractional-reference": attributes["JOINTS_0"] = .5; break;
            case "negative-reference": attributes["WEIGHTS_0"] = -1; break;
            case "missing-accessors": fixture.Root.Remove("accessors"); break;
            case "short-view": fixture.Views[2]!["byteLength"] = 47; break;
            case "overflow-view": fixture.Views[2]!["byteOffset"] = uint.MaxValue; break;
            case "overflow-view-length": fixture.Views[2]!["byteLength"] = uint.MaxValue; break;
            case "view-buffer": fixture.Views[2]!["buffer"] = 1; break;
            case "view-reference": fixture.Accessors[2]!["bufferView"] = 99; break;
            case "accessor-offset": fixture.Accessors[2]!["byteOffset"] = uint.MaxValue; break;
            case "overflow-count": fixture.Accessors[2]!["count"] = int.MaxValue; break;
            case "stride-short": fixture.Views[2]!["byteStride"] = 4; break;
            case "stride-overflow": fixture.Views[2]!["byteStride"] = uint.MaxValue; break;
            case "bind-count": fixture.Accessors[3]!["count"] = 3; break;
            case "bind-reference": fixture.Skin["inverseBindMatrices"] = 99; break;
            case "bind-short": fixture.Views[3]!["byteLength"] = 255; break;
            case "multiple-buffers": fixture.Root["buffers"]!.AsArray().Add(new JsonObject { ["byteLength"] = 0 }); break;
            case "missing-buffer": fixture.Root["buffers"] = new JsonArray(); break;
            case "external-buffer": fixture.Root["buffers"]![0]!["uri"] = "other.bin"; break;
            case "data-buffer": fixture.Root["buffers"]![0]!["uri"] = "data:application/octet-stream;base64,AA=="; break;
            case "external-image": fixture.Root["images"] = new JsonArray(new JsonObject { ["uri"] = "image.png" }); break;
            case "data-image": fixture.Root["images"] = new JsonArray(new JsonObject { ["uri"] = "data:image/png;base64,AA==" }); break;
            case "nested-duplicate": fixture.Root["extensions"] = JsonNode.Parse("""{"VENDOR_test":{"nested":[{"x":1}]}}"""); break;
            default:
                if (name.StartsWith("sparse-", StringComparison.Ordinal)) ConfigureSparse(fixture, name);
                break;
        }
        byte[] bad = fixture.Build(name == "nested-duplicate"
            ? json => json.Replace("\"x\":1", "\"x\":1,\"x\":2", StringComparison.Ordinal) : null);
        var error = Assert.Throws<InvalidDataException>(() => AttachmentGlb.ValidateSkinnedEmbedded(bad, "bad.glb"));
        Assert.Contains("bad.glb", error.Message);
        Assert.Contains(expectedContext, error.Message);
    }

    static void ConfigureSparse(SkinnedAttachmentGlbFixture fixture, string name)
    {
        int accessor = name == "sparse-joint-range" ? 1 : 2;
        fixture.SetSparse(accessor, new[] { 1 }, new[]
        {
            name == "sparse-zero" ? new float[] { 0, 0, 0, 0 }
                : name == "sparse-joint-range" ? new float[] { 0, 0, 0, 4 } : new float[] { 1, 0, 0, 0 },
        });
        JsonNode sparse = fixture.Accessors[accessor]!["sparse"]!;
        int indexView = sparse["indices"]!["bufferView"]!.GetValue<int>();
        int valueView = sparse["values"]!["bufferView"]!.GetValue<int>();
        switch (name)
        {
            case "sparse-short-values": fixture.Views[valueView]!["byteLength"] = 15; break;
            case "sparse-short-indices": fixture.Views[indexView]!["byteLength"] = 1; break;
            case "sparse-offset": sparse["values"]!["byteOffset"] = uint.MaxValue; break;
            case "sparse-overflow": sparse["indices"]!["byteOffset"] = uint.MaxValue; break;
            case "sparse-index-range": fixture.SetSparse(2, new[] { 3 }, new[] { new float[] { 1, 0, 0, 0 } }); break;
            case "sparse-index-order":
                fixture.SetSparse(2, new[] { 1, 1 }, new[] { new float[] { 1, 0, 0, 0 }, new float[] { 1, 0, 0, 0 } });
                break;
            case "sparse-index-type": sparse["indices"]!["componentType"] = 5126; break;
            case "sparse-count": sparse["count"] = int.MaxValue; break;
            case "sparse-reference": sparse["values"]!["bufferView"] = 99; break;
        }
    }
}
