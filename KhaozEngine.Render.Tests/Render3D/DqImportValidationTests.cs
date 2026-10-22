using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqImportValidationTests
{
    [Fact]
    public void Opt_in_is_explicit_and_all_material_parts_agree()
    {
        var fixture = Fixture();
        fixture.Root["extras"] = JsonNode.Parse("""{"preserveVolume":true,"skinning":"DualQuaternionRigid"}""");
        fixture.Skin["extras"] = fixture.Root["extras"]!.DeepClone();
        byte[] glb = fixture.Build();
        byte[] original = (byte[])glb.Clone();
        WithPath(glb, path =>
        {
            Assert.Equal(SkinningProfile.LinearBlend, GltfLoader.LoadSkinned(path).Profile);
            Assert.Equal(SkinningProfile.LinearBlend, GltfLoader.LoadSkinned(glb, path).Profile);
            Assert.Equal(SkinningProfile.LinearBlend, GltfLoader.LoadSkinnedWithMaterial(path).Mesh.Profile);
            Assert.All(GltfLoader.LoadSkinnedPartsWithMaterials(path), p => Assert.Equal(SkinningProfile.LinearBlend, p.Mesh.Profile));
            Assert.All(GltfLoader.LoadSkinnedPartsWithMaterials(glb, path), p => Assert.Equal(SkinningProfile.LinearBlend, p.Mesh.Profile));

            var parts = LoadParts(glb, path, SkinningProfile.DualQuaternionRigid);
            Assert.Equal(2, parts.Count);
            Assert.All(parts, part => Assert.Equal(SkinningProfile.DualQuaternionRigid, part.Mesh.Profile));
            Assert.All(LoadPathParts(path), part => Assert.Equal(SkinningProfile.DualQuaternionRigid, part.Mesh.Profile));
            Assert.Equal(SkinningProfile.DualQuaternionRigid, LoadPathWithMaterial(path).Mesh.Profile);
            SkinnedGltfMesh memory = Load(glb, path, SkinningProfile.DualQuaternionRigid);
            SkinnedGltfMesh file = LoadPath(path);
            Assert.Equal(memory.Vertices, file.Vertices);
            Assert.Equal(SkinningProfile.DualQuaternionRigid, file.Profile);
            Assert.Same(parts[0].Mesh.Skeleton, parts[1].Mesh.Skeleton);
            Assert.Equal(new[] { "joint-2", "joint-0", "joint-3", "joint-1" },
                memory.Skeleton!.JointToNode.Select(i => memory.Skeleton.NodeNames[i]));
            Assert.All(memory.Vertices, v => Assert.Equal(new Vector4(3, 1, 2, 0), v.BoneIndices));
            Assert.Equal(original, glb);
        });
    }

    [Theory]
    [InlineData(1, .2f)]
    [InlineData(2, float.Epsilon)]
    public void Extra_positive_source_influences_are_not_truncated(int set, float extraWeight)
    {
        var fixture = Fixture();
        SetWeights(fixture, new Vector4(set == 1 ? .2f : .25f));
        AddSet(fixture, set, new Vector4(3, 2, 1, 0), new Vector4(extraWeight, 0, 0, 0));
        byte[] fivePositiveGlb = fixture.Build();
        Assert.Equal(SkinningProfile.LinearBlend, GltfLoader.LoadSkinned(fivePositiveGlb, "five-positive").Profile);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Load(fivePositiveGlb, "five-positive", SkinningProfile.DualQuaternionRigid));
        Assert.Contains("five-positive", error.Message);
        Assert.Contains("5 positive", error.Message);
        Assert.Contains("vertex 0", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Positive_later_sets_are_rejected_even_below_the_four_influence_limit(int set)
    {
        var fixture = Fixture();
        SetWeights(fixture, new Vector4(set == 1 ? .75f : 1f, 0, 0, 0));
        AddSet(fixture, set, Vector4.Zero, new Vector4(.25f, 0, 0, 0));
        byte[] glb = fixture.Build();
        Assert.All(GltfLoader.LoadSkinned(glb, "later-positive").Vertices, v => Assert.Equal(new Vector4(1, 0, 0, 0), v.BoneWeights));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Load(glb, "later-positive", SkinningProfile.DualQuaternionRigid));
        Assert.Contains($"WEIGHTS_{set}", error.Message);
        Assert.Contains("JOINTS_0", error.Message);
    }

    [Fact]
    public void Zero_extra_sets_preserve_four_slot_order_and_weight_normalization()
    {
        var fixture = Fixture();
        SetWeights(fixture, new Vector4(.25f, .25f, .25f, .2500005f));
        AddSet(fixture, 1, new Vector4(3, 1, 2, 0), Vector4.Zero);
        AddSet(fixture, 2, Vector4.Zero, Vector4.Zero);
        SkinnedGltfMesh mesh = Load(fixture.Build(), "zero-extras", SkinningProfile.DualQuaternionRigid);
        Assert.All(mesh.Vertices, vertex =>
        {
            Assert.Equal(new Vector4(3, 1, 2, 0), vertex.BoneIndices);
            Assert.Equal(new Vector4(.24999988f, .24999988f, .24999988f, .2500004f), vertex.BoneWeights);
        });
        SetWeights(fixture, new Vector4(1e-10f, 0, 0, 0));
        byte[] tiny = fixture.Build();
        Assert.Throws<InvalidOperationException>(() => GltfLoader.LoadSkinned(tiny, "legacy-tiny-weights"));
        Assert.Throws<InvalidOperationException>(() => Load(tiny, "tiny-weights", SkinningProfile.DualQuaternionRigid));
    }

    [Fact]
    public void Overflowing_raw_weight_totals_do_not_become_unweighted_vertices()
    {
        var fixture = Fixture();
        SetWeights(fixture, new Vector4(float.MaxValue, float.MaxValue, 0, 0));
        byte[] glb = fixture.Build();
        Assert.Throws<InvalidOperationException>(() => GltfLoader.LoadSkinned(glb, "legacy-overflow"));
        Assert.Throws<InvalidOperationException>(() => Load(glb, "overflow", SkinningProfile.DualQuaternionRigid));
    }

    [Fact]
    public void Missing_set_zero_preserves_the_legacy_schema_refusal()
    {
        var fixture = Fixture();
        AddSet(fixture, 2, Vector4.Zero, new Vector4(1, 0, 0, 0));
        fixture.Primitives[0]!["attributes"]!.AsObject().Remove("JOINTS_2");
        fixture.Primitives[0]!["attributes"]!.AsObject().Remove("WEIGHTS_2");
        fixture.Primitives[1]!["attributes"]!.AsObject().Remove("JOINTS_0");
        fixture.Primitives[1]!["attributes"]!.AsObject().Remove("WEIGHTS_0");
        byte[] glb = fixture.Build();
        Assert.Throws<InvalidDataException>(() => GltfLoader.LoadSkinned(glb, "legacy-missing-zero"));
        Assert.Throws<InvalidOperationException>(() => Load(glb, "missing-zero", SkinningProfile.DualQuaternionRigid));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Large_set_numbers_cannot_hide_later_influences(bool positive)
    {
        var fixture = Fixture();
        AddSet(fixture, 2, Vector4.Zero, positive ? new Vector4(1, 0, 0, 0) : Vector4.Zero);
        foreach (JsonNode? primitive in fixture.Primitives)
        {
            JsonObject attributes = primitive!["attributes"]!.AsObject();
            attributes["JOINTS_2147483648"] = attributes["JOINTS_2"]!.DeepClone();
            attributes["WEIGHTS_2147483648"] = attributes["WEIGHTS_2"]!.DeepClone();
            attributes.Remove("JOINTS_2");
            attributes.Remove("WEIGHTS_2");
        }
        byte[] glb = fixture.Build();
        Assert.Equal(6, GltfLoader.LoadSkinned(glb, "legacy-large-set").Vertices.Length);
        if (positive) Assert.Throws<InvalidOperationException>(() => Load(glb, "large-set", SkinningProfile.DualQuaternionRigid));
        else Assert.Equal(6, Load(glb, "large-set", SkinningProfile.DualQuaternionRigid).Vertices.Length);
    }

    [Theory]
    [InlineData(0, -1f)]
    [InlineData(0, -float.Epsilon)]
    [InlineData(0, float.NaN)]
    [InlineData(0, float.PositiveInfinity)]
    [InlineData(0, float.NegativeInfinity)]
    [InlineData(1, -1f)]
    [InlineData(1, float.NaN)]
    [InlineData(1, float.PositiveInfinity)]
    [InlineData(2, -1f)]
    [InlineData(2, float.NaN)]
    [InlineData(2, float.PositiveInfinity)]
    public void Raw_invalid_weights_are_rejected_before_normalization(int set, float weight)
    {
        var fixture = Fixture();
        if (set == 0) SetWeights(fixture, new Vector4(weight, 0, 0, 0));
        else AddSet(fixture, set, Vector4.Zero, new Vector4(weight, 0, 0, 0));
        byte[] glb = fixture.Build();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Load(glb, "raw-weights", SkinningProfile.DualQuaternionRigid));
        Assert.Contains("raw-weights", error.Message);
        WithPath(glb, path =>
        {
            InvalidOperationException memory = Assert.Throws<InvalidOperationException>(() => Load(glb, path, SkinningProfile.DualQuaternionRigid));
            InvalidOperationException file = Assert.Throws<InvalidOperationException>(() => LoadPath(path));
            Assert.Equal(memory.Message, file.Message);
        });
    }

    [Theory]
    [InlineData(0, -1f)]
    [InlineData(0, -.5f)]
    [InlineData(0, .5f)]
    [InlineData(0, 4f)]
    [InlineData(0, float.NaN)]
    [InlineData(0, float.PositiveInfinity)]
    [InlineData(1, -1f)]
    [InlineData(1, .5f)]
    [InlineData(1, 4f)]
    [InlineData(1, float.NaN)]
    [InlineData(2, -1f)]
    [InlineData(2, .5f)]
    [InlineData(2, 4f)]
    [InlineData(2, float.NaN)]
    public void Invalid_indices_in_zero_weight_slots_are_rejected(int set, float index)
    {
        var fixture = Fixture(jointComponentType: 5126);
        if (set == 0)
        {
            SetWeights(fixture, Vector4.Zero);
            fixture.SetElement(1, 0, 0, 0, 0, index);
        }
        else AddSet(fixture, set, new Vector4(0, 0, 0, index), Vector4.Zero, jointComponentType: 5126);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Load(fixture.Build(), "raw-indices", SkinningProfile.DualQuaternionRigid));
        Assert.Contains("raw-indices", error.Message);
    }

    [Theory]
    [InlineData(1.00000762939453125f, true)]
    [InlineData(1.0000077486038208f, false)]
    [InlineData(.99999237060546875f, true)]
    [InlineData(.999992311000823974609375f, false)]
    public void Import_audits_both_sides_of_inverse_bind_drift(float scale, bool accepted)
    {
        var fixture = Fixture();
        fixture.SetInverseBind(2, Matrix4x4.CreateScale(scale, 1, 1));
        byte[] glb = fixture.Build();
        if (accepted) Load(glb, "bind-drift", SkinningProfile.DualQuaternionRigid);
        else
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Load(glb, "bind-drift", SkinningProfile.DualQuaternionRigid));
            Assert.Contains("joint 2", error.Message);
            Assert.Contains("joint-3", error.Message);
            Assert.Contains("sample -1", error.Message);
        }
        Assert.Equal(SkinningProfile.LinearBlend, GltfLoader.LoadSkinned(glb, "lbs-drift").Profile);
    }

    [Fact]
    public void Sample_audit_reports_the_effective_composed_joint()
    {
        SkinnedGltfMesh mesh = Load(Fixture().Build(), "sample-asset", SkinningProfile.DualQuaternionRigid);
        var context = new SkinningValidationContext("sample-asset", "stretch-clip", 37);
        Matrix4x4[] joints = (Matrix4x4[])mesh.RestPose.Clone();
        ValidatePose(mesh, joints, context);
        joints[2] = Matrix4x4.CreateScale(1, 2, 1);
        ArgumentException error = Assert.Throws<ArgumentException>(() => ValidatePose(mesh, joints, context));
        Assert.Contains("sample-asset", error.Message);
        Assert.Contains("stretch-clip", error.Message);
        Assert.Contains("sample 37", error.Message);
        Assert.Contains("joint 2", error.Message);
        Assert.Contains("joint-3", error.Message);
        Assert.Contains("DualQuaternionRigid", error.Message);
        joints[2] = Matrix4x4.Identity;
        joints[2].M12 = .01f;
        Assert.Throws<ArgumentException>(() => ValidatePose(mesh, joints, context));
        ValidatePose(mesh, mesh.RestPose, context with { Clip = "valid-other-sample", Sample = 38 });
        Assert.Throws<ArgumentException>(() => ValidatePose(mesh, joints, context with { Clip = null, Sample = 39 }));
        Assert.Equal(Matrix4x4.Identity, mesh.RestPose[2]);
    }

    [Fact]
    public void Cancelling_bind_scale_is_eligible_but_unsampled_clips_and_procedural_poses_are_not_certified()
    {
        var fixture = Fixture();
        fixture.Nodes[0]!["scale"] = new JsonArray(2, .5f, 4);
        for (int joint = 0; joint < 4; joint++) fixture.SetInverseBind(joint, Matrix4x4.CreateScale(.5f, 2, .25f));
        int times = AddAccessor(fixture, "SCALAR", [new[] { 0f }, new[] { 1f }]);
        fixture.Accessors[times]!["min"] = new JsonArray(0);
        fixture.Accessors[times]!["max"] = new JsonArray(1);
        int scales = AddAccessor(fixture, "VEC3", [new[] { 2f, .5f, 4 }, new[] { 3f, .5f, 4 }]);
        fixture.Root["animations"] = JsonNode.Parse("""
            [{"name":"stretch","samplers":[{"input":0,"output":0}],
              "channels":[{"sampler":0,"target":{"node":0,"path":"scale"}}]}]
            """);
        fixture.Root["animations"]![0]!["samplers"]![0]!["input"] = times;
        fixture.Root["animations"]![0]!["samplers"]![0]!["output"] = scales;
        byte[] glb = fixture.Build();
        WithPath(glb, path =>
        {
            SkinnedGltfMesh mesh = LoadPath(path);
            AnimationClip clip = Assert.Single(GltfLoader.LoadAnimations(path));
            var context = new SkinningValidationContext(path, clip.Name, 0);
            ValidatePose(mesh, AnimationSampler.SampleToBonePalette(clip, mesh.Skeleton!, 0), context);
            Assert.Throws<ArgumentException>(() => ValidatePose(mesh,
                AnimationSampler.SampleToBonePalette(clip, mesh.Skeleton!, 1), context with { Sample = 1 }));
            Matrix4x4[] procedural = (Matrix4x4[])mesh.RestPose.Clone();
            procedural[0] = Matrix4x4.Identity;
            Assert.Throws<ArgumentException>(() => ValidatePose(mesh, procedural, context with { Clip = null, Sample = 2 }));
            ValidatePose(GltfLoader.LoadSkinned(path), procedural, context);
        });
    }

    [Fact]
    public void Pose_audit_requires_a_complete_palette_and_rechecks_mutable_source_data()
    {
        SkinnedGltfMesh mesh = Load(Fixture().Build(), "source-audit", SkinningProfile.DualQuaternionRigid);
        var context = new SkinningValidationContext("source-audit", "pose", 4);
        Assert.Throws<ArgumentException>(() => ValidatePose(mesh, mesh.RestPose[..3], context));
        Assert.Throws<ArgumentException>(() => ValidatePose(mesh, new Matrix4x4[5], context));
        mesh.Vertices[0].BoneIndices.W = .5f;
        ArgumentException error = Assert.Throws<ArgumentException>(() => ValidatePose(mesh, mesh.RestPose, context));
        Assert.Contains("BoneIndices", error.Message);
        Assert.Contains("source-audit", error.Message);
    }

    static SkinnedAttachmentGlbFixture Fixture(int jointComponentType = 5121)
    {
        var fixture = new SkinnedAttachmentGlbFixture(jointComponentType: jointComponentType);
        fixture.SetSkinJoints(2, 0, 3, 1);
        for (int vertex = 0; vertex < 3; vertex++) fixture.SetElement(1, vertex, 3, 1, 2, 0);
        fixture.Root["materials"] = JsonNode.Parse("""[{"name":"first"},{"name":"second"}]""");
        fixture.Primitives[0]!["material"] = 0;
        JsonObject second = fixture.Primitives[0]!.DeepClone().AsObject();
        second["material"] = 1;
        fixture.Primitives.Add(second);
        return fixture;
    }

    static void SetWeights(SkinnedAttachmentGlbFixture fixture, Vector4 weights)
    {
        for (int vertex = 0; vertex < 3; vertex++) fixture.SetElement(2, vertex, weights.X, weights.Y, weights.Z, weights.W);
    }

    static void AddSet(SkinnedAttachmentGlbFixture fixture, int set, Vector4 joints, Vector4 weights, int jointComponentType = 5121)
    {
        int indices = AddAccessor(fixture, "VEC4", Enumerable.Repeat(new[] { joints.X, joints.Y, joints.Z, joints.W }, 3).ToArray(), jointComponentType);
        int values = AddAccessor(fixture, "VEC4", Enumerable.Repeat(new[] { weights.X, weights.Y, weights.Z, weights.W }, 3).ToArray());
        foreach (JsonNode? primitive in fixture.Primitives)
        {
            primitive!["attributes"]![$"JOINTS_{set}"] = indices;
            primitive["attributes"]![$"WEIGHTS_{set}"] = values;
        }
    }

    static int AddAccessor(SkinnedAttachmentGlbFixture fixture, string type, float[][] values, int componentType = 5126)
    {
        int index = fixture.Accessors.Count;
        int size = componentType == 5121 ? 1 : sizeof(float);
        fixture.Accessors.Add(new JsonObject
        {
            ["bufferView"] = fixture.AddView(new byte[values.Length * values[0].Length * size]),
            ["componentType"] = componentType, ["count"] = values.Length, ["type"] = type,
        });
        for (int element = 0; element < values.Length; element++) fixture.SetElement(index, element, values[element]);
        return index;
    }

    static void WithPath(byte[] glb, Action<string> action)
    {
        string directory = Path.Combine("/tmp/khaozengine-dq-skinning", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.glb");
        File.WriteAllBytes(path, glb);
        try { action(path); }
        finally { Directory.Delete(directory, recursive: true); }
    }

    static SkinnedGltfMesh Load(ReadOnlyMemory<byte> glb, string identity, SkinningProfile profile) =>
        GltfLoader.LoadSkinned(glb, identity, profile);

    static SkinnedGltfMesh LoadPath(string path) => GltfLoader.LoadSkinned(path, SkinningProfile.DualQuaternionRigid);

    static System.Collections.Generic.IReadOnlyList<GltfSkinnedMeshPart> LoadParts(ReadOnlyMemory<byte> glb, string identity, SkinningProfile profile) =>
        GltfLoader.LoadSkinnedPartsWithMaterials(glb, identity, profile);

    static System.Collections.Generic.IReadOnlyList<GltfSkinnedMeshPart> LoadPathParts(string path) =>
        GltfLoader.LoadSkinnedPartsWithMaterials(path, SkinningProfile.DualQuaternionRigid);

    static (SkinnedGltfMesh Mesh, GltfMaterialMaps Maps) LoadPathWithMaterial(string path) =>
        GltfLoader.LoadSkinnedWithMaterial(path, SkinningProfile.DualQuaternionRigid);

    static void ValidatePose(SkinnedGltfMesh mesh, ReadOnlySpan<Matrix4x4> joints, in SkinningValidationContext context) =>
        SkinningValidation.ValidatePose(mesh, joints, context);
}
