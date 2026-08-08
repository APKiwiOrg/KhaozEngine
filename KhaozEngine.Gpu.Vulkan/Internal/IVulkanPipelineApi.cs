using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SIX REAL DRIVER CALLS PIPELINE CREATION IS, behind an interface for the same reason
    /// <see cref="IVulkanDescriptorApi"/> and <see cref="IVulkanShaderApi"/> are ones: everything that can be
    /// WRONG about a pipeline (which attribute sits at which location, which blend state each attachment really
    /// gets, which formats the rendering create-info carries, which dynamic state is left dynamic, and whether a
    /// cache blob on disk may be handed to the driver at all) is engine logic, and it runs under
    /// <c>dotnet test</c> on a machine with no Vulkan loader. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>THIS SEAM IS UNREACHABLE FROM THE RECORDING TYPE, for the reason
    /// <see cref="IVulkanDescriptorApi"/> is.</b> Creating a <c>VkPipeline</c> is the single most expensive thing
    /// a driver does on demand: it is a shader compile, and doing one inside a frame is the classic hitch. None of
    /// <c>vkCreateGraphicsPipelines</c>, <c>vkCreateComputePipelines</c> or <c>vkCreatePipelineCache</c> is a
    /// bind, a draw or a barrier, so the native-call budget sink cannot see any of them and no counting seam ever
    /// will. The enforcement is the same structural one: a recorder's field graph cannot reach this interface,
    /// asserted by <c>VulkanRecordingUnreachabilityTests</c>. Binding a pipeline that already exists is a
    /// different act and has its own one-call seam, <see cref="IVulkanPipelineBinder"/>.</para>
    ///
    /// <para><b>HANDLES ARE <c>ulong</c></b>, as they are on every other seam here. <c>VkPipeline</c> and
    /// <c>VkPipelineCache</c> are non-dispatchable handles and are 64-bit integers on the native side, so nothing
    /// above this line names a Silk.NET type.</para>
    ///
    /// <para><b>THE CACHE HANDLE IS A PARAMETER RATHER THAN STATE HELD DOWN HERE.</b> Which cache a pipeline is
    /// compiled through, whether there is one at all, and what happens when the disk blob is rejected are all
    /// decisions <see cref="VulkanPipelineCache"/> takes above this line, where a plain <c>[Fact]</c> can drive
    /// them. An implementation that held its own cache would decide them where nothing can see.</para>
    /// </summary>
    internal interface IVulkanPipelineApi
    {
        /// <summary>
        /// <c>vkCreatePipelineCache</c>, seeded with <paramref name="seed"/> when it is non-empty and empty
        /// otherwise. The bytes have ALREADY been header-validated against this device
        /// (<see cref="VulkanPipelineCacheFile.Validate"/>) by the time they arrive.
        /// </summary>
        /// <returns>The <c>VkPipelineCache</c> handle, or 0 when creation failed, which is a cold start rather
        /// than an error: the whole path is best-effort (V-S7).</returns>
        ulong CreateCache(ReadOnlySpan<byte> seed);

        /// <summary>
        /// <c>vkGetPipelineCacheData</c>, in its two-call form (ask the size, then fill a buffer).
        /// </summary>
        /// <returns>The blob to persist, or an empty array when the call failed or the cache is empty. Never
        /// null, and never a partially filled buffer: a driver that grew the cache between the two calls yields
        /// nothing rather than a truncated blob.</returns>
        byte[] ReadCacheData(ulong cache);

        /// <summary><c>vkDestroyPipelineCache</c>. Terminal, and skipped on a dead device like every other destroy
        /// in this package.</summary>
        void DestroyCache(ulong cache);

        /// <summary>
        /// <c>vkCreateGraphicsPipelines</c> for exactly ONE pipeline, with no
        /// <c>VkRenderPass</c> and a <c>VkPipelineRenderingCreateInfo</c> chained on instead (V-A1).
        /// </summary>
        /// <param name="cache">The <c>VkPipelineCache</c> to compile through, or 0 for none.</param>
        /// <param name="spec">Everything the create info is built from, as plain data.</param>
        /// <returns>The <c>VkPipeline</c> handle. Never 0 on success.</returns>
        ulong CreateGraphicsPipeline(ulong cache, VulkanGraphicsPipelineSpec spec);

        /// <summary><c>vkCreateComputePipelines</c> for exactly one pipeline.</summary>
        /// <param name="cache">The <c>VkPipelineCache</c> to compile through, or 0 for none.</param>
        /// <param name="spec">The layout and the module.</param>
        /// <returns>The <c>VkPipeline</c> handle. Never 0 on success.</returns>
        ulong CreateComputePipeline(ulong cache, in VulkanComputePipelineSpec spec);

        /// <summary><c>vkDestroyPipeline</c>. Terminal, deferred behind the timeline by its caller (V-F9), and
        /// skipped on a dead device.</summary>
        void DestroyPipeline(ulong pipeline);
    }
}
