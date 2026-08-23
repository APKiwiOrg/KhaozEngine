namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// WHAT A BUFFER ANSWERS WHEN A WRITE PATH ASKS WHERE ITS BYTES GO: a ring for a uniform buffer, and null for
    /// everything else. The routing seam of section 9.2 and 9.3, and the reason both <c>UpdateBuffer</c> levels
    /// make ONE decision rather than repeating a convention.
    /// <para>
    /// IT KEPT THE ROUTING BUILDABLE ONE ROW EARLY. Buffers are
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/519">row 9</see>'s, and the ring is row 8's,
    /// so the write paths name this interface rather than a concrete buffer type. <see cref="VulkanBuffer"/>
    /// implements it and returns a <see cref="VulkanUniformRing"/> for exactly the
    /// <see cref="GpuBufferUsage.UniformBuffer"/> buffers, which
    /// <see cref="VulkanBufferRingPolicy.ForBuffer"/> makes an exclusive set: a uniform buffer combined with any
    /// other binding is refused at creation, so a ring-backed buffer can never also be a vertex, index, indirect
    /// or storage buffer whose bind would address segment zero while its uniform bind addressed segment N.
    /// </para>
    /// <para>
    /// EVERY WRITE PATH ASKS THE SAME MEMBER: the record-time <c>IGpuCommandList.UpdateBuffer</c> and the
    /// off-timeline <c>IGpuDevice.UpdateBuffer</c>. A buffer that is ring-backed is never written any other way,
    /// and a buffer that is not never touches a ring.
    /// </para>
    /// </summary>
    internal interface IVulkanRingBacked
    {
        /// <summary>This buffer's uniform ring, or null when it has none.</summary>
        VulkanUniformRing? Ring { get; }
    }

    /// <summary>
    /// WHAT A NON-RING BUFFER ANSWERS WHEN A RECORD-TIME WRITE HAS TO STAGE THROUGH THE ARENA (9.3): its
    /// <c>VkBuffer</c> handle and the usage the destination barrier is narrowed to.
    /// <para>
    /// The other half of the same routing decision, and separate from <see cref="IVulkanRingBacked"/> because the
    /// two questions have different answers for the same buffer: a ring-backed buffer answers the first and never
    /// needs the second, and a vertex buffer answers the second and never the first.
    /// </para>
    /// <para>
    /// THE USAGE IS CARRIED RATHER THAN GUESSED, which is the whole of the narrowed barrier. The incumbent emitted
    /// one global <c>VkMemoryBarrier</c> whose destination is <c>VertexAttributeRead</c> at <c>VertexInput</c> for
    /// every upload it makes, so an index buffer, an indirect argument buffer and a storage buffer are all
    /// synchronised as though they were vertex attributes. See <see cref="VulkanUploadBarrier"/>.
    /// </para>
    /// </summary>
    internal interface IVulkanUploadDestination
    {
        /// <summary>The <c>VkBuffer</c> handle a <c>vkCmdCopyBuffer</c> names as its destination.</summary>
        ulong DeviceBuffer { get; }

        /// <summary>The usage flags the buffer was created with, which decide the barrier's destination stage and
        /// access masks.</summary>
        GpuBufferUsage UploadUsage { get; }
    }
}
