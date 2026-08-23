using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// ONE RING-BACKED UNIFORM BUFFER (decisions U1 and U2): the geometry that turns one <c>ID3D11Buffer</c> of
    /// <c>segmentStride * framesInFlight</c> bytes into per-frame segments, the lazily acquired mapping every
    /// write goes through, and the write itself.
    /// <para>
    /// WHAT PROBLEM THIS IS. Veldrid's <c>UpdateBuffer</c> put a partial write to a <c>DEFAULT</c> constant
    /// buffer on a pooled staging path that mapped the immediate context with plain <c>D3D11_MAP_WRITE</c> and no
    /// <c>DO_NOT_WAIT</c>, so every such write BLOCKED until the GPU was done with the staging buffer being
    /// recycled. A reporting client paid 22 of those a frame and 12 to 17 ms a pass on a scene that encodes in
    /// under a millisecond elsewhere. Zero renderer sites ask for <see cref="GpuBufferUsage.Dynamic"/>, so every
    /// per-frame uniform buffer in the engine takes that path by construction. Here a write is a memcpy into
    /// already-mapped memory, at an offset inside the frame's own segment, with no staging buffer, no copy
    /// command, no stall and no whole-buffer requirement.
    /// </para>
    /// <para>
    /// THE IDENTITY NEVER CHANGES, which is decision U3 and the reason this shape survives contact with
    /// <c>CreateResourceSet</c>. One <see cref="IGpuBuffer"/> is one <c>ID3D11Buffer</c>, allocated N times larger
    /// and never re-pointed, so a resource set's pinned <see cref="GpuBufferRange"/> still names the same handle
    /// and the same logical offset across all 68 call sites that build one at load time. The per-frame base is
    /// applied at BIND time by the first-constant computation (see <see cref="D3D11ConstantRange"/>) and is never
    /// baked into a set.
    /// </para>
    /// <para>
    /// SEGMENTS ARE 256-ALIGNED, and that is a hard Direct3D requirement rather than a rounding habit.
    /// <c>*SetConstantBuffers1</c> counts in 16-byte constants and wants the first constant on a 16-constant
    /// boundary, so a frame base that was not a multiple of 256 bytes would be unbindable. Every real engine
    /// stride is already 256-aligned (256, 768, 8448, 9472), and rounding the stride up here is what keeps that
    /// true for a buffer whose size is not.
    /// </para>
    /// <para>
    /// THE MAPPING IS THE RECORD PHASE'S, not the write's, under the default driver. The first write of a record
    /// phase maps <c>NO_OVERWRITE</c>, every later write in that phase reuses the mapping, and the start of the
    /// next <c>Submit</c> unmaps. That is two native calls per ring per submit, which is the floor, and it is
    /// legal ONLY because recording is deferred (decision R1): Direct3D 11 has no persistent mapping and forbids a
    /// mapped resource being bound to the pipeline, so a ring mapped across a phase in which draws happen would be
    /// a mapped resource on the pipeline. Under <c>KE_D3D11_RECORD=immediate</c> draws DO happen during record, so
    /// the mapping degrades to write-scoped (see <see cref="D3D11RingMapScope"/>).
    /// </para>
    /// <para>
    /// NOT DEVICE-FACING BY NATURE. Everything here is engine arithmetic and one pointer, with the two native
    /// calls behind <see cref="ID3D11RingMemory"/>, so the segment maths, the map lifecycle and the write are
    /// driven by plain <c>[Fact]</c>s on every operating system.
    /// </para>
    /// </summary>
    internal sealed class D3D11UniformRing
    {
        readonly D3D11RingAllocator _allocator;
        readonly ID3D11RingMemory _memory;

        // The pointer is written BEFORE _mapped is set and read only after _mapped reads true, which is what
        // makes the lock-free fast path in Write safe: _mapped is volatile, so the write to it publishes the
        // pointer written before it and a reader that sees true sees a pointer.
        IntPtr _pointer;
        volatile bool _mapped;

        // The off-timeline writes this ring owes segments the GPU had not finished with, or null for a ring that
        // has never deferred one, which is every ring in a program that writes uniforms only at record time.
        // Allocated on the first deferral rather than per ring, because a device may hold hundreds of rings and
        // the per-segment lists would otherwise be pure overhead in the common case. Touched ONLY under the
        // device's submit lock, from D3D11RingAllocator's record and apply sites, so it needs no synchronisation
        // of its own.
        D3D11RingPendingPatches? _patches;

        internal D3D11UniformRing(D3D11RingAllocator allocator, ID3D11RingMemory memory, uint sizeInBytes)
        {
            ArgumentNullException.ThrowIfNull(allocator);
            ArgumentNullException.ThrowIfNull(memory);
            if (sizeInBytes == 0)
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "A zero-byte uniform buffer cannot be ring-backed.");

            _allocator = allocator;
            _memory = memory;
            SizeInBytes = sizeInBytes;
            SegmentStrideBytes = SegmentStrideFor(sizeInBytes);
            TotalBytes = TotalBytesFor(sizeInBytes, allocator.FramesInFlight);
        }

        /// <summary>The LOGICAL size, which is the only size the seam ever sees. A range, a dynamic offset and a
        /// write offset are all inside this, and the segment they land in is added underneath.</summary>
        internal uint SizeInBytes { get; }

        /// <summary>The distance between two segments, which is <see cref="SizeInBytes"/> rounded up to the
        /// 256-byte constant-buffer offset alignment.</summary>
        internal uint SegmentStrideBytes { get; }

        /// <summary>The whole allocation: <see cref="SegmentStrideBytes"/> times the allocator's frame count.
        /// This is what the native buffer is created with, and it is the one number the seam's caller does not
        /// know about their own buffer.</summary>
        internal uint TotalBytes { get; }

        /// <summary>How many segments this ring is cut into. Read off the allocator, because every ring in a
        /// device rotates together.</summary>
        internal int FramesInFlight => _allocator.FramesInFlight;

        /// <summary>Whether the ring currently holds a mapping. Volatile, because the record path reads it
        /// without the submit lock.</summary>
        internal bool IsMapped => _mapped;

        /// <summary>The mapped pointer, or <see cref="IntPtr.Zero"/> when unmapped. Present for the write path
        /// and for tests.</summary>
        internal IntPtr MappedPointer => _mapped ? _pointer : IntPtr.Zero;

        /// <summary>
        /// The byte offset of the segment the NEXT submit will bind, which is the one any recording in progress
        /// is writing and the one a bind's first constant is computed against. Deliberately not the segment the
        /// GPU is executing.
        /// </summary>
        internal uint CurrentFrameBaseBytes => FrameBaseBytes(_allocator.CurrentSegment);

        /// <summary>The byte offset of any segment. Frame N uses segment <c>N % FramesInFlight</c>.</summary>
        internal uint FrameBaseBytes(int segment)
        {
            if (segment < 0 || segment >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(segment), segment,
                    $"A uniform ring has {FramesInFlight} segments, so segment {segment} does not exist.");

            return SegmentStrideBytes * (uint)segment;
        }

        /// <summary>
        /// WRITE INTO THE CURRENT SEGMENT, which is what a record-time <c>UpdateBuffer</c> on a uniform buffer
        /// becomes. Maps the ring if this is the first write since the last unmap, then copies at
        /// <c>mapped + frameBase + offsetBytes</c>.
        /// <para>
        /// LOCK-FREE ON THE HOT PATH. Recording is lock-free (decision W4) and this is the record path, so the
        /// only synchronised step is acquiring the mapping, which is a context call and happens once per ring per
        /// record phase. The copy itself takes nothing, because two writes into one mapped segment are two writes
        /// into plain memory.
        /// </para>
        /// <para>
        /// UNDER <see cref="D3D11RingMapScope.PerWrite"/> THE WHOLE WRITE IS SERIALIZED INSTEAD, because that
        /// scope unmaps at the end of every write and a mapping held with no lock is a mapping another thread can
        /// withdraw mid-copy. That scope exists only under <c>KE_D3D11_RECORD=immediate</c>, the M1 fallback
        /// lever, where every write already pays a map and an unmap, so the lock is the cheapest part of it. See
        /// <see cref="D3D11RingAllocator.WriteUnderPerWriteScope"/>.
        /// </para>
        /// <para>
        /// THE OFFSET IS AGAINST THE LOGICAL BUFFER and a write that runs past its end is refused. Without the
        /// check it would spill into the NEXT frame's segment, which is memory the GPU may be reading right now,
        /// and would present as another frame's uniforms being subtly wrong rather than as an error here.
        /// </para>
        /// <para>
        /// THE OFF-TIMELINE WRITE IS THE OTHER SHAPE AND IT REACHES EVERY SEGMENT. A device-level
        /// <see cref="D3D11RingAllocator.UpdateBuffer"/> is not a record-time write and does not come here: it
        /// copies every segment it can and leaves a PENDING PATCH on the ones an earlier frame is still reading,
        /// so a value written once persists for the buffer's life. This path stays current-segment only because
        /// every shipped record-time uniform write is unconditional per frame, and replicating those would be N
        /// memcpys for a value the next frame overwrites, on the one path the whole design exists to make cheap.
        /// </para>
        /// </summary>
        internal void Write(uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ValidateWriteRange(offsetBytes, data.Length);

            if (data.Length == 0) return;

            if (_allocator.MapScope == D3D11RingMapScope.PerWrite)
            {
                _allocator.WriteUnderPerWriteScope(this, offsetBytes, data);
                return;
            }

            _allocator.EnsureMapped(this);
            CopyInto(_pointer, CurrentFrameBaseBytes + offsetBytes, data);
        }

        /// <summary>
        /// REFUSE A WRITE THAT WOULD LEAVE THE LOGICAL BUFFER, which is the one bounds check both write shapes
        /// owe. Shared rather than duplicated because the off-timeline path writes every segment and would
        /// otherwise spill each of them into the next, turning one overrun into
        /// <see cref="FramesInFlight"/> of them.
        /// </summary>
        internal void ValidateWriteRange(uint offsetBytes, int length)
        {
            if ((ulong)offsetBytes + (ulong)length <= SizeInBytes) return;

            throw new ArgumentOutOfRangeException(nameof(offsetBytes), offsetBytes,
                $"A {length}-byte write at offset {offsetBytes} runs past the end of a {SizeInBytes}-byte "
                + "uniform buffer. On a ring-backed buffer that would spill into the next frame's segment, "
                + "which the GPU may be reading, so it is refused here rather than corrupting a frame.");
        }

        /// <summary>The copy alone, for the write-scoped path that holds the mapping, the copy and the unmap in
        /// one critical section. CALLED ONLY BY <see cref="D3D11RingAllocator"/>, under the submit lock and with
        /// the mapping in hand. Bounds are the caller's, the same as for the copy in
        /// <see cref="Write"/>.</summary>
        internal void CopyIntoCurrentSegmentUnderLock(uint offsetBytes, ReadOnlySpan<byte> data)
            => CopyIntoSegmentUnderLock(_allocator.CurrentSegment, offsetBytes, data);

        /// <summary>The same copy into ANY segment, which is what the off-timeline write walks. CALLED ONLY BY
        /// <see cref="D3D11RingAllocator"/>, under the submit lock, with the mapping in hand and with the target
        /// segment already known to be free of the GPU. Bounds are the caller's.</summary>
        internal void CopyIntoSegmentUnderLock(int segment, uint offsetBytes, ReadOnlySpan<byte> data)
            => CopyInto(_pointer, FrameBaseBytes(segment) + offsetBytes, data);

        /// <summary>Whether this ring owes any segment a deferred off-timeline write. This is what puts it in, and
        /// takes it out of, the allocator's patched-ring registry.</summary>
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
        /// patches it fully covered and therefore replaced. CALLED ONLY BY <see cref="D3D11RingAllocator"/>, under
        /// the submit lock. No mapping is needed and none is touched: the bytes go into managed memory and reach
        /// the segment at <see cref="ApplyPendingPatchesUnderLock"/>.
        /// </summary>
        internal int RecordPendingPatchUnderLock(int segment, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            _patches ??= new D3D11RingPendingPatches(FramesInFlight);
            return _patches.Record(segment, offsetBytes, data);
        }

        /// <summary>
        /// REPLAY ONE SEGMENT'S QUEUED WRITES INTO IT, oldest first, and forget them. Returns how many were
        /// applied. CALLED ONLY BY <see cref="D3D11RingAllocator"/>, under the submit lock, with the mapping in
        /// hand and immediately after the segment's fence gate has proved the GPU is done with it, which is the
        /// same proof a record-time write into that segment rests on.
        /// <para>
        /// OLDEST FIRST IS THE WHOLE ORDERING GUARANTEE. Two off-timeline writes to overlapping ranges resolve
        /// last-write-wins here exactly as two direct copies into mapped memory would.
        /// </para>
        /// </summary>
        internal int ApplyPendingPatchesUnderLock(int segment)
        {
            if (_patches is null) return 0;

            IReadOnlyList<D3D11RingPatch> patches = _patches.ForSegment(segment);
            if (patches.Count == 0) return 0;

            int applied = patches.Count;
            uint segmentBase = FrameBaseBytes(segment);
            for (int i = 0; i < patches.Count; i++)
                CopyInto(_pointer, segmentBase + patches[i].OffsetBytes, patches[i].Data);

            _patches.ClearSegment(segment);
            return applied;
        }

        /// <summary>Forget every queued write, for a ring whose buffer is going away, and return how many were
        /// dropped so the allocator's counters can reconcile them. CALLED ONLY BY
        /// <see cref="D3D11RingAllocator.Forget"/>, under the submit lock: a patch left behind would be replayed
        /// into a mapping that no longer exists.</summary>
        internal int DropPendingPatchesUnderLock() => _patches?.ClearAll() ?? 0;

        /// <summary>The 256-aligned stride one segment of a <paramref name="sizeInBytes"/> buffer occupies.
        /// Static because <see cref="D3D11Buffer"/> has to size the native buffer before a ring exists to ask.
        /// </summary>
        internal static uint SegmentStrideFor(uint sizeInBytes)
            => D3D11ConstantRange.AlignUpToOffsetBoundary(sizeInBytes);

        /// <summary>The whole allocation for a ring-backed buffer, which is what the native buffer is created
        /// with.</summary>
        internal static uint TotalBytesFor(uint sizeInBytes, int framesInFlight)
        {
            if (framesInFlight < 1)
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    "A ring needs at least one segment.");

            ulong total = (ulong)SegmentStrideFor(sizeInBytes) * (ulong)framesInFlight;
            if (total > uint.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    $"A {sizeInBytes}-byte uniform buffer across {framesInFlight} frame segments is {total} bytes, "
                    + "which is past what a Direct3D 11 buffer can be. Lower KE_D3D11_FRAMES_IN_FLIGHT or split "
                    + "the buffer.");

            return (uint)total;
        }

        /// <summary>
        /// Take the mapping. CALLED ONLY BY <see cref="D3D11RingAllocator"/>, under the device's submit lock and
        /// after it has re-checked <see cref="IsMapped"/>, because a <c>Map</c> is a context call and the context
        /// is not free-threaded.
        /// <para>
        /// The pointer is stored BEFORE the flag, deliberately: the flag is volatile, so setting it last is what
        /// publishes the pointer to a record thread reading the flag without the lock.
        /// </para>
        /// </summary>
        internal void MapUnderLock()
        {
            _pointer = _memory.MapWriteNoOverwrite();
            _mapped = true;
        }

        /// <summary>Release the mapping. Called only by <see cref="D3D11RingAllocator"/>, under the submit lock.
        /// The flag is cleared BEFORE the native call, so a concurrent record-thread write cannot see true and
        /// then copy into a pointer the runtime has already taken back.</summary>
        internal void UnmapUnderLock()
        {
            if (!_mapped) return;

            _mapped = false;
            _pointer = IntPtr.Zero;
            _memory.Unmap();
        }

        // The one raw-pointer write in the package, and the reason the project allows unsafe blocks. Everything
        // above it is arithmetic and everything below it is the driver's memory. Bounds are the caller's: Write
        // has already refused anything that would leave the frame's own segment.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static unsafe void CopyInto(IntPtr mapped, uint byteOffset, ReadOnlySpan<byte> data)
            => data.CopyTo(new Span<byte>((byte*)mapped + byteOffset, data.Length));
    }
}
