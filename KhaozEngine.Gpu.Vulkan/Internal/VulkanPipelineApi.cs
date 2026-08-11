using System;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SIX REAL DRIVER CALLS BEHIND <see cref="IVulkanPipelineApi"/>, and nothing else. Which attribute sits
    /// at which location, how many blend attachments there really are, which dynamic state stays dynamic and
    /// whether a disk blob may be handed to <c>vkCreatePipelineCache</c> at all are decided ABOVE this line, in
    /// <see cref="VulkanGraphicsPipelineSpec"/>, <see cref="VulkanPipelineDynamicState"/> and
    /// <see cref="VulkanPipelineCacheFile"/>, which is what makes all of it testable with no loader.
    ///
    /// <para><b>THE EIGHT STATE STRUCTURES ARE BUILT HERE AND NOWHERE ELSE</b>, the same split
    /// <see cref="VulkanRenderApi"/> takes with the rendering info: the spec decides in this backend's own values
    /// and the translation into <c>VkPipelineVertexInputStateCreateInfo</c> and its seven siblings happens at this
    /// line.</para>
    ///
    /// <para><b>THERE IS NO <c>VkRenderPass</c> AND NO SUBPASS INDEX THAT MEANS ANYTHING (V-A1).</b>
    /// <c>renderPass</c> is the null handle and a <c>VkPipelineRenderingCreateInfo</c> built from the seam's
    /// <see cref="GpuOutputDescription"/> is chained onto the create info instead, which is the whole of what a
    /// render pass would have carried and is why nothing in this backend caches a pass or invalidates one on a
    /// resize.</para>
    ///
    /// <para><b>THE CACHE PATH NEVER THROWS AND THAT IS THE CONTRACT RATHER THAN LENIENCY (V-S7).</b>
    /// <see cref="CreateCache"/> answers 0 and <see cref="ReadCacheData"/> answers an empty array on any failure,
    /// so a driver that refuses a blob, a device out of host memory and a cache nothing was compiled into all
    /// reach the same place: a cold start. Pipeline CREATION is the opposite and throws, because a pipeline that
    /// failed to compile has no fallback and a null handle would surface at the first draw.</para>
    /// </summary>
    internal sealed unsafe class VulkanPipelineApi : IVulkanPipelineApi
    {
        // Every shipped stage is compiled from GLSL by the engine's own front end, and glslang names the entry
        // point of a GLSL module "main" with no way to ask for another. A UTF-8 literal so no marshalling
        // allocation happens per pipeline.
        static ReadOnlySpan<byte> EntryPoint => "main\0"u8;

        const ColorComponentFlags AllChannels =
            ColorComponentFlags.RBit | ColorComponentFlags.GBit
            | ColorComponentFlags.BBit | ColorComponentFlags.ABit;

        readonly Vk _vk;
        readonly Device _device;
        readonly VulkanDeviceLossLatch _loss;
        readonly IDeviceLiveness _liveness;

        /// <param name="vk">The instance's loaded API.</param>
        /// <param name="device">The device that owns every pipeline made here and outlives them all.</param>
        /// <param name="loss">The device's loss latch, which every create result is checked against.</param>
        /// <param name="liveness">The device's liveness token, which gates the destroys.</param>
        internal VulkanPipelineApi(Vk vk, Device device, VulkanDeviceLossLatch loss,
            IDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(vk);
            ArgumentNullException.ThrowIfNull(loss);
            ArgumentNullException.ThrowIfNull(liveness);

            _vk = vk;
            _device = device;
            _loss = loss;
            _liveness = liveness;
        }

        /// <inheritdoc/>
        public ulong CreateCache(ReadOnlySpan<byte> seed)
        {
            fixed (byte* data = seed)
            {
                var createInfo = new PipelineCacheCreateInfo(
                    sType: StructureType.PipelineCacheCreateInfo,
                    initialDataSize: (nuint)seed.Length,
                    pInitialData: seed.IsEmpty ? null : data);

                Result created = _vk.CreatePipelineCache(_device, in createInfo, null, out PipelineCache cache);

                // LATCHED BUT NOT THROWN. The latch is how a lost device is REPORTED once, and losing it here
                // would hide the loss from the session log, but a cache is a time saver and its absence is a
                // supported state, so the caller carries on with no cache and the next real call reports.
                _loss.Check(created, "vkCreatePipelineCache");

                return created == Result.Success ? cache.Handle : 0;
            }
        }

        /// <inheritdoc/>
        public byte[] ReadCacheData(ulong cache)
        {
            if (cache == 0 || _liveness.IsDead) return [];

            var handle = new PipelineCache(cache);

            nuint size = 0;
            Result sized = _vk.GetPipelineCacheData(_device, handle, ref size, null);
            if (sized != Result.Success || size == 0) return [];

            var blob = new byte[(int)size];
            fixed (byte* target = blob)
            {
                nuint filled = size;
                Result read = _vk.GetPipelineCacheData(_device, handle, ref filled, target);

                // VK_INCOMPLETE means the cache grew between the two calls, which hands back a buffer with a tail
                // nothing wrote. Answering an empty array there costs one launch's worth of warm start and is the
                // alternative to persisting a blob that is a header plus garbage.
                if (read != Result.Success || filled != size) return [];
            }

            return blob;
        }

        /// <inheritdoc/>
        public void DestroyCache(ulong cache)
        {
            if (cache == 0 || _liveness.IsDead) return;

            _vk.DestroyPipelineCache(_device, new PipelineCache(cache), null);
        }

        /// <inheritdoc/>
        public ulong CreateGraphicsPipeline(ulong cache, VulkanGraphicsPipelineSpec spec)
        {
            ArgumentNullException.ThrowIfNull(spec);

            VertexInputBindingDescription[] bindings = Bindings(spec);
            VertexInputAttributeDescription[] attributes = Attributes(spec);
            PipelineColorBlendAttachmentState[] blends = Blends(spec);
            DynamicState[] dynamics = Dynamics();
            Format[] colourFormats = ColourFormats(spec);

            fixed (byte* entry = EntryPoint)
            fixed (VertexInputBindingDescription* pBindings = bindings)
            fixed (VertexInputAttributeDescription* pAttributes = attributes)
            fixed (PipelineColorBlendAttachmentState* pBlends = blends)
            fixed (DynamicState* pDynamics = dynamics)
            fixed (Format* pColourFormats = colourFormats)
            {
                PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2];
                stages[0] = Stage(ShaderStageFlags.VertexBit, spec.VertexModule, entry);
                stages[1] = Stage(ShaderStageFlags.FragmentBit, spec.FragmentModule, entry);

                var vertexInput = new PipelineVertexInputStateCreateInfo(
                    sType: StructureType.PipelineVertexInputStateCreateInfo,
                    vertexBindingDescriptionCount: (uint)bindings.Length,
                    pVertexBindingDescriptions: pBindings,
                    vertexAttributeDescriptionCount: (uint)attributes.Length,
                    pVertexAttributeDescriptions: pAttributes);

                var assembly = new PipelineInputAssemblyStateCreateInfo(
                    sType: StructureType.PipelineInputAssemblyStateCreateInfo,
                    topology: VulkanFormats.ToTopology(spec.Topology),
                    primitiveRestartEnable: false);

                // COUNTS ONLY, WITH NO VALUES, because both are dynamic. A viewport count of 1 is what makes
                // vkCmdSetViewport for viewport 0 legal, and it is also the whole of decision V-A5's dependency
                // on this structure.
                var viewport = new PipelineViewportStateCreateInfo(
                    sType: StructureType.PipelineViewportStateCreateInfo,
                    viewportCount: 1,
                    pViewports: null,
                    scissorCount: 1,
                    pScissors: null);

                var raster = new PipelineRasterizationStateCreateInfo(
                    sType: StructureType.PipelineRasterizationStateCreateInfo,
                    // THE INVERSE OF THE SEAM'S FLAG, deliberately. Direct3D's DepthClipEnable and Vulkan's
                    // depthClampEnable are opposites of each other, and clamping instead of clipping is exactly
                    // what a Direct3D caller asking for no depth clip gets. It needs the depthClamp feature, which
                    // VulkanFeatureChain enables by name.
                    depthClampEnable: !spec.Rasterizer.DepthClipEnabled,
                    rasterizerDiscardEnable: false,
                    polygonMode: VulkanFormats.ToPolygonMode(spec.Rasterizer.FillMode),
                    cullMode: VulkanFormats.ToCullMode(spec.Rasterizer.CullMode),
                    frontFace: VulkanFormats.ToFrontFace(spec.Rasterizer.FrontFace),
                    depthBiasEnable: false,
                    // 1.0 AND NOTHING ELSE. A width other than 1 needs the wideLines feature, which this backend
                    // does not enable and the seam cannot ask for.
                    lineWidth: 1f);

                var multisample = new PipelineMultisampleStateCreateInfo(
                    sType: StructureType.PipelineMultisampleStateCreateInfo,
                    rasterizationSamples: VulkanFormats.ToSampleCount(spec.SampleCount),
                    sampleShadingEnable: false,
                    alphaToCoverageEnable: false,
                    alphaToOneEnable: false);

                var depthStencil = new PipelineDepthStencilStateCreateInfo(
                    sType: StructureType.PipelineDepthStencilStateCreateInfo,
                    depthTestEnable: spec.DepthStencil.DepthTestEnabled,
                    depthWriteEnable: spec.DepthStencil.DepthWriteEnabled,
                    depthCompareOp: VulkanFormats.ToCompareOp(spec.DepthStencil.Comparison),
                    depthBoundsTestEnable: false,
                    // The seam carries no stencil state at all, on any backend.
                    stencilTestEnable: false);

                var blend = new PipelineColorBlendStateCreateInfo(
                    sType: StructureType.PipelineColorBlendStateCreateInfo,
                    logicOpEnable: false,
                    attachmentCount: (uint)blends.Length,
                    pAttachments: pBlends);

                blend.BlendConstants[0] = spec.BlendFactor.X;
                blend.BlendConstants[1] = spec.BlendFactor.Y;
                blend.BlendConstants[2] = spec.BlendFactor.Z;
                blend.BlendConstants[3] = spec.BlendFactor.W;

                var dynamic = new PipelineDynamicStateCreateInfo(
                    sType: StructureType.PipelineDynamicStateCreateInfo,
                    dynamicStateCount: (uint)dynamics.Length,
                    pDynamicStates: pDynamics);

                Format depthFormat = spec.DepthFormat is { } depth
                    ? VulkanFormats.ToVkFormat(depth, depthStencil: true)
                    : Format.Undefined;

                var rendering = new PipelineRenderingCreateInfo(
                    sType: StructureType.PipelineRenderingCreateInfo,
                    colorAttachmentCount: (uint)colourFormats.Length,
                    pColorAttachmentFormats: pColourFormats,
                    depthAttachmentFormat: depthFormat,
                    // THE STENCIL PLANE IS NAMED SEPARATELY, exactly as it is at a begin (V-A1): dynamic
                    // rendering splits the two, and both of the seam's depth formats are combined ones.
                    stencilAttachmentFormat: spec.DepthFormat is { } combined
                        && VulkanFormats.IsStencilFormat(combined) ? depthFormat : Format.Undefined);

                var createInfo = new GraphicsPipelineCreateInfo(
                    sType: StructureType.GraphicsPipelineCreateInfo,
                    pNext: &rendering,
                    stageCount: 2,
                    pStages: stages,
                    pVertexInputState: &vertexInput,
                    pInputAssemblyState: &assembly,
                    pViewportState: &viewport,
                    pRasterizationState: &raster,
                    pMultisampleState: &multisample,
                    pDepthStencilState: &depthStencil,
                    pColorBlendState: &blend,
                    pDynamicState: &dynamic,
                    layout: new PipelineLayout(spec.PipelineLayout),
                    renderPass: default,
                    subpass: 0);

                Result created = _vk.CreateGraphicsPipelines(
                    _device, new PipelineCache(cache), 1, in createInfo, null, out Pipeline pipeline);

                Require(created, "vkCreateGraphicsPipelines");
                return pipeline.Handle;
            }
        }

        /// <inheritdoc/>
        public ulong CreateComputePipeline(ulong cache, in VulkanComputePipelineSpec spec)
        {
            fixed (byte* entry = EntryPoint)
            {
                var createInfo = new ComputePipelineCreateInfo(
                    sType: StructureType.ComputePipelineCreateInfo,
                    stage: Stage(ShaderStageFlags.ComputeBit, spec.Module, entry),
                    layout: new PipelineLayout(spec.PipelineLayout));

                Result created = _vk.CreateComputePipelines(
                    _device, new PipelineCache(cache), 1, in createInfo, null, out Pipeline pipeline);

                Require(created, "vkCreateComputePipelines");
                return pipeline.Handle;
            }
        }

        /// <inheritdoc/>
        public void DestroyPipeline(ulong pipeline)
        {
            if (pipeline == 0 || _liveness.IsDead) return;

            _vk.DestroyPipeline(_device, new Pipeline(pipeline), null);
        }

        // The loss latch first, then the result code, in every configuration, as every other creation call in this
        // package does. vkCreate*Pipelines is named among the calls that can return VK_ERROR_DEVICE_LOST, and the
        // incumbent's own CheckResult is [Conditional("DEBUG")], so a Release build of it carries on with a handle
        // that is not one.
        void Require(Result created, string call)
        {
            if (_loss.Check(created, call))
            {
                throw new InvalidOperationException(
                    "The native Vulkan backend could not create a pipeline, because the device was LOST. The loss "
                    + "itself is in the session log and in the telemetry session header, with the call that first "
                    + "noticed it.");
            }

            VulkanResultCodes.Require(created, call);
        }

        static PipelineShaderStageCreateInfo Stage(ShaderStageFlags stage, ulong module, byte* entryPoint)
            => new(
                sType: StructureType.PipelineShaderStageCreateInfo,
                stage: stage,
                module: new ShaderModule(module),
                pName: entryPoint,
                // NO SPECIALIZATION CONSTANTS anywhere in this backend. The engine compiles a distinct source per
                // variant (the ocean kernels are compiled per cascade resolution), so there is nothing to
                // specialize and a null here is the whole story rather than a gap.
                pSpecializationInfo: null);

        static VertexInputBindingDescription[] Bindings(VulkanGraphicsPipelineSpec spec)
        {
            var bindings = new VertexInputBindingDescription[spec.VertexBindings.Length];
            for (int i = 0; i < bindings.Length; i++)
            {
                VulkanVertexBinding binding = spec.VertexBindings[i];
                bindings[i] = new VertexInputBindingDescription(
                    binding: binding.Binding,
                    stride: binding.Stride,
                    inputRate: binding.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex);
            }

            return bindings;
        }

        static VertexInputAttributeDescription[] Attributes(VulkanGraphicsPipelineSpec spec)
        {
            var attributes = new VertexInputAttributeDescription[spec.VertexAttributes.Length];
            for (int i = 0; i < attributes.Length; i++)
            {
                VulkanVertexAttribute attribute = spec.VertexAttributes[i];
                attributes[i] = new VertexInputAttributeDescription(
                    location: attribute.Location,
                    binding: attribute.Binding,
                    format: VulkanFormats.ToVertexFormat(attribute.Format),
                    offset: attribute.Offset);
            }

            return attributes;
        }

        static PipelineColorBlendAttachmentState[] Blends(VulkanGraphicsPipelineSpec spec)
        {
            var blends = new PipelineColorBlendAttachmentState[spec.BlendAttachments.Length];
            for (int i = 0; i < blends.Length; i++)
            {
                GpuBlendAttachment declared = spec.BlendAttachments[i];
                blends[i] = new PipelineColorBlendAttachmentState(
                    blendEnable: declared.BlendEnabled,
                    srcColorBlendFactor: VulkanFormats.ToBlendFactor(declared.SourceColorFactor),
                    dstColorBlendFactor: VulkanFormats.ToBlendFactor(declared.DestinationColorFactor),
                    colorBlendOp: VulkanFormats.ToBlendOp(declared.ColorFunction),
                    srcAlphaBlendFactor: VulkanFormats.ToBlendFactor(declared.SourceAlphaFactor),
                    dstAlphaBlendFactor: VulkanFormats.ToBlendFactor(declared.DestinationAlphaFactor),
                    alphaBlendOp: VulkanFormats.ToBlendOp(declared.AlphaFunction),
                    // EVERY CHANNEL, ALWAYS. The seam has no write mask, and the one shape that would want a
                    // partial one (an MRT attachment a pass must not modify) is expressed as
                    // GpuBlendAttachment.PreserveDestination instead, which is a blend rather than a mask.
                    colorWriteMask: AllChannels);
            }

            return blends;
        }

        static DynamicState[] Dynamics()
        {
            ReadOnlySpan<VulkanDynamicState> states = VulkanPipelineDynamicState.States;
            var dynamics = new DynamicState[states.Length];
            for (int i = 0; i < dynamics.Length; i++) dynamics[i] = VulkanFormats.ToDynamicState(states[i]);

            return dynamics;
        }

        static Format[] ColourFormats(VulkanGraphicsPipelineSpec spec)
        {
            var formats = new Format[spec.ColourFormats.Length];
            for (int i = 0; i < formats.Length; i++)
            {
                formats[i] = VulkanFormats.ToVkFormat(spec.ColourFormats[i], depthStencil: false);
            }

            return formats;
        }
    }
}
