namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE FIVE NATIVE CALLS A MEMORY CHUNK IS, behind an interface so everything above them is device-free and
    /// testable: <c>vkAllocateMemory</c>, <c>vkMapMemory</c>, <c>vkFlushMappedMemoryRanges</c>,
    /// <c>vkInvalidateMappedMemoryRanges</c> and <c>vkFreeMemory</c>.
    /// <para>
    /// The same split <see cref="IVulkanTimelineSemaphore"/> takes, and for the same reason. What is left below
    /// this line is five driver calls with no policy in them. What sits above it is the part that can be WRONG:
    /// the free-list arithmetic, the memory-type ladders, the pool keying, the map-once rule, the atom-size
    /// widening and the retire ordering. All of it runs under <c>dotnet test</c> on a machine with no Vulkan
    /// loader, which matters more here than anywhere else in this backend: 9.1's own counterargument to declining
    /// VMA is that hand-rolled allocators are where memory corruption lives.
    /// </para>
    /// <para>
    /// HANDLES ARE <c>ulong</c>, not <c>VkDeviceMemory</c>, so this interface and every type above it name no
    /// Silk.NET type at all. A fake in a test therefore invents plain numbers rather than binding handles it has
    /// no device to make.
    /// </para>
    /// <para>
    /// THERE IS NO UNMAP MEMBER, deliberately (V-M3). A host-visible chunk is mapped once at creation and NEVER
    /// unmapped, and <c>vkFreeMemory</c> implicitly unmaps memory that is still mapped, so the only way a chunk's
    /// mapping ends is with the chunk itself. Adding an unmap would make it possible to write the map-and-unmap
    /// dance the Direct3D 11 backend needs and Vulkan does not.
    /// </para>
    /// </summary>
    internal interface IVulkanDeviceMemoryApi
    {
        /// <summary>
        /// <c>vkAllocateMemory</c>: one whole <c>VkDeviceMemory</c> object.
        /// <para>
        /// This is the call MV6 counts. Every chunk is exactly one of these, pooled or dedicated, and the bet is
        /// that an engine-owned suballocator keeps the live count under a quarter of the device's
        /// <c>maxMemoryAllocationCount</c>.
        /// </para>
        /// </summary>
        /// <param name="memoryTypeIndex">The type chosen by <see cref="VulkanMemoryTypeSelection"/>.</param>
        /// <param name="size">The allocation size in bytes.</param>
        /// <param name="dedicated">The resource to chain <c>VkMemoryDedicatedAllocateInfo</c> for, or
        /// <see cref="VulkanDedicatedTarget.None"/> for an ordinary allocation.</param>
        /// <returns>The <c>VkDeviceMemory</c> handle. Never 0 on success.</returns>
        ulong Allocate(uint memoryTypeIndex, ulong size, VulkanDedicatedTarget dedicated);

        /// <summary>
        /// <c>vkMapMemory</c> over the WHOLE object, from offset 0 to <c>VK_WHOLE_SIZE</c>, once, at creation
        /// (V-M3). Only ever called for a host-visible type.
        /// </summary>
        /// <param name="memory">The handle <see cref="Allocate"/> returned.</param>
        /// <param name="size">The object's size, for the diagnostic. The mapping itself is whole-object.</param>
        /// <returns>The mapped base address. Never zero on success.</returns>
        nint MapWhole(ulong memory, ulong size);

        /// <summary><c>vkFlushMappedMemoryRanges</c> over one range, already widened to
        /// <c>nonCoherentAtomSize</c> by <see cref="VulkanMappedRange"/>. Never called for a coherent
        /// type.</summary>
        void Flush(ulong memory, ulong offset, ulong size);

        /// <summary><c>vkInvalidateMappedMemoryRanges</c> over one range, already widened. Never called for a
        /// coherent type.</summary>
        void Invalidate(ulong memory, ulong offset, ulong size);

        /// <summary>
        /// <c>vkFreeMemory</c>, which also unmaps a still-mapped object. Skipped by the implementation when the
        /// device is dead, because <c>vkDestroyDevice</c> (or the loss that killed it) already freed every object
        /// made from it and calling in afterwards aborts the process through the Vulkan loader.
        /// </summary>
        void Free(ulong memory);
    }
}
