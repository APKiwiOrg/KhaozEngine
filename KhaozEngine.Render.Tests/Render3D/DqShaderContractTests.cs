using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqShaderContractTests
{
    [Fact]
    public void CPU_execution_still_uses_DQ()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = false;
        SkinnedGltfMesh mesh = DqGpuReadback.Patch(SkinningProfile.DualQuaternionRigid);
        SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        Matrix4x4[] joints = DqGpuReadback.Twist;
        PreparedSkinningPose pose = DqGpuReadback.Queue(scene, mesh, new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
        { CastsShadows = false }, joints);
        RigidDualQuaternion[] prepared = pose.Owner.Dq(pose).ToArray();
        // Neither the caller's pose nor a recomposed matrix slice is the authority for CPU DQ execution.
        joints[1] = Matrix4x4.CreateTranslation(99, 99, 99);
        harness.Render();
        var actual = (List<ModelVertex>)DqGpuReadback.Field(scene, "_cpuSkinnedVerts");
        Assert.Equal(mesh.Vertices.Length, actual.Count);
        for (int i = 0; i < actual.Count; i++)
        {
            ModelVertex expected = SkinningEvaluation.Vertex(mesh.Vertices[i], mesh.Profile, [], prepared);
            Assert.Equal(expected.Position, actual[i].Position);
            Assert.Equal(expected.Normal, actual[i].Normal);
            Assert.Equal(expected.Tangent, actual[i].Tangent);
            Assert.True(Vector3.Distance(actual[i].Position,
                SkinningMath.SkinVertex(mesh.Vertices[i], DqGpuReadback.Twist).Position) > .1f);
        }
    }

    [Fact]
    public void Material_variants_share_the_DQ_graph()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.Begin();
        foreach (SkinningProfile profile in new[] { SkinningProfile.LinearBlend, SkinningProfile.DualQuaternionRigid })
        {
            SkinnedGltfMesh mesh = DqGpuReadback.Patch(profile);
            SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh);
            foreach (bool dissolve in new[] { false, true })
                DqGpuReadback.Queue(scene, mesh, new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
                { CastsShadows = false, Dissolve = dissolve ? .5f : 0 }, DqGpuReadback.Twist);
        }
        scene.PrepareFrame();
        using var recorded = new RecordingGpuCommandList(new NullGpuCommandList()) { CaptureBindings = true };
        using var commands = new CommandTallyGpuCommandList(recorded);
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        var bound = commands.PipelineBinds.OfType<FakePipeline>().Select(p => p.Request)
            .Where(p => p.HasValue).Select(p => p!.Value).ToArray();
        string? helper = DqGpuReadback.Helper("dqBones");
        Assert.NotNull(helper);
        var dq = bound.Where(p => p.VertexGlsl.Contains(helper, StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, dq.Length);
        Assert.Equal(dq[0].VertexGlsl, dq[1].VertexGlsl);
        Assert.Contains(dq, p => p.FragmentGlsl == ShaderSources.SkinnedModelFrag);
        Assert.Contains(dq, p => p.FragmentGlsl == ShaderSources.SkinnedModelDissolveFrag);
        Assert.All(dq, p =>
        {
            Assert.DoesNotContain("mat4 bones[128]", p.VertexGlsl);
            Assert.DoesNotContain("mat4 skin", p.VertexGlsl);
            Assert.Contains("vNormalW = normalize(mat3(Model) * nLocal);", p.VertexGlsl);
            Assert.Contains("vTangent = vec4(mat3(Model) * tLocal.xyz, tLocal.w);", p.VertexGlsl);
            // Compile the real programs without a device. Numerical evidence still comes from GpuFact readback.
            CrossCompiledPair compiled = SpirvCrossCompile.GlslPairToHlsl(p.VertexGlsl, p.FragmentGlsl, "DQ material");
            Assert.NotEmpty(compiled.VertexSource);
        });
        Assert.Contains(bound, p => p.VertexGlsl == ShaderSources.SkinnedModelVert);
        var draws = recorded.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p } &&
            (p.VertexGlsl == ShaderSources.SkinnedModelVert || p.VertexGlsl == DqGpuReadback.MaterialVertex)).ToArray();
        Assert.Equal(4, draws.Length);
        Assert.Equal(new uint[] { 0, 256, 512, 768 }, draws.Select(d => d.Sets[0].DynamicOffset));
        Assert.Equal(new uint[] { 0, 8192, 0, 4096 }, draws.Select(d => d.Sets[2].DynamicOffset));
        Assert.NotSame(draws[0].Sets[2].Set, draws[2].Sets[2].Set);
    }

    [Fact]
    public void Material_variants_follow_output_changes_and_release_their_shaders()
    {
        using var device = new FakeGpuDevice();
        var factory = (FakeGpuResourceFactory)device.Factory;
        FakeShaderSet[] dqShaders;
        using (var renderer = new ModelRenderer(device,
            new GpuOutputDescription(GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R32Float), 64, 1))
        {
            using var commands = new CommandTallyGpuCommandList(new NullGpuCommandList());
            renderer.BindSkinnedMaterialPass(commands, SkinningProfile.DualQuaternionRigid, dissolve: false);
            dqShaders = factory.ShaderSets.Where(s => s.VertexGlsl == DqGpuReadback.MaterialVertex).ToArray();
            Assert.Equal(2, dqShaders.Length);
            var original = Assert.IsType<FakePipeline>(commands.PipelineBinds[^1]);
            renderer.SetOutputs(original.Request!.Value.Description.Outputs.WithSampleCount(4));
            renderer.BindSkinnedMaterialPass(commands, SkinningProfile.DualQuaternionRigid, dissolve: true);
            var rebuilt = Assert.IsType<FakePipeline>(commands.PipelineBinds[^1]);
            Assert.NotSame(original, rebuilt);
            Assert.Equal(4, rebuilt.Request!.Value.Description.Outputs.SampleCount);
            Assert.All(dqShaders, s => Assert.False(s.Disposed));
            Assert.Equal(2, factory.ShaderSets.Count(s => s.VertexGlsl == DqGpuReadback.MaterialVertex));
        }
        Assert.All(dqShaders, s => Assert.True(s.Disposed));
    }
}
