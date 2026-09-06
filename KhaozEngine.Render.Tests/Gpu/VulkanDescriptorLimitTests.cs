using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SECTION 8.3's SECOND AND THIRD DEFENCES, device-free, plus the pipeline-layout half of decision V-D5's
    /// content dedup. Work-breakdown row 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/520).
    ///
    /// <para><b>THE LIMIT DECISION V-D4 SPENDS.</b> Every uniform buffer in every layout becomes a
    /// <c>UNIFORM_BUFFER_DYNAMIC</c> descriptor, not only the ones the engine declared dynamic, because the
    /// per-frame ring base has to be applied at bind and the dynamic offset is the only bind-time knob Vulkan
    /// offers on a uniform buffer. That spends <c>maxDescriptorSetUniformBuffersDynamic</c>, whose Vulkan REQUIRED
    /// MINIMUM is 8 across a whole pipeline layout. Beyond that floor nothing about real device values is
    /// verifiable from this repository, so no claim is made here about what lavapipe, NVIDIA or AMD report.</para>
    ///
    /// <para><b>THE SHIPPED-LAYOUT SWEEP IS THE DEFENCE THAT MATTERS</b>, because it is the only one that fires
    /// before anybody runs anything: a layout combination that would break a minimum-spec device fails on the free
    /// Linux leg rather than on a player's machine. It is the same shape
    /// <c>VulkanUniformRingTests.EveryShippedResourceSetShape_KeepsItsBindWindowInsideItsSegment</c> and
    /// <c>D3D11RegisterNumberingTests.AcrossLayouts_TheShippedPipelinesFlattenInArrayOrder</c> use: the shipped
    /// declarations transcribed here with their own source line cited, because reading them off the real
    /// renderers would need a device.</para>
    /// </summary>
    public sealed class VulkanDescriptorLimitTests
    {
        const GpuShaderStages V = GpuShaderStages.Vertex;
        const GpuShaderStages F = GpuShaderStages.Fragment;
        const GpuShaderStages C = GpuShaderStages.Compute;
        const GpuShaderStages VF = GpuShaderStages.Vertex | GpuShaderStages.Fragment;

        static GpuResourceLayoutElement U(string n, GpuShaderStages s, bool dynamic = false)
            => new(n, GpuResourceKind.UniformBuffer, s, dynamic);

        static GpuResourceLayoutElement T(string n, GpuShaderStages s = F)
            => new(n, GpuResourceKind.TextureReadOnly, s);

        static GpuResourceLayoutElement S(string n, GpuShaderStages s = F)
            => new(n, GpuResourceKind.Sampler, s);

        static GpuResourceLayoutElement Rw(string n, GpuShaderStages s)
            => new(n, GpuResourceKind.StructuredBufferReadWrite, s);

        static GpuResourceLayoutElement Img(string n, GpuShaderStages s)
            => new(n, GpuResourceKind.TextureReadWrite, s);

        static GpuResourceLayoutDescription L(params GpuResourceLayoutElement[] elements) => new(elements);

        /// <summary>
        /// EVERY SHIPPED <c>CreateResourceLayout</c> SITE, transcribed with its own source line. All of them are in
        /// <c>KhaozEngine.Render2D</c> and <c>KhaozEngine.Render3D</c>: no other package declares a layout at all,
        /// because every other renderer draws through those two. The count is asserted below rather than restated
        /// here, so it cannot go stale.
        /// </summary>
        internal static IReadOnlyDictionary<string, GpuResourceLayoutDescription> ShippedLayouts { get; } =
            new Dictionary<string, GpuResourceLayoutDescription>(StringComparer.Ordinal)
            {
                // Render2D/SpriteBatch.cs:190 and :194. The UBO is at SET 1, which is why "set 0 first" is false
                // in shipped code and why the pipeline grouping below is the load-bearing half of this table.
                ["SpriteBatch.texture"] = L(T("Tex"), S("Samp")),
                ["SpriteBatch.vp"] = L(U("Vp", V, dynamic: true)),

                // Render3D/Rendering/BeamRenderer.cs:54
                ["Beam"] = L(U("U", VF)),
                // Render3D/Rendering/DepthLineRenderer.cs:36
                ["DepthLine"] = L(U("U", V)),
                // Render3D/Rendering/DistortionRenderer.cs:84
                ["Distortion"] = L(U("Frame", VF), T("DepthTex"), S("Samp")),
                // Render3D/Rendering/GroundDecalRenderer.cs:126
                ["GroundDecal"] = L(T("DepthTex"), S("Samp"), U("Frame", F, dynamic: true), T("NormalTex")),

                // Render3D/Rendering/ModelRenderer.cs:225, :270 and :273
                ["Model"] = L(U("U", VF), T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"),
                    T("ShadowMap"), S("ShadowSamp")),
                ["Foliage"] = L(U("Foliage", V, dynamic: true)),
                // TWO uniform buffers in ONE set since #604 unfolded the combined skinned block: the shared frame
                // block both stages read, then the per-draw one only the vertex reads. That order is the layout's
                // half of the prefix property, and this is the only shipped set that spends two uniform buffers.
                ["Model.skinnedMain"] = L(U("U", VF), U("VBlock", V, dynamic: true)),
                ["Model.skinnedFrag"] = L(T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"),
                    T("ShadowMap"), S("ShadowSamp")),
                // Render3D/Rendering/SkinnedBonePalette.cs:54. The one shipped layout used by TWO pipelines at
                // DIFFERENT slots since #407: set 2 of the skinned model pair and set 1 of the skinned depth
                // pipeline. A set layout carries no set number, so one declaration really does serve both, and it
                // is what lets one palette upload feed the main pass and every shadow cascade.
                ["SkinnedBonePalette"] = L(U("Palette", V, dynamic: true)),
                // Render3D/Rendering/ModelRenderer.Splat.cs:41 and :48. TWO sets since #604: the shared frame
                // block, then everything the material owns. One of the two ground pipelines that put a uniform
                // buffer in BOTH of their sets, and one of the three that spend two uniform buffers in total.
                ["Model.splatFrame"] = L(U("U", VF)),
                ["Model.splatMaterial"] = L(U("SplatParams", F), T("AlbedoArray"), T("NormalArray"), S("Sampler"),
                    T("ShadowMap"), S("ShadowSamp")),
                // Render3D/Rendering/ModelRenderer.TileGround.cs:47 and :55. The same two-set split since #727,
                // which unfolded the last combined frame+params buffer in the tree. Albedo only, so one array
                // where the splat material layout has two, and the shadow map stays last.
                ["Model.tileGroundFrame"] = L(U("U", VF)),
                ["Model.tileGroundMaterial"] = L(U("TileGroundParams", F), T("AlbedoArray"), S("Sampler"),
                    T("ShadowMap"), S("ShadowSamp")),

                // Render3D/Rendering/OceanFftProducer.cs:506 and :510, both compute
                ["OceanFft.row"] = L(U("Params", C), Rw("H0Buf", C), Rw("WorkBuf", C)),
                ["OceanFft.col"] = L(U("Params", C), Rw("WorkBuf", C), Rw("FoamBuf", C), Img("OceanMap", C)),

                // Render3D/Rendering/OverlayMeshRenderer.cs:54
                ["OverlayMesh"] = L(U("Draw", V, dynamic: true)),
                // Render3D/Rendering/OverlayRenderer.cs:47, shared by Billboard, Fill and Line
                ["Overlay"] = L(U("U", V)),
                // Render3D/Rendering/ParticleRenderer.cs:93
                ["Particle"] = L(U("Frame", VF), T("DepthTex"), S("Samp"), T("MotionTex"), T("AtlasTex"),
                    S("AtlasSamp")),

                // Render3D/Rendering/PixelPostProcess.cs:125 to :141, the nine fullscreen passes. Their T, S and
                // U helpers at :160-162 are all fragment-stage and none is dynamic.
                ["Pixel.pal"] = L(T("Src"), S("Samp"), U("Pal", F)),
                ["Pixel.edge"] = L(T("ColorTex"), T("NormalTex"), T("DepthTex"), S("Samp"), U("Edge", F)),
                ["Pixel.blit"] = L(T("Src"), S("Samp"), U("Final", F)),
                ["Pixel.fxaa"] = L(T("Src"), S("Samp"), U("Fxaa", F)),
                ["Pixel.bright"] = L(T("Src"), S("Samp"), U("Bright", F)),
                ["Pixel.blur"] = L(T("Src"), S("Samp"), U("Blur", F)),
                ["Pixel.composite"] = L(T("Src"), T("Bloom"), S("Samp"), U("Composite", F)),
                ["Pixel.tone"] = L(T("Src"), S("Samp"), U("Tone", F)),
                ["Pixel.apply"] = L(T("Src"), T("OffsetTex"), S("Samp"), U("Apply", F)),

                // Render3D/Rendering/ShadowMapRenderer.cs:125 and :141
                ["Shadow"] = L(U("U", V, dynamic: true)),
                ["Shadow.skinned"] = L(U("VBlock", V, dynamic: true)),

                // Render3D/Rendering/SkyRenderer.cs:50 and StarfieldRenderer.cs:53
                ["Sky"] = L(U("Sky", F)),
                ["Starfield"] = L(U("Starfield", F)),

                // Render3D/Rendering/TexturedBillboardRenderer.cs:38 and TrailRenderer.cs:53
                ["TexturedBillboard"] = L(U("U", V), T("Tex"), S("Samp")),
                ["Trail"] = L(U("U", V)),

                // Render3D/Rendering/TransitionRenderer.cs:66 and :68
                ["Transition.solid"] = L(U("Fill", F)),
                ["Transition.cross"] = L(T("Src"), S("Samp"), U("Params", F)),

                // Render3D/Rendering/WaterRenderer.cs:188
                ["Water"] = L(T("BathyTex", VF), S("BathySamp", VF), T("OceanMap", VF), S("OceanSamp", VF),
                    T("DepthTex"), S("Samp"), U("Water", VF, dynamic: true)),
            };

        /// <summary>
        /// EVERY SHIPPED PIPELINE'S LAYOUT ARRAY, in SLOT order. This is the half that matters, because
        /// <c>maxDescriptorSetUniformBuffersDynamic</c> is a PIPELINE LAYOUT limit rather than a per-set one: one
        /// set with two dynamic uniform buffers is legal everywhere, and a pipeline combining several sets is
        /// where a ceiling is really reached.
        /// <para>
        /// Five shipped families use more than one layout, and FOUR of them spend more than one uniform buffer.
        /// SpriteBatch still splits ONE uniform buffer into one set with pure texture and sampler resources in the
        /// other. The splat and tile-ground pipelines each put a uniform buffer in BOTH their sets (the shared frame
        /// block, then the material's own params beside its textures). The skinned model pair carries THREE across
        /// three sets since #407: the shared frame block and the per-draw header together in set 0, material
        /// textures alone in set 1, the per-caster bone palette in set 2. The skinned depth pipeline carries two,
        /// its light matrix and the very same palette layout, which is what makes one palette upload serve the main
        /// pass and every cascade.
        /// </para>
        /// </summary>
        internal static IReadOnlyList<(string Pipeline, string[] Slots)> ShippedPipelines { get; } =
        [
            // Render2D/SpriteBatch.cs:214, both the alpha and the additive pipeline through one Describe(blend).
            ("SpriteBatch", ["SpriteBatch.texture", "SpriteBatch.vp"]),

            // Render3D/Rendering/ModelRenderer.cs:407 and :419. THREE slots since #407 added the shared palette.
            ("ModelRenderer skinned", ["Model.skinnedMain", "Model.skinnedFrag", "SkinnedBonePalette"]),
            ("ModelRenderer skinned dissolve", ["Model.skinnedMain", "Model.skinnedFrag", "SkinnedBonePalette"]),

            // The rest, alphabetically. Every one is a single-set pipeline except the two ground passes, which
            // carry their own two-slot arrays below since #604 and #727.
            ("BeamRenderer", ["Beam"]),
            ("DepthLineRenderer", ["DepthLine"]),
            ("DistortionRenderer", ["Distortion"]),
            ("GroundDecalRenderer", ["GroundDecal"]),
            ("ModelRenderer", ["Model"]),
            ("ModelRenderer foliage", ["Model", "Foliage"]),
            ("ModelRenderer dissolve", ["Model"]),
            ("ModelRenderer splat", ["Model.splatFrame", "Model.splatMaterial"]),
            ("ModelRenderer tile ground", ["Model.tileGroundFrame", "Model.tileGroundMaterial"]),
            ("OceanFftProducer row", ["OceanFft.row"]),
            ("OceanFftProducer col", ["OceanFft.col"]),
            ("OverlayMeshRenderer", ["OverlayMesh"]),
            ("OverlayRenderer", ["Overlay"]),
            ("ParticleRenderer", ["Particle"]),
            ("PixelPostProcess pal", ["Pixel.pal"]),
            ("PixelPostProcess edge", ["Pixel.edge"]),
            ("PixelPostProcess blit", ["Pixel.blit"]),
            ("PixelPostProcess fxaa", ["Pixel.fxaa"]),
            ("PixelPostProcess bright", ["Pixel.bright"]),
            ("PixelPostProcess blur", ["Pixel.blur"]),
            ("PixelPostProcess composite", ["Pixel.composite"]),
            ("PixelPostProcess tone", ["Pixel.tone"]),
            ("PixelPostProcess apply", ["Pixel.apply"]),
            ("ShadowMapRenderer depth", ["Shadow"]),
            ("ShadowMapRenderer skinned depth", ["Shadow.skinned", "SkinnedBonePalette"]),
            ("SkyRenderer", ["Sky"]),
            ("StarfieldRenderer", ["Starfield"]),
            ("TexturedBillboardRenderer", ["TexturedBillboard"]),
            ("TrailRenderer", ["Trail"]),
            ("TransitionRenderer solid", ["Transition.solid"]),
            ("TransitionRenderer cross", ["Transition.cross"]),
            ("WaterRenderer", ["Water"]),
        ];

        /// <summary>
        /// 8.3's SECOND DEFENCE: every pipeline the shipped renderers declare spends at most
        /// <see cref="VulkanDescriptorLimits.SpecRequiredMinimum"/> dynamic uniform descriptors, so a
        /// minimum-spec device can run all of them.
        /// </summary>
        [Fact]
        public void EveryShippedPipeline_StaysWithinTheRequiredMinimumDynamicUniformBuffers()
        {
            // Foliage adds one layout and one pipeline beside the existing shared model material layout.
            // Both counts are stated so an emptied table cannot pass by agreeing with itself.
            Assert.Equal(38, ShippedLayouts.Count);
            Assert.Equal(35, ShippedPipelines.Count);

            foreach ((string pipeline, string[] slots) in ShippedPipelines)
            {
                int dynamicUniforms = slots.Sum(
                    slot => VulkanDescriptorPolicy.DynamicUniformCount(ShippedLayouts[slot]));

                Assert.True(dynamicUniforms <= VulkanDescriptorLimits.SpecRequiredMinimum,
                    $"{pipeline} spends {dynamicUniforms} dynamic uniform buffer descriptors across "
                    + $"{slots.Length} sets, above Vulkan's required minimum of "
                    + $"{VulkanDescriptorLimits.SpecRequiredMinimum} for "
                    + "maxDescriptorSetUniformBuffersDynamic. Every uniform buffer in every layout is a dynamic "
                    + "one on this backend (decision V-D4), so this is the pipeline's uniform buffer count rather "
                    + "than its declared-dynamic count, and a pipeline over the floor cannot be created on a "
                    + "minimum-spec device at all.");
            }
        }

        /// <summary>
        /// AND THE HEADROOM IS PINNED so a change to it is visible rather than silent. Every shipped pipeline spent
        /// exactly ONE until <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/604">#604</see>, which was
        /// the engine's own one-uniform-buffer-per-pipeline convention (the retired Veldrid Metal backend's
        /// numbering, documented in <c>ModelRenderer</c>, <c>WaterRenderer</c> and <c>GroundDecalRenderer</c>)
        /// arriving here as seven descriptors of headroom. The splat pipeline raised it to TWO when its frame and
        /// material uniforms were split across its two sets, and the skinned and tile-ground pipelines joined it
        /// there at #604 and #727. <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/407">#407</see> then
        /// raised it to THREE, on the skinned model pair alone: the shared frame block and the per-draw header in
        /// set 0 plus the per-caster bone palette in a set of its own. The skinned DEPTH pipeline took the same
        /// palette and sits at two with the other three. Five descriptors of headroom are left.
        /// <para>
        /// RAISING THIS NUMBER IS FINE UP TO <see cref="VulkanDescriptorLimits.SpecRequiredMinimum"/>. Update it
        /// with the pipeline that raised it. This is a per-PIPELINE-LAYOUT sum over EVERY uniform buffer, so both
        /// a pipeline whose several sets each carry one and a pipeline with two in a single set move it.
        /// </para>
        /// </summary>
        [Fact]
        public void TheHeaviestShippedPipeline_SpendsThreeDynamicUniformDescriptors()
        {
            int heaviest = ShippedPipelines.Max(
                p => p.Slots.Sum(slot => VulkanDescriptorPolicy.DynamicUniformCount(ShippedLayouts[slot])));

            Assert.Equal(3, heaviest);
            Assert.True(heaviest < VulkanDescriptorLimits.SpecRequiredMinimum,
                "the heaviest shipped pipeline is at the spec floor, so the next uniform buffer added to it cannot "
                + "be created on a minimum-spec device at all.");
        }

        /// <summary>
        /// AND EVERY SHIPPED LAYOUT MAPS, which is what stops the table above from silently passing on a
        /// description this backend would refuse. A declared-dynamic structured buffer or texture would throw
        /// here rather than at whatever the first renderer to be constructed happened to be.
        /// </summary>
        [Fact]
        public void EveryShippedLayout_MapsToDescriptorTypesThisBackendCanProduce()
        {
            foreach ((string site, GpuResourceLayoutDescription description) in ShippedLayouts)
            {
                VulkanDescriptorBinding[] bindings = VulkanDescriptorPolicy.BindingsFor(description);

                Assert.All(bindings, binding => Assert.Contains(binding.Type,
                    new[]
                    {
                        VulkanDescriptorType.UniformBufferDynamic, VulkanDescriptorType.StorageBuffer,
                        VulkanDescriptorType.SampledImage, VulkanDescriptorType.StorageImage,
                        VulkanDescriptorType.Sampler,
                    }));

                Assert.True(bindings.Length > 0, site + " declares no elements, which no shipped layout does.");
            }
        }

        /// <summary>
        /// 8.3's THIRD DEFENCE: pipeline-layout creation counts the dynamic uniform descriptors and refuses above
        /// the device's actual limit, with a message naming the limit and the count.
        /// </summary>
        [Fact]
        public void APipelineLayoutOverTheDevicesLimit_IsRefusedByName()
        {
            var api = new FakeVulkanDescriptorApi();
            var cache = new VulkanPipelineLayoutCache(api, maxDynamicUniformBuffers: 2);

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => cache.GetOrCreate([1, 2, 3], dynamicUniformCount: 3));

            Assert.Contains("maxDescriptorSetUniformBuffersDynamic", ex.Message, StringComparison.Ordinal);
            Assert.Contains("V-D4", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, api.PipelineLayoutCreateCount);

            // AND ON EVERY REQUEST, not only the first: the check runs before the dedup lookup, so a layout over
            // the limit cannot be created once and then handed back forever.
            Assert.Throws<NotSupportedException>(() => cache.GetOrCreate([1, 2, 3], 3));

            Assert.Equal(2u, cache.MaxDynamicUniformBuffers);
            Assert.NotEqual(0UL, cache.GetOrCreate([1, 2], dynamicUniformCount: 2));
        }

        /// <summary>
        /// A DEVICE WHOSE LIMIT WAS NEVER READ DEGRADES TO VULKAN'S REQUIRED MINIMUM rather than to zero, which
        /// is the same reading <see cref="VulkanRingStride.AlignmentFor"/> gives an unread alignment. A device
        /// reporting a genuine 0 fails the support probe long before a pipeline layout is created.
        /// </summary>
        [Fact]
        public void AnUnreadLimit_DegradesToTheSpecFloor()
        {
            Assert.Equal(VulkanDescriptorLimits.SpecRequiredMinimum, VulkanDescriptorLimits.EffectiveLimit(0));
            Assert.Equal(64u, VulkanDescriptorLimits.EffectiveLimit(64));

            VulkanDescriptorLimits.RequirePipelineWithinLimit(8, reportedLimit: 0, setLayoutCount: 1);
            Assert.Throws<NotSupportedException>(
                () => VulkanDescriptorLimits.RequirePipelineWithinLimit(9, 0, 1));
        }

        /// <summary>
        /// THE SPEC FLOOR IS THE SAME CONSTANT THE SUPPORT PROBE GATES ON, referenced rather than restated so the
        /// probe's bar and this sweep's bar cannot drift apart.
        /// </summary>
        [Fact]
        public void TheSpecFloor_IsTheProbesOwnConstant()
            => Assert.Equal(VulkanDeviceRequirements.RequiredDynamicUniformBuffers,
                VulkanDescriptorLimits.SpecRequiredMinimum);

        /// <summary>
        /// TWO PIPELINES OVER THE SAME SET LAYOUTS SHARE ONE <c>VkPipelineLayout</c> (V-D5), which is what makes
        /// two pipelines built from separately created but identically shaped layouts compatible for their whole
        /// array by identity. Slot ORDER is part of the key, because a pipeline layout is exactly its set layout
        /// array.
        /// </summary>
        [Fact]
        public void PipelineLayouts_AreContentDeduplicatedAndOrderSensitive()
        {
            var api = new FakeVulkanDescriptorApi();
            var cache = new VulkanPipelineLayoutCache(api, 8);

            ulong first = cache.GetOrCreate([10, 20], 1);
            ulong same = cache.GetOrCreate([10, 20], 1);
            ulong reordered = cache.GetOrCreate([20, 10], 1);
            ulong shorter = cache.GetOrCreate([10], 1);

            Assert.Equal(first, same);
            Assert.NotEqual(first, reordered);
            Assert.NotEqual(first, shorter);

            Assert.Equal(3, api.PipelineLayoutCreateCount);
            Assert.Equal(3, cache.DistinctPipelineLayoutCount);
            Assert.Equal(4, cache.RequestCount);

            Assert.Equal(3, cache.DestroyAll());
            Assert.Empty(api.Live);
        }

        /// <summary>
        /// AND THE ENGINE-TYPED ENTRY POINT SUMS THE COUNT AND THE HANDLES FROM ONE PLACE, so the number the
        /// limit is measured against and the array the layout is built from cannot disagree. That is the entry
        /// point row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523) calls.
        /// </summary>
        [Fact]
        public void ThePipelineEntryPoint_SumsTheDynamicUniformsOfItsOwnLayouts()
        {
            var api = new FakeVulkanDescriptorApi();
            var setLayouts = new VulkanDescriptorSetLayoutCache(api);
            var cache = new VulkanPipelineLayoutCache(api, maxDynamicUniformBuffers: 3);

            using var main = new VulkanResourceLayout(setLayouts, ShippedLayouts["Model.skinnedMain"]);
            using var fragment = new VulkanResourceLayout(setLayouts, ShippedLayouts["Model.skinnedFrag"]);
            using var palette = new VulkanResourceLayout(setLayouts, ShippedLayouts["SkinnedBonePalette"]);
            using var extra = new VulkanResourceLayout(setLayouts, ShippedLayouts["Sky"]);

            // The real shipped skinned pipeline: two dynamic uniforms in its first set since #604, a texture-only
            // second set, and the shared bone palette's third since #407. It fits a limit of 3 exactly, which is
            // what makes the throw below a real boundary rather than a comfortable one.
            ulong skinned = cache.GetOrCreate(new[] { main, fragment, palette });
            Assert.Equal(new ulong[] { main.SetLayout, fragment.SetLayout, palette.SetLayout },
                api.PipelineLayouts[skinned]);

            // A fourth set carrying one more uniform buffer takes it over the same limit.
            Assert.Throws<NotSupportedException>(
                () => cache.GetOrCreate(new[] { main, fragment, palette, extra }));
        }
    }
}
