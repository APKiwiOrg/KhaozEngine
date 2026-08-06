using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The resource a DEDICATED allocation is dedicated TO, which
    /// <c>VkMemoryDedicatedAllocateInfo</c> has to name. Section 9.1.
    /// <para>
    /// Raw handles rather than <c>VkBuffer</c> and <c>VkImage</c>, so the whole allocator above the native seam
    /// stays free of Silk.NET types and therefore testable with no loader. Exactly one of the two is set, or
    /// neither: a dedicated allocation with no target is legal and simply omits the chain, which is the form a
    /// size-threshold dedication takes before row 9
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) exists to hand a resource over.
    /// </para>
    /// </summary>
    /// <param name="Buffer">A <c>VkBuffer</c> handle, or 0.</param>
    /// <param name="Image">A <c>VkImage</c> handle, or 0.</param>
    internal readonly record struct VulkanDedicatedTarget(ulong Buffer, ulong Image)
    {
        /// <summary>No target: allocate dedicated memory without chaining
        /// <c>VkMemoryDedicatedAllocateInfo</c>.</summary>
        internal static VulkanDedicatedTarget None => default;

        /// <summary>Whether either handle is set, and therefore whether the allocate info gets a chain.</summary>
        internal bool IsSet => Buffer != 0 || Image != 0;

        /// <summary>Whether BOTH are set, which is illegal:
        /// <c>VUID-VkMemoryDedicatedAllocateInfo-image-01432</c> permits at most one.</summary>
        internal bool IsAmbiguous => Buffer != 0 && Image != 0;
    }

    /// <summary>
    /// One allocation request, in the allocator's own vocabulary. Everything here except
    /// <see cref="DedicatedTarget"/> comes straight off a <c>VkMemoryRequirements</c> and a
    /// <c>VkMemoryDedicatedRequirements</c>, translated at the one call site that has a device.
    /// </summary>
    /// <param name="Size">Bytes wanted, from <c>VkMemoryRequirements.size</c>. Zero throws.</param>
    /// <param name="Alignment">Required offset alignment, from <c>VkMemoryRequirements.alignment</c>. A non-zero
    /// power of two. The allocator may RAISE it (never lower it) on a non-coherent host-visible type, so a widened
    /// flush range can never reach a neighbour.</param>
    /// <param name="MemoryTypeBits">The resource's own <c>VkMemoryRequirements.memoryTypeBits</c>. Bit <c>i</c>
    /// set means memory type <c>i</c> is legal for it. <c>uint.MaxValue</c> is "unrestricted", which a caller with
    /// no resource in hand passes.</param>
    /// <param name="Usage">Which preference ladder chooses the memory type.</param>
    /// <param name="Tiling">The pool key's second half (V-M2): linear and optimal never share a chunk, which is
    /// how <c>bufferImageGranularity</c> is satisfied without arithmetic.</param>
    /// <param name="PrefersDedicated"><c>VkMemoryDedicatedRequirements.prefersDedicatedAllocation</c>. Honoured,
    /// because a driver that says this is usually describing a compression or fast-clear path it can only take on
    /// memory it owns outright.</param>
    /// <param name="RequiresDedicated"><c>VkMemoryDedicatedRequirements.requiresDedicatedAllocation</c>. Not
    /// honouring this is a spec violation rather than a missed optimisation.</param>
    /// <param name="DedicatedTarget">The resource to name in <c>VkMemoryDedicatedAllocateInfo</c> when this
    /// request ends up dedicated. Ignored on the pooled path.</param>
    internal readonly record struct VulkanMemoryRequest(
        ulong Size,
        ulong Alignment,
        uint MemoryTypeBits,
        VulkanMemoryUsage Usage,
        VulkanMemoryTiling Tiling,
        bool PrefersDedicated = false,
        bool RequiresDedicated = false,
        VulkanDedicatedTarget DedicatedTarget = default)
    {
        /// <summary>Whether the driver asked for a dedicated allocation, either way round. The size threshold is
        /// the allocator's own third reason and is deliberately not folded in here, because this property is
        /// about what the DRIVER said.</summary>
        internal bool DriverWantsDedicated => PrefersDedicated || RequiresDedicated;
    }

    /// <summary>
    /// A live suballocation: which chunk it came from, where in that chunk, how big, and where it is mapped.
    /// <para>
    /// A struct carrying a reference to its chunk rather than an object of its own, because a resource holds one
    /// of these for its whole life and the allocator hands out one per buffer and per image. The chunk reference
    /// is what <see cref="Flush(ulong, ulong)"/> and <see cref="Invalidate(ulong, ulong)"/> go through, so an
    /// allocation never has to be told its memory type's coherence or the device's atom size.
    /// </para>
    /// </summary>
    internal readonly struct VulkanMemoryAllocation
    {
        internal VulkanMemoryAllocation(VulkanMemoryChunk chunk, ulong offset, ulong size)
        {
            Chunk = chunk;
            Offset = offset;
            Size = size;
        }

        /// <summary>The chunk this came out of, or null on a default-constructed value.</summary>
        internal VulkanMemoryChunk? Chunk { get; }

        /// <summary>The offset within the chunk's <c>VkDeviceMemory</c>, which is what
        /// <c>vkBindBufferMemory</c> and <c>vkBindImageMemory</c> take.</summary>
        internal ulong Offset { get; }

        /// <summary>The suballocation's size in bytes. May be LARGER than the request on a non-coherent
        /// host-visible chunk, where sizes round up to <c>nonCoherentAtomSize</c>.</summary>
        internal ulong Size { get; }

        /// <summary>Whether this refers to anything. A default value does not, which is what a failed or
        /// not-yet-made allocation looks like.</summary>
        internal bool IsValid => Chunk is not null;

        /// <summary>The chunk's <c>VkDeviceMemory</c> handle, for the bind call. Zero when invalid.</summary>
        internal ulong Memory => Chunk?.Memory ?? 0;

        /// <summary>Whether this allocation has the whole chunk to itself (V-M2's dedicated path).</summary>
        internal bool IsDedicated => Chunk?.IsDedicated ?? false;

        /// <summary>
        /// The mapped address of this allocation's first byte, or <see cref="IntPtr.Zero"/> when the chunk is not
        /// host-visible.
        /// <para>
        /// STABLE FOR THE CHUNK'S WHOLE LIFE (V-M3). Host-visible chunks are <c>vkMapMemory</c>'d once at creation
        /// and never unmapped, so this pointer is valid from the moment the allocation is made until it is freed,
        /// with no map, no unmap and no record-phase dance. This is the thing Direct3D 11 could not do and had to
        /// emulate, and anyone porting that backend's map-and-unmap sequence across is porting a workaround for a
        /// restriction Vulkan does not have.
        /// </para>
        /// </summary>
        internal nint MappedPointer
        {
            get
            {
                nint chunkBase = Chunk?.MappedPointer ?? 0;
                return chunkBase == 0 ? 0 : chunkBase + (nint)Offset;
            }
        }

        /// <summary>
        /// Make host writes to <paramref name="offset"/> for <paramref name="size"/> bytes, relative to THIS
        /// allocation, visible to the device. Free and skipped entirely when the chunk's memory type is coherent,
        /// which is the ordinary case on every ladder that prefers one.
        /// </summary>
        internal void Flush(ulong offset, ulong size) => Require().Flush(Range(offset, size), size);

        /// <summary>Make host writes to the WHOLE allocation visible to the device.</summary>
        internal void Flush() => Flush(0, Size);

        /// <summary>
        /// Make device writes to <paramref name="offset"/> for <paramref name="size"/> bytes, relative to THIS
        /// allocation, visible to the host. Free and skipped when the chunk's memory type is coherent. This is the
        /// readback path's real work on a cached, non-coherent type.
        /// </summary>
        internal void Invalidate(ulong offset, ulong size) => Require().Invalidate(Range(offset, size), size);

        /// <summary>Make device writes to the WHOLE allocation visible to the host.</summary>
        internal void Invalidate() => Invalidate(0, Size);

        VulkanMemoryChunk Require() => Chunk ?? throw new InvalidOperationException(
            "A default VulkanMemoryAllocation has no chunk behind it, so there is nothing to flush, invalidate or "
            + "read a pointer out of. It is what a failed allocation looks like, and the allocator throws on "
            + "failure rather than returning one, so reaching this means a value was stored before it was made.");

        // Allocation-relative to chunk-relative, with the bounds check that stops a caller's own arithmetic
        // reaching into the neighbouring suballocation. The widening to nonCoherentAtomSize happens below this,
        // inside the chunk, and cannot escape the allocation because the allocator aligned it (see
        // VulkanMappedRange).
        ulong Range(ulong offset, ulong size)
        {
            if (offset > Size || size > Size - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset,
                    "A flush or invalidate range runs past the end of the native Vulkan suballocation it is "
                    + "against. Ranges are relative to the allocation, not to the chunk, which is the mistake this "
                    + "check exists to catch before it reaches a neighbour's bytes.");
            }

            return Offset + offset;
        }
    }
}
