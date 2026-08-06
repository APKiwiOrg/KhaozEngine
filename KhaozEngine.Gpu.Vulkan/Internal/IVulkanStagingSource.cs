using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE STAGING BLOCK: a host-visible, coherent, persistently mapped <c>VkBuffer</c> the arena sub-allocates out
    /// of. Section 9.3.
    /// </summary>
    /// <param name="Buffer">The <c>VkBuffer</c> handle a <c>vkCmdCopyBuffer</c> names as its source.</param>
    /// <param name="Mapped">The first byte of that buffer's mapping. Stable for the block's life, because row 6
    /// maps a host-visible chunk once at creation and never unmaps it (V-M3).</param>
    /// <param name="SizeBytes">How much the block holds. Blocks are pooled BY THIS, so it is the pool key rather
    /// than a description.</param>
    internal readonly record struct VulkanStagingBlock(ulong Buffer, nint Mapped, ulong SizeBytes)
    {
        /// <summary>Whether this is a real block rather than a default value. A block with no buffer and no mapping
        /// is what a caller gets from a default struct, and copying into one would be a write through a null
        /// pointer.</summary>
        internal bool IsValid => Buffer != 0 && Mapped != 0 && SizeBytes != 0;
    }

    /// <summary>
    /// A SUB-ALLOCATION OUT OF ONE BLOCK: where the caller writes, and what a copy command names.
    /// </summary>
    /// <param name="Buffer">The source <c>VkBuffer</c>.</param>
    /// <param name="OffsetBytes">Where in that buffer this lease starts, which is the copy's
    /// <c>srcOffset</c>.</param>
    /// <param name="Mapped">The mapped address of that offset, which is where the caller memcpys to.</param>
    /// <param name="SizeBytes">How many bytes were reserved.</param>
    internal readonly record struct VulkanStagingLease(ulong Buffer, ulong OffsetBytes, nint Mapped, ulong SizeBytes)
    {
        /// <summary>Whether this lease names real memory.</summary>
        internal bool IsValid => Buffer != 0 && Mapped != 0;

        /// <summary>Copy <paramref name="data"/> into the lease, which is the whole of what a staged upload does on
        /// the CPU side.</summary>
        internal unsafe void Write(ReadOnlySpan<byte> data)
        {
            if ((ulong)data.Length > SizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(data), data.Length,
                    "A staged upload wrote more bytes than its native Vulkan staging lease reserved, which would "
                    + "run into the next sub-allocation of the same block.");
            }

            data.CopyTo(new Span<byte>((byte*)Mapped, data.Length));
        }
    }

    /// <summary>
    /// THE TWO NATIVE CALLS A STAGING BLOCK IS, behind an interface for the same reason
    /// <see cref="IVulkanDeviceMemoryApi"/> is one: everything that can be WRONG about an arena (the size classes,
    /// the sub-allocation, the recycling boundary, the retention cap and what it destroys) is engine logic, and it
    /// is tested by a plain <c>[Fact]</c> on a machine with no Vulkan loader. What is left on the far side is a
    /// <c>vkCreateBuffer</c> plus a bind out of a host-visible chunk, and a destroy.
    /// <para>
    /// HANDLES ARE <c>ulong</c> and the mapping is an <c>nint</c>, so this interface and the arena above it name no
    /// Silk.NET type at all.
    /// </para>
    /// <para>
    /// ROW 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519) IMPLEMENTED IT as
    /// <see cref="VulkanStagingSource"/>, over <see cref="VulkanMemoryAllocator"/> with
    /// <see cref="VulkanMemoryUsage.Upload"/>, because that is the row where a <c>VkBuffer</c> starts existing at
    /// all. The arena was complete and driven by tests one row early on purpose: it is the type the incumbent's
    /// allocation storm lives in, and the fix is its POLICY rather than its native calls.
    /// </para>
    /// </summary>
    internal interface IVulkanStagingSource
    {
        /// <summary>
        /// Create a host-visible, coherent, persistently mapped buffer of at least <paramref name="sizeBytes"/>
        /// bytes with <c>TRANSFER_SRC</c> usage.
        /// </summary>
        VulkanStagingBlock Create(ulong sizeBytes);

        /// <summary>
        /// Destroy a block and free its memory. Called when the retention cap turns one away and at the arena's own
        /// disposal, and by nothing else: a block returned inside the cap is KEPT, which is the whole point.
        /// <para>
        /// THE CONTRACT ROW 9'S IMPLEMENTATION MUST SATISFY. On a live device, Destroy DEFERS the native free
        /// through the device's retire list rather than making it, because an immediate free of a block an
        /// in-flight submission still reads is the corruption class the arena's own
        /// <see cref="VulkanStagingArena.BeginSlot"/> gate exists to prevent, and calling this method natively
        /// rather than deferred is how that same corruption arrives anyway, through the one call the arena trusts
        /// to be safe. It defers at the timeline's LAST ALLOCATED value
        /// (<see cref="VulkanResourceOwner.RetireTerminal"/>), which satisfies the requirement and then exceeds
        /// it: the allocated high-water is at or above every value a live submission can hold, so it covers a
        /// submission that has taken its value and not yet raised the submitted high-water, which the command
        /// pools' own highest-SUBMITTED value (<see cref="VulkanCommandPoolRing.RetireInto"/>) does not. On a dead
        /// device Destroy abandons rather than frees, matching
        /// <see cref="VulkanRetireList.Abandon"/> and <see cref="VulkanMemoryAllocator.Abandon"/>: the block's
        /// memory went with the device, so a free now is a call against memory the driver already released.
        /// </para>
        /// </summary>
        void Destroy(in VulkanStagingBlock block);
    }
}
