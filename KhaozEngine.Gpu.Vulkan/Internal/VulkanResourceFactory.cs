using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuResourceFactory"/> for the native Vulkan backend: the one place a native resource is created,
    /// and therefore the one place decision V-M11's eager views and decision V-F7's resting layouts are handed out.
    /// Work-breakdown row 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519).
    ///
    /// <para><b>EVERY CREATION IS EAGER AND COMPLETE.</b> A buffer arrives with its memory bound and, for a uniform
    /// buffer, its ring cut. A texture arrives with every image view it will ever need, its canonical resting
    /// layout assigned, and its first-ever transition plus its creation-time clear already appended to the device's
    /// setup command buffer. Nothing here defers work to the draw path, which is what makes "no view factory
    /// reachable from the recording type" enforceable rather than aspirational.</para>
    ///
    /// <para><b>AND NO CREATION SUBMITS ANYTHING TO THE QUEUE (V-M10).</b> The incumbent's texture constructor
    /// issues a whole <c>vkQueueSubmit</c> for the clear and another for the sampled transition, so loading a scene
    /// with two hundred textures is two hundred submissions before a frame is drawn. Here they are appended and
    /// flushed lazily. See <see cref="VulkanSetupCommands"/>.</para>
    ///
    /// <para><b>EVERY MEMBER OF THE SEAM IS NOW BUILT, and this paragraph is the ledger of which row built
    /// which.</b> Row 9 owns buffers, textures, samplers, command lists and fences, row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520) added RESOURCE LAYOUTS and RESOURCE SETS (a
    /// content-deduplicated <c>VkDescriptorSetLayout</c> and one <c>VkDescriptorSet</c> allocated and written
    /// once). Row 12 (https://github.com/APKiwiOrg/KhaozEngine/issues/522) added FRAMEBUFFERS, which are the one
    /// creation here that makes no native object at all (V-A1), row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526) added SHADER SETS and COMPUTE SHADERS (GLSL to
    /// SPIR-V through the engine's own front end, then <c>vkCreateShaderModule</c> over the bytes verbatim, with
    /// the modules shared by SPIR-V hash), and row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523) added both PIPELINES, which are the last two and are
    /// what closes the list. <c>D3D11ResourceFactory</c> established the naming discipline that got this factory
    /// here, and <c>VulkanResourceCreationTests</c> keeps the pair of sets that tracked it honest: the refusal
    /// half is empty now, which is the fact worth stating rather than a reason to delete the assertion.</para>
    ///
    /// <para><b>AND THE DESCRIPTOR SUBSYSTEM IS HELD HERE RATHER THAN ON THE RESOURCE OWNER, WHICH IS DECISION
    /// V-D2 (6.3).</b> The recording type's field graph legitimately reaches a <see cref="VulkanResourceOwner"/>
    /// through the staging block lifetime edge, so a descriptor pool hung off that record would sit on the far
    /// side of the one allowance the unreachability walk makes, and the architecture test would keep passing
    /// while a draw could allocate a descriptor set. This factory is already on that test's forbidden list, so
    /// <see cref="VulkanDescriptors"/> lives here and on the device and nowhere a recorder can see.</para>
    ///
    /// <para><b>CREATION IS FREE-THREADED, WITH TWO SHORT LOCKS UNDERNEATH IT (V-W8).</b> Vulkan has no
    /// <c>DriverConcurrentCreates</c> analogue to ask about, so there is no creation gate here at all. What is
    /// genuinely shared is the block allocator, which takes its own lock around a suballocation, and the SETUP
    /// COMMAND BUFFER, which takes the third short lock around the append a texture makes. Neither is held across
    /// a creation call and neither is held when this factory returns.</para>
    /// </summary>
    internal sealed class VulkanResourceFactory : IGpuResourceFactory
    {
        readonly VulkanResourceOwner _owner;
        readonly VulkanRingAllocator _rings;
        readonly VulkanSetupCommands _setup;
        readonly VulkanDescriptors _descriptors;
        readonly VulkanShaderModuleCache _modules;
        readonly VulkanPipelines _pipelines;
        readonly Func<IGpuCommandList> _createCommandList;
        readonly Func<IGpuFence> _createFence;
        readonly ulong _minUniformBufferOffsetAlignment;
        readonly bool _samplerAnisotropy;
        readonly int _maxMsaaSampleCount;

        /// <param name="owner">The device's resource seam, allocator, timeline and retire list.</param>
        /// <param name="rings">The device's ONE ring allocator, which a uniform buffer cuts a ring out of.</param>
        /// <param name="setup">The device's setup command buffer, which every texture appends to.</param>
        /// <param name="descriptors">The device's ONE descriptor subsystem: the two content-dedup caches and the
        /// pools (row 10). It is held HERE and by the device and by nothing a recorder can reach, which is
        /// decision V-D2's structural enforcement rather than a preference.</param>
        /// <param name="modules">The device's ONE <c>VkShaderModule</c> cache (row 16), which dedups by SPIR-V
        /// hash. It is the device's rather than this factory's for the reason the descriptor subsystem is: a
        /// second cache would hand out two handles for one module and destroy neither at the right time.</param>
        /// <param name="pipelines">The device's ONE pipeline subsystem (row 13): the pipeline seam and the
        /// <c>VkPipelineCache</c> every creation compiles through. Held HERE and on the device and nowhere a
        /// recorder can see, for the reason <paramref name="descriptors"/> is: creating a pipeline is a shader
        /// compile, and a recorder that could reach one could compile inside a frame.</param>
        /// <param name="createCommandList">The device's own list factory. It comes from the device rather than
        /// being built here for the reason <c>D3D11ResourceFactory</c>'s equivalent does: the depth, the timeline
        /// and the backpressure accumulator a list gates on are all the device's, and threading them through this
        /// factory would put them in every signature.</param>
        /// <param name="createFence">The device's timeline fence factory, for the same reason.</param>
        /// <param name="capabilities">The device's own capability set. Two members are read: the MSAA ceiling for
        /// the sample-count refusal, and the anisotropy feature for the sampler degradation. The whole set is taken
        /// rather than those two numbers so a factory that has to validate against a third later needs no signature
        /// change.</param>
        /// <param name="minUniformBufferOffsetAlignment">The device limit the ring stride is rounded to.</param>
        internal VulkanResourceFactory(VulkanResourceOwner owner, VulkanRingAllocator rings,
            VulkanSetupCommands setup, VulkanDescriptors descriptors, VulkanShaderModuleCache modules,
            VulkanPipelines pipelines, Func<IGpuCommandList> createCommandList, Func<IGpuFence> createFence,
            in GpuCapabilities capabilities,
            ulong minUniformBufferOffsetAlignment = VulkanRingStride.OffsetAlignmentFloor)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(rings);
            ArgumentNullException.ThrowIfNull(setup);
            ArgumentNullException.ThrowIfNull(descriptors);
            ArgumentNullException.ThrowIfNull(modules);
            ArgumentNullException.ThrowIfNull(pipelines);
            ArgumentNullException.ThrowIfNull(createCommandList);
            ArgumentNullException.ThrowIfNull(createFence);

            _owner = owner;
            _rings = rings;
            _setup = setup;
            _descriptors = descriptors;
            _modules = modules;
            _pipelines = pipelines;
            _createCommandList = createCommandList;
            _createFence = createFence;
            _minUniformBufferOffsetAlignment = minUniformBufferOffsetAlignment;
            _samplerAnisotropy = capabilities.SamplerAnisotropy;
            _maxMsaaSampleCount = capabilities.MaxMsaaSampleCount;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <see cref="VulkanBufferRingPolicy.ForBuffer"/> IS THE FIRST STATEMENT of the constructor this calls,
        /// before a single byte is allocated. It either refuses the one combination this backend cannot honour (a
        /// uniform buffer that is also bound some other way, a documented divergence from the Veldrid leg) or
        /// answers whether the native buffer holds one segment or <see cref="VulkanFramesInFlight"/> of them.
        /// </remarks>
        public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
            => new VulkanBuffer(_owner, _rings, d, _minUniformBufferOffsetAlignment);

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// <see cref="GpuTextureDescription.SampleCount"/> is above this device's
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/>. It THROWS rather than rounding down, which is decision
        /// C4's departure inherited for the reason it gives: the engine already has the one place a request is
        /// meant to be clamped (<c>AntiAliasing.ResolveFor</c> in KhaozEngine.Render3D), so a count arriving here
        /// above the maximum came from a caller that skipped it, and rounding down would hide that behind a
        /// framebuffer that is quietly not multisampled.
        /// <para>
        /// THE CEILING IS THE DRIVER'S OWN ANSWER, not a pin. <see cref="VulkanMsaaLimit.MinOverTheEngineTargets"/>
        /// reduces each of the engine's three MRT formats to the highest sample bit
        /// <c>vkGetPhysicalDeviceImageFormatProperties</c> reports for the usage that format is used under, and
        /// takes the minimum, which is the incumbent's own <c>GetSampleCountLimit</c> fold reproduced (V-C5). It
        /// reaches this type as <see cref="GpuCapabilities.MaxMsaaSampleCount"/> through
        /// <c>VulkanPhysicalDeviceReader</c>, so a refusal here is a real device limit and never a number the
        /// engine invented for <c>AntiAliasing.ResolveFor</c> to act on.
        /// </para>
        /// </exception>
        public IGpuTexture CreateTexture(in GpuTextureDescription d)
        {
            if (d.SampleCount > (uint)_maxMsaaSampleCount)
            {
                throw new ArgumentException(
                    "A texture was created with a sample count of "
                    + d.SampleCount.ToString(CultureInfo.InvariantCulture)
                    + " on a native Vulkan device whose MaxMsaaSampleCount is "
                    + _maxMsaaSampleCount.ToString(CultureInfo.InvariantCulture)
                    + ". It is refused rather than rounded down, because the engine clamps upstream in "
                    + "AntiAliasing.ResolveFor and a silent downgrade presents as a golden mismatch that reads "
                    + "like a rendering bug. That ceiling is what this driver reported, not an engine pin: "
                    + "vkGetPhysicalDeviceImageFormatProperties is asked for each of the engine's three MRT "
                    + "targets (R8G8B8A8_UNorm, R32_Float and D32_Float_S8_UInt) with the usage that target is "
                    + "used under, each answer is reduced to its highest supported sample bit, and the minimum "
                    + "of the three is the device's MaxMsaaSampleCount.",
                    nameof(d));
            }

            return new VulkanTexture(_owner, _setup, d);
        }

        /// <inheritdoc/>
        /// <remarks>The address modes are taken from the description as written. The DEVICE'S OWN shared pair is
        /// built from <see cref="VulkanSharedSamplers"/>, which is wrap on all three axes and is emphatically not
        /// <see cref="GpuSamplerDescription.Point"/> or <see cref="GpuSamplerDescription.Linear"/>: those default
        /// every axis to clamp, and reading the mode off them because the names matched cost two goldens on the
        /// Direct3D 11 leg.</remarks>
        public IGpuSampler CreateSampler(in GpuSamplerDescription d)
            => new VulkanSampler(_owner, d, _samplerAnisotropy);

        /// <inheritdoc/>
        public IGpuCommandList CreateCommandList() => _createCommandList();

        /// <inheritdoc/>
        /// <remarks>A fence on this backend is a VALUE on the device's one timeline rather than a
        /// <c>VkFence</c>, so this creates no native object at all. There is no capability gate in front of it,
        /// because this backend's <see cref="GpuCapabilities.SupportsCompletionFences"/> is unconditionally true
        /// and identical to the incumbent's.</remarks>
        public IGpuFence CreateFence() => _createFence();

        /// <inheritdoc/>
        /// <remarks>
        /// IT CREATES NOTHING NATIVE, which is decision V-A1 arriving at the seam. There is no
        /// <c>VkFramebuffer</c> and no <c>VkRenderPass</c> in this backend, so a framebuffer is an aggregate of
        /// attachment views the textures already own, and its disposal releases nothing. See
        /// <see cref="VulkanFramebuffer"/>.
        /// </remarks>
        /// <exception cref="ArgumentException">There are no attachments at all, an attachment's size or sample
        /// count differs from the first one's, an attachment was created by another backend, or an attachment's
        /// texture never declared the usage that would have given it a view.</exception>
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        {
            ArgumentNullException.ThrowIfNull(colour);

            var attachments = new VulkanTexture[colour.Length];
            for (int i = 0; i < colour.Length; i++)
            {
                attachments[i] = VulkanTexture.Require(colour[i], "a native Vulkan framebuffer colour attachment");
            }

            return new VulkanFramebuffer(
                depth is null
                    ? null
                    : VulkanTexture.Require(depth, "a native Vulkan framebuffer depth attachment"),
                attachments);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE <c>VkDescriptorSetLayout</c> IS CONTENT-DEDUPLICATED AND SHARED (V-D5), so two layouts created
        /// from identical descriptions hand back the same native handle. That is what makes row 11's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) pipeline compatibility test a pointer compare and
        /// what makes bound descriptors survive a pipeline switch at all. It follows that
        /// <c>IGpuResourceLayout.Dispose</c> destroys nothing: the cache retires every handle at device teardown.
        /// <para>
        /// EVERY <see cref="GpuResourceKind.UniformBuffer"/> ELEMENT BECOMES A DYNAMIC UNIFORM DESCRIPTOR (V-D4),
        /// whether or not it carries <see cref="GpuResourceLayoutElement.Dynamic"/>, because the per-frame ring
        /// base is applied at bind. A declared-dynamic element that is NOT a uniform buffer is refused here.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">An element is declared dynamic and is not a uniform buffer
        /// (<see cref="VulkanDescriptorPolicy.TypeFor"/>).</exception>
        public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
            => _descriptors.CreateLayout(d);

        /// <inheritdoc/>
        /// <remarks>
        /// ONE <c>VkDescriptorSet</c>, ALLOCATED AND WRITTEN ONCE and never touched again (V-D1). A single
        /// <c>vkUpdateDescriptorSets</c> covers every binding, every <see cref="GpuBufferRange"/> is resolved
        /// here rather than at a draw, and the descriptor's range is the BIND WINDOW: never
        /// <c>VK_WHOLE_SIZE</c> and never the ring stride (V-M6). See <see cref="VulkanResourceSet"/>.
        /// </remarks>
        /// <exception cref="ArgumentException">The layout was not created by this backend, the resource count
        /// does not match the element count, or a resource does not fit the element it was given to.</exception>
        public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
            => _descriptors.CreateSet(d);

        /// <inheritdoc/>
        /// <remarks>
        /// THERE IS NO CROSS-COMPILATION HERE AND THAT IS THE HEADLINE (V-S1). The seam's name says "FromSpirv"
        /// and on this backend it is literal: the engine's own front end turns each GLSL 450 source into SPIR-V
        /// and <c>vkCreateShaderModule</c> takes the bytes verbatim. No HLSL, no FXC, no register numbering and no
        /// reflection read back off the module.
        /// <para>
        /// THE MODULES ARE SHARED BY SPIR-V HASH (V-S7), so eleven fullscreen post programs name ONE vertex
        /// module, and <c>IGpuShaderSet.Dispose</c> destroys nothing. See <see cref="VulkanShaderModuleCache"/>.
        /// </para>
        /// </remarks>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V.</exception>
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
            => new VulkanShaderSet(_modules, vertGlsl, fragGlsl);

        /// <inheritdoc/>
        /// <remarks>The compute twin, with the workgroup size read out of the module itself rather than taken
        /// from a caller. There is no capability gate in front of it, because this backend's
        /// <see cref="GpuCapabilities.SupportsCompute"/> is unconditionally true, for the same reason
        /// <see cref="CreateFence"/> has none.</remarks>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, or declares no
        /// resolvable workgroup size.</exception>
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
            => new VulkanComputeShader(_modules, computeGlsl);

        /// <inheritdoc/>
        /// <remarks>
        /// EVERYTHING EXCEPT VIEWPORT AND SCISSOR IS BAKED INTO THE PIPELINE OBJECT, which is the incumbent's
        /// shape kept deliberately (7.1), and the target's formats arrive as a
        /// <c>VkPipelineRenderingCreateInfo</c> built from <see cref="GpuPipelineDescription.Outputs"/> rather
        /// than as a <c>VkRenderPass</c> (V-A1). Vertex input comes from the caller's own layouts with no
        /// reflection read off the module, which is what makes the shader path three lines long.
        /// <para>
        /// THE <c>VkPipelineLayout</c> IS THE SHARED ONE (V-D5), which is what makes row 11's compatibility
        /// prefix a pointer compare, and taking it is also where 8.3's third defence counts this pipeline's
        /// dynamic uniform descriptors against the device's limit.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentException">The shader set or a resource layout came from another backend, or
        /// a vertex layout declares an instance step rate this backend cannot express.</exception>
        /// <exception cref="NotSupportedException">The layouts spend more dynamic uniform descriptors between
        /// them than the device allows.</exception>
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d) => _pipelines.CreateGraphics(d);

        /// <inheritdoc/>
        /// <remarks>The compute twin, which is two handles and no graphics state at all. There is no capability
        /// gate in front of it, because this backend's <see cref="GpuCapabilities.SupportsCompute"/> is
        /// unconditionally true.</remarks>
        /// <exception cref="ArgumentException">The compute shader or a resource layout came from another backend.
        /// </exception>
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
            => _pipelines.CreateCompute(d);
    }
}
