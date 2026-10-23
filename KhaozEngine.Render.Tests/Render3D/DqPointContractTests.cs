using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqPointContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Camera_culled_point_casters_keep_DQ_and_renderer_replacement_frees_variants(bool gpu)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        DqPointScene.Configure(scene);
        scene.UseGpuSkinning = gpu;
        var mesh = DqPointScene.Patch(SkinningProfile.DualQuaternionRigid);
        var handle = scene.LoadSkinnedMesh(mesh);
        for (int frame = 0; frame < 3; frame++)
        {
            scene.Begin();
            DqPointScene.Lights(scene, new Vector3(0, 0, 32));
            foreach (int variant in new[] { 0, 1, 2 })
                DqGpuReadback.Queue(scene, mesh, new SkinnedInstanceDraw(handle, Matrix4x4.CreateTranslation(0, 0, 32))
                { Dissolve = variant == 0 ? 0 : .5f, InvertShadowDissolve = variant == 2 }, DqCascadeScene.HalfTurn);
            harness.Render();
        }
        Assert.Equal(0, scene.DrawnSkinnedInstances);
        Assert.Equal(3, scene.CulledSkinnedInstances);
        Assert.Equal(18, scene.LastShadowPassDiagnostics.PointStaticTransientSkinnedDrawCalls);
        Assert.Equal(18, scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls);
        var shaders = ((FakeGpuResourceFactory)harness.Device.Factory).ShaderSets.Where(s =>
            s.VertexGlsl.Contains("dqBonesSkin", StringComparison.Ordinal)
            && s.VertexGlsl.Contains("LightPosRadius", StringComparison.Ordinal)).ToArray();
        Assert.Equal(gpu ? 3 : 0, shaders.Length);
        Assert.All(shaders, s => Assert.False(s.Disposed));
        scene.RequestPointShadowSettings(new PointShadowSettings { Enabled = false });
        scene.Begin();
        Assert.All(shaders, s => Assert.True(s.Disposed));
    }

    [Fact]
    public void Point_variant_selection_covers_all_face_row_kinds()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        DqPointScene.Configure(scene);
        var meshes = new[] { DqPointScene.Patch(SkinningProfile.LinearBlend),
            DqPointScene.Patch(SkinningProfile.DualQuaternionRigid) };
        var handles = meshes.Select(m => scene.LoadSkinnedMesh(m)).ToArray();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
        { CaptureBindings = true, CapturePayloads = true };
        // Allocate the normal static, dynamic and transient rows through their frame-boundary lifecycle.
        for (int frame = 0; frame < 3; frame++)
        {
            scene.Begin();
            DqPointScene.Lights(scene, Vector3.Zero);
            foreach (int variant in new[] { 0, 1, 2 })
                for (int method = 0; method < 2; method++)
                    DqGpuReadback.Queue(scene, meshes[method], new SkinnedInstanceDraw(handles[method], Matrix4x4.Identity)
                    {
                        ShadowOnly = true,
                        Dissolve = variant == 0 ? 0 : .5f,
                        InvertShadowDissolve = variant == 2,
                    }, DqCascadeScene.HalfTurn);
            scene.PrepareFrame();
            commands.Clear();
            scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        }
        var draws = commands.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p }
            && p.VertexGlsl.Contains("uniform Palette", StringComparison.Ordinal)
            && p.VertexGlsl.Contains("LightPosRadius", StringComparison.Ordinal)).ToArray();
        Assert.Equal(72, draws.Length);
        int observedFaceMask = 0;
        string helper = Assert.IsType<string>(DqGpuReadback.Helper("dqBones"));
        for (int group = 0; group < 12; group++)
            for (int caster = 0; caster < 6; caster++)
            {
                var draw = draws[group * 6 + caster];
                var request = Assert.IsType<FakePipeline>(draw.Pipeline).Request!.Value;
                observedFaceMask |= 1 << (int)(draw.Sets[0].DynamicOffset / 256 % 6);
                Assert.Equal((uint)(caster * 256), draw.Sets[1].DynamicOffset);
                Assert.Equal((uint)(caster / 2 * (caster % 2 == 1 ? 4096 : 8192)), draw.Sets[2].DynamicOffset);
                if (caster % 2 == 1) Assert.Contains(helper, request.VertexGlsl);
                else Assert.Equal(caster < 2 ? ShaderSources.PointShadowSkinnedVert
                    : ShaderSources.PointShadowSkinnedDissolveVert, request.VertexGlsl);
                Assert.Equal(caster < 2 ? ShaderSources.PointShadowRigidFrag : caster < 4
                    ? ShaderSources.PointShadowSkinnedDissolveFrag : ShaderSources.PointShadowSkinnedDissolveInvertedFrag,
                    request.FragmentGlsl);
                Assert.Same(draws[caster % 2].Sets[2].Set, draw.Sets[2].Set);
                Assert.NotSame(draws[1 - caster % 2].Sets[2].Set, draw.Sets[2].Set);
            }
        Assert.Equal(0x3f, observedFaceMask);
        Assert.Equal(12, draws.Select(d => d.Sets[0].DynamicOffset).Distinct().Count());
        bool staticTransientRowsObserved = scene.LastShadowPassDiagnostics.PointStaticTransientSkinnedDrawCalls == 36;
        bool dynamicRowsObserved = scene.LastShadowPassDiagnostics.PointDynamicSkinnedDrawCalls == 36;
        Assert.True(staticTransientRowsObserved && dynamicRowsObserved);
        Assert.Equal(0, scene.DrawnSkinnedInstances);
        foreach (var palette in draws.Take(2).Select(d => Assert.IsType<FakeResourceSet>(d.Sets[2].Set)))
        {
            var buffer = Assert.Single(palette.Resources.OfType<GpuBufferRange>()).Buffer;
            Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, buffer));
        }
        foreach (var pipeline in draws.Select(d => Assert.IsType<FakePipeline>(d.Pipeline)).Distinct())
        {
            var p = pipeline.Request!.Value;
            Assert.NotEmpty(SpirvCrossCompile.GlslPairToHlsl(p.VertexGlsl, p.FragmentGlsl, "DQ point").VertexSource);
        }
    }
}
