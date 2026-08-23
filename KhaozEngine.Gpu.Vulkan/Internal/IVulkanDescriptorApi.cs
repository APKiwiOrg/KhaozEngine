using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The seven <c>VkDescriptorType</c>s this backend can ever produce or account for, as an engine enum so
    /// every type above the seam names no Silk.NET type.
    /// <para>
    /// SEVEN IS THE POOL'S NUMBER RATHER THAN VULKAN'S. Vulkan has eleven core descriptor types, and these are
    /// the seven the incumbent's pool counted. A pool that counts a type it can never hand out is harmless while a
    /// pool that fails to count one it does hand out is an allocation failure nobody can explain. The layout
    /// policy of <see cref="VulkanDescriptorPolicy"/> can produce only five of them today (V-D4 turns every
    /// uniform buffer dynamic, and a dynamic structured buffer is refused), and the other two are still counted,
    /// because the whole point of decision V-D3 is that the accounting is complete rather than nearly complete.
    /// </para>
    /// </summary>
    internal enum VulkanDescriptorType
    {
        /// <summary><c>VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER</c>. Counted, and never produced by this backend's own
        /// layout policy: V-D4 makes every uniform element the DYNAMIC form.</summary>
        UniformBuffer,

        /// <summary><c>VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER_DYNAMIC</c>, which is what EVERY
        /// <see cref="GpuResourceKind.UniformBuffer"/> element becomes (V-D4).</summary>
        UniformBufferDynamic,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER</c>, which BOTH structured kinds map to.</summary>
        StorageBuffer,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_BUFFER_DYNAMIC</c>. Counted and never produced: see
        /// <see cref="VulkanDescriptorPolicy.TypeFor"/> for why a declared-dynamic structured buffer is refused
        /// at layout creation rather than mapped here.</summary>
        StorageBufferDynamic,

        /// <summary><c>VK_DESCRIPTOR_TYPE_SAMPLED_IMAGE</c>, separate from the sampler and never
        /// <c>COMBINED_IMAGE_SAMPLER</c>.</summary>
        SampledImage,

        /// <summary><c>VK_DESCRIPTOR_TYPE_STORAGE_IMAGE</c>.</summary>
        StorageImage,

        /// <summary><c>VK_DESCRIPTOR_TYPE_SAMPLER</c>, separate from the image.</summary>
        Sampler,
    }

    /// <summary>
    /// The image layout a sampled or storage image descriptor is written with. Two values, because the seam can
    /// express exactly two image bindings and each has one legal resting layout on this backend.
    /// </summary>
    internal enum VulkanDescriptorImageLayout
    {
        /// <summary>Not an image descriptor at all.</summary>
        None,

        /// <summary><c>VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL</c>, for a sampled image.</summary>
        ShaderReadOnlyOptimal,

        /// <summary><c>VK_IMAGE_LAYOUT_GENERAL</c>, for a storage image.</summary>
        General,
    }

    /// <summary>
    /// One <c>VkDescriptorSetLayoutBinding</c>'s worth of decisions, and the unit the content-dedup key of
    /// decision V-D5 is built out of.
    /// <para>
    /// IT CARRIES EXACTLY WHAT <c>vkCreateDescriptorSetLayout</c> READS AND NOTHING ELSE, which is what makes
    /// "content-keyed" a checkable claim rather than a hopeful one. The element's NAME is deliberately absent:
    /// Vulkan binds by number, so two layouts differing only in the names their elements were declared with are
    /// the same object to the driver, and giving them separate handles would break the pipeline-layout
    /// compatibility prefix compare that row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521) turns into a
    /// pointer compare.
    /// </para>
    /// </summary>
    /// <param name="Binding">The binding number, which is always the element's index (8.1).</param>
    /// <param name="Type">The descriptor type, which is where Vulkan's own notion of dynamic lives.</param>
    /// <param name="DescriptorCount">Always 1 (8.1). Carried rather than assumed so the key is literally the
    /// create-info's content.</param>
    /// <param name="Stages">Which shader stages see the binding.</param>
    internal readonly record struct VulkanDescriptorBinding(
        uint Binding, VulkanDescriptorType Type, uint DescriptorCount, GpuShaderStages Stages);

    /// <summary>
    /// One <c>VkWriteDescriptorSet</c>'s worth of decisions, resolved at SET CREATION and never again (V-D1).
    /// <para>
    /// THE RANGE IS THE BIND WINDOW AND IS NEVER <c>VK_WHOLE_SIZE</c> AND NEVER THE STRIDE (V-M6). See
    /// <see cref="VulkanResourceSet"/> for the whole argument and for which of <see cref="BufferOffset"/> and the
    /// bind-time dynamic offset carries a <see cref="GpuBufferRange.Offset"/>.
    /// </para>
    /// </summary>
    /// <param name="Binding">The binding number this write targets.</param>
    /// <param name="Type">The descriptor type, which decides which of the three payloads below is read.</param>
    /// <param name="Buffer">The <c>VkBuffer</c> for a buffer descriptor, else 0.</param>
    /// <param name="BufferOffset">The descriptor's own offset. ZERO for a dynamic uniform buffer, whose whole
    /// offset travels in <c>pDynamicOffsets</c> at bind time.</param>
    /// <param name="BufferRange">The bind window in bytes. Never 0, never <c>VK_WHOLE_SIZE</c>, never the ring
    /// stride.</param>
    /// <param name="ImageView">The <c>VkImageView</c> for an image descriptor, else 0.</param>
    /// <param name="ImageLayout">The layout an image descriptor is read in.</param>
    /// <param name="Sampler">The <c>VkSampler</c> for a sampler descriptor, else 0.</param>
    internal readonly record struct VulkanDescriptorWrite(
        uint Binding, VulkanDescriptorType Type, ulong Buffer, ulong BufferOffset, ulong BufferRange,
        ulong ImageView, VulkanDescriptorImageLayout ImageLayout, ulong Sampler);

    /// <summary>
    /// What one <c>VkDescriptorPool</c> is created with: how many sets it can hand out and how many descriptors
    /// of each type it holds. Decision V-D3's whole departure from the incumbent lived in these numbers being
    /// computed from demand rather than being the constants 1000 and 100.
    /// </summary>
    /// <param name="MaxSets"><c>VkDescriptorPoolCreateInfo.maxSets</c>.</param>
    /// <param name="Counts">The per-type budget. A type whose count is 0 contributes no
    /// <c>VkDescriptorPoolSize</c> entry at all, because the spec requires every entry's
    /// <c>descriptorCount</c> to be non-zero.</param>
    internal readonly record struct VulkanDescriptorPoolSize(uint MaxSets, VulkanDescriptorCounts Counts);

    /// <summary>
    /// A live <c>VkDescriptorSet</c> and the pool it came out of, which is what
    /// <c>vkFreeDescriptorSets</c> needs and what the pool's accounting is keyed on.
    /// </summary>
    /// <param name="Set">The <c>VkDescriptorSet</c> handle.</param>
    /// <param name="Pool">The <c>VkDescriptorPool</c> it was allocated from.</param>
    internal readonly record struct VulkanDescriptorSetToken(ulong Set, ulong Pool);

    /// <summary>
    /// THE NINE REAL DRIVER CALLS THE DESCRIPTOR SUBSYSTEM IS, behind an interface for the same reason
    /// <see cref="IVulkanResourceApi"/> is one: everything that can be WRONG about a descriptor (which Vulkan type
    /// an element maps to, which layouts share a handle, how a pool is sized, which per-type budget an allocation
    /// spends and which one a free restores, what range a descriptor is written with) is engine logic, and it runs
    /// under <c>dotnet test</c> on a machine with no Vulkan loader.
    ///
    /// <para><b>HANDLES ARE <c>ulong</c></b>, as they are on the resource seam. <c>VkDescriptorSetLayout</c>,
    /// <c>VkPipelineLayout</c>, <c>VkDescriptorPool</c> and <c>VkDescriptorSet</c> are all non-dispatchable
    /// handles and are 64-bit integers on the native side.</para>
    ///
    /// <para><b>THIS SEAM IS UNREACHABLE FROM THE RECORDING TYPE, AND THAT IS DECISION V-D2 (6.3).</b>
    /// <see cref="AllocateSet"/> and <see cref="UpdateSet"/> are <c>vkAllocateDescriptorSets</c> and
    /// <c>vkUpdateDescriptorSets</c>, and NEITHER is a bind, a draw or a barrier, so the native-call budget sink
    /// cannot see them and no counting seam ever will. The enforcement is that a recorder cannot reach this
    /// interface at all, asserted over the type graph by
    /// <c>VulkanRecordingUnreachabilityTests</c> alongside V-M11's view factory. Do not hand this interface, the
    /// pool manager, or <see cref="VulkanDescriptors"/> to a recorder to save a parameter.</para>
    /// </summary>
    internal interface IVulkanDescriptorApi
    {
        /// <summary><c>vkCreateDescriptorSetLayout</c> over <paramref name="bindings"/> in order. Called ONLY by
        /// <see cref="VulkanDescriptorSetLayoutCache"/>, which is what makes the handles content-shared
        /// (V-D5).</summary>
        /// <returns>The <c>VkDescriptorSetLayout</c> handle. Never 0 on success.</returns>
        ulong CreateSetLayout(ReadOnlySpan<VulkanDescriptorBinding> bindings);

        /// <summary><c>vkDestroyDescriptorSetLayout</c>. Called ONLY at device teardown, because a handle is
        /// shared by every layout with the same content and no single one of them may end it.</summary>
        void DestroySetLayout(ulong setLayout);

        /// <summary><c>vkCreatePipelineLayout</c> over <paramref name="setLayouts"/> in slot order, with NO push
        /// constant ranges at all (V-D8).</summary>
        /// <returns>The <c>VkPipelineLayout</c> handle. Never 0 on success.</returns>
        ulong CreatePipelineLayout(ReadOnlySpan<ulong> setLayouts);

        /// <summary><c>vkDestroyPipelineLayout</c>. Called ONLY at device teardown, for the same sharing reason
        /// as <see cref="DestroySetLayout"/>.</summary>
        void DestroyPipelineLayout(ulong pipelineLayout);

        /// <summary><c>vkCreateDescriptorPool</c> with <c>VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT</c>
        /// and the budget <paramref name="size"/> carries (V-D3).</summary>
        /// <returns>The <c>VkDescriptorPool</c> handle. Never 0 on success.</returns>
        ulong CreatePool(in VulkanDescriptorPoolSize size);

        /// <summary><c>vkDestroyDescriptorPool</c>, which implicitly frees every set still in it.</summary>
        void DestroyPool(ulong pool);

        /// <summary><c>vkAllocateDescriptorSets</c> for exactly ONE set (V-D1).</summary>
        /// <returns>The <c>VkDescriptorSet</c> handle. Never 0 on success.</returns>
        ulong AllocateSet(ulong pool, ulong setLayout);

        /// <summary><c>vkFreeDescriptorSets</c> for exactly one set, which the <c>FREE_DESCRIPTOR_SET</c> flag is
        /// what makes legal.</summary>
        void FreeSet(ulong pool, ulong set);

        /// <summary>ONE <c>vkUpdateDescriptorSets</c> covering EVERY binding of one set (V-D1). Called exactly
        /// once per set, at its creation, and never again for its whole life.</summary>
        void UpdateSet(ulong set, ReadOnlySpan<VulkanDescriptorWrite> writes);
    }
}
