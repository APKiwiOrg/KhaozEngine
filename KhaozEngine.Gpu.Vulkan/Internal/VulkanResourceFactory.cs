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
    /// <para><b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> Row 9
    /// owns buffers, textures, samplers, command lists and fences, and row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520) added RESOURCE LAYOUTS and RESOURCE SETS: a
    /// content-deduplicated <c>VkDescriptorSetLayout</c> and one <c>VkDescriptorSet</c> allocated and written
    /// once. Framebuffers are row 12's, shader sets are row 16's and pipelines are row 13's, and each refuses with
    /// its own issue in the message rather than returning something that fails later somewhere less informative.
    /// That is the same discipline <c>D3D11ResourceFactory</c> established between its own row and the ones that
    /// filled it in, and this paragraph is a ledger: a stale one is worse than none.</para>
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
            VulkanSetupCommands setup, VulkanDescriptors descriptors, Func<IGpuCommandList> createCommandList,
            Func<IGpuFence> createFence, in GpuCapabilities capabilities,
            ulong minUniformBufferOffsetAlignment = VulkanRingStride.OffsetAlignmentFloor)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(rings);
            ArgumentNullException.ThrowIfNull(setup);
            ArgumentNullException.ThrowIfNull(descriptors);
            ArgumentNullException.ThrowIfNull(createCommandList);
            ArgumentNullException.ThrowIfNull(createFence);

            _owner = owner;
            _rings = rings;
            _setup = setup;
            _descriptors = descriptors;
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
        /// THE CEILING IS 1 UNTIL ROW 18 (https://github.com/APKiwiOrg/KhaozEngine/issues/528) FILLS IT IN, which
        /// is a real refusal rather than a placeholder: the incumbent's own MSAA computation is what that row
        /// reproduces (V-C5), and a number invented here would be a silent lie that
        /// <c>AntiAliasing.ResolveFor</c> would act on.
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
                    + "like a rendering bug. This device's ceiling is still the conservative 1 that row 4 pinned: "
                    + "the real computation is read off the incumbent's own by the capability row "
                    + "(https://github.com/APKiwiOrg/KhaozEngine/issues/528).",
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
        public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
            => throw NotBuiltYet("Creating a framebuffer", RenderingRow);

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
        public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
            => throw NotBuiltYet("Creating a shader set", ShaderRow);

        /// <inheritdoc/>
        public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl)
            => throw NotBuiltYet("Creating a compute shader", ShaderRow);

        /// <inheritdoc/>
        public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
            => throw NotBuiltYet("Creating a graphics pipeline", PipelineRow);

        /// <inheritdoc/>
        public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d)
            => throw NotBuiltYet("Creating a compute pipeline", PipelineRow);

        // The row that owns each unbuilt member, as a full URL, because these messages are read by somebody who has
        // just hit one and needs to know whether to wait for a row or file a bug.
        const string DescriptorRow = "the descriptor row (https://github.com/APKiwiOrg/KhaozEngine/issues/520)";
        const string RenderingRow =
            "the dynamic-rendering row (https://github.com/APKiwiOrg/KhaozEngine/issues/522)";
        const string PipelineRow = "the pipeline row (https://github.com/APKiwiOrg/KhaozEngine/issues/523)";
        const string ShaderRow = "the shader-path row (https://github.com/APKiwiOrg/KhaozEngine/issues/526)";

        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Vulkan backend: it lands in {row}. Buffers, textures, "
                + "samplers, command lists, fences, resource layouts and resource sets ARE live (work-breakdown "
                + "rows 9 and 10, https://github.com/APKiwiOrg/KhaozEngine/issues/519 and "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/520). This is a statement about the package and "
                + "not about this machine. Select GpuBackendKind.Vulkan, which goes through Veldrid, for a fully "
                + "working Vulkan device.");
    }
}
