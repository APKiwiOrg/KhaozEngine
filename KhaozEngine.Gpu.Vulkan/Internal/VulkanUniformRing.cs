using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE RING-BACKED UNIFORM BUFFER (V-M5, section 9.2): the geometry that turns one <c>VkBuffer</c> of
    /// <c>stride * FramesInFlight</c> bytes into per-frame segments, and the write into the persistently mapped
    /// memory behind it.
    ///
    /// <para><b>THERE IS NO MAP AND NO UNMAP HERE, AND THAT ASYMMETRY IS THE HEADLINE.</b> The Direct3D 11 ring
    /// carries a whole mapping lifecycle (a map scope, a mapped-ring registry, an unmap at every submit) because
    /// that API has no persistent mapping and forbids a mapped resource being bound to the pipeline. Vulkan has
    /// neither restriction: row 6's host-visible chunks are mapped once at creation and never unmapped (V-M3), so a
    /// ring is a base pointer plus arithmetic. A record-time write is a <c>memcpy</c> and NOTHING else: no staging
    /// buffer, no <c>vkCmdCopyBuffer</c>, no memory barrier and NO RENDER-PASS SPLIT. That is what makes this ring
    /// strictly simpler than the other one while running the same policy, and nobody should reintroduce a map
    /// lifecycle here by analogy.</para>
    ///
    /// <para><b>WHY IT IS WORTH AS MUCH HERE AS THERE, in the corrected form (9.2).</b> The obvious reading is that
    /// Vulkan has no Direct3D 11-shaped problem because its <c>UpdateBuffer</c> records a copy instead of blocking
    /// the CPU. On the shipped incumbent that copy path's FIRST statement ends the active render pass, which
    /// transitions the attachments and emits a full pipeline flush, then copies, then emits a GLOBAL
    /// <c>VkMemoryBarrier</c>, and the next draw lazily re-begins the pass. So a record-time uniform write there is
    /// a render-pass split plus a pipeline flush plus a global barrier, not a memcpy. And that global barrier's
    /// destination is <c>VertexAttributeRead</c> at <c>VertexInput</c>, so it does not cover a uniform read at all:
    /// the write is both expensive AND under-synchronised for the usage the engine's per-frame uniform buffers
    /// have.</para>
    ///
    /// <para><b>AND HERE THE SEGMENTS ARE A REQUIREMENT RATHER THAN AN OPTIMISATION.</b> Direct3D 11's
    /// <c>MAP_WRITE_DISCARD</c> gives the driver licence to rename the buffer under a write. Vulkan renames
    /// nothing, so writing bytes the GPU may still be reading from a previous frame's submission is a plain data
    /// race with no diagnostic. What makes the writes visible without a flush is that the memory is
    /// <c>HOST_COHERENT</c> BY REQUIREMENT (V-M4) and that <c>vkQueueSubmit</c> performs an implicit host-write
    /// availability operation for coherent memory. The only invariant left is that the CPU never writes into a
    /// segment the GPU is still reading, which is exactly <see cref="VulkanRingAllocator"/>'s fence gate.</para>
    ///
    /// <para><b>THE <c>IGpuBuffer</c> IDENTITY NEVER CHANGES</b> and the base is applied at BIND, as the
    /// <c>pDynamicOffsets</c> entry row 11 composes (V-D4). One <see cref="IGpuBuffer"/> is one <c>VkBuffer</c>,
    /// allocated N times larger and never re-pointed, so a resource set's pinned <c>GpuBufferRange</c> still names
    /// the same handle and the same logical offset across all 68 call sites that build one at load time.</para>
    ///
    /// <para><b>NOT DEVICE-FACING BY NATURE.</b> Everything here is engine arithmetic and one pointer, so the
    /// segment maths, the write offset and the routing are driven by plain <c>[Fact]</c>s on every operating
    /// system. There is no native seam to interpose on because there are no native calls to make.</para>
    /// </summary>
    internal sealed class VulkanUniformRing
    {
        readonly VulkanRingAllocator _allocator;

        // The persistently mapped base of the WHOLE ring, which is the chunk's own mapping plus this buffer's
        // suballocation offset. Stable for the buffer's life (V-M3), which is why it is a plain field rather than
        // anything with a lifecycle: row 6 maps a host-visible chunk at creation and unmaps it never.
        readonly nint _mapped;

        // The off-timeline writes this ring owes segments the GPU had not finished with, or null for a ring that
        // has never deferred one, which is every ring in a program that writes uniforms only at record time.
        // Allocated on the first deferral rather than per ring, because a device may hold hundreds of rings and the
        // per-segment lists would otherwise be pure overhead in the common case. Touched ONLY under the device's
        // submit lock, from VulkanRingAllocator's record and apply sites.
        VulkanRingPendingPatches? _patches;

        /// <param name="allocator">The device's one ring allocator, which owns the segment index every ring in the
        /// device rotates on together.</param>
        /// <param name="mappedBase">The persistently mapped first byte of this buffer's whole allocation. Row 9
        /// takes it from the chunk's <c>MappedPointer</c> plus the suballocation offset, and a test pins an
        /// array.</param>
        /// <param name="sizeInBytes">The LOGICAL size, which is the only size the seam ever sees.</param>
        /// <param name="minUniformBufferOffsetAlignment">The device limit the stride is rounded to, or 0 for
        /// <see cref="VulkanRingStride.OffsetAlignmentFloor"/>. See <see cref="VulkanRingStride"/> for why the
        /// floor is the load-bearing half.</param>
        internal VulkanUniformRing(VulkanRingAllocator allocator, nint mappedBase, ulong sizeInBytes,
            ulong minUniformBufferOffsetAlignment = VulkanRingStride.OffsetAlignmentFloor)
        {
            ArgumentNullException.ThrowIfNull(allocator);

            if (mappedBase == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mappedBase), mappedBase,
                    "A native Vulkan uniform ring was built over a null mapped pointer. Its memory is host-visible "
                    + "and mapped once at chunk creation (V-M3), so a zero pointer means the allocation came from "
                    + "a device-local chunk that was never mapped.");
            }

            _allocator = allocator;
            _mapped = mappedBase;

            SizeInBytes = sizeInBytes;
            OffsetAlignmentBytes = VulkanRingStride.AlignmentFor(minUniformBufferOffsetAlignment);
            SegmentStrideBytes = VulkanRingStride.SegmentStrideFor(sizeInBytes, minUniformBufferOffsetAlignment);
            TotalBytes = VulkanRingStride.TotalBytesFor(
                sizeInBytes, allocator.FramesInFlight, minUniformBufferOffsetAlignment);
        }

        /// <summary>The LOGICAL size, which is the only size the seam ever sees. A range, a dynamic offset and a
        /// write offset are all inside this, and the segment they land in is added underneath.</summary>
        internal ulong SizeInBytes { get; }

        /// <summary>
        /// The alignment every <c>pDynamicOffsets</c> entry composed against this ring owes
        /// (<c>VUID-vkCmdBindDescriptorSets-pDynamicOffsets-01971</c>): <see cref="VulkanRingStride.AlignmentFor"/>
        /// of the device limit, so it is the 256-byte floor on every conformant device rather than whatever this
        /// driver happens to report. Every segment base is a multiple of it by construction, which is what leaves
        /// only the caller's own terms to check at the bind. Row 11 does that check.
        /// </summary>
        internal ulong OffsetAlignmentBytes { get; }

        /// <summary>The distance between two segments (V-M5). NOT what a descriptor's range is set to: see
        /// <see cref="VulkanRingStride.BindWindowFits"/> for the invariant that separates the two.</summary>
        internal ulong SegmentStrideBytes { get; }

        /// <summary>The whole allocation, which is what the native <c>VkBuffer</c> is created with and the one
        /// number the seam's caller does not know about their own buffer.</summary>
        internal ulong TotalBytes { get; }

        /// <summary>How many segments this ring is cut into. Read off the allocator, because every ring in a device
        /// rotates together.</summary>
        internal int FramesInFlight => _allocator.FramesInFlight;

        /// <summary>
        /// The byte offset of the segment the NEXT submit will bind, which is the one any recording in progress is
        /// writing and the one a bind's dynamic offset is composed against. Deliberately not the segment the GPU is
        /// executing.
        /// </summary>
        internal ulong CurrentFrameBaseBytes => FrameBaseBytes(_allocator.CurrentSegment);

        /// <summary>The byte offset of any segment. Frame N uses segment <c>N % FramesInFlight</c>.</summary>
        internal ulong FrameBaseBytes(int segment)
        {
            if (segment < 0 || segment >= FramesInFlight)
            {
                throw new ArgumentOutOfRangeException(nameof(segment), segment,
                    "A native Vulkan uniform ring has "
                    + FramesInFlight.ToString(CultureInfo.InvariantCulture)
                    + " segments, so segment "
                    + segment.ToString(CultureInfo.InvariantCulture)
                    + " does not exist.");
            }

            return SegmentStrideBytes * (ulong)segment;
        }

        /// <summary>
        /// Whether a bind of <paramref name="range"/> bytes at <paramref name="rangeOffset"/> with a caller dynamic
        /// offset of <paramref name="callerDynamicOffset"/> stays inside its own segment on THIS ring, which at the
        /// last frame slot is the same question as whether it stays inside the buffer (V-M6). Row 10 writes the
        /// descriptor this is stated against and row 11 composes the offset the VUID measures.
        /// </summary>
        internal bool BindWindowFits(ulong rangeOffset, ulong callerDynamicOffset, ulong range)
            => VulkanRingStride.BindWindowFits(rangeOffset, callerDynamicOffset, range, SegmentStrideBytes);

        /// <summary>
        /// WRITE INTO THE CURRENT SEGMENT, which is what a record-time <c>UpdateBuffer</c> on a uniform buffer
        /// becomes: <c>memcpy(mapped + frameBase + offsetBytes, data, n)</c> and nothing else.
        /// <para>
        /// LOCK-FREE ON THE HOT PATH, and here that is unqualified rather than nearly true. There is no mapping to
        /// acquire, so this path takes no lock at all, ever. Two writes into one segment are two writes into plain
        /// memory.
        /// </para>
        /// <para>
        /// THE OFFSET IS AGAINST THE LOGICAL BUFFER and a write that runs past its end is refused. Without the
        /// check it would spill into the NEXT frame's segment, which is memory the GPU may be reading right now,
        /// and would present as another frame's uniforms being subtly wrong rather than as an error here.
        /// </para>
        /// <para>
        /// THE OFF-TIMELINE WRITE IS THE OTHER SHAPE AND IT REACHES EVERY SEGMENT. A device-level
        /// <see cref="VulkanRingAllocator.UpdateBuffer"/> does not come here: it copies every segment it can and
        /// leaves a PENDING PATCH on the ones an earlier frame is still reading, so a value written once persists
        /// for the buffer's life (V-M8). This path stays current-segment only because every shipped record-time
        /// uniform write is unconditional per frame, so replicating those would be
        /// <see cref="FramesInFlight"/> memcpys for a value the next frame overwrites, on the one path the whole
        /// design exists to make cheap.
        /// </para>
        /// </summary>
        internal void Write(ulong offsetBytes, ReadOnlySpan<byte> data)
        {
            ValidateWriteRange(offsetBytes, data.Length);

            if (data.Length == 0) return;

            CopyInto(_mapped, CurrentFrameBaseBytes + offsetBytes, data);
        }

        /// <summary>
        /// REFUSE A WRITE THAT WOULD LEAVE THE LOGICAL BUFFER, which is the one bounds check both write shapes owe.
        /// Shared rather than duplicated because the off-timeline path writes every segment and would otherwise
        /// spill each of them into the next, turning one overrun into <see cref="FramesInFlight"/> of them.
        /// </summary>
        internal void ValidateWriteRange(ulong offsetBytes, int length)
        {
            if (offsetBytes <= SizeInBytes && (ulong)length <= SizeInBytes - offsetBytes) return;

            throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes,
                "A "
                + length.ToString(CultureInfo.InvariantCulture)
                + "-byte write at offset "
                + offsetBytes.ToString(CultureInfo.InvariantCulture)
                + " runs past the end of a "
                + SizeInBytes.ToString(CultureInfo.InvariantCulture)
                + "-byte native Vulkan uniform buffer. On a ring-backed buffer that would spill into the next "
                + "frame's segment, which the GPU may be reading, so it is refused here rather than corrupting a "
                + "frame.");
        }

        /// <summary>The copy into ANY segment, which is what the off-timeline write walks. CALLED ONLY BY
        /// <see cref="VulkanRingAllocator"/>, under the submit lock and with the target segment already known to be
        /// free of the GPU. Bounds are the caller's.</summary>
        internal void CopyIntoSegmentUnderLock(int segment, ulong offsetBytes, ReadOnlySpan<byte> data)
            => CopyInto(_mapped, FrameBaseBytes(segment) + offsetBytes, data);

        /// <summary>Read a segment's bytes back. Present for a diagnostic and for the tests that assert WHERE a
        /// write landed, which is the only way a segment policy is observable at all.</summary>
        internal byte[] ReadSegment(int segment, ulong offsetBytes, int length)
        {
            ValidateWriteRange(offsetBytes, length);

            var copy = new byte[length];
            CopyOut(_mapped, FrameBaseBytes(segment) + offsetBytes, copy);
            return copy;
        }

        /// <summary>Whether this ring owes any segment a deferred off-timeline write. This is what puts it in, and
        /// takes it out of, the allocator's patched-ring registry.</summary>
        internal bool HasPendingPatches => _patches is not null && !_patches.IsEmpty;

        /// <summary>Whether one segment has a deferred write queued. The off-timeline write asks before copying
        /// directly: a segment already carrying a patch queues every later write too, so the two cannot be applied
        /// out of order.</summary>
        internal bool HasPendingPatchesFor(int segment) => _patches is not null && _patches.HasAnyFor(segment);

        /// <summary>How many deferred writes this ring is carrying, across every segment. For a test and a
        /// diagnostic, which is where the coalescing rule is observable.</summary>
        internal int PendingPatchCount => _patches?.PendingCount ?? 0;

        /// <summary>How many are queued for one segment.</summary>
        internal int PendingPatchCountFor(int segment) => _patches?.CountFor(segment) ?? 0;

        /// <summary>
        /// QUEUE AN OFF-TIMELINE WRITE FOR A SEGMENT THE GPU HAS NOT FINISHED WITH, and return how many earlier
        /// patches it fully covered and therefore replaced. CALLED ONLY BY <see cref="VulkanRingAllocator"/>, under
        /// the submit lock. Nothing is mapped or unmapped: the bytes go into managed memory and reach the segment
        /// at <see cref="ApplyPendingPatchesUnderLock"/>.
        /// </summary>
        internal int RecordPendingPatchUnderLock(int segment, ulong offsetBytes, ReadOnlySpan<byte> data)
        {
            _patches ??= new VulkanRingPendingPatches(FramesInFlight);
            return _patches.Record(segment, offsetBytes, data);
        }

        /// <summary>
        /// REPLAY ONE SEGMENT'S QUEUED WRITES INTO IT, oldest first, and forget them. Returns how many were
        /// applied. CALLED ONLY BY <see cref="VulkanRingAllocator"/>, under the submit lock and immediately after
        /// that segment's fence gate has proved the GPU is done with it, which is the same proof a record-time
        /// write into it rests on.
        /// <para>
        /// OLDEST FIRST IS THE WHOLE ORDERING GUARANTEE. Two off-timeline writes to overlapping ranges resolve
        /// last-write-wins here exactly as two direct copies into mapped memory would.
        /// </para>
        /// </summary>
        internal int ApplyPendingPatchesUnderLock(int segment)
        {
            if (_patches is null) return 0;

            IReadOnlyList<VulkanRingPatch> patches = _patches.ForSegment(segment);
            if (patches.Count == 0) return 0;

            int applied = patches.Count;
            ulong segmentBase = FrameBaseBytes(segment);
            for (int i = 0; i < patches.Count; i++)
            {
                CopyInto(_mapped, segmentBase + patches[i].OffsetBytes, patches[i].Data);
            }

            _patches.ClearSegment(segment);
            return applied;
        }

        /// <summary>Forget every queued write, for a ring whose buffer is going away, and return how many were
        /// dropped so the allocator's counters can reconcile them. CALLED ONLY BY
        /// <see cref="VulkanRingAllocator.Forget"/>, under the submit lock: a patch left behind would be replayed
        /// into a mapping whose chunk has since been freed.</summary>
        internal int DropPendingPatchesUnderLock() => _patches?.ClearAll() ?? 0;

        // The raw-pointer write, and one of the reasons this project allows unsafe blocks (the other is that
        // Vulkan's own structures carry pointer arrays as a matter of ABI). Everything above it is arithmetic and
        // everything below it is the driver's memory. Bounds are the caller's: both write shapes have already
        // refused anything that would leave the frame's own segment.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyInto(nint mapped, ulong byteOffset, ReadOnlySpan<byte> data)
            => data.CopyTo(new Span<byte>((byte*)mapped + byteOffset, data.Length));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyOut(nint mapped, ulong byteOffset, Span<byte> destination)
            => new ReadOnlySpan<byte>((byte*)mapped + byteOffset, destination.Length).CopyTo(destination);
    }
}
