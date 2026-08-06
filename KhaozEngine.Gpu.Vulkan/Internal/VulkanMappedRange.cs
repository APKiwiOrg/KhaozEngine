using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE <c>nonCoherentAtomSize</c> ARITHMETIC that <c>vkFlushMappedMemoryRanges</c> and
    /// <c>vkInvalidateMappedMemoryRanges</c> require, and the one hazard it carries. Section 9.1, decision V-M4.
    ///
    /// <para><b>WHAT THE SPEC ASKS FOR.</b> A <c>VkMappedMemoryRange</c>'s <c>offset</c> must be a multiple of
    /// <c>nonCoherentAtomSize</c>, and its <c>size</c> must be either a multiple of the same OR exactly the
    /// remainder to the end of the memory object (<c>VUID-VkMappedMemoryRange-size-01390</c> and
    /// <c>-01389</c>). So a range is widened outwards: the offset rounds DOWN, the end rounds UP, and the end is
    /// then clamped to the memory object's own size, which is what makes the last range in a chunk legal without a
    /// special case.</para>
    ///
    /// <para><b>THE HAZARD, and why the ALLOCATOR and not this method is where it is answered.</b> Widening a
    /// range makes it cover bytes that belong to the NEIGHBOURING suballocations either side. For a flush that is
    /// merely wasteful: flushing extra host writes makes more writes visible and destroys nothing. For an
    /// INVALIDATE it is a correctness defect, because invalidating a range discards the host's cached view of it,
    /// and a neighbour's not-yet-flushed writes sitting in that cache line go with it. The fix is structural
    /// rather than arithmetic: on a host-visible chunk whose type is NOT coherent, the allocator raises every
    /// suballocation's alignment to at least <c>nonCoherentAtomSize</c> and rounds every suballocation's size up
    /// to the same, so a widened range can never leave the allocation it came from. This method is therefore
    /// correct in isolation AND is only ever called where widening is a no-op, and both halves are asserted.</para>
    ///
    /// <para>A coherent chunk never calls any of this. It needs no flush and no invalidate at all, which is why
    /// coherent types are preferred on every ladder (V-M4) and why the incumbent has never been bitten by having
    /// neither call anywhere in it.</para>
    /// </summary>
    internal static class VulkanMappedRange
    {
        /// <summary>
        /// Widen <paramref name="offset"/> and <paramref name="size"/> to the atom boundaries a
        /// <c>VkMappedMemoryRange</c> requires, clamped to <paramref name="memorySize"/>.
        /// </summary>
        /// <param name="offset">The chunk-relative start of the range to flush or invalidate.</param>
        /// <param name="size">Its length in bytes. Zero is legal and produces a zero-length result, which the
        /// caller skips rather than passing to the driver.</param>
        /// <param name="memorySize">The whole <c>VkDeviceMemory</c> object's <c>allocationSize</c>. The end never
        /// rounds past it, which is the clause that lets the last range in a chunk be legal.</param>
        /// <param name="atomSize"><c>VkPhysicalDeviceLimits.nonCoherentAtomSize</c>. A non-zero power of two,
        /// and 1 makes this an identity.</param>
        /// <param name="alignedOffset">The rounded-down offset, always a multiple of
        /// <paramref name="atomSize"/>.</param>
        /// <param name="alignedSize">The rounded-up length, a multiple of <paramref name="atomSize"/> unless the
        /// range reaches the end of the memory object, in which case it is the remainder.</param>
        internal static void Align(ulong offset, ulong size, ulong memorySize, ulong atomSize,
            out ulong alignedOffset, out ulong alignedSize)
        {
            if (!VulkanMemoryFreeList.IsPowerOfTwo(atomSize))
            {
                throw new ArgumentOutOfRangeException(nameof(atomSize), atomSize,
                    "nonCoherentAtomSize must be a non-zero power of two, which the Vulkan spec requires of it. A "
                    + "value that is not means the limit was not read off a device.");
            }

            if (offset > memorySize)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), offset,
                    "A flush or invalidate range starts past the end of the native Vulkan memory object it is "
                    + "against. The range is chunk-relative and the memory object is the chunk, so this is an "
                    + "engine-internal offset that was never inside it.");
            }

            if (size > memorySize - offset)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size,
                    "A flush or invalidate range runs past the end of the native Vulkan memory object it is "
                    + "against. Widening it to the atom boundary would then have to be clamped to a range shorter "
                    + "than the caller asked for, which would silently not flush what it was told to.");
            }

            ulong mask = atomSize - 1;

            alignedOffset = offset & ~mask;

            ulong end = offset + size;

            // The end rounds UP, then clamps. The clamp is the spec's "or the remainder to the end of the memory
            // object" clause rather than a safety net: a memory object whose own size is not a multiple of the
            // atom would otherwise produce a range that ends past it, which is the invalid form.
            ulong alignedEnd = end > ulong.MaxValue - mask ? memorySize : (end + mask) & ~mask;
            if (alignedEnd > memorySize) alignedEnd = memorySize;

            alignedSize = alignedEnd - alignedOffset;
        }
    }
}
