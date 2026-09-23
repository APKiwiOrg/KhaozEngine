using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of which depth pipeline each caster takes in the key light's cascade pass: the alpha-cutout
    /// pipeline for a MASK caster (issue #15) and the dissolve-aware and opted-out skinned casters (issue #387). The
    /// pure rules (<see cref="ShadowDepthSelection"/>) are asserted directly, and their
    /// wiring is asserted on a real <see cref="Scene3D"/> frame recorded against <see cref="FakeGpuDevice"/>, which
    /// remembers the shader sources behind every pipeline the pass binds. The pixel proof is in
    /// <c>AlphaCutoutShadowGpuTests</c> and <c>SkinnedShadowCasterPolicyGpuTests</c>.
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
            Assert.Throws<ArgumentOutOfRangeException>(() => ShadowDepthSelection.ForCpuSkinned(ShadowCastKind.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => ShadowDepthSelection.ForGpuSkinned(ShadowCastKind.None));
        }

        [Fact]
        public void Skinned_casters_take_the_rigid_pipelines_on_the_cpu_path_and_their_own_on_the_gpu_path()
        {
            Assert.Equal(ShadowDepthVariant.Opaque, ShadowDepthSelection.ForCpuSkinned(ShadowCastKind.Opaque));
            Assert.Equal(ShadowDepthVariant.Dissolve, ShadowDepthSelection.ForCpuSkinned(ShadowCastKind.Dissolving));
            Assert.Equal(ShadowDepthVariant.Skinned, ShadowDepthSelection.ForGpuSkinned(ShadowCastKind.Opaque));
            Assert.Equal(ShadowDepthVariant.SkinnedDissolve, ShadowDepthSelection.ForGpuSkinned(ShadowCastKind.Dissolving));
        }

        static SkinnedSceneInstances.Instance Skinned(float dissolve = 0f, bool castsShadows = true)
            => new(new SkinnedMeshHandle(0, 1), Matrix4x4.Identity, Color.White, Material.None, dissolve, 0f, default,
                castsShadows);

        [Fact]
        public void Skinned_classification_covers_the_three_cases()
        {
            Assert.Equal(ShadowCastKind.Opaque, ShadowDepthSelection.ClassifySkinnedCaster(Skinned()));
            Assert.Equal(ShadowCastKind.Dissolving, ShadowDepthSelection.ClassifySkinnedCaster(Skinned(dissolve: 0.4f)));
            Assert.Equal(ShadowCastKind.None, ShadowDepthSelection.ClassifySkinnedCaster(Skinned(castsShadows: false)));
            // Opted out wins over dissolving, as on the rigid side: no shadow beats a thin one.
            Assert.Equal(ShadowCastKind.None,
                ShadowDepthSelection.ClassifySkinnedCaster(Skinned(dissolve: 0.4f, castsShadows: false)));
        }

        [Fact]
        public void A_skinned_draw_casts_unless_it_opts_out()
        {
            var queue = new SkinnedSceneInstances();
            queue.Add(new SkinnedMeshHandle(0, 1), Matrix4x4.Identity, Color.White, Material.None);
            queue.Add(new SkinnedMeshHandle(0, 1), Matrix4x4.Identity, Color.White, Material.None, 0.3f, 0.1f, Color.White);
            queue.Add(new SkinnedMeshHandle(0, 1), Matrix4x4.Identity, Color.White, Material.None, 0f, 0f, default,
                castsShadows: false);

            Assert.True(queue.Items[0].CastsShadows);
            Assert.True(queue.Items[1].CastsShadows);
            Assert.False(queue.Items[2].CastsShadows);
            Assert.False(queue.Items[2].Dissolving);
        }

        [Fact]
        public void The_cutout_depth_pipelines_cull_nothing_and_the_rest_keep_front_culling()
        {
            // A MASK caster is usually a single-plane card the colour pass draws two-sided, so a culled cutout
            // pipeline would erase it from the atlas whenever its culled side faces the sun. The opaque and dissolve
            // depth pipelines keep the second-depth trick.
            var gd = new FakeGpuDevice();
            var factory = (FakeGpuResourceFactory)gd.Factory;
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using var scene = new Scene3D(gd, fb.Outputs);

            var cutout = factory.GraphicsPipelines
                .Where(r => string.Equals(r.VertexGlsl, ShaderSources.ShadowDepthCutoutVert, StringComparison.Ordinal)).ToList();
            var rigid = factory.GraphicsPipelines
                .Where(r => string.Equals(r.VertexGlsl, ShaderSources.ShadowDepthVert, StringComparison.Ordinal)
                    || string.Equals(r.VertexGlsl, ShaderSources.ShadowDepthDissolveVert, StringComparison.Ordinal)).ToList();
            Assert.Equal(2, cutout.Count);   // the plain and the inverted-dither cutout pipelines
            Assert.All(cutout, r => Assert.Equal(GpuFaceCull.None, r.Description.Rasterizer.CullMode));
            Assert.Equal(3, rigid.Count);    // opaque, dissolve, inverted dissolve
            Assert.All(rigid, r => Assert.Equal(GpuFaceCull.Front, r.Description.Rasterizer.CullMode));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void A_gpu_skinned_variant_is_refused_by_the_rigid_bind(bool dissolving)
        {
            // The variant travels as a bool because the enum is internal and a public test may not name it.
            ShadowDepthVariant variant = dissolving ? ShadowDepthVariant.SkinnedDissolve : ShadowDepthVariant.Skinned;
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using var scene = new Scene3D(gd, fb.Outputs);
            using IGpuCommandList cl = gd.Factory.CreateCommandList();
            Assert.Throws<ArgumentOutOfRangeException>(() => scene.BeginRigidDepthVariant(cl, 0, variant));
        }

        [Fact]
        public void A_dissolving_skinned_depth_slot_carries_the_block_the_dissolve_vertex_reads()
        {
            // The slot the SkinnedShadowDepthDissolveVert block is read from, packed by the C# side: the std140
            // offsets (mat4, mat4, vec4, vec4) must be where ShadowMapRenderer writes them, and it must fit a slot.
            string block = ShaderSources.SkinnedShadowDepthDissolveVert.Split("uniform Palette")[0];
            int mvp = block.IndexOf("mat4 LightMvp;", StringComparison.Ordinal);
            int model = block.IndexOf("mat4 Model;", StringComparison.Ordinal);
            int origin = block.IndexOf("vec4 RenderOrigin;", StringComparison.Ordinal);
            int parms = block.IndexOf("vec4 DissolveParams;", StringComparison.Ordinal);
            Assert.True(mvp >= 0 && mvp < model && model < origin && origin < parms, "the VBlock members moved");
            Assert.Equal(64, ShadowMapRenderer.SkinnedDissolveModelOffset);
            Assert.Equal(128, ShadowMapRenderer.SkinnedDissolveOriginOffset);
            Assert.Equal(144, ShadowMapRenderer.SkinnedDissolveParamsOffset);
            Assert.Equal(160, ShadowMapRenderer.SkinnedDissolvePayloadBytes);
            Assert.True(ShadowMapRenderer.SkinnedDissolvePayloadBytes <= ShadowMapRenderer.SkinnedDepthSlotBytes);

            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            using var renderer = new ModelRenderer(gd, fb.Outputs, 256, 1);
            renderer.EnsureSkinnedShadowCapacity(2);
            Matrix4x4 world = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(1f, 2f, 3f);
            Matrix4x4 depth = Matrix4x4.CreateScale(0.5f) * Matrix4x4.CreateTranslation(0f, 0f, 0.25f);
            var renderOrigin = new Vector3(4096f, 0f, -2048f);

            renderer.PackSkinnedShadowSlot(0, world, depth);   // an opaque caster: the matrix alone
            renderer.PackSkinnedShadowSlot(1, world, depth, renderOrigin, noiseScale: 3.5f, dissolveThreshold: 0.6f);

            ReadOnlySpan<byte> opaque = renderer.ShadowMap.SkinnedShadowSlotBytes(0);
            ReadOnlySpan<byte> dissolving = renderer.ShadowMap.SkinnedShadowSlotBytes(1);
            Assert.Equal(world * depth, MemoryMarshal.Read<Matrix4x4>(opaque));
            Assert.All(opaque.Slice(64).ToArray(), b => Assert.Equal(0, b));   // nothing beyond the matrix
            Assert.Equal(world * depth, MemoryMarshal.Read<Matrix4x4>(dissolving));
            Assert.Equal(world, MemoryMarshal.Read<Matrix4x4>(dissolving.Slice(ShadowMapRenderer.SkinnedDissolveModelOffset)));
            Assert.Equal(new Vector4(renderOrigin, 0f),
                MemoryMarshal.Read<Vector4>(dissolving.Slice(ShadowMapRenderer.SkinnedDissolveOriginOffset)));
            Assert.Equal(new Vector4(3.5f, 0.6f, 0f, 0f),
                MemoryMarshal.Read<Vector4>(dissolving.Slice(ShadowMapRenderer.SkinnedDissolveParamsOffset)));
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

        static (Scene3D Scene, SkinnedGltfMesh Tube, SkinnedMeshHandle Handle) SkinnedScene(FakeGpuDevice gd,
            IGpuFramebuffer fb, bool gpuSkinning)
        {
            Scene3D scene = NewScene(gd, fb);
            scene.UseGpuSkinning = gpuSkinning;
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 4f, 10, 10, 6, Axis.Z);
            return (scene, tube, scene.LoadSkinnedMesh(tube));
        }

        static readonly Matrix4x4 TubeAt = Matrix4x4.CreateTranslation(0f, 0.6f, 0f);

        [Fact]
        public void A_dissolving_gpu_skinned_caster_binds_the_skinned_dissolve_depth_pipeline()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            var (scene, tube, h) = SkinnedScene(gd, fb, gpuSkinning: true);
            using (scene)
            {
                List<FakeGraphicsPipelineRequest> solid = RecordFrame(scene, gd.Factory, fb,
                    () => scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White));
                Assert.True(Bound(solid, ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag));
                Assert.False(Bound(solid, ShaderSources.SkinnedShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag));

                List<FakeGraphicsPipelineRequest> fading = RecordFrame(scene, gd.Factory, fb,
                    () => scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White, Material.None, 0.5f, 0.05f, Color.White));
                Assert.True(Bound(fading, ShaderSources.SkinnedShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag),
                    "a dissolving GPU-skinned caster still wrote solid depth");
                Assert.False(Bound(fading, ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag));
            }
        }

        [Fact]
        public void A_dissolving_cpu_skinned_caster_binds_the_rigid_dissolve_depth_pipeline()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            var (scene, tube, h) = SkinnedScene(gd, fb, gpuSkinning: false);
            using (scene)
            {
                List<FakeGraphicsPipelineRequest> solid = RecordFrame(scene, gd.Factory, fb,
                    () => scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White));
                Assert.False(Bound(solid, ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag));

                List<FakeGraphicsPipelineRequest> fading = RecordFrame(scene, gd.Factory, fb,
                    () => scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White, Material.None, 0.5f, 0.05f, Color.White));
                Assert.True(Bound(fading, ShaderSources.ShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag),
                    "a dissolving CPU-skinned caster still wrote solid depth");
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void An_opted_out_skinned_draw_casts_nothing_but_still_draws(bool gpuSkinning)
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            var (scene, tube, h) = SkinnedScene(gd, fb, gpuSkinning);
            using (scene)
            {
                MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));

                RecordFrame(scene, gd.Factory, fb, () =>
                {
                    scene.Draw(floor, Matrix4x4.Identity);
                    scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White);
                });
                ShadowPassDiagnostics casting = scene.LastShadowPassDiagnostics;
                Assert.True(casting.AnySkinnedCaster);
                Assert.Equal(1, casting.SkinnedCasterCount);
                Assert.Equal(casting.CascadeCount, casting.SkinnedDrawCalls);   // one depth draw per cascade

                List<FakeGraphicsPipelineRequest> binds = RecordFrame(scene, gd.Factory, fb, () =>
                {
                    scene.Draw(floor, Matrix4x4.CreateTranslation(0.01f, 0f, 0f));   // keeps the rigid pass dirty
                    scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White, Material.None, castsShadows: false);
                });
                ShadowPassDiagnostics optedOut = scene.LastShadowPassDiagnostics;
                Assert.False(optedOut.AnySkinnedCaster);
                Assert.Equal(0, optedOut.SkinnedCasterCount);
                Assert.Equal(0, optedOut.SkinnedDrawCalls);
                Assert.False(Bound(binds, ShaderSources.SkinnedShadowDepthVert, ShaderSources.ShadowDepthFrag));
                // A shadow policy, not a cull: the main pass still draws it.
                Assert.Equal(1, scene.DrawnSkinnedInstances);
            }
        }

        [Fact]
        public void An_opted_out_dissolving_skinned_draw_casts_nothing()
        {
            var gd = new FakeGpuDevice();
            using IGpuFramebuffer fb = NewTarget(gd.Factory);
            var (scene, tube, h) = SkinnedScene(gd, fb, gpuSkinning: true);
            using (scene)
            {
                MeshHandle floor = scene.LoadMesh(MeshPrimitives.Tile(10f, 0.1f));
                List<FakeGraphicsPipelineRequest> binds = RecordFrame(scene, gd.Factory, fb, () =>
                {
                    scene.Draw(floor, Matrix4x4.Identity);
                    scene.DrawSkinned(h, tube.RestPose, TubeAt, Color.White, Material.None, 0.5f, 0.05f, Color.White,
                        castsShadows: false);
                });
                Assert.Equal(0, scene.LastShadowPassDiagnostics.SkinnedDrawCalls);
                Assert.False(Bound(binds, ShaderSources.SkinnedShadowDepthDissolveVert, ShaderSources.ShadowDepthDissolveFrag));
                // The colour pass still dissolves it.
                Assert.True(Bound(binds, ShaderSources.SkinnedModelVert, ShaderSources.SkinnedModelDissolveFrag));
            }
        }
    }
}
