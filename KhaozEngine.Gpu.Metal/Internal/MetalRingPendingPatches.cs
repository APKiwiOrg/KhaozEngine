using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE DEFERRED OFF-TIMELINE WRITE: the byte range it covers and a private copy of what it wrote. Recorded by
    /// <see cref="MetalRingAllocator.UpdateBuffer"/> for a segment the GPU has not finished with, and applied by
    /// the <see cref="MetalRingAllocator.BeginRecording"/> that next claims that segment.
    /// <para>
    /// THE DATA IS COPIED RATHER THAN REFERENCED, and there is no way around it. The caller hands in a
    /// <c>ReadOnlySpan&lt;byte&gt;</c>, which may be a <c>stackalloc</c> or a pooled array the caller reuses the
    /// instant the write returns, and the patch outlives the call by up to <c>FramesInFlight</c> recordings.
    /// </para>
    /// </summary>
    internal readonly struct MetalRingPatch
    {
        internal MetalRingPatch(uint offsetBytes, byte[] data)
        {
            OffsetBytes = offsetBytes;
            Data = data;
        }

        /// <summary>Where in the LOGICAL buffer this write started. The segment base is added when it is applied,
        /// so one patch is not tied to the segment it was recorded for by anything but its list.</summary>
        internal uint OffsetBytes { get; }

        /// <summary>What was written, copied out of the caller's span at record time.</summary>
        internal byte[] Data { get; }

        /// <summary>
        /// Whether this patch's range FULLY covers <paramref name="other"/>'s, which is the one case where
        /// dropping the older entry changes nothing: a later write that spans an earlier one entirely overwrites
        /// every byte of it, so replaying both and replaying only this one leave identical memory. A partial
        /// overlap is NOT coalesced, because there the older bytes outside the newer range still have to land.
        /// </summary>
        internal bool Covers(in MetalRingPatch other)
            => OffsetBytes <= other.OffsetBytes
                && (ulong)OffsetBytes + (ulong)Data.Length
                    >= (ulong)other.OffsetBytes + (ulong)other.Data.Length;
    }

    /// <summary>
    /// THE PENDING PATCHES OF ONE RING, per segment and in arrival order. This is what an off-timeline write
    /// leaves behind INSTEAD OF WAITING, and it is the whole of the mechanism M-M5 adopts wholesale from
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/484 rather than re-deriving. Third implementation, third
    /// time unchanged.
    /// <para>
    /// WHY NOT A RETRY LOOP, which is the shape that gets drafted first and never terminates. Waiting for every
    /// non-current segment to come free AT ONCE is unsatisfiable in the GPU-bound steady state, because the frame
    /// thread submits again for every frame the GPU retires, so at least one non-current segment is always in
    /// flight and the writer chases a pipeline it can never catch. A deferral has no such failure mode: the thing
    /// it waits for is a frame boundary that is going to happen anyway.
    /// </para>
    /// <para>
    /// ARRIVAL ORDER IS THE POINT. The list is replayed front to back, so two off-timeline writes to overlapping
    /// ranges resolve LAST-WRITE-WINS exactly as two direct copies into <c>contents()</c> would. Sorting, merging
    /// or de-duplicating by range would quietly reorder them, and a uniform buffer whose two halves came from
    /// different calls is precisely the class of bug the ring makes invisible.
    /// </para>
    /// <para>
    /// THE STORAGE IS BOUNDED BY COALESCING PLUS THE SHAPE OF THE CALLER. A new patch drops every earlier one it
    /// fully covers, so the repeated case (one caller rewriting the same range off-timeline) stays at one entry
    /// per segment forever rather than growing without limit. Beyond that the bound is the workload's: an
    /// off-timeline uniform write is a LOAD-TIME or settings-change write by construction, because the per-frame
    /// ones go through the record-time path, which touches the current segment alone.
    /// </para>
    /// <para>
    /// NOT THREAD-SAFE, and it does not need to be. Every mutation and every read happens under the device's
    /// submit lock, on both the recording side (<see cref="MetalRingAllocator.UpdateBuffer"/>) and the applying
    /// side (<see cref="MetalRingAllocator.BeginRecording"/>).
    /// </para>
    /// </summary>
    internal sealed class MetalRingPendingPatches
    {
        readonly List<MetalRingPatch>[] _bySegment;
        int _pending;

        internal MetalRingPendingPatches(int framesInFlight)
        {
            _bySegment = new List<MetalRingPatch>[framesInFlight];
            for (int i = 0; i < framesInFlight; i++) _bySegment[i] = new List<MetalRingPatch>();
        }

        /// <summary>Whether nothing at all is outstanding, which is what takes a ring back out of the allocator's
        /// patched-ring registry.</summary>
        internal bool IsEmpty => _pending == 0;

        /// <summary>How many patches are outstanding across every segment. A diagnostic number and the thing a
        /// test asserts the coalescing rule against.</summary>
        internal int PendingCount => _pending;

        /// <summary>How many are outstanding for one segment.</summary>
        internal int CountFor(int segment) => _bySegment[segment].Count;

        /// <summary>Whether one segment has anything outstanding. The off-timeline write asks this before it
        /// copies directly: a segment with a patch queued takes the queue for every later write too, so the two
        /// cannot land out of order.</summary>
        internal bool HasAnyFor(int segment) => _bySegment[segment].Count > 0;

        /// <summary>The outstanding patches for one segment, oldest first, which is the order they are applied
        /// in.</summary>
        internal IReadOnlyList<MetalRingPatch> ForSegment(int segment) => _bySegment[segment];

        /// <summary>
        /// Queue a write for <paramref name="segment"/>, dropping every earlier patch it fully covers, and return
        /// how many were dropped so the allocator's counters can report the coalescing rather than losing it. The
        /// scan runs back to front, so an entry removed cannot shift one that has not been looked at yet.
        /// </summary>
        internal int Record(int segment, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            var patch = new MetalRingPatch(offsetBytes, data.ToArray());
            List<MetalRingPatch> list = _bySegment[segment];

            int coalesced = 0;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!patch.Covers(list[i])) continue;

                list.RemoveAt(i);
                _pending--;
                coalesced++;
            }

            list.Add(patch);
            _pending++;
            return coalesced;
        }

        /// <summary>Forget one segment's patches, which is what applying them ends with.</summary>
        internal void ClearSegment(int segment)
        {
            _pending -= _bySegment[segment].Count;
            _bySegment[segment].Clear();
        }

        /// <summary>
        /// Forget everything, for a ring whose buffer is being disposed, and return how many went with it. The
        /// patches name memory that is about to stop existing, so carrying them would be a write through a
        /// released <c>contents()</c> pointer at the next frame boundary.
        /// <para>
        /// THE COUNT IS RETURNED RATHER THAN DISCARDED because these patches were deferred and will never be
        /// replayed, so an allocator that dropped them silently would leave them counted as outstanding for the
        /// life of the device. See <see cref="MetalRingPatchStats.Dropped"/>.
        /// </para>
        /// </summary>
        internal int ClearAll()
        {
            int dropped = _pending;
            for (int i = 0; i < _bySegment.Length; i++) _bySegment[i].Clear();
            _pending = 0;
            return dropped;
        }
    }
}
