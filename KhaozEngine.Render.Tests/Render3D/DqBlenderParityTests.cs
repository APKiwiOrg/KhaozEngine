using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqBlenderParityTests
{
    static readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory,
        "Fixtures", "dual-quaternion", "blender-rigid");

    [Fact]
    public void Rigid_export_matches_preserve_volume_with_matching_inputs()
    {
        using JsonDocument document = ReadFixture();
        JsonElement root = document.RootElement;
        SkinnedGltfMesh mesh = ReadMesh(root);
        AnimationClip clip = Assert.Single(GltfLoader.LoadAnimations(Path.Combine(DirectoryPath, "rigid.glb")));
        int comparisons = 0;
        foreach (JsonElement sample in root.GetProperty("samples").EnumerateArray())
        {
            if (sample.GetProperty("kind").GetString() != "parity") continue;
            RigidDualQuaternion[] recorded = Prepare(mesh, sample, sample.GetProperty("jointWorldWords")
                .EnumerateArray().Select(ReadMatrix).ToArray());
            var joints = new Matrix4x4[mesh.BoneCount];
            AnimationSampler.Compose(mesh.Skeleton!, AnimationSampler.SamplePose(clip, mesh.Skeleton!,
                sample.GetProperty("timeSeconds").GetSingle()), joints);
            RigidDualQuaternion[] exported = Prepare(mesh, sample, joints);
            float L = LengthScale(mesh, recorded);
            foreach (JsonElement vertex in root.GetProperty("vertices").EnumerateArray())
            {
                string kind = vertex.GetProperty("case").GetString()!;
                if (kind == "ambiguous-four") continue;
                SkinnedVertex input = mesh.Vertices[vertex.GetProperty("exportedVertex").GetInt32()];
                SkinnedVertex sourceOrder = SourceOrder(input, vertex);
                if (kind == "four-unambiguous") AssertPositivePairwiseDots(sourceOrder, recorded);
                if (kind == "two-influence")
                    Assert.True(MathF.Abs(Vector4.Dot(recorded[(int)sourceOrder.BoneIndices.X].Real,
                        recorded[(int)sourceOrder.BoneIndices.Y].Real)) > .01f);
                Vector3 blenderPosition = Read3(sample.GetProperty("blenderPositions")
                    [vertex.GetProperty("sourceVertex").GetInt32()]);
                AssertPosition(DualQuaternionSkinning.Position(input, recorded), blenderPosition, L);
                AssertPosition(DualQuaternionSkinning.Position(sourceOrder, recorded), blenderPosition, L);
                AssertPosition(DualQuaternionSkinning.Position(input, exported), blenderPosition, L);
                comparisons++;
            }
        }
        Assert.Equal(27, comparisons);
        Assert.Contains(root.GetProperty("vertices").EnumerateArray(), vertex =>
            vertex.GetProperty("case").GetString() == "pivot" && Read3(vertex.GetProperty("sourcePosition")).Length() > 1f);
    }

    [Fact]
    public void Ambiguous_reordering_is_reported_not_hidden()
    {
        using JsonDocument document = ReadFixture();
        JsonElement root = document.RootElement;
        SkinnedGltfMesh mesh = ReadMesh(root);
        JsonElement sample = Assert.Single(root.GetProperty("samples").EnumerateArray(),
            item => item.GetProperty("kind").GetString() == "ambiguous");
        RigidDualQuaternion[] palette = Prepare(mesh, sample, sample.GetProperty("jointWorldWords")
            .EnumerateArray().Select(ReadMatrix).ToArray());
        float L = LengthScale(mesh, palette);
        int comparisons = 0;
        foreach (JsonElement vertex in root.GetProperty("vertices").EnumerateArray())
        {
            if (vertex.GetProperty("case").GetString() != "ambiguous-four") continue;
            int sourceIndex = vertex.GetProperty("sourceVertex").GetInt32();
            SkinnedVertex input = mesh.Vertices[vertex.GetProperty("exportedVertex").GetInt32()];
            SkinnedVertex sourceOrder = SourceOrder(input, vertex);
            Assert.NotEqual(sourceOrder.BoneIndices, input.BoneIndices);
            Vector3 ambiguousSourceOrderPosition = DualQuaternionSkinning.Position(sourceOrder, palette);
            Vector3 ambiguousExportOrderPosition = DualQuaternionSkinning.Position(input, palette);
            Assert.False(ambiguousSourceOrderPosition == ambiguousExportOrderPosition);
            Assert.True(Vector3.Distance(ambiguousSourceOrderPosition, ambiguousExportOrderPosition) > .1f);
            AssertPosition(ambiguousSourceOrderPosition, Read3(sample.GetProperty("blenderPositions")[sourceIndex]), L);
            AssertPosition(ambiguousExportOrderPosition, Read3(sample.GetProperty("reorderedBlenderPositions")[sourceIndex]), L);
            Assert.True(sample.GetProperty("divergentOutcome").GetProperty("maxPositionDistance").GetSingle() > .1f);
            comparisons++;
        }
        Assert.Equal(3, comparisons);
    }

    [Fact]
    public void Unsupported_source_fixtures_are_outside_the_parity_promise()
    {
        using JsonDocument document = ReadFixture();
        JsonElement excluded = document.RootElement.GetProperty("excluded");
        Assert.Equal(new[] { "scale", "shear", "five-positive", "additional-modifier" },
            excluded.EnumerateArray().Select(item => item.GetProperty("name").GetString()));
        foreach (JsonElement item in excluded.EnumerateArray())
        {
            Assert.Equal("outside-promise", item.GetProperty("disposition").GetString());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("reason").GetString()));
            string name = item.GetProperty("name").GetString()!;
            if (name is "scale" or "shear")
            {
                Matrix4x4 matrix = ReadMatrix(item.GetProperty("composedWords"));
                Assert.Throws<ArgumentException>(() => DualQuaternionPalette.FromComposed(matrix,
                    new SkinningValidationContext("blender-excluded", name, 49), 0));
            }
            if (name == "five-positive")
                Assert.Equal(5, item.GetProperty("sourceWeights").EnumerateArray().Count(w => w.GetSingle() > 0f));
            if (name == "additional-modifier")
                Assert.Equal(new[] { "ARMATURE", "SIMPLE_DEFORM" }, item.GetProperty("modifierStack")
                    .EnumerateArray().Select(value => value.GetString()));
        }
    }

    static JsonDocument ReadFixture() => JsonDocument.Parse(File.ReadAllText(Path.Combine(DirectoryPath, "expected.json")));

    static SkinnedGltfMesh ReadMesh(JsonElement root)
    {
        var mesh = GltfLoader.LoadSkinned(Path.Combine(DirectoryPath, "rigid.glb"), SkinningProfile.DualQuaternionRigid);
        Assert.Equal(root.GetProperty("jointNames").EnumerateArray().Select(value => value.GetString()),
            mesh.Skeleton!.JointToNode.Select(index => mesh.Skeleton.NodeNames[index]));
        for (int i = 0; i < mesh.BoneCount; i++)
        {
            Matrix4x4 matrix = mesh.InverseBind[i];
            Assert.Equal(root.GetProperty("inverseBindWords")[i].EnumerateArray().Select(value => value.GetUInt32()),
                MemoryMarshal.Cast<Matrix4x4, uint>(MemoryMarshal.CreateReadOnlySpan(ref matrix, 1)).ToArray());
        }
        Assert.Equal(12, mesh.Vertices.Length);
        Assert.Equal(12, root.GetProperty("vertices").GetArrayLength());
        foreach (JsonElement vertex in root.GetProperty("vertices").EnumerateArray())
        {
            SkinnedVertex exported = mesh.Vertices[vertex.GetProperty("exportedVertex").GetInt32()];
            Assert.Equal(Read3(vertex.GetProperty("sourcePosition")), exported.Position);
            Assert.Equal(Read4(vertex.GetProperty("exportedOrder")), exported.BoneIndices);
            Assert.Equal(Read4(vertex.GetProperty("exportedWeights")), exported.BoneWeights);
            SkinnedVertex source = SourceOrder(exported, vertex);
            for (int slot = 0; slot < 4; slot++)
            {
                if (source.BoneWeights[slot] <= 0f) continue;
                int match = Enumerable.Range(0, 4).Single(i => exported.BoneWeights[i] > 0f &&
                    exported.BoneIndices[i] == source.BoneIndices[slot]);
                Assert.Equal(source.BoneWeights[slot], exported.BoneWeights[match]);
            }
        }
        return mesh;
    }

    static RigidDualQuaternion[] Prepare(SkinnedGltfMesh mesh, JsonElement sample, Matrix4x4[] joints)
    {
        var palette = new RigidDualQuaternion[mesh.BoneCount];
        DualQuaternionPalette.Prepare(joints, mesh.InverseBind, palette,
            new SkinningValidationContext("public-blender-rigid", "rigid", sample.GetProperty("frame").GetInt32()));
        return palette;
    }

    static SkinnedVertex SourceOrder(SkinnedVertex input, JsonElement vertex)
    {
        input.BoneIndices = Read4(vertex.GetProperty("sourceOrder"));
        input.BoneWeights = Read4(vertex.GetProperty("sourceWeights"));
        return input;
    }

    static void AssertPositivePairwiseDots(SkinnedVertex vertex, RigidDualQuaternion[] palette)
    {
        for (int i = 0; i < 4; i++)
            for (int j = i + 1; j < 4; j++)
                Assert.True(Vector4.Dot(palette[(int)vertex.BoneIndices[i]].Real,
                    palette[(int)vertex.BoneIndices[j]].Real) > 0f);
    }

    static float LengthScale(SkinnedGltfMesh mesh, RigidDualQuaternion[] palette)
    {
        float result = MathF.Max(1f, mesh.Vertices.Max(vertex => vertex.Position.Length()));
        foreach (RigidDualQuaternion joint in palette)
        {
            var input = new SkinnedVertex { BoneWeights = Vector4.UnitX };
            result = MathF.Max(result, DualQuaternionSkinning.Position(input, [joint]).Length());
        }
        return result;
    }

    static void AssertPosition(Vector3 enginePosition, Vector3 blenderPosition, float L)
    {
        for (int axis = 0; axis < 3; axis++)
            Assert.InRange(MathF.Abs(enginePosition[axis] - blenderPosition[axis]), 0f, 2e-5f * L);
    }

    static Vector3 Read3(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
    static Vector4 Read4(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle(), value[3].GetSingle());

    static Matrix4x4 ReadMatrix(JsonElement value)
    {
        uint[] words = value.EnumerateArray().Select(word => word.GetUInt32()).ToArray();
        Assert.Equal(16, words.Length);
        return MemoryMarshal.Cast<uint, Matrix4x4>(words.AsSpan())[0];
    }
}
