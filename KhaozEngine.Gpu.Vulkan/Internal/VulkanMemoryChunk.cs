using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE <c>vkAllocateMemory</c> AND EVERYTHING SUBALLOCATED OUT OF IT. Decisions V-M2 and V-M3, section 9.1.
    ///
    /// <para><b>ONE CHUNK IS ONE MEMORY TYPE AND ONE TILING</b>, which is the whole of the pool key. A linear
    /// allocation and an optimal-tiling one can never land in the same chunk, so they can never share a
    /// <c>bufferImageGranularity</c> page, so this backend contains no granularity arithmetic at all. The
    /// incumbent instead rounds every non-dedicated request up to a granule and shares chunks, which is correct
    /// and wasteful and adds a granule even when the size is already aligned.</para>
    ///
    /// <para><b>HOST-VISIBLE CHUNKS ARE MAPPED ONCE AND NEVER UNMAPPED (V-M3).</b> The mapping is taken at
    /// creation, over the whole object, and it ends only when the chunk is freed, since <c>vkFreeMemory</c>
    /// implicitly unmaps. Every host-visible suballocation therefore has a stable pointer for the chunk's life
    /// with no map call on any path. There is no unmap member on the native seam to make the alternative
    /// expressible.</para>
    ///
    /// <para><b>A NON-COHERENT HOST-VISIBLE CHUNK RAISES ITS OWN ALIGNMENT, and that is a correctness fix rather
    /// than tidiness.</b> Flush and invalidate ranges widen outwards to <c>nonCoherentAtomSize</c>, so a widened
    /// range over one suballocation would otherwise reach into its neighbours, and for an INVALIDATE that discards
    /// a neighbour's un-flushed host writes. Forcing every suballocation in such a chunk to start on an atom
    /// boundary and to occupy a whole number of atoms makes the widening a no-op. It is done here, at the chunk,
    /// because only the chunk knows whether its type is coherent.</para>
    ///
    /// <para><b>DESTRUCTION IS IDEMPOTENT AND HAS TWO FORMS.</b> <see cref="Destroy"/> makes the native call and
    /// is what the retire list runs once the timeline has passed the value recorded at free time.
    /// <see cref="Forget"/> makes none, for a device that is already dead, where every object went with the device
    /// and a <c>vkFreeMemory</c> would be a call against freed memory. Both answer whether THIS call was the one
    /// that ended the chunk, which is what keeps the allocation counter honest without a second flag.</para>
    /// </summary>
    internal sealed class VulkanMemoryChunk
    {
        readonly IVulkanDeviceMemoryApi _api;
        readonly VulkanMemoryFreeList _free;
        readonly ulong _atomSize;

        bool _destroyed;

        /// <summary>Allocate the chunk and, when its type is host-visible, map the whole of it.</summary>
        /// <param name="api">The native seam.</param>
        /// <param name="memoryTypeIndex">The memory type, already chosen.</param>
        /// <param name="traits">That type's properties, which decide mapping, flushing and alignment.</param>
        /// <param name="tiling">Half the pool key. Carried for the diagnostic and for the pool's own
        /// bookkeeping.</param>
        /// <param name="size">The chunk's size in bytes.</param>
        /// <param name="atomSize">The device's <c>nonCoherentAtomSize</c>.</param>
        /// <param name="dedicated">The resource this chunk is dedicated to, or
        /// <see cref="VulkanDedicatedTarget.None"/>. A chunk created with a target, or created by the dedicated
        /// path at all, still goes through the same free list: a dedicated chunk is one whose size is the
        /// request's size, so the single suballocation fills it and no second one can fit. That keeps ONE code
        /// path for both, which is what stops the dedicated path being the one nothing tests.</param>
        /// <param name="isDedicated">Whether this chunk was created for one request and bypasses the
        /// pools.</param>
        internal VulkanMemoryChunk(IVulkanDeviceMemoryApi api, uint memoryTypeIndex, VulkanMemoryTrait traits,
            VulkanMemoryTiling tiling, ulong size, ulong atomSize, VulkanDedicatedTarget dedicated,
            bool isDedicated)
        {
            ArgumentNullException.ThrowIfNull(api);

            if (!VulkanMemoryFreeList.IsPowerOfTwo(atomSize))
            {
                throw new ArgumentOutOfRangeException(nameof(atomSize), atomSize,
                    "nonCoherentAtomSize must be a non-zero power of two, which the Vulkan spec requires of it.");
            }

            if (dedicated.IsAmbiguous)
            {
                throw new ArgumentException(
                    "A dedicated native Vulkan allocation names a buffer OR an image, never both: "
                    + "VUID-VkMemoryDedicatedAllocateInfo-image-01432 permits at most one of the two.",
                    nameof(dedicated));
            }

            _api = api;
            _atomSize = atomSize;
            _free = new VulkanMemoryFreeList(size);

            MemoryTypeIndex = memoryTypeIndex;
            Traits = traits;
            Tiling = tiling;
            Size = size;
            IsDedicated = isDedicated;

            Memory = api.Allocate(memoryTypeIndex, size, dedicated);

            // MAPPED ONCE, HERE, FOR THE CHUNK'S WHOLE LIFE (V-M3). Not lazily on first use: a lazy map would be
            // a native call on whatever path first wrote through the pointer, which is exactly the frame path the
            // uniform ring exists to keep call-free.
            if (HostVisible) MappedPointer = api.MapWhole(Memory, size);
        }

        /// <summary>The <c>VkDeviceMemory</c> handle, for the bind calls row 9 makes.</summary>
        internal ulong Memory { get; }

        /// <summary>The memory type this chunk was allocated from. Half the pool key.</summary>
        internal uint MemoryTypeIndex { get; }

        /// <summary>That type's properties.</summary>
        internal VulkanMemoryTrait Traits { get; }

        /// <summary>The other half of the pool key (V-M2).</summary>
        internal VulkanMemoryTiling Tiling { get; }

        /// <summary>The chunk's size in bytes.</summary>
        internal ulong Size { get; }

        /// <summary>Whether this chunk belongs to one request and is outside the pools.</summary>
        internal bool IsDedicated { get; }

        /// <summary>The whole-object mapping, or zero when the type is not host-visible. Stable for the chunk's
        /// life.</summary>
        internal nint MappedPointer { get; }

        /// <summary>Whether the chunk is mapped at all.</summary>
        internal bool HostVisible => (Traits & VulkanMemoryTrait.HostVisible) != 0;

        /// <summary>Whether flush and invalidate are free no-ops on it.</summary>
        internal bool HostCoherent => (Traits & VulkanMemoryTrait.HostCoherent) != 0;

        /// <summary>True once <see cref="Destroy"/> or <see cref="Forget"/> has run.</summary>
        internal bool IsDestroyed => _destroyed;

        /// <summary>Whether nothing is suballocated out of it, which is what lets a pool retire it.</summary>
        internal bool IsEmpty => _free.IsEmpty;

        /// <summary>Bytes handed out.</summary>
        internal ulong UsedBytes => _free.UsedBytes;

        /// <summary>The largest request this chunk could still satisfy at alignment 1, for the diagnostic that
        /// has to tell a full chunk from a fragmented one.</summary>
        internal ulong LargestFreeBlock => _free.LargestFreeBlock;

        /// <summary>
        /// The alignment a chunk of <paramref name="traits"/> applies to <paramref name="requested"/>: the
        /// request's own, or <paramref name="atomSize"/> when that is larger AND the type is host-visible but not
        /// coherent. See the class note for why raising it is a correctness fix.
        /// <para>
        /// Static so the allocator can ask the same question BEFORE a chunk exists, which is what it needs to
        /// decide whether a request goes dedicated. One implementation, two callers, no chance of the pre-check
        /// and the chunk disagreeing.
        /// </para>
        /// </summary>
        internal static ulong AlignmentFor(ulong requested, VulkanMemoryTrait traits, ulong atomSize)
            => NeedsIsolation(traits) && atomSize > requested ? atomSize : requested;

        /// <summary>
        /// The size a chunk of <paramref name="traits"/> reserves for <paramref name="requested"/>: the request's
        /// own, rounded up to <paramref name="atomSize"/> when the type is host-visible but not coherent.
        /// </summary>
        internal static ulong SizeFor(ulong requested, VulkanMemoryTrait traits, ulong atomSize)
        {
            if (!NeedsIsolation(traits)) return requested;

            return VulkanMemoryFreeList.TryAlignUp(requested, atomSize, out ulong rounded) ? rounded : requested;
        }

        /// <summary>The alignment this chunk will actually apply to <paramref name="requested"/>.</summary>
        internal ulong EffectiveAlignment(ulong requested) => AlignmentFor(requested, Traits, _atomSize);

        /// <summary>The size this chunk will actually reserve for <paramref name="requested"/>.</summary>
        internal ulong EffectiveSize(ulong requested) => SizeFor(requested, Traits, _atomSize);

        /// <summary>
        /// Suballocate out of this chunk, applying <see cref="EffectiveAlignment"/> and
        /// <see cref="EffectiveSize"/>.
        /// </summary>
        /// <returns>False when the chunk has no free range that fits, which is the pool's signal to try the next
        /// chunk. Not an error.</returns>
        internal bool TryAllocate(ulong size, ulong alignment, out VulkanMemoryAllocation allocation)
        {
            RequireAlive();

            ulong reserved = EffectiveSize(size);
            if (!_free.TryAllocate(reserved, EffectiveAlignment(alignment), out ulong offset))
            {
                allocation = default;
                return false;
            }

            allocation = new VulkanMemoryAllocation(this, offset, reserved);
            return true;
        }

        /// <summary>Give a suballocation back, merging it with its free neighbours.</summary>
        /// <param name="offset">The offset a previous <see cref="TryAllocate"/> produced.</param>
        internal void Free(ulong offset)
        {
            RequireAlive();
            _free.Free(offset);
        }

        /// <summary>
        /// <c>vkFlushMappedMemoryRanges</c> over a CHUNK-relative range, widened to the atom boundary.
        /// <para>
        /// A COHERENT CHUNK RETURNS WITHOUT CALLING ANYTHING, and that is free rather than cheap: coherent memory
        /// needs no flush by definition, and <c>vkQueueSubmit</c> performs an implicit host-write availability
        /// operation for it, so writes made before a submit are visible with no barrier and no call. That is what
        /// every ladder preferring coherent buys, and it is why the incumbent having neither call anywhere has
        /// never been noticed.
        /// </para>
        /// </summary>
        internal void Flush(ulong offset, ulong size)
        {
            if (!TryWiden(offset, size, out ulong widenedOffset, out ulong widenedSize)) return;

            _api.Flush(Memory, widenedOffset, widenedSize);
        }

        /// <summary>
        /// <c>vkInvalidateMappedMemoryRanges</c> over a CHUNK-relative range, widened to the atom boundary. The
        /// readback path's real work on a cached, non-coherent type, and a no-op on a coherent one.
        /// </summary>
        internal void Invalidate(ulong offset, ulong size)
        {
            if (!TryWiden(offset, size, out ulong widenedOffset, out ulong widenedSize)) return;

            _api.Invalidate(Memory, widenedOffset, widenedSize);
        }

        /// <summary>
        /// <c>vkFreeMemory</c>, once. The callback the retire list holds until the timeline has passed the value
        /// recorded when the last thing in this chunk was freed, so memory is never returned to the driver while a
        /// submission that could still be reading it is outstanding.
        /// </summary>
        /// <returns>True when THIS call ended the chunk, false when it was already destroyed or forgotten. The
        /// allocation counter decrements on true, which is what stops a double drain double-counting.</returns>
        internal bool Destroy()
        {
            if (_destroyed) return false;
            _destroyed = true;

            _api.Free(Memory);
            return true;
        }

        /// <summary>
        /// Drop the chunk WITHOUT freeing it, for a device that is already dead. Its memory went with the device,
        /// so a <c>vkFreeMemory</c> now would be a call against freed memory, which aborts the process through the
        /// Vulkan loader rather than failing quietly.
        /// </summary>
        /// <returns>True when this call ended the chunk.</returns>
        internal bool Forget()
        {
            if (_destroyed) return false;
            _destroyed = true;
            return true;
        }

        // Host-visible AND non-coherent is the only combination where a range has to be widened at all, and
        // therefore the only one where suballocations have to be isolated to atom boundaries.
        static bool NeedsIsolation(VulkanMemoryTrait traits)
            => (traits & VulkanMemoryTrait.HostVisible) != 0 && (traits & VulkanMemoryTrait.HostCoherent) == 0;

        bool TryWiden(ulong offset, ulong size, out ulong widenedOffset, out ulong widenedSize)
        {
            widenedOffset = 0;
            widenedSize = 0;

            if (HostCoherent || size == 0) return false;

            if (!HostVisible)
            {
                throw new InvalidOperationException(
                    "A native Vulkan memory chunk that is not host-visible was asked to flush or invalidate a "
                    + "range. There is no mapping to make available in either direction, and vkFlushMappedMemory"
                    + "Ranges requires the memory to be mapped. This is engine-internal misuse: the caller has a "
                    + "device-local allocation and is treating it as an upload or readback one.");
            }

            RequireAlive();
            VulkanMappedRange.Align(offset, size, Size, _atomSize, out widenedOffset, out widenedSize);
            return widenedSize != 0;
        }

        void RequireAlive()
        {
            if (!_destroyed) return;

            throw new InvalidOperationException(
                "A native Vulkan memory chunk was used after it was destroyed. Its VkDeviceMemory has been freed, "
                + "so every suballocation out of it and every mapped pointer into it are dangling. A chunk is "
                + "destroyed only once nothing is suballocated out of it and the timeline has passed the retire "
                + "value, so reaching this means an allocation outlived the free that released its chunk.");
        }
    }
}
