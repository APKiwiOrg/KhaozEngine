using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE RING-BACKED UNIFORM BUFFER (M-M3, section 9.2): the geometry that turns one <c>MTLBuffer</c> of
    /// <c>stride * FramesInFlight</c> bytes into per-frame segments, and the write into the persistently mapped
    /// <c>contents()</c> behind it.
    ///
    /// <para><b>A RECORD-TIME WRITE IS A <c>memcpy</c> AND NOTHING ELSE, AND THE THING IT DOES NOT DO IS THE
    /// WHOLE POINT (2.1).</b> No staging buffer, no blit, no allocation, no release, and above all NO ENCODER.
    /// On the incumbent a record-time uniform write allocates an <c>MTLBuffer</c>, copies, ENDS THE RENDER
    /// ENCODER to open a blit encoder, copies again, releases, and then the next draw pays a full graphics state
    /// re-activation because ending a render encoder discards the pipeline, every argument-table entry, the
    /// viewport, the scissor and every vertex stream (M-R4). The saved work is not the copy. It is the
    /// encoder.</para>
    ///
    /// <para><b>AND IT IS A CORRECTNESS CHANGE HERE, WHICH IT WAS NOT ON DIRECT3D 11 (M-M7).</b>
    /// <c>MTLGraphicsDevice.UpdateBufferCore</c> is an unguarded <c>Unsafe.CopyBlock</c> into <c>contents()</c>
    /// with no fence, no frame index and no diagnostic. Direct3D 11's <c>MAP_WRITE_DISCARD</c> gives the driver
    /// licence to rename a buffer under a write and Metal renames nothing, so that is a plain data race in
    /// shipped code, and <see cref="MetalRingAllocator"/>'s completion gate is what removes it. Automatic hazard
    /// tracking (M-H1) does not help: it orders GPU work against GPU work and says nothing about a CPU write
    /// racing a GPU read.</para>
    ///
    /// <para><b>THE <see cref="IGpuBuffer"/> IDENTITY NEVER CHANGES</b> and the base is applied at BIND, through
    /// the <c>offset</c> slot of an array setter or through <c>setBufferOffset:</c> on an offsets-only rebind
    /// (M-R7). One <see cref="IGpuBuffer"/> is one <c>MTLBuffer</c>, allocated N times larger and never
    /// re-pointed, so a resource set's pinned <c>GpuBufferRange</c> still names the same handle and the same
    /// logical offset across every site that builds one at load time.</para>
    ///
    /// <para><b>THERE IS NO MAP LIFECYCLE HERE AND NOBODY SHOULD ADD ONE BY ANALOGY.</b> The Direct3D 11 ring
    /// carries a whole mapping lifecycle because that API has no persistent mapping and forbids a mapped
    /// resource being bound. A Shared <c>MTLBuffer</c>'s <c>contents()</c> is stable for the buffer's life and
    /// there is no map call at all, so a ring here is a base pointer plus arithmetic, exactly as the Vulkan
    /// sibling's is, and it reaches that shape without needing a host-coherent memory type to be chosen first.
    /// This is the simplest of the three rings and section 9.2 says so.</para>
    ///
    /// <para><b>NOT DEVICE-FACING BY NATURE.</b> Everything here is engine arithmetic and one pointer, so the
    /// segment maths, the write offset and the routing are driven by plain <c>[Fact]</c>s on every operating
    /// system. There is no native seam to interpose on because there are no native calls to make.</para>
    /// </summary>
    internal sealed class MetalUniformRing
    {
        readonly MetalRingAllocator _allocator;

        // The persistently mapped base of the WHOLE ring, which is the MTLBuffer's own contents() pointer taken
        // ONCE at creation. Stable for the buffer's life by Metal's own contract (M-M2), which is why it is a
        // plain field rather than anything with a lifecycle, and why a record-time write carries no message send
        // in front of it.
        readonly IntPtr _contents;

        // The off-timeline writes this ring owes segments the GPU had not finished with, or null for a ring that
        // has never deferred one, which is every ring in a program that writes uniforms only at record time.
        // Allocated on the first deferral rather than per ring, because a device may hold hundreds of rings and
        // the per-segment lists would otherwise be pure overhead in the common case. Touched ONLY under the
        // device's submit lock, from MetalRingAllocator's record and apply sites.
        MetalRingPendingPatches? _patches;

        /// <param name="allocator">The device's one ring allocator, which owns the segment index every ring in
        /// the device rotates on together.</param>
        /// <param name="contents">The <c>MTLBuffer</c>'s <c>contents()</c> pointer, which is the first byte of
        /// this buffer's whole allocation. <c>MetalBuffer</c> takes it at creation and a test pins an
        /// array.</param>
        /// <param name="sizeInBytes">The LOGICAL size, which is the only size the seam ever sees.</param>
        internal MetalUniformRing(MetalRingAllocator allocator, IntPtr contents, uint sizeInBytes)
        {
            ArgumentNullException.ThrowIfNull(allocator);

            if (contents == IntPtr.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(contents), contents,
                    "A native Metal uniform ring was built over a null contents() pointer. Every buffer this "
                    + "backend creates is MTLStorageModeShared (M-M2), and a Shared buffer always answers a real "
                    + "pointer, so a zero here means the storage mode changed rather than that this allocation "
                    + "failed.");
            }

            _allocator = allocator;
            _contents = contents;

            SizeInBytes = sizeInBytes;
            SegmentStrideBytes = MetalRingStride.SegmentStrideFor(sizeInBytes);
            TotalBytes = MetalRingStride.TotalBytesFor(sizeInBytes, allocator.FramesInFlight);
        }

        /// <summary>The LOGICAL size, which is the only size the seam ever sees. A range, a dynamic offset and a
        /// write offset are all inside this, and the segment they land in is added underneath.</summary>
        internal uint SizeInBytes { get; }

        /// <summary>The distance between two segments (M-M3), which is <see cref="SizeInBytes"/> rounded up to
        /// <see cref="MetalRingStride.SegmentAlignment"/>.</summary>
        internal uint SegmentStrideBytes { get; }

        /// <summary>The whole allocation, which is what the native <c>MTLBuffer</c> is created with and the one
        /// number the seam's caller does not know about their own buffer.</summary>
        internal ulong TotalBytes { get; }

        /// <summary>How many segments this ring is cut into. Read off the allocator, because every ring in a
        /// device rotates together.</summary>
        internal int FramesInFlight => _allocator.FramesInFlight;

        /// <summary>
        /// The byte offset of the segment the NEXT submit will bind, which is the one any recording in progress
        /// is writing and the one a bind's offset is composed against. Deliberately not the segment the GPU is
        /// executing.
        /// </summary>
        internal ulong CurrentFrameBaseBytes => FrameBaseBytes(_allocator.CurrentSegment);

        /// <summary>The byte offset of any segment. Frame N uses segment <c>N % FramesInFlight</c>.</summary>
        internal ulong FrameBaseBytes(int segment)
        {
            if (segment < 0 || segment >= FramesInFlight)
            {
                throw new ArgumentOutOfRangeException(nameof(segment), segment,
                    "A native Metal uniform ring has " + FramesInFlight + " segments, so segment " + segment
                    + " does not exist.");
            }

            return (ulong)SegmentStrideBytes * (ulong)segment;
        }

        /// <summary>
        /// Whether a bind of <paramref name="range"/> bytes at <paramref name="rangeOffset"/> with a caller
        /// dynamic offset of <paramref name="callerDynamicOffset"/> stays inside its own segment on THIS ring
        /// (M-M4). Row 13 composes the offset this measures.
        /// </summary>
        internal bool BindWindowFits(uint rangeOffset, uint callerDynamicOffset, uint range)
            => MetalRingStride.BindWindowFits(rangeOffset, callerDynamicOffset, range, SegmentStrideBytes);

        /// <summary>
        /// WRITE INTO THE CURRENT SEGMENT, which is what a record-time <c>UpdateBuffer</c> on a uniform buffer
        /// becomes: <c>memcpy(contents + frameBase + offsetBytes, data, n)</c> and nothing else.
        /// <para>
        /// LOCK-FREE ON THE HOT PATH, unqualified. There is no mapping to acquire and no encoder to open, so this
        /// path takes no lock and makes no native call at all, ever. Two writes into one segment are two writes
        /// into plain memory.
        /// </para>
        /// <para>
        /// THE OFFSET IS AGAINST THE LOGICAL BUFFER and a write that runs past its end is refused. Without the
        /// check it would spill into the NEXT frame's segment, which is memory the GPU may be reading right now,
        /// and would present as another frame's uniforms being subtly wrong rather than as an error here. The
        /// incumbent's own <c>UpdateBufferCore</c> has no bound check whatsoever.
        /// </para>
        /// <para>
        /// THE OFF-TIMELINE WRITE IS THE OTHER SHAPE AND IT REACHES EVERY SEGMENT. A device-level
        /// <see cref="MetalRingAllocator.UpdateBuffer"/> does not come here: it copies every segment it can and
        /// leaves a PENDING PATCH on the ones an earlier frame is still reading, so a value written once persists
        /// for the buffer's life (M-M5). This path stays current-segment only because every shipped record-time
        /// uniform write is unconditional per frame, so replicating those would be
        /// <see cref="FramesInFlight"/> memcpys for a value the next frame overwrites, on the one path the whole
        /// design exists to make cheap.
        /// </para>
        /// </summary>
        internal void Write(uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ValidateWriteRange(offsetBytes, data.Length);

            if (data.Length == 0) return;

            CopyInto(_contents, CurrentFrameBaseBytes + offsetBytes, data);
        }

        /// <summary>
        /// REFUSE A WRITE THAT WOULD LEAVE THE LOGICAL BUFFER, which is the one bounds check both write shapes
        /// owe. Shared rather than duplicated because the off-timeline path writes every segment and would
        /// otherwise spill each of them into the next, turning one overrun into <see cref="FramesInFlight"/> of
        /// them.
        /// </summary>
        internal void ValidateWriteRange(uint offsetBytes, int length)
        {
            if (length >= 0 && offsetBytes <= SizeInBytes && (ulong)length <= SizeInBytes - offsetBytes) return;

            throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes,
                "A " + length + "-byte write at offset " + offsetBytes + " runs past the end of a " + SizeInBytes
                + "-byte native Metal uniform buffer. On a ring-backed buffer that would spill into the next "
                + "frame's segment, which the GPU may be reading, so it is refused here rather than corrupting a "
                + "frame.");
        }

        /// <summary>The copy into ANY segment, which is what the off-timeline write walks. CALLED ONLY BY
        /// <see cref="MetalRingAllocator"/>, under the submit lock and with the target segment already known to
        /// be free of the GPU. Bounds are the caller's.</summary>
        internal void CopyIntoSegmentUnderLock(int segment, uint offsetBytes, ReadOnlySpan<byte> data)
            => CopyInto(_contents, FrameBaseBytes(segment) + offsetBytes, data);

        /// <summary>Read a segment's bytes back. Present for a diagnostic, for <c>Map</c> on a ring-backed
        /// buffer, and for the tests that assert WHERE a write landed, which is the only way a segment policy is
        /// observable at all.</summary>
        internal byte[] ReadSegment(int segment, uint offsetBytes, int length)
        {
            ValidateWriteRange(offsetBytes, length);

            var copy = new byte[length];
            CopyOut(_contents, FrameBaseBytes(segment) + offsetBytes, copy);
            return copy;
        }

        /// <summary>Whether this ring owes any segment a deferred off-timeline write. This is what puts it in,
        /// and takes it out of, the allocator's patched-ring registry.</summary>
        internal bool HasPendingPatches => _patches is not null && !_patches.IsEmpty;

        /// <summary>Whether one segment has a deferred write queued. The off-timeline write asks before copying
        /// directly: a segment already carrying a patch queues every later write too, so the two cannot be
        /// applied out of order.</summary>
        internal bool HasPendingPatchesFor(int segment) => _patches is not null && _patches.HasAnyFor(segment);

        /// <summary>How many deferred writes this ring is carrying, across every segment. For a test and a
        /// diagnostic, which is where the coalescing rule is observable.</summary>
        internal int PendingPatchCount => _patches?.PendingCount ?? 0;

        /// <summary>How many are queued for one segment.</summary>
        internal int PendingPatchCountFor(int segment) => _patches?.CountFor(segment) ?? 0;

        /// <summary>
        /// QUEUE AN OFF-TIMELINE WRITE FOR A SEGMENT THE GPU HAS NOT FINISHED WITH, and return how many earlier
        /// patches it fully covered and therefore replaced. CALLED ONLY BY <see cref="MetalRingAllocator"/>,
        /// under the submit lock. The bytes go into managed memory and reach the segment at
        /// <see cref="ApplyPendingPatchesUnderLock"/>.
        /// </summary>
        internal int RecordPendingPatchUnderLock(int segment, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            _patches ??= new MetalRingPendingPatches(FramesInFlight);
            return _patches.Record(segment, offsetBytes, data);
        }

        /// <summary>
        /// REPLAY ONE SEGMENT'S QUEUED WRITES INTO IT, oldest first, and forget them. Returns how many were
        /// applied. CALLED ONLY BY <see cref="MetalRingAllocator"/>, under the submit lock and immediately after
        /// that segment's completion gate has proved the GPU is done with it, which is the same proof a
        /// record-time write into it rests on.
        /// <para>
        /// OLDEST FIRST IS THE WHOLE ORDERING GUARANTEE. Two off-timeline writes to overlapping ranges resolve
        /// last-write-wins here exactly as two direct copies into <c>contents()</c> would.
        /// </para>
        /// </summary>
        internal int ApplyPendingPatchesUnderLock(int segment)
        {
            if (_patches is null) return 0;

            IReadOnlyList<MetalRingPatch> patches = _patches.ForSegment(segment);
            if (patches.Count == 0) return 0;

            int applied = patches.Count;
            ulong segmentBase = FrameBaseBytes(segment);
            for (int i = 0; i < patches.Count; i++)
            {
                CopyInto(_contents, segmentBase + patches[i].OffsetBytes, patches[i].Data);
            }

            _patches.ClearSegment(segment);
            return applied;
        }

        /// <summary>
        /// Forget every queued write, for a ring whose buffer is going away, and return how many were dropped so
        /// the allocator's counters can reconcile them. CALLED ONLY BY <see cref="MetalRingAllocator.Forget"/>,
        /// under the submit lock: a patch left behind would be replayed through a <c>contents()</c> pointer whose
        /// <c>MTLBuffer</c> has since been released.
        /// </summary>
        internal int DropPendingPatchesUnderLock() => _patches?.ClearAll() ?? 0;

        // The raw-pointer write. Everything above it is arithmetic and everything below it is memory both the CPU
        // and the GPU address, which on unified memory is the same pages. Bounds are the caller's: both write
        // shapes have already refused anything that would leave the frame's own segment.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyInto(IntPtr contents, ulong byteOffset, ReadOnlySpan<byte> data)
            => data.CopyTo(new Span<byte>((byte*)contents + byteOffset, data.Length));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyOut(IntPtr contents, ulong byteOffset, Span<byte> destination)
            => new ReadOnlySpan<byte>((byte*)contents + byteOffset, destination.Length).CopyTo(destination);
    }
}
