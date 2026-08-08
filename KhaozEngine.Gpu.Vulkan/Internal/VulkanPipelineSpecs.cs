using System;
using System.Globalization;
using System.Numerics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE ONLY TWO PIECES OF PIPELINE STATE THAT ARE NOT BAKED INTO THE PIPELINE OBJECT (7.1). Everything else a
    /// <see cref="GpuPipelineDescription"/> carries is frozen at creation, which is the incumbent's shape and is
    /// kept: it is what makes a pipeline switch the only thing that changes blend, depth or raster state, and it
    /// is why the disk pipeline cache of decision V-S7 is worth having at all, since baking everything means many
    /// more pipeline permutations to compile on a cold start.
    /// </summary>
    internal enum VulkanDynamicState
    {
        /// <summary><c>VK_DYNAMIC_STATE_VIEWPORT</c>, emitted on a framebuffer CHANGE (V-A5).</summary>
        Viewport,

        /// <summary><c>VK_DYNAMIC_STATE_SCISSOR</c>, emitted on a framebuffer change and by
        /// <c>SetScissorRect</c>.</summary>
        Scissor,
    }

    /// <summary>
    /// THE DYNAMIC STATE LIST, AS A VALUE RATHER THAN AS A LINE INSIDE THE SEAM. Row 13's spec says "dynamic state
    /// is exactly viewport and scissor", and a claim buried in a <c>VkPipelineDynamicStateCreateInfo</c> built
    /// under a real driver is a claim no headless test can read. Here it is a two-element array a plain
    /// <c>[Fact]</c> asserts on, and <see cref="VulkanPipelineApi"/> translates it verbatim.
    /// </summary>
    internal static class VulkanPipelineDynamicState
    {
        static readonly VulkanDynamicState[] states = [VulkanDynamicState.Viewport, VulkanDynamicState.Scissor];

        /// <summary>Viewport and scissor, in that order, and nothing else ever.</summary>
        internal static ReadOnlySpan<VulkanDynamicState> States => states;
    }

    /// <summary>
    /// EVERYTHING <c>vkCreateGraphicsPipelines</c> IS ASKED FOR, as this backend's own plain data. Work-breakdown
    /// row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>IT NAMES NO SILK.NET TYPE</b>, the same split <see cref="IVulkanRenderApi"/> takes: the decisions
    /// (which attributes sit at which location and offset, how many blend attachments there really are, which
    /// formats the rendering create-info carries) are engine logic and run under <c>dotnet test</c> with no
    /// loader, and the translation into the eight <c>VkPipeline*StateCreateInfo</c> structures happens at
    /// <see cref="VulkanPipelineApi"/> and nowhere above it. The state enums are the SEAM's own
    /// (<see cref="GpuBlendFactor"/> and friends) rather than a second family invented here, because a copy of
    /// eleven blend factors would be eleven more mappings to get wrong for no reader benefit.</para>
    ///
    /// <para><b>THERE IS NO <c>VkRenderPass</c> IN IT (V-A1).</b> <see cref="ColourFormats"/> and
    /// <see cref="DepthFormat"/> come straight off <see cref="GpuPipelineDescription.Outputs"/> and become a
    /// <c>VkPipelineRenderingCreateInfo</c> chained onto the create info, which under dynamic rendering is the
    /// whole of what a render pass would have carried. That is why row 12
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) needed no pass cache and why a resize invalidates
    /// nothing here. <see cref="SampleCount"/> is off the same description and lands somewhere else:
    /// <c>VkPipelineMultisampleStateCreateInfo.rasterizationSamples</c>, because the rendering create-info has no
    /// sample-count field at all.</para>
    /// </summary>
    internal sealed class VulkanGraphicsPipelineSpec
    {
        VulkanGraphicsPipelineSpec(ulong pipelineLayout, ulong vertexModule, ulong fragmentModule,
            VulkanVertexBinding[] vertexBindings, VulkanVertexAttribute[] vertexAttributes,
            GpuBlendAttachment[] blendAttachments, in GpuPipelineDescription description)
        {
            PipelineLayout = pipelineLayout;
            VertexModule = vertexModule;
            FragmentModule = fragmentModule;
            VertexBindings = vertexBindings;
            VertexAttributes = vertexAttributes;
            BlendAttachments = blendAttachments;
            BlendFactor = description.BlendFactor;
            Topology = description.Topology;
            Rasterizer = description.Rasterizer;
            DepthStencil = description.DepthStencil;
            ColourFormats = description.Outputs.Colour ?? [];
            DepthFormat = description.Outputs.Depth;
            SampleCount = (uint)Math.Max(description.Outputs.SampleCount, 1);
        }

        /// <summary>The SHARED <c>VkPipelineLayout</c> from <see cref="VulkanPipelineLayoutCache"/> (V-D5).
        /// </summary>
        internal ulong PipelineLayout { get; }

        /// <summary>The vertex stage's shared <c>VkShaderModule</c>.</summary>
        internal ulong VertexModule { get; }

        /// <summary>The fragment stage's shared <c>VkShaderModule</c>.</summary>
        internal ulong FragmentModule { get; }

        /// <summary>One per vertex buffer slot, in slot order.</summary>
        internal VulkanVertexBinding[] VertexBindings { get; }

        /// <summary>One per shader input location, in location order.</summary>
        internal VulkanVertexAttribute[] VertexAttributes { get; }

        /// <summary>One per COLOUR ATTACHMENT, which is also one per DECLARED blend state, because the two counts
        /// are required to match and a description whose counts differ never gets this far. See
        /// <see cref="ResolveBlends"/>.</summary>
        internal GpuBlendAttachment[] BlendAttachments { get; }

        /// <summary>The constant blend colour, baked in rather than dynamic (see
        /// <see cref="VulkanPipelineDynamicState"/>).</summary>
        internal Vector4 BlendFactor { get; }

        /// <summary>The primitive topology.</summary>
        internal GpuPrimitiveTopology Topology { get; }

        /// <summary>Cull mode, fill mode, winding and the depth clip flag.</summary>
        internal GpuRasterizerState Rasterizer { get; }

        /// <summary>Depth test, depth write and the comparison. There is no stencil state on the seam.</summary>
        internal GpuDepthStencilState DepthStencil { get; }

        /// <summary>The colour attachment formats, in order, which the rendering create-info carries.</summary>
        internal GpuPixelFormat[] ColourFormats { get; }

        /// <summary>The depth attachment format, or null when the target declares none.</summary>
        internal GpuPixelFormat? DepthFormat { get; }

        /// <summary>The target's MSAA sample count, at least 1. A pipeline's count MUST match the framebuffer it
        /// renders into.</summary>
        internal uint SampleCount { get; }

        /// <summary>
        /// Build the spec for one <see cref="GpuPipelineDescription"/> against already-resolved native handles.
        /// PURE and device-free: the caller resolves the shader set, the layouts and the pipeline layout, and
        /// everything decided here is decided from the description alone.
        /// </summary>
        /// <param name="description">The seam's description, as the caller wrote it.</param>
        /// <param name="pipelineLayout">The shared <c>VkPipelineLayout</c>, non-zero.</param>
        /// <param name="vertexModule">The vertex stage's module, non-zero.</param>
        /// <param name="fragmentModule">The fragment stage's module, non-zero.</param>
        /// <exception cref="ArgumentException">A handle is null, a vertex layout declares an instance step rate
        /// this backend cannot express, or the description declares a blend state count that is not its colour
        /// output count.</exception>
        internal static VulkanGraphicsPipelineSpec For(in GpuPipelineDescription description, ulong pipelineLayout,
            ulong vertexModule, ulong fragmentModule)
        {
            RequireHandle(pipelineLayout, "pipeline layout");
            RequireHandle(vertexModule, "vertex shader module");
            RequireHandle(fragmentModule, "fragment shader module");

            VulkanVertexBinding[] bindings = VulkanVertexInput.Build(
                description.VertexLayouts, out VulkanVertexAttribute[] attributes);

            return new VulkanGraphicsPipelineSpec(
                pipelineLayout, vertexModule, fragmentModule, bindings, attributes,
                ResolveBlends(description.BlendAttachments, (description.Outputs.Colour ?? []).Length),
                description);
        }

        /// <summary>
        /// ONE DECLARED BLEND STATE PER COLOUR OUTPUT, ENFORCED RATHER THAN REPAIRED. A count that is not the
        /// output count is refused by name, in both directions.
        /// <para>
        /// Vulkan requires <c>VkPipelineColorBlendStateCreateInfo.attachmentCount</c> to EQUAL the rendering
        /// create-info's colour attachment count, and the seam lets the two differ, because the engine's
        /// descriptions are not checked against that pairing anywhere. An earlier draft of this row REPAIRED the
        /// mismatch instead, dropping a declared state past the last colour output and padding an output nobody
        /// declared with <see cref="GpuBlendAttachment.OverrideBlend"/>. Refusing is better on both halves. The
        /// padding wrote every channel of an attachment the caller never described, which is a per-backend answer
        /// invented for a state that was never given, and the Direct3D 11 native backend answers the same
        /// description with its own struct defaults, so the two backends would quietly disagree about the same
        /// undeclared attachment. The drop threw away a state the caller wrote and meant.
        /// </para>
        /// <para>
        /// IT COSTS NOTHING TO ENFORCE, because the contract is the one the seam already states:
        /// <see cref="GpuPipelineDescription.BlendAttachments"/> is documented as one per colour output, and every
        /// shipped call site declares exactly that. So the refusal only fires on a description that was already
        /// wrong under at least one backend, at creation, by name, instead of at a draw.
        /// </para>
        /// </summary>
        /// <param name="declared">What the caller declared, possibly null, which counts as none.</param>
        /// <param name="colourCount">How many colour attachments the pipeline's outputs really carry.</param>
        /// <exception cref="ArgumentException">The two counts differ.</exception>
        internal static GpuBlendAttachment[] ResolveBlends(GpuBlendAttachment[]? declared, int colourCount)
        {
            int declaredCount = declared?.Length ?? 0;
            if (declaredCount != colourCount)
            {
                throw new ArgumentException(
                    "A graphics pipeline declared " + declaredCount.ToString(CultureInfo.InvariantCulture)
                    + " blend attachment state(s) for " + colourCount.ToString(CultureInfo.InvariantCulture)
                    + " colour output(s). GpuPipelineDescription.BlendAttachments is one per colour output, and "
                    + "Vulkan requires the colour blend state's attachment count to equal the rendering "
                    + "create-info's, so neither dropping the extras nor inventing a state for an attachment "
                    + "nobody described is an answer this backend is willing to give.",
                    nameof(declared));
            }

            // Copied rather than kept, because the spec holds it for the pipeline's creation and the array is the
            // caller's own, which a shipped renderer is free to reuse for the next description it builds.
            return declaredCount == 0 ? [] : [.. declared!];
        }

        static void RequireHandle(ulong handle, string what)
        {
            if (handle != 0) return;

            throw new ArgumentException(
                "A native Vulkan graphics pipeline was built with a null " + what
                + ". Every handle a pipeline names is created before the pipeline is, so a zero here means the "
                + "creation that should have produced it was skipped rather than that the pipeline declares none.",
                nameof(handle));
        }
    }

    /// <summary>
    /// EVERYTHING <c>vkCreateComputePipelines</c> IS ASKED FOR, which is two handles. A compute pipeline has no
    /// vertex input, no blend, depth or raster state, no dynamic state and no attachment formats, so the
    /// asymmetry with <see cref="VulkanGraphicsPipelineSpec"/> is the seam's own
    /// (<see cref="GpuComputePipelineDescription"/> is deliberately a separate type) arriving intact.
    /// </summary>
    /// <param name="PipelineLayout">The shared <c>VkPipelineLayout</c>.</param>
    /// <param name="Module">The compute stage's shared <c>VkShaderModule</c>. The workgroup size is INSIDE it and
    /// is never passed alongside, which is why the seam's description carries no thread-group size either.</param>
    internal readonly record struct VulkanComputePipelineSpec(ulong PipelineLayout, ulong Module)
    {
        /// <summary>Build the spec against already-resolved handles.</summary>
        /// <exception cref="ArgumentException">Either handle is null.</exception>
        internal static VulkanComputePipelineSpec For(ulong pipelineLayout, ulong module)
        {
            if (pipelineLayout == 0 || module == 0)
            {
                throw new ArgumentException(
                    "A native Vulkan compute pipeline was built with a null handle: pipeline layout 0x"
                    + pipelineLayout.ToString("x", CultureInfo.InvariantCulture) + ", module 0x"
                    + module.ToString("x", CultureInfo.InvariantCulture)
                    + ". Both are created before the pipeline is.",
                    nameof(pipelineLayout));
            }

            return new VulkanComputePipelineSpec(pipelineLayout, module);
        }
    }
}
