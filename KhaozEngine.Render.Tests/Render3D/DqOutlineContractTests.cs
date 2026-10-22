using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqOutlineContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outline_only_submission_retains_profile_and_validates_first(bool gpu)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        SkinnedGltfMesh mesh = DqOutlineScene.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        var store = (PreparedSkinningStore)DqGpuReadback.Field(scene, "_preparedSkinning");
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.DrawSkinnedOutline(handle,
            mesh.RestPose, Matrix4x4.Identity, Color.White, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => scene.DrawSkinnedOutline(handle,
            mesh.RestPose, Matrix4x4.Identity, Color.White, 1, (MeshOutlineOcclusion)2));
        Assert.Throws<ArgumentException>(() => scene.DrawSkinnedOutline(handle,
            [Matrix4x4.Identity, Matrix4x4.CreateScale(2)], Matrix4x4.Identity, Color.White, 1));
        Matrix4x4 invalidPlacement = Matrix4x4.Identity;
        invalidPlacement.M14 = .5f;
        Assert.Throws<ArgumentException>(() => scene.DrawSkinnedOutline(handle,
            mesh.RestPose, invalidPlacement, Color.White, 1));
        Assert.Equal(0, store.PoseCount);
        Assert.Equal(0, scene.MeshOutlinePartCount);
        Assert.Equal(0, scene.MeshOutlineGroupCount);

        Matrix4x4[] joints = DqCascadeScene.HalfTurn;
        MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 2);
        scene.DrawSkinnedOutline(group, handle, joints, Matrix4x4.Identity);
        joints[1] = Matrix4x4.CreateScale(99);
        Assert.Equal(1, store.PoseCount);
        scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        { CaptureBindings = true, CapturePayloads = true };
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        int colourDrawCount = scene.DrawnSkinnedInstances;
        Assert.Equal(0, colourDrawCount);
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);
        Assert.Equal(1, store.PoseCount);
        var masks = commands.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p }
            && p.FragmentGlsl.Contains("oCoverage", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, masks.Length);
        if (gpu)
        {
            foreach (var draw in masks)
            {
                var request = Assert.IsType<FakePipeline>(draw.Pipeline).Request!.Value;
                Assert.Contains(DualQuaternionGlsl.Source("dqBones"), request.VertexGlsl);
                Assert.DoesNotContain("mat4 skin", request.VertexGlsl);
                Assert.NotEmpty(SpirvCrossCompile.GlslPairToHlsl(request.VertexGlsl, request.FragmentGlsl,
                    "DQ outline").VertexSource);
                var set = Assert.IsType<FakeResourceSet>(draw.Sets[2].Set);
                var range = Assert.Single(set.Resources.OfType<GpuBufferRange>());
                Assert.Equal(4096u, range.Size);
            }
        }
        else
        {
            var upload = Assert.Single(commands.Uploads, u => u.Bytes == mesh.Vertices.Length * ModelVertex.SizeInBytes);
            ModelVertex[] actual = MemoryMarshal.Cast<byte, ModelVertex>(upload.Data!).ToArray();
            for (int i = 0; i < actual.Length; i++)
                Assert.Equal(new Vector3(-mesh.Vertices[i].Position.Y, mesh.Vertices[i].Position.X, 0), actual[i].Position);
        }
        scene.Begin();
        Assert.Equal(0, store.PoseCount);
        Assert.Equal(0, scene.MeshOutlinePartCount);
    }

    [Fact]
    public void Mixed_outline_methods_and_groups_bind_independent_slots_and_retire_variants()
    {
        FakeShaderSet[] shaders;
        using (var harness = new MotionTestScene())
        {
            Scene3D scene = harness.Scene;
            var meshes = new[] { DqOutlineScene.Patch(SkinningProfile.LinearBlend),
                DqOutlineScene.Patch(SkinningProfile.DualQuaternionRigid) };
            var handles = meshes.Select(m => scene.LoadSkinnedMesh(m)).ToArray();
            scene.Begin();
            for (int groupIndex = 0; groupIndex < 2; groupIndex++)
            {
                MeshOutlineGroup group = scene.BeginMeshOutline(Color.White, 2);
                for (int actor = 0; actor < 4; actor++)
                {
                    int method = actor % 2;
                    DqOutlineScene.Queue(scene, meshes[method], group, handles[method],
                        actor < 2 ? DqCascadeScene.HalfTurn : meshes[method].RestPose, Matrix4x4.Identity);
                }
            }
            scene.PrepareFrame();
            using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
            { CaptureBindings = true, CapturePayloads = true };
            scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
            var masks = commands.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p }
                && p.FragmentGlsl.Contains("oCoverage", StringComparison.Ordinal)).ToArray();
            Assert.Equal(16, masks.Length);
            Assert.Equal(new uint[] { 0, 0, 8192, 4096, 0, 0, 8192, 4096,
                16384, 8192, 24576, 12288, 16384, 8192, 24576, 12288 },
                masks.Select(d => d.Sets[2].DynamicOffset));
            Assert.NotSame(masks[0].Sets[2].Set, masks[1].Sets[2].Set);
            Assert.All(masks.Where((_, i) => i % 2 == 0), d =>
                Assert.Equal(ShaderSources.TargetOutlineSkinnedMaskVert, Assert.IsType<FakePipeline>(d.Pipeline).Request!.Value.VertexGlsl));
            shaders = ((FakeGpuResourceFactory)harness.Device.Factory).ShaderSets.Where(s =>
                s.VertexGlsl.Contains("dqBonesSkin", StringComparison.Ordinal)
                && s.FragmentGlsl.Contains("oCoverage", StringComparison.Ordinal)).ToArray();
            Assert.Equal(2, shaders.Length);
        }
        Assert.All(shaders, s => Assert.True(s.Disposed));
    }

    [Fact]
    public void DQ_motion_is_admitted_with_its_selected_profile()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        SkinnedGltfMesh mesh = DqOutlineScene.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
            { Motion = MotionKey.From(12) }, DqCascadeScene.HalfTurn);
        harness.Render();
        Assert.Equal(1, scene.DrawnSkinnedInstances);
        Assert.True(scene.ActiveMotionHistory!.TryGetCurrentBounds(MotionKey.From(12), out MeshBounds bounds));
        DqCullingTests.Contains(bounds, new Vector3(.45f, -.7f, 0));
    }
}
