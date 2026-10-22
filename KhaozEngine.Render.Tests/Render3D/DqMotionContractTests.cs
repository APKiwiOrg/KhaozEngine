using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqMotionContractTests
{
    [Fact]
    public void DQ_motion_layout_reads_both_prepared_poses()
    {
        // A missing temporal family is a behavioral contract failure, not a compile failure at a missing API.
        Assert.True(DqMotionWitness.HasDq, "Missing previous DQ evaluation in the temporal material family.");
        Assert.Contains(DqGpuReadback.Helper("dqBones")!, DqMotionWitness.Vertex);
        Assert.Contains(DqGpuReadback.Helper("prevDqBones")!, DqMotionWitness.Vertex);
        CrossCompiledPair words = SpirvCrossCompile.GlslPairToHlsl(DqMotionWitness.CaptureVertex(), @"#version 450
layout(location=0) flat in uvec3 capturedCurrent;
layout(location=1) flat in uvec3 capturedPrevious;
layout(location=0) out vec4 color;
void main() { color = vec4(capturedCurrent.x + capturedPrevious.x); }", "DQ temporal capture");
        Assert.NotEmpty(words.VertexSource);
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        DqMotionScene.Configure(scene);
        var meshes = new[] { DqMotionWitness.Patch(SkinningProfile.LinearBlend),
            DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid), DqMotionWitness.Patch(SkinningProfile.LinearBlend),
            DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid) };
        SkinnedMeshHandle[] handles = meshes.Select(scene.LoadSkinnedMesh).ToArray();
        scene.Begin();
        uint[][] previousWords = new uint[4][];
        for (int i = 0; i < 4; i++)
        {
            var pose = DqMotionWitness.Queue(scene, meshes[i], handles[i], MotionKey.From((ulong)(i + 1)),
                Matrix4x4.CreateTranslation(i, 2, 3), DqMotionWitness.Previous, 0);
            if (i % 2 != 0) previousWords[i] = MemoryMarshal.Cast<RigidDualQuaternion, uint>(pose.Owner.Dq(pose)).ToArray();
        }
        harness.Render();
        scene.RenderOrigin = new Vector3(128, 0, 128);
        scene.Begin();
        for (int i = 0; i < 4; i++)
            DqMotionWitness.Queue(scene, meshes[i], handles[i], MotionKey.From((ulong)(i + 1)),
                Matrix4x4.CreateTranslation(i, 4, 5), DqMotionWitness.Current, i >= 2 ? .25f : 0);
        scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true, CapturePayloads = true };
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        Assert.NotNull(scene.PreviousFrameView);
        var draws = commands.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p }
            && (p.VertexGlsl == DqMotionWitness.Vertex || p.VertexGlsl == ShaderSources.SkinnedModelMotionVert)).ToArray();
        Assert.Equal(4, draws.Length);
        Assert.Equal(new uint[] { 0, 0, 8192, 4096 }, draws.Select(d => d.Sets[2].DynamicOffset));
        Assert.Equal(new uint[] { 0, 0, 8448, 4352 }, draws.Select(d => d.Sets[3].DynamicOffset));
        Assert.NotSame(draws[0].Sets[3].Set, draws[1].Sets[3].Set);
        for (int i = 0; i < 4; i++)
        {
            var pipeline = Assert.IsType<FakePipeline>(draws[i].Pipeline).Request!.Value;
            Assert.Equal(4, pipeline.Description.ResourceLayouts.Length);
            Assert.Equal(i >= 2 ? ShaderSources.SkinnedModelDissolveMotionFrag : ShaderSources.SkinnedModelMotionFrag,
                pipeline.FragmentGlsl);
            var set = Assert.IsType<FakeResourceSet>(draws[i].Sets[3].Set);
            var range = Assert.IsType<GpuBufferRange>(set.Resources[1]);
            byte[] bytes = Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, range.Buffer)).Data!;
            int offset = (int)draws[i].Sets[3].DynamicOffset;
            Assert.Equal(Matrix4x4.CreateTranslation(i - 128, 2, 3 - 128), MemoryMarshal.Read<Matrix4x4>(bytes.AsSpan(offset)));
            Assert.Equal(i % 2 == 0 ? 8448u : 4352u, range.Size);
            if (i % 2 != 0)
                Assert.Equal(previousWords[i], MemoryMarshal.Cast<byte, uint>(bytes.AsSpan(offset + 64, previousWords[i].Length * 4)).ToArray());
            else Assert.Equal(DqMotionWitness.Previous[1], MemoryMarshal.Read<Matrix4x4>(bytes.AsSpan(offset + 128)));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Replacement_seeds_only_its_actor_and_keeps_previous_DQ_words(bool gpu, bool method)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        DqMotionScene.Configure(scene);
        scene.UseGpuSkinning = gpu;
        SkinnedGltfMesh mesh = DqMotionWitness.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle a = scene.LoadSkinnedMesh(mesh), b = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        DqMotionWitness.Queue(scene, mesh, a, MotionKey.From(1), Matrix4x4.Identity, DqMotionWitness.Previous, 0);
        var oldB = DqMotionWitness.Queue(scene, mesh, b, MotionKey.From(2), Matrix4x4.CreateTranslation(3, 0, 0), DqMotionWitness.Previous, 0);
        uint[] expectedPreviousDqWords = MemoryMarshal.Cast<RigidDualQuaternion, uint>(oldB.Owner.Dq(oldB)).ToArray();
        harness.Render();
        scene.UnloadSkinnedMesh(a);
        SkinnedGltfMesh replacement = DqMotionWitness.Patch(method ? SkinningProfile.LinearBlend : SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle newA = scene.LoadSkinnedMesh(replacement);
        Assert.Equal(a.Index, newA.Index);
        Assert.NotEqual(a.Generation, newA.Generation);
        scene.Begin();
        DqMotionWitness.Queue(scene, replacement, newA, MotionKey.From(1), Matrix4x4.CreateTranslation(1, 0, 0), DqMotionWitness.Current, 0);
        DqMotionWitness.Queue(scene, mesh, b, MotionKey.From(2), Matrix4x4.CreateTranslation(4, 0, 0), DqMotionWitness.Current, .25f);
        scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true, CaptureBindings = true };
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        if (!gpu)
        {
            var previous = (System.Collections.Generic.List<Vector3>)DqGpuReadback.Field(scene, "_cpuSkinnedPrevious");
            for (int v = 0; v < 4; v++)
            {
                Assert.Equal(replacement.Vertices[v].Position + Vector3.UnitX, previous[v]);
                Vector3 p = mesh.Vertices[v].Position;
                Vector3 expected = new Vector3(-p.Y, p.X, p.Z) + new Vector3(3, 0, 0);
                Assert.InRange(Vector3.Distance(expected, previous[4 + v]), 0f, 2e-5f);
            }
        }
        else
        {
            var draws = commands.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p }
                && (p.VertexGlsl == DqMotionWitness.Vertex || p.VertexGlsl == ShaderSources.SkinnedModelMotionVert)).ToArray();
            Assert.Equal(2, draws.Length);
            var set = Assert.IsType<FakeResourceSet>(draws[1].Sets[3].Set);
            var range = Assert.IsType<GpuBufferRange>(set.Resources[1]);
            byte[] image = Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, range.Buffer)).Data!;
            int offset = (int)draws[1].Sets[3].DynamicOffset;
            Assert.Equal(Matrix4x4.CreateTranslation(3, 0, 0), MemoryMarshal.Read<Matrix4x4>(image.AsSpan(offset)));
            uint[] uploadedPreviousDqWords = MemoryMarshal.Cast<byte, uint>(image.AsSpan(offset + 64, expectedPreviousDqWords.Length * 4)).ToArray();
            Assert.Equal(expectedPreviousDqWords, uploadedPreviousDqWords);
            var seedSet = Assert.IsType<FakeResourceSet>(draws[0].Sets[3].Set);
            var seedRange = Assert.IsType<GpuBufferRange>(seedSet.Resources[1]);
            byte[] seed = Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, seedRange.Buffer)).Data!;
            Assert.Equal(Matrix4x4.CreateTranslation(1, 0, 0), MemoryMarshal.Read<Matrix4x4>(seed));
            if (!method) Assert.Equal(DqMotionWitness.Prepare(DqMotionWitness.Current),
                MemoryMarshal.Cast<byte, RigidDualQuaternion>(seed.AsSpan(64, 64)).ToArray());
            else Assert.Equal(Matrix4x4.Identity, MemoryMarshal.Read<Matrix4x4>(seed.AsSpan(64)));
        }
    }
}
