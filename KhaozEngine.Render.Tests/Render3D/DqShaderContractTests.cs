using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
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
        using var commands = new CommandTallyGpuCommandList(new NullGpuCommandList());
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
        });
        Assert.Contains(bound, p => p.VertexGlsl == ShaderSources.SkinnedModelVert);
    }
}
