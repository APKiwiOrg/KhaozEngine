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
        /// EVERY SHIPPED <c>CreateResourceLayout</c> SITE, transcribed with its own source line. Thirty-three of
        /// them, all in <c>KhaozEngine.Render2D</c> and <c>KhaozEngine.Render3D</c>: no other package declares a
        /// layout at all, because every other renderer draws through those two.
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
                ["GroundDecal"] = L(T("DepthTex"), S("Samp"), U("Frame", F), T("NormalTex")),

                // Render3D/Rendering/ModelRenderer.cs:215, :258, :260 and :279
                ["Model"] = L(U("U", VF), T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"),
                    T("ShadowMap"), S("ShadowSamp")),
                ["Model.skinnedVertex"] = L(U("VBlock", VF, dynamic: true)),
                ["Model.skinnedFrag"] = L(T("Albedo"), T("NormalMap"), T("RoughnessMap"), S("Sampler"),
                    T("ShadowMap"), S("ShadowSamp")),
                ["Model.splat"] = L(U("U", VF), T("AlbedoArray"), T("NormalArray"), S("Sampler"), T("ShadowMap"),
                    S("ShadowSamp")),

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

                // Render3D/Rendering/ShadowMapRenderer.cs:119 and :135
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
        /// Only two shipped families use more than one layout, and BOTH split the uniform buffer into one set and
        /// pure texture and sampler resources into the other, so neither places a uniform buffer in both.
        /// </para>
        /// </summary>
        static IReadOnlyList<(string Pipeline, string[] Slots)> ShippedPipelines { get; } =
        [
            // Render2D/SpriteBatch.cs:214, both the alpha and the additive pipeline through one Describe(blend).
            ("SpriteBatch", ["SpriteBatch.texture", "SpriteBatch.vp"]),

            // Render3D/Rendering/ModelRenderer.cs:414 and :426.
            ("ModelRenderer skinned", ["Model.skinnedVertex", "Model.skinnedFrag"]),
            ("ModelRenderer skinned dissolve", ["Model.skinnedVertex", "Model.skinnedFrag"]),

            // Everything else is a single-set pipeline.
            ("BeamRenderer", ["Beam"]),
            ("DepthLineRenderer", ["DepthLine"]),
            ("DistortionRenderer", ["Distortion"]),
            ("GroundDecalRenderer", ["GroundDecal"]),
            ("ModelRenderer", ["Model"]),
            ("ModelRenderer dissolve", ["Model"]),
            ("ModelRenderer splat", ["Model.splat"]),
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
            ("ShadowMapRenderer skinned depth", ["Shadow.skinned"]),
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
            Assert.Equal(33, ShippedLayouts.Count);
            Assert.Equal(33, ShippedPipelines.Count);

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
        /// AND THE HEADROOM IS PINNED so a change to it is visible rather than silent. Every shipped pipeline
        /// spends exactly ONE, which is the engine's own one-uniform-buffer-per-pipeline convention (a Metal
        /// constraint documented in <c>ModelRenderer</c>, <c>WaterRenderer</c> and <c>GroundDecalRenderer</c>)
        /// arriving here as seven descriptors of headroom.
        /// <para>
        /// RAISING THIS NUMBER IS FINE UP TO <see cref="VulkanDescriptorLimits.SpecRequiredMinimum"/>. Update it
        /// with the pipeline that raised it, and re-read the Metal note before going past two.
        /// </para>
        /// </summary>
        [Fact]
        public void TheHeaviestShippedPipeline_SpendsExactlyOneDynamicUniformDescriptor()
        {
            int heaviest = ShippedPipelines.Max(
                p => p.Slots.Sum(slot => VulkanDescriptorPolicy.DynamicUniformCount(ShippedLayouts[slot])));

            Assert.Equal(1, heaviest);
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
            var cache = new VulkanPipelineLayoutCache(api, maxDynamicUniformBuffers: 1);

            using var vertex = new VulkanResourceLayout(setLayouts, ShippedLayouts["Model.skinnedVertex"]);
            using var fragment = new VulkanResourceLayout(setLayouts, ShippedLayouts["Model.skinnedFrag"]);
            using var extra = new VulkanResourceLayout(setLayouts, ShippedLayouts["Sky"]);

            // The real shipped skinned pipeline: one dynamic uniform across two sets, which fits a limit of 1.
            ulong skinned = cache.GetOrCreate(new[] { vertex, fragment });
            Assert.Equal(new ulong[] { vertex.SetLayout, fragment.SetLayout }, api.PipelineLayouts[skinned]);

            // A third set carrying a second uniform buffer takes it over the same limit.
            Assert.Throws<NotSupportedException>(
                () => cache.GetOrCreate(new[] { vertex, fragment, extra }));
        }
    }
}
