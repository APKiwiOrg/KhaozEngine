using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class DqCascadeContractTests
{
    [Fact]
    public void All_cascade_variants_request_the_selected_palette()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        DqCascadeScene.Configure(scene);
        scene.ReplaceShadowLayout(256, 4);
        scene.Begin();
        foreach (int variant in new[] { 0, 1, 2 })
        foreach (SkinningProfile profile in new[] { SkinningProfile.LinearBlend, SkinningProfile.DualQuaternionRigid })
        {
            SkinnedGltfMesh mesh = DqCascadeScene.Patch(profile);
            var handle = scene.LoadSkinnedMesh(mesh);
            DqGpuReadback.Queue(scene, mesh, new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
            {
                ShadowOnly = true, Dissolve = variant == 0 ? 0 : .5f, InvertShadowDissolve = variant == 2,
            }, DqCascadeScene.HalfTurn);
        }
        scene.PrepareFrame();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList())
            { CaptureBindings = true, CapturePayloads = true };
        scene.RenderInternal(commands, MotionTestScene.Width, MotionTestScene.Height, harness.Target);
        var draws = commands.Bindings.Where(d => d.Pipeline is FakePipeline { Request: { } p }
            && p.VertexGlsl.Contains("uniform Palette", StringComparison.Ordinal)
            && p.VertexGlsl.Contains("LightMvp", StringComparison.Ordinal)).ToArray();
        Assert.Equal(24, draws.Length);
        string helper = Assert.IsType<string>(DqGpuReadback.Helper("dqBones"));
        uint[] paletteOffsets = [0, 0, 8192, 4096, 16384, 8192];
        for (int c = 0; c < 4; c++)
        for (int d = 0; d < 6; d++)
        {
            var draw = draws[c * 6 + d];
            var request = Assert.IsType<FakePipeline>(draw.Pipeline).Request!.Value;
            Assert.Equal((uint)((c * 6 + d) * 256), draw.Sets[0].DynamicOffset);
            Assert.Equal(paletteOffsets[d], draw.Sets[1].DynamicOffset);
            bool dq = d % 2 == 1;
            if (dq) Assert.Contains(helper, request.VertexGlsl);
            else Assert.Equal(d < 2 ? ShaderSources.SkinnedShadowDepthVert : ShaderSources.SkinnedShadowDepthDissolveVert,
                request.VertexGlsl);
            Assert.Equal(d < 2 ? ShaderSources.ShadowDepthFrag : d < 4
                ? ShaderSources.SkinnedShadowDepthDissolveFrag : ShaderSources.SkinnedShadowDepthDissolveInvertedFrag,
                request.FragmentGlsl);
            Assert.Same(draws[dq ? 1 : 0].Sets[1].Set, draw.Sets[1].Set);
            Assert.NotSame(draws[dq ? 0 : 1].Sets[1].Set, draw.Sets[1].Set);
        }
        foreach (var palette in draws.Take(2).Select(d => Assert.IsType<FakeResourceSet>(d.Sets[1].Set)))
        {
            var buffer = Assert.Single(palette.Resources.OfType<GpuBufferRange>()).Buffer;
            Assert.Single(commands.Uploads, u => ReferenceEquals(u.Buffer, buffer));
        }
        Assert.Equal(4, scene.LastShadowPassDiagnostics.CascadeCount);
        Assert.Equal(24, scene.LastShadowPassDiagnostics.SkinnedDrawCalls);
        Assert.Equal(0, scene.DrawnSkinnedInstances);
        // 8-slot LBS + DQ palettes, plus the actual 24-slot shadow header image. No main draw header upload.
        Assert.Equal(65536 + 32768 + 24 * 256, scene.LastFrameStats.SkinnedUniformUploadBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unready_consumers_still_refuse_DQ(bool gpu)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        var mesh = DqCascadeScene.Patch(SkinningProfile.DualQuaternionRigid);
        var handle = scene.LoadSkinnedMesh(mesh);
        scene.Begin();
        Assert.Throws<NotSupportedException>(() => scene.DrawSkinned(handle, DqCascadeScene.HalfTurn,
            Matrix4x4.Identity, Color.White));
        foreach (MeshOutlineOcclusion occlusion in new[] { MeshOutlineOcclusion.SceneDepth, MeshOutlineOcclusion.None })
            Assert.Throws<NotSupportedException>(() => scene.DrawSkinnedOutline(handle, DqCascadeScene.HalfTurn,
                Matrix4x4.Identity, Color.White, 1, occlusion));
        Assert.Empty(scene.QueuedSkinnedInstancesForTests);

        scene.ForceTemporalForTests = true;
        DqGpuReadback.Queue(scene, mesh, new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
            { CastsShadows = false }, DqCascadeScene.HalfTurn);
        Assert.Throws<NotSupportedException>(() => harness.Render());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Internal_point_shadow_draws_still_refuse_DQ(bool gpu)
    {
        using var harness = new MotionTestScene();
        var scene = harness.Scene;
        scene.UseGpuSkinning = gpu;
        scene.FrustumCulling = false;
        scene.Begin();
        scene.AddLight(new Vector3(0, 0, 3), Color.White, 10, 1, LightShadow.Dynamic);
        var mesh = DqCascadeScene.Patch(SkinningProfile.DualQuaternionRigid);
        var handle = scene.LoadSkinnedMesh(mesh);
        DqGpuReadback.Queue(scene, mesh, new SkinnedInstanceDraw(handle, Matrix4x4.Identity)
            { ShadowOnly = true }, DqCascadeScene.HalfTurn);
        Assert.Throws<NotSupportedException>(() => harness.Render());
    }
}
