namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>What a <c>vkGetBufferMemoryRequirements2</c> or <c>vkGetImageMemoryRequirements2</c> answered,
    /// translated once so the allocator's request can be built without a Vulkan structure.</summary>
    /// <param name="Size"><c>VkMemoryRequirements.size</c>.</param>
    /// <param name="Alignment"><c>VkMemoryRequirements.alignment</c>, a non-zero power of two by spec.</param>
    /// <param name="MemoryTypeBits"><c>VkMemoryRequirements.memoryTypeBits</c>.</param>
    /// <param name="PrefersDedicated"><c>VkMemoryDedicatedRequirements.prefersDedicatedAllocation</c>.</param>
    /// <param name="RequiresDedicated"><c>VkMemoryDedicatedRequirements.requiresDedicatedAllocation</c>.</param>
    internal readonly record struct VulkanResourceRequirements(
        ulong Size, ulong Alignment, uint MemoryTypeBits, bool PrefersDedicated, bool RequiresDedicated);

    /// <summary>One <c>VkImageCreateInfo</c>'s worth of decisions, in the seam's own vocabulary.</summary>
    /// <param name="Width">Texel width.</param>
    /// <param name="Height">Texel height.</param>
    /// <param name="MipLevels">Mip level count.</param>
    /// <param name="ArrayLayers">The LOGICAL array layer count, before the cubemap expansion. The implementation
    /// multiplies by six when <paramref name="Cubemap"/> is set, exactly as the incumbent's
    /// <c>_actualImageArrayLayers</c> does.</param>
    /// <param name="Format">The pixel format.</param>
    /// <param name="DepthStencil">Whether the depth reading of the format map applies.</param>
    /// <param name="Usage">The derived usage bits.</param>
    /// <param name="SampleCount">The requested sample count, refused here rather than clamped (V-C6).</param>
    /// <param name="Cubemap">Whether the image is cube-compatible.</param>
    internal readonly record struct VulkanImageSpec(
        uint Width, uint Height, uint MipLevels, uint ArrayLayers, GpuPixelFormat Format, bool DepthStencil,
        VulkanImageUsage Usage, uint SampleCount, bool Cubemap);

    /// <summary>One <c>VkImageViewCreateInfo</c>'s worth of decisions.</summary>
    /// <param name="Image">The <c>VkImage</c> the view is over.</param>
    /// <param name="Format">The pixel format, which with <paramref name="DepthStencil"/> gives the view's own
    /// <c>VkFormat</c>.</param>
    /// <param name="DepthStencil">Whether the aspect is depth rather than colour.</param>
    /// <param name="Cubemap">Whether the view type is a cube rather than a 2D one.</param>
    /// <param name="BaseMipLevel">First mip level in the view.</param>
    /// <param name="MipLevels">How many mip levels the view covers.</param>
    /// <param name="BaseArrayLayer">First LOGICAL array layer.</param>
    /// <param name="ArrayLayers">How many LOGICAL array layers, multiplied by six for a cubemap exactly as the
    /// incumbent's <c>VkTextureView</c> does.</param>
    /// <param name="ArrayView"><see cref="GpuTextureDescription.IsArray"/>: the view takes
    /// <c>VK_IMAGE_VIEW_TYPE_2D_ARRAY</c> even over a single layer, which is the only way a one-layer array can
    /// reach a fragment that declares <c>texture2DArray</c> (#666). Defaulted to false so a view that is a 2D
    /// plane by nature (the mip 0, layer 0 attachment view) says nothing and gets the count rule.</param>
    internal readonly record struct VulkanImageViewSpec(
        ulong Image, GpuPixelFormat Format, bool DepthStencil, bool Cubemap, uint BaseMipLevel, uint MipLevels,
        uint BaseArrayLayer, uint ArrayLayers, bool ArrayView = false);

    /// <summary>
    /// THE TWELVE REAL DRIVER CALLS RESOURCE CREATION IS, behind an interface for the same reason
    /// <see cref="IVulkanCommandApi"/> and <see cref="IVulkanDeviceMemoryApi"/> are ones: everything that can be
    /// WRONG about a resource (which usage bits it gets, which views are created and over what range, which
    /// memory ladder it allocates from, what its resting layout is, when its destroy runs) is engine logic, and it
    /// runs under <c>dotnet test</c> on a machine with no Vulkan loader.
    ///
    /// <para><b>HANDLES ARE <c>ulong</c></b>, so this interface and every type above it name no Silk.NET type at
    /// all. <c>VkBuffer</c>, <c>VkImage</c>, <c>VkImageView</c> and <c>VkSampler</c> are all NON-dispatchable
    /// handles and are 64-bit integers on the native side, so unlike <c>VkCommandBuffer</c> there is not even a
    /// pointer conversion to do at the boundary.</para>
    ///
    /// <para><b>THERE IS NO VIEW FACTORY REACHABLE FROM THE RECORDING TYPE (V-M11), AND THIS INTERFACE IS WHERE
    /// THAT IS TRUE.</b> <see cref="CreateImageView"/> lives here, this seam is held by the resource factory and by
    /// the device, and <see cref="VulkanCommandList"/> reaches neither. The architecture test asserts it over the
    /// type graph, alongside V-D2's descriptor pool. Do not hand this interface to a recorder to save a
    /// parameter.</para>
    ///
    /// <para><b>THE MEMORY REQUIREMENTS CALL IS THE <c>2</c> FORM UNCONDITIONALLY.</b> The incumbent probed for
    /// <c>vkGetBufferMemoryRequirements2</c> at run time and falls back to the 1.0 call with
    /// <c>prefersDedicatedAllocation</c> hardcoded false, because it targets Vulkan 1.0. This backend requires 1.3
    /// (row 2's probe), where both the <c>2</c> form and <c>VkMemoryDedicatedRequirements</c> are core, so the
    /// fallback is unreachable and is not carried.</para>
    /// </summary>
    internal interface IVulkanResourceApi
    {
        /// <summary><c>vkCreateBuffer</c> of <paramref name="sizeBytes"/> bytes with
        /// <paramref name="binding"/>'s usage bits, exclusive sharing (there is one queue family, V-N5).</summary>
        /// <returns>The <c>VkBuffer</c> handle. Never 0 on success.</returns>
        ulong CreateBuffer(ulong sizeBytes, VulkanBufferBinding binding);

        /// <summary><c>vkGetBufferMemoryRequirements2</c> with <c>VkMemoryDedicatedRequirements</c>
        /// chained.</summary>
        VulkanResourceRequirements BufferRequirements(ulong buffer);

        /// <summary><c>vkBindBufferMemory</c>.</summary>
        void BindBufferMemory(ulong buffer, ulong memory, ulong offset);

        /// <summary><c>vkDestroyBuffer</c>. TERMINAL and skipped on a dead device, like every other destroy in
        /// this package: the memory behind it is freed separately, by the allocator, and never from inside
        /// here.</summary>
        void DestroyBuffer(ulong buffer);

        /// <summary><c>vkCreateImage</c> with <c>VK_IMAGE_TILING_OPTIMAL</c>,
        /// <c>VK_IMAGE_LAYOUT_UNDEFINED</c> as the initial layout and
        /// <c>VK_IMAGE_CREATE_MUTABLE_FORMAT_BIT</c>.</summary>
        /// <returns>The <c>VkImage</c> handle. Never 0 on success.</returns>
        ulong CreateImage(in VulkanImageSpec spec);

        /// <summary><c>vkGetImageMemoryRequirements2</c> with <c>VkMemoryDedicatedRequirements</c>
        /// chained.</summary>
        VulkanResourceRequirements ImageRequirements(ulong image);

        /// <summary><c>vkBindImageMemory</c>.</summary>
        void BindImageMemory(ulong image, ulong memory, ulong offset);

        /// <summary><c>vkDestroyImage</c>. Terminal, and skipped on a dead device.</summary>
        void DestroyImage(ulong image);

        /// <summary><c>vkCreateImageView</c>. Called ONLY at resource creation (V-M11).</summary>
        /// <returns>The <c>VkImageView</c> handle. Never 0 on success.</returns>
        ulong CreateImageView(in VulkanImageViewSpec spec);

        /// <summary><c>vkDestroyImageView</c>. Terminal, and skipped on a dead device.</summary>
        void DestroyImageView(ulong view);

        /// <summary><c>vkCreateSampler</c>.</summary>
        /// <returns>The <c>VkSampler</c> handle. Never 0 on success.</returns>
        ulong CreateSampler(in VulkanSamplerSpec spec);

        /// <summary><c>vkDestroySampler</c>. Terminal, and skipped on a dead device.</summary>
        void DestroySampler(ulong sampler);
    }
}
