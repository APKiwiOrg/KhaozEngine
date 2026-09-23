using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of which depth pipeline each caster takes in the key light's cascade pass: the alpha-cutout
    /// pipeline for a MASK caster (issue #15). The pure rules (<see cref="ShadowDepthSelection"/>) are asserted
    /// directly, and their wiring is asserted on a real <see cref="Scene3D"/> frame recorded against
    /// <see cref="FakeGpuDevice"/>, which remembers the shader sources behind every pipeline the pass binds. The
    /// pixel proof is in <c>AlphaCutoutShadowGpuTests</c>.
    /// </summary>
    public sealed class ShadowDepthSelectionTests
    {
        const int W = 128, H = 96;

        // ---- the pure rules -------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0f, false, false)]
        [InlineData(0f, true, false)]       // an opaque mesh never cuts out, whatever it was loaded with
        [InlineData(0.5f, false, false)]    // a MASK mesh with no albedo samples white in the colour pass too
        [InlineData(0.5f, true, true)]
        public void A_mesh_cuts_out_its_shadow_only_with_a_cutoff_and_an_albedo(float cutoff, bool hasSet, bool expected)
            => Assert.Equal(expected, ShadowDepthSelection.MeshCutsOutShadow(cutoff, hasSet));

        [Fact]
        public void Each_rigid_span_takes_the_pipeline_for_its_kind_and_its_mesh()
        {
            // An opaque mesh keeps today's three pipelines, and a MASK mesh routes every kind through a cutout one:
            // a dissolving MASK caster must still alpha-test, and the inverted crossfade half keeps its dither.
            var table = new (ShadowCastKind Kind, bool CutsOut, ShadowDepthVariant Expected)[]
            {
                (ShadowCastKind.Opaque, false, ShadowDepthVariant.Opaque),
                (ShadowCastKind.Dissolving, false, ShadowDepthVariant.Dissolve),
                (ShadowCastKind.DissolvingInverted, false, ShadowDepthVariant.DissolveInverted),
                (ShadowCastKind.Opaque, true, ShadowDepthVariant.Cutout),
                (ShadowCastKind.Dissolving, true, ShadowDepthVariant.Cutout),
                (ShadowCastKind.DissolvingInverted, true, ShadowDepthVariant.CutoutInverted),
            };
            foreach (var (kind, cutsOut, expected) in table)
                Assert.Equal(expected, ShadowDepthSelection.ForRigidSpan(kind, cutsOut));
        }

        [Fact]
        public void An_opted_out_instance_never_reaches_pipeline_selection()
        {
            // AppendCasterSpans emits no span for it, so a None here is a bug upstream and is refused loudly.
            Assert.Throws<ArgumentOutOfRangeException>(() => ShadowDepthSelection.ForRigidSpan(ShadowCastKind.None, false));
        }

        // ---- the wiring, on a recorded frame -----------------------------------------------------------------

        static IGpuFramebuffer NewTarget(IGpuResourceFactory f)
        {
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            return f.CreateFramebuffer(null, tex);
        }

        static Scene3D NewScene(IGpuDevice gd, IGpuFramebuffer fb)
        {
            var scene = new Scene3D(gd, fb.Outputs);
            scene.Post.Starfield = false;
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            scene.Post.LightDirection = new Vector3(-0.55f, -0.8f, -0.25f);
            scene.Camera.Frame(new Vector3(0f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
            return scene;
        }

        // Record ONE frame (the first shadowed frame always renders its depth pass) and return what it bound.
        static List<FakeGraphicsPipelineRequest> RecordFrame(Scene3D scene, IGpuResourceFactory f, IGpuFramebuffer fb,
            Action draw)
        {
            using IGpuCommandList sink = f.CreateCommandList();
            var cl = new CommandTallyGpuCommandList(sink);
            scene.Begin();
            draw();
            scene.PrepareFrame();
            cl.Begin();
            scene.RenderInternal(cl, W, H, fb);
            cl.End();
            Assert.True(scene.LastShadowPassDiagnostics.Rendered, "the recorded frame must have run the depth pass");
            return cl.PipelineBinds.Select(p => ((FakePipeline)p).Request)
                .Where(r => r.HasValue).Select(r => r!.Value).ToList();
        }

        static bool Bound(List<FakeGraphicsPipelineRequest> binds, string vert, string frag)
            => binds.Any(b => string.Equals(b.VertexGlsl, vert, StringComparison.Ordinal)
                && string.Equals(b.FragmentGlsl, frag, StringComparison.Ordinal));

        static bool BoundAnyCutout(List<FakeGraphicsPipelineRequest> binds)
            => binds.Any(b => string.Equals(b.VertexGlsl, ShaderSources.ShadowDepthCutoutVert, StringComparison.Ordinal));

        static Scene3D.TextureHandle Leaf(Scene3D scene) => scene.LoadTexture(new byte[4 * 4 * 4], 4, 4);

        [Fact]
        public void A_mask_mesh_with_an_albedo_takes_the_cutout_depth_pipeline()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle leaf = scene.LoadMesh(MeshPrimitives.Box(1f), new Scene3D.SurfaceMaps(Leaf(scene), alphaCutoff: 0.5f));

            List<FakeGraphicsPipelineRequest> binds = RecordFrame(scene, gd.Factory, fb, () =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                scene.Draw(leaf, Matrix4x4.CreateTranslation(0f, 0.8f, 0f));
            });

            Assert.True(Bound(binds, ShaderSources.ShadowDepthCutoutVert, ShaderSources.ShadowDepthCutoutFrag),
                "the MASK caster's span did not bind the cutout depth pipeline");
            // The opaque floor in the same frame still takes the depth-only pipeline.
            Assert.True(Bound(binds, ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthFrag));
            Assert.False(Bound(binds, ShaderSources.ShadowDepthCutoutVert, ShaderSources.ShadowDepthCutoutInvertedFrag));
        }

        [Fact]
        public void An_opaque_scene_binds_only_the_depth_only_rigid_pipeline()
        {
            // The cost claim: a scene with no MASK, no dissolve and no skinned caster never builds a texture sample
            // or a discard into its depth pass.
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb);
            MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
            MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f), new Scene3D.SurfaceMaps(Leaf(scene)));   // textured, OPAQUE

            List<FakeGraphicsPipelineRequest> binds = RecordFrame(scene, gd.Factory, fb, () =>
            {
                scene.Draw(floor, Matrix4x4.Identity);
                scene.Draw(box, Matrix4x4.CreateTranslation(0f, 0.8f, 0f));
            });

            var depthBinds = binds.Where(b => b.VertexGlsl.Contains("vLightDepth", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(depthBinds);
            Assert.All(depthBinds, b => Assert.Equal(ShaderSources.ShadowDepthFrag, b.FragmentGlsl));
        }

        [Fact]
        public void A_mask_mesh_without_an_albedo_keeps_the_depth_only_pipeline()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb);
            MeshHandle card = scene.LoadMesh(MeshPrimitives.Box(1f), new Scene3D.SurfaceMaps(default, alphaCutoff: 0.5f));

            List<FakeGraphicsPipelineRequest> binds = RecordFrame(scene, gd.Factory, fb,
                () => scene.Draw(card, Matrix4x4.CreateTranslation(0f, 0.8f, 0f)));

            Assert.False(BoundAnyCutout(binds), "a MASK mesh with nothing to alpha-test took the cutout pipeline");
            Assert.True(Bound(binds, ShaderSources.ShadowDepthVert, ShaderSources.ShadowDepthFrag));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_dissolving_mask_caster_still_cuts_out(bool invertShadowDissolve)
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using Scene3D scene = NewScene(gd, fb);
            MeshHandle leaf = scene.LoadMesh(MeshPrimitives.Box(1f), new Scene3D.SurfaceMaps(Leaf(scene), alphaCutoff: 0.5f));

            List<FakeGraphicsPipelineRequest> binds = RecordFrame(scene, gd.Factory, fb, () =>
                scene.Draw(leaf, Matrix4x4.CreateTranslation(0f, 0.8f, 0f), Color.White, Material.None,
                    0.4f, 0.05f, Color.White, castsShadows: true, invertShadowDissolve: invertShadowDissolve));

            string frag = invertShadowDissolve ? ShaderSources.ShadowDepthCutoutInvertedFrag : ShaderSources.ShadowDepthCutoutFrag;
            Assert.True(Bound(binds, ShaderSources.ShadowDepthCutoutVert, frag));
            // Neither plain dissolve pipeline: that would drop the alpha test for the whole fade.
            Assert.False(Bound(binds, ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag));
            Assert.False(Bound(binds, ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveInvertedFrag));
        }
    }
}
