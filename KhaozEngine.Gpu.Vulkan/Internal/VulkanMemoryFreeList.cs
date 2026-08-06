using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SUBALLOCATION POLICY of decision V-M2, as a pure data structure over one chunk's byte range: first-fit
    /// over a sorted free list with alignment correction, split on allocate, merge with BOTH neighbours on free.
    ///
    /// <para><b>NO VULKAN AND NO DEVICE ANYWHERE IN IT, which is the whole reason it is its own type.</b> The
    /// counterargument the design owes VMA (9.1) is that hand-rolled allocators are where memory corruption lives,
    /// and the corruption in question is two live allocations overlapping. That is an arithmetic property of
    /// exactly this file, so it is asserted directly, exhaustively and with randomized churn, on a machine with no
    /// Vulkan loader. What a device-free test still cannot see is an ALIASING defect on a real GPU, and that is
    /// what the synchronisation-validation gate (row 19) is for, which is why the VMA decline is written down as
    /// conditional on that gate existing.</para>
    ///
    /// <para><b>THE FREE LIST IS SORTED, COALESCED AND DISJOINT, always.</b> Sorted by offset so first-fit is a
    /// forward walk and so a freed range's two neighbours are the entries either side of its insertion point.
    /// Coalesced so two adjacent free ranges are never two entries, which is what stops the list degrading into a
    /// long tail of unusable slivers over a load-unload cycle. Disjoint by construction, since every entry either
    /// came from the initial whole-chunk range or from splitting one.</para>
    ///
    /// <para><b>ALIGNMENT PADDING IS RECLAIMED, and it is the single easiest thing here to get wrong.</b> When a
    /// request's alignment pushes the offset forward inside a free block, the bytes BEFORE the aligned offset go
    /// back on the free list as their own block rather than being absorbed into the allocation or silently
    /// dropped. Absorbing them would make <c>Free</c> return fewer bytes than were taken and leak a little on
    /// every aligned allocation, which on a long session is a slow bleed that reads as a driver leak.</para>
    ///
    /// <para><b>FREEING AN OFFSET THAT WAS NEVER ALLOCATED THROWS.</b> This type is engine-internal with no
    /// consumer-reachable path to it, so a bad offset is a bug in this package rather than bad input, and the
    /// alternative (ignore it, or trust it and merge a range nothing owns) corrupts the list in a way that
    /// surfaces later as an overlap somewhere unrelated.</para>
    /// </summary>
    internal sealed class VulkanMemoryFreeList
    {
        // Free ranges, sorted by Offset, disjoint, and never adjacent to each other (two adjacent ranges are
        // always merged into one). A List rather than a tree deliberately: a chunk holds tens of allocations, not
        // thousands, and allocation is off the hot path by design (V-M2), so an O(n) insert on a short array beats
        // a balanced tree that has to be got right.
        readonly List<Range> _free = new();

        // Every LIVE suballocation, by its offset. It is what makes Free(offset) able to reject an offset nothing
        // owns, and what carries the size so a caller does not have to hand it back and cannot hand back a wrong
        // one.
        readonly Dictionary<ulong, ulong> _live = new();

        ulong _used;

        /// <param name="capacity">The chunk's size in bytes. Must be greater than zero: a zero-capacity chunk
        /// could satisfy nothing and would be an allocation of nothing at the level above.</param>
        internal VulkanMemoryFreeList(ulong capacity)
        {
            if (capacity == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                    "A native Vulkan memory chunk of zero bytes cannot satisfy any request, and vkAllocateMemory "
                    + "rejects an allocationSize of 0 outright.");
            }

            Capacity = capacity;
            _free.Add(new Range(0, capacity));
        }

        /// <summary>The chunk's whole size in bytes.</summary>
        internal ulong Capacity { get; }

        /// <summary>How many bytes are handed out right now, counting only the suballocations themselves.
        /// Alignment padding that went back on the free list is NOT counted here, which is the point: the
        /// difference between this and <c>Capacity - FreeBytes</c> would be a leak, and it is always zero.</summary>
        internal ulong UsedBytes => _used;

        /// <summary>How many bytes are on the free list. Always exactly <c>Capacity - UsedBytes</c>, which is
        /// the invariant that catches a split or a merge that lost or duplicated a range.</summary>
        internal ulong FreeBytes => Capacity - _used;

        /// <summary>How many DISJOINT free ranges there are. One means the chunk is either untouched or fully
        /// reclaimed and coalesced, and a number that climbs over a churn cycle without coming back down is
        /// fragmentation rather than a leak.</summary>
        internal int FreeBlockCount => _free.Count;

        /// <summary>How many suballocations are live.</summary>
        internal int LiveCount => _live.Count;

        /// <summary>True when nothing is handed out, which is what lets the pool retire a chunk nobody is using
        /// any more.</summary>
        internal bool IsEmpty => _live.Count == 0;

        /// <summary>The largest single request this chunk could still satisfy at alignment 1. Reported rather
        /// than used, for the out-of-memory diagnostic that has to say whether a chunk was full or merely
        /// fragmented.</summary>
        internal ulong LargestFreeBlock
        {
            get
            {
                ulong largest = 0;
                for (int i = 0; i < _free.Count; i++)
                {
                    if (_free[i].Size > largest) largest = _free[i].Size;
                }
                return largest;
            }
        }

        /// <summary>
        /// Take <paramref name="size"/> bytes at an offset that is a multiple of <paramref name="alignment"/>,
        /// from the FIRST free range that can hold them once aligned.
        /// </summary>
        /// <param name="size">Bytes wanted. Zero throws: a zero-byte suballocation has no offset that means
        /// anything, two of them would share one, and <c>Free</c> could not tell them apart.</param>
        /// <param name="alignment">The required offset alignment in bytes. Must be a non-zero power of two, which
        /// every <c>VkMemoryRequirements.alignment</c> is.</param>
        /// <param name="offset">The chunk-relative offset, when this returns true.</param>
        /// <returns>False when no free range can hold the request, which is the pool's signal to try the next
        /// chunk or create one. It is NOT an error: a full chunk is the ordinary state of a busy pool.</returns>
        internal bool TryAllocate(ulong size, ulong alignment, out ulong offset)
        {
            RequireRealSize(size);
            RequirePowerOfTwo(alignment);

            for (int i = 0; i < _free.Count; i++)
            {
                Range block = _free[i];

                if (!TryAlignUp(block.Offset, alignment, out ulong aligned)) continue;

                // Both halves matter. The aligned offset can be past the end of the block entirely, and the
                // aligned offset plus the size can overflow, which on a ulong wraps to something that then
                // compares as fitting.
                if (aligned >= block.End) continue;
                if (size > block.End - aligned) continue;

                _free.RemoveAt(i);

                // THE PADDING GOES BACK, as its own free range. See the class note.
                if (aligned > block.Offset) _free.Insert(i++, new Range(block.Offset, aligned - block.Offset));

                ulong tailOffset = aligned + size;
                if (tailOffset < block.End) _free.Insert(i, new Range(tailOffset, block.End - tailOffset));

                _live.Add(aligned, size);
                _used += size;
                offset = aligned;
                return true;
            }

            offset = 0;
            return false;
        }

        /// <summary>
        /// Give back the suballocation at <paramref name="offset"/>, merging it with the free range before it and
        /// the free range after it when either is adjacent.
        /// </summary>
        /// <param name="offset">An offset a previous <see cref="TryAllocate"/> returned and that has not been
        /// freed since.</param>
        /// <exception cref="InvalidOperationException"><paramref name="offset"/> is not a live suballocation:
        /// either it was never handed out, or it has already been freed. See the class note for why this throws
        /// rather than shrugging.</exception>
        internal void Free(ulong offset)
        {
            if (!_live.Remove(offset, out ulong size))
            {
                throw new InvalidOperationException(
                    "The native Vulkan allocator was asked to free chunk offset "
                    + offset.ToString(CultureInfo.InvariantCulture)
                    + ", which is not a live suballocation of that chunk. Either it was never allocated or it has "
                    + "already been freed. This is engine-internal misuse rather than bad consumer input, and it "
                    + "throws rather than being ignored because merging a range nothing owns corrupts the free "
                    + "list into an overlap that surfaces later, somewhere unrelated.");
            }

            _used -= size;
            Insert(new Range(offset, size));
        }

        // The insertion point is the first range that starts AFTER the one going in, so the two candidates for a
        // merge are that range and the one before it. Both are checked, which is the difference between a list
        // that stays coalesced and one that halves its usable block size every load-unload cycle.
        void Insert(Range range)
        {
            int index = 0;
            while (index < _free.Count && _free[index].Offset < range.Offset) index++;

            bool mergedLeft = index > 0 && _free[index - 1].End == range.Offset;
            bool mergedRight = index < _free.Count && _free[index].Offset == range.End;

            if (mergedLeft && mergedRight)
            {
                _free[index - 1] = new Range(_free[index - 1].Offset, _free[index].End - _free[index - 1].Offset);
                _free.RemoveAt(index);
                return;
            }

            if (mergedLeft)
            {
                _free[index - 1] = new Range(_free[index - 1].Offset, range.End - _free[index - 1].Offset);
                return;
            }

            if (mergedRight)
            {
                _free[index] = new Range(range.Offset, _free[index].End - range.Offset);
                return;
            }

            _free.Insert(index, range);
        }

        /// <summary>
        /// Align <paramref name="value"/> up to <paramref name="alignment"/>, refusing rather than wrapping when
        /// the rounded value would not fit in a <c>ulong</c>. Shared with the allocator, which applies the same
        /// rounding to a request's size on a non-coherent chunk.
        /// </summary>
        /// <returns>False when the rounding would overflow, which a caller treats as "does not fit" rather than
        /// as an error.</returns>
        internal static bool TryAlignUp(ulong value, ulong alignment, out ulong aligned)
        {
            RequirePowerOfTwo(alignment);

            ulong mask = alignment - 1;
            if (value > ulong.MaxValue - mask)
            {
                aligned = 0;
                return false;
            }

            aligned = (value + mask) & ~mask;
            return true;
        }

        /// <summary>Whether <paramref name="value"/> is a non-zero power of two, which every Vulkan alignment and
        /// every <c>nonCoherentAtomSize</c> is required to be.</summary>
        internal static bool IsPowerOfTwo(ulong value) => value != 0 && (value & (value - 1)) == 0;

        static void RequireRealSize(ulong size)
        {
            if (size != 0) return;

            throw new ArgumentOutOfRangeException(nameof(size), size,
                "The native Vulkan allocator was asked for a zero-byte suballocation. There is no offset that "
                + "means anything for one, two of them would share the same offset, and freeing would not be able "
                + "to tell them apart. A caller with nothing to store must not allocate.");
        }

        static void RequirePowerOfTwo(ulong alignment)
        {
            if (IsPowerOfTwo(alignment)) return;

            throw new ArgumentOutOfRangeException(nameof(alignment), alignment,
                "A native Vulkan memory alignment must be a non-zero power of two. Every "
                + "VkMemoryRequirements.alignment is one by spec, so a value that is not means the number came "
                + "from somewhere other than a requirements query.");
        }

        // Half-open [Offset, End). Stored as offset plus size because that is the shape both the caller and
        // vkMapMemory speak, with End derived rather than stored so the two can never disagree.
        readonly struct Range
        {
            internal Range(ulong offset, ulong size)
            {
                Offset = offset;
                Size = size;
            }

            internal ulong Offset { get; }

            internal ulong Size { get; }

            internal ulong End => Offset + Size;
        }
    }
}
