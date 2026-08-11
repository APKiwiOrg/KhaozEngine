using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DEVICE'S ONE RING ALLOCATOR (V-M5, V-M8, section 9.2): the frame segment every uniform ring writes into,
    /// the completion gate that decides when a segment may be reused, the off-timeline write's pending-patch queue,
    /// and the backpressure this row folds into MV3's accumulator.
    ///
    /// <para><b>SEGMENT ROTATION IS DEVICE-WIDE.</b> Frame N writes segment <c>N % FramesInFlight</c> in EVERY ring
    /// at once, so this type holds the index and each <see cref="VulkanUniformRing"/> multiplies it by its own
    /// stride. One index rather than one per buffer is what makes a frame's uniforms consistent. It is NOT the
    /// command list's pool slot, which advances per <c>Begin</c> and belongs to one list: one depth, two indexes
    /// (see <see cref="VulkanFramesInFlight"/>).</para>
    ///
    /// <para><b>THE GATE IS A COMPLETION READ AND NOTHING ELSE.</b> Before handing out a segment the allocator
    /// reads <see cref="VulkanTimeline.CompletedValue"/> and blocks while the GPU has not reached the value that
    /// segment's frame was closed at. A ring gated on a submit RECEIPT would recycle a segment the moment the CPU
    /// finished ASKING for the work rather than when the GPU finished doing it, which is a silent, intermittent
    /// corruption several frames from its cause. That dependency is why this row waits on row 5.</para>
    ///
    /// <para><b>AND THE OWNER VALUE IS <see cref="VulkanTimeline.LastSubmitted"/> READ UNDER THE SUBMIT LOCK, which
    /// is where this backend's mechanism differs from the other one's while the policy does not.</b> Direct3D 11
    /// has the submit path call back with the value it signalled. Here the timeline already carries the registered
    /// high-water, so closing a frame records it directly and no callback is needed. Two properties make that
    /// exact rather than approximate. It is read UNDER THE SUBMIT LOCK, and a submission allocates its value and
    /// registers it inside that same lock (row 7), so there is no submission in the window between the two: every
    /// submission ever made has either registered at or below this value or FAILED. And it is
    /// <see cref="VulkanTimeline.LastSubmitted"/> rather than <see cref="VulkanTimeline.LastAllocated"/>, which
    /// matters in exactly one direction: a submit that failed with a non-loss result took a value nothing will ever
    /// signal, so gating a segment on the allocation high-water would block that segment forever. The retire list
    /// gates on the allocation high-water instead, and that asymmetry is deliberate on both sides.</para>
    ///
    /// <para><b>A DEAD DEVICE RELEASES THE WAIT</b>, without this type knowing what a device is:
    /// <see cref="VulkanTimeline.CompletedValue"/> answers with everything ever allocated once liveness has
    /// flipped, so a segment wait during teardown finds its target already reached and returns.</para>
    ///
    /// <para><b>THE OFF-TIMELINE WRITE NEVER WAITS FOR ANYTHING</b>, which is what keeps
    /// <see cref="BeginFrame"/> the only member here that can block, and what makes a caller who already holds the
    /// submit lock LEGAL: the lock is a <see cref="Monitor"/>, so the acquisition inside is a free re-entry, and
    /// there is nothing inside that could wait for work only another thread could do.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE.</b> The timeline is behind
    /// <see cref="IVulkanTimelineSemaphore"/> and there is no other native surface at all: the writes are memcpys
    /// into memory row 6 mapped once. So the rotation, the gate, the deferral rule, the replay order and the
    /// counters all run under <c>dotnet test</c> on a machine with no Vulkan loader.</para>
    /// </summary>
    internal sealed class VulkanRingAllocator
    {
        readonly VulkanTimeline _timeline;
        readonly WaitAccumulator _backpressure;
        readonly object _submitLock;

        // The timeline value each segment's frame was CLOSED at, which is at or above every value a submission made
        // while that segment was current took and had accepted. Zero means the segment has never been closed with
        // anything submitted, which is the only case that needs no wait: the timeline's first value is 1, so zero
        // cannot be a real target.
        readonly ulong[] _segmentOwner;

        // Every ring currently owing at least one segment a deferred off-timeline write, so a frame boundary can
        // apply exactly those. A device may hold hundreds of rings and a walk over all of them at every boundary
        // would put an O(buffers) cost on the one path that has to stay cheap, for a list that is empty in most
        // programs and one entry long in the rest.
        readonly List<VulkanUniformRing> _patchedRings = new();

        ulong _frameIndex;

        // The segment the next submit binds. WRITTEN under the submit lock even though only the frame thread
        // advances it, because the off-timeline write reads it under that lock and the pair has to be exact. The
        // lock-free readers are CurrentSegment's record-path callers, whose stale read is the same window a
        // recording built across a frame boundary already has (V-W8).
        int _segment;

        int _stallCount;

        // The off-timeline write's deferrals, their replays, the ones a later write superseded and the ones that
        // went away with a disposed ring, cumulative since the device was created and deliberately NOT rolled per
        // frame. All four are mutated under the submit lock alone, and read volatile because a diagnostic may be on
        // any thread. See RingPatchStats.
        int _patchesDeferred;
        int _patchesApplied;
        int _patchesCoalesced;
        int _patchesDropped;

        /// <param name="framesInFlight">Segments per ring, from <see cref="VulkanFramesInFlight"/>. Resolved by the
        /// caller rather than read from the environment here, so the behaviour is testable without touching process
        /// state.</param>
        /// <param name="timeline">The device's one completion timeline. Read for the gate, waited on when the gate
        /// is not satisfied, and never signalled.</param>
        /// <param name="backpressure">The device's ONE backpressure accumulator, shared with the command list's
        /// slot wait rather than duplicated: both are the same statement about the same lever (MV3).</param>
        /// <param name="submitLock">The device's single submit lock. Not created here, because the same lock has to
        /// order <c>vkQueueSubmit</c> and the timeline value it signals.</param>
        internal VulkanRingAllocator(int framesInFlight, VulkanTimeline timeline, WaitAccumulator backpressure,
            object submitLock)
        {
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentNullException.ThrowIfNull(backpressure);
            ArgumentNullException.ThrowIfNull(submitLock);

            if (framesInFlight < VulkanFramesInFlight.Minimum || framesInFlight > VulkanFramesInFlight.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    $"A native Vulkan uniform ring runs between {VulkanFramesInFlight.Minimum} and "
                    + $"{VulkanFramesInFlight.Maximum} frame segments. {VulkanFramesInFlight.EnvVarName} clamps to "
                    + "that range before it gets here.");
            }

            _timeline = timeline;
            _backpressure = backpressure;
            _submitLock = submitLock;
            _segmentOwner = new ulong[framesInFlight];
            FramesInFlight = framesInFlight;
        }

        /// <summary>How many segments every ring in this device is cut into.</summary>
        internal int FramesInFlight { get; }

        /// <summary>The segment the next submit will bind, which is the one any recording in progress is writing.
        /// Deliberately not the segment the GPU is executing.</summary>
        internal int CurrentSegment => _segment;

        /// <summary>Frames begun since the device was created. <see cref="CurrentSegment"/> is this modulo
        /// <see cref="FramesInFlight"/>, and the wrap is the whole mechanism.</summary>
        internal ulong FrameIndex => _frameIndex;

        /// <summary>
        /// Segment acquisitions that ACTUALLY BLOCKED, since the device was created. The same entries are recorded
        /// into the device's BACKPRESSURE <see cref="WaitAccumulator"/>, which is what the seam reports, and this is
        /// the ring's own half of that fold so a test can say which of the two sources produced a count.
        /// </summary>
        internal int StallCount => Volatile.Read(ref _stallCount);

        /// <summary>The off-timeline write's deferrals and replays, cumulative since the device was created. A
        /// SEPARATE reading from the backpressure count on purpose: see <see cref="RingPatchStats"/>.
        /// </summary>
        internal RingPatchStats OffTimelinePatches => new(
            Volatile.Read(ref _patchesDeferred),
            Volatile.Read(ref _patchesApplied),
            Volatile.Read(ref _patchesCoalesced),
            Volatile.Read(ref _patchesDropped));

        /// <summary>The timeline value <paramref name="segment"/>'s frame was closed at, or 0 for a segment that
        /// has never been closed with anything submitted. Present so a test and a diagnostic can see the gate's
        /// input.</summary>
        internal ulong SegmentOwner(int segment) => _segmentOwner[segment];

        /// <summary>
        /// CLOSE THE FRAME JUST BUILT AND OPEN THE NEXT ONE: record what the outgoing segment has to be waited for,
        /// advance, block there if the GPU has not finished with it, replay anything queued for it, and publish it.
        /// The frame boundary, which on a windowed device is <c>Present</c>
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/527">row 17</see>).
        /// <para>
        /// CALL IT WITHOUT THE SUBMIT LOCK, AND A CALLER HOLDING IT IS REFUSED BY NAME. This is the one member here
        /// that can BLOCK: the gate waits until the GPU has finished with the segment being opened, which is up to
        /// a frame. Under the lock that would be a frame-long hold of the lock V-W8 caps at microseconds, and it
        /// would also shut out the submission that would end the wait.
        /// </para>
        /// <para>
        /// THE THREE STEPS ARE IN THIS ORDER FOR A REASON. The outgoing segment's owner is recorded FIRST, under
        /// the lock, so it names every submission that could have referenced it (see the class note on why
        /// <see cref="VulkanTimeline.LastSubmitted"/> read there is exact). The wait happens SECOND, outside the
        /// lock. The patch replay and the publish happen THIRD and in ONE hold, because an off-timeline write that
        /// observes the new segment as current copies into it directly, so observing that before the replay would
        /// have its bytes overwritten by older queued ones. Draining and publishing together leaves a concurrent
        /// writer only two orderings and both are correct.
        /// </para>
        /// </summary>
        internal void BeginFrame()
        {
            if (Monitor.IsEntered(_submitLock))
            {
                throw new InvalidOperationException(
                    "BeginFrame was called on the native Vulkan ring allocator while the caller held the submit "
                    + "lock. Opening a frame waits for the GPU to finish with the segment it opens, which is up to "
                    + "a frame, and decision V-W8 holds the submit lock for microseconds. Call it after the present "
                    + "has released the lock, not inside it.");
            }

            CloseCurrentSegmentUnderLock();

            _frameIndex++;
            int next = (int)(_frameIndex % (ulong)FramesInFlight);

            AcquireSegment(next);
            AdoptSegmentUnderLock(next);
        }

        /// <summary>
        /// THE DEVICE-LEVEL <c>UpdateBuffer</c> ON A RING-BACKED BUFFER (V-M8, section 9.2): the off-timeline
        /// write. No recording is required, one may be open, and the caller may be any thread. This is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484's correction, ADOPTED WHOLESALE rather than
        /// re-derived, because it cost a consumer defect to learn once.
        /// <para>
        /// IT WRITES EVERY SEGMENT, so a value written ONCE persists for the buffer's life exactly as the same call
        /// on the Veldrid leg persists it, where the buffer has one copy. Writing only the current segment was the
        /// shipped shape on the other backend for one release and it was a defect rather than a documentation
        /// problem: a load-time write reached one segment out of <see cref="FramesInFlight"/>, so two frames out of
        /// every three bound memory nothing had ever written, intermittently, with nothing thrown and nothing
        /// logged.
        /// </para>
        /// <para>
        /// A SEGMENT STILL IN FLIGHT IS PATCHED RATHER THAN WAITED FOR, AND THIS CALL NEVER BLOCKS. Writing a
        /// segment the GPU is reading is the data race the gate exists to prevent, so a segment whose owner value
        /// has not been reached does not receive the copy: the byte range and a private copy of the data are queued
        /// on the ring, and the <see cref="BeginFrame"/> that next opens that segment applies them, right after the
        /// gate has proved the GPU is done with it. The writer returns immediately, always, on every thread, at any
        /// pipeline depth. A RETRY LOOP WAITING FOR EVERY NON-CURRENT SEGMENT AT ONCE NEVER TERMINATES in the
        /// GPU-bound steady state.
        /// </para>
        /// <para>
        /// EVENTUAL CONSISTENCY IS THE GUARANTEE, and it is exactly what the Veldrid leg's persistence gives. When
        /// this returns, every segment either already holds the write or holds a pending patch its next acquire
        /// applies, so ANY segment BOUND after this call carries the value. The window in which an in-flight
        /// segment still holds the old bytes is unobservable through the seam, because that segment is not bound
        /// again until it has been acquired, and acquiring it applies the patch first.
        /// </para>
        /// <para>
        /// ONE COMPLETION POLL PER CALL AT MOST. The gate reads the timeline once and compares that one value
        /// against every segment's owner, since the timeline is monotonic and a second read inside one hold could
        /// only make more segments look free. A segment with a zero owner has never been closed with anything
        /// submitted and is skipped without a poll at all, so a load-time write, when every owner is zero, costs no
        /// completion read whatsoever.
        /// </para>
        /// <para>
        /// A SEGMENT THAT ALREADY CARRIES A PATCH TAKES THIS WRITE AS A PATCH TOO, even when its owner has since
        /// completed. That is what keeps arrival order intact: copying directly into a segment with an older patch
        /// still queued would let the frame boundary replay the OLDER bytes over the newer ones.
        /// </para>
        /// <para>
        /// THE CURRENT SEGMENT IS ALWAYS COPIED, deliberately, and it is the only ungated one. Gating it would
        /// change the documented semantic that the write lands when it is called and the next list submitted reads
        /// it, and deferring it would be worse still, since the current segment is bound by the very next submit
        /// and its next acquire is a whole wrap away.
        /// </para>
        /// </summary>
        internal void UpdateBuffer(VulkanUniformRing ring, ulong offsetBytes, ReadOnlySpan<byte> data)
        {
            ArgumentNullException.ThrowIfNull(ring);
            ring.ValidateWriteRange(offsetBytes, data.Length);

            if (data.Length == 0) return;

            lock (_submitLock)
            {
                bool wasPatched = ring.HasPendingPatches;

                ulong completed = 0;
                bool polled = false;

                for (int segment = 0; segment < _segmentOwner.Length; segment++)
                {
                    // _segment is read and written under this lock alone, so this comparison is exact rather than a
                    // benign stale read.
                    if (segment != _segment && DeferralIsOwed(ring, segment, ref completed, ref polled))
                    {
                        _patchesCoalesced += ring.RecordPendingPatchUnderLock(segment, offsetBytes, data);
                        _patchesDeferred++;
                        continue;
                    }

                    ring.CopyIntoSegmentUnderLock(segment, offsetBytes, data);
                }

                if (!wasPatched && ring.HasPendingPatches) _patchedRings.Add(ring);
            }
        }

        /// <summary>
        /// Drop <paramref name="ring"/>, for a ring-backed buffer being disposed.
        /// <para>
        /// ITS PENDING PATCHES GO WITH IT: a queued off-timeline write names memory that is about to stop existing,
        /// so a frame boundary replaying it after the buffer was released would write through a pointer into a
        /// freed chunk.
        /// </para>
        /// <para>
        /// AND THEY ARE COUNTED ON THE WAY OUT, into <see cref="RingPatchStats.Dropped"/>. Dropping them
        /// silently would leave them counted as deferred and never as resolved, so
        /// <see cref="RingPatchStats.Outstanding"/> would sit permanently high in any program that streams
        /// uniform buffers in and out, and the reading that number is FOR would be wrong for exactly the programs
        /// most likely to consult it.
        /// </para>
        /// </summary>
        internal void Forget(VulkanUniformRing ring)
        {
            ArgumentNullException.ThrowIfNull(ring);

            lock (_submitLock)
            {
                _patchesDropped += ring.DropPendingPatchesUnderLock();
                _patchedRings.Remove(ring);
            }
        }

        // Record what the OUTGOING segment has to be waited for before it is handed out again. Under the submit
        // lock, which is what makes LastSubmitted exact: a submission allocates and registers its value inside that
        // same lock, so there is no submission in flight between the two while this runs. See the class note.
        void CloseCurrentSegmentUnderLock()
        {
            lock (_submitLock)
            {
                ulong submitted = _timeline.LastSubmitted;
                if (submitted > _segmentOwner[_segment]) _segmentOwner[_segment] = submitted;
            }
        }

        // THE BACKPRESSURE WAIT, and MV3's second source. The poll first, then the block, and only the block is
        // recorded: a counter that ticks on a non-wait cannot answer "was anything ever blocked" with a zero.
        //
        // It blocks on vkWaitSemaphores rather than spinning, unlike the other backend's ring, and the difference
        // is the primitive rather than a preference. There a completion poll on the fallback mechanism re-enters
        // the submit lock, so a sleeping wait would shut out the submission that would end it. Here the wait holds
        // no lock at all and the semaphore has a real blocking form, so burning a core to avoid a syscall would
        // trade the one resource a stalled frame still needs.
        void AcquireSegment(int segment)
        {
            ulong target = _segmentOwner[segment];
            if (target == 0) return;

            // A dead device answers its own last allocated value here, which is at or above anything a segment can
            // hold, so this returns without waiting rather than blocking on a counter nothing can advance.
            if (_timeline.CompletedValue >= target) return;

            long start = Stopwatch.GetTimestamp();
            _timeline.WaitForValue(target);
            long elapsed = Stopwatch.GetTimestamp() - start;

            Interlocked.Increment(ref _stallCount);
            _backpressure.Record(elapsed);
        }

        // Whether one non-current segment has to take this write as a patch rather than as a copy. Called under the
        // submit lock, once per segment, sharing ONE completion read across the whole pass.
        //
        // TWO REASONS, AND ORDER IS THE SECOND ONE. A segment whose owner value has not been reached is still being
        // read by the GPU. A segment that already has a patch queued takes this write as a patch too even though it
        // is free, because a direct copy would be overwritten by the older queued bytes when the boundary replays
        // them.
        bool DeferralIsOwed(VulkanUniformRing ring, int segment, ref ulong completed, ref bool polled)
        {
            if (ring.HasPendingPatchesFor(segment)) return true;

            ulong owner = _segmentOwner[segment];
            if (owner == 0) return false;

            if (!polled)
            {
                completed = _timeline.CompletedValue;
                polled = true;
            }

            return completed < owner;
        }

        // Apply the segment's pending patches and THEN publish it as current, in ONE hold. See BeginFrame's remarks
        // for why the two steps are one critical section. It walks the patched rings alone, not every ring in the
        // device, and a ring leaves that registry as soon as it owes nothing anywhere.
        void AdoptSegmentUnderLock(int segment)
        {
            lock (_submitLock)
            {
                for (int i = _patchedRings.Count - 1; i >= 0; i--)
                {
                    VulkanUniformRing ring = _patchedRings[i];
                    if (!ring.HasPendingPatchesFor(segment)) continue;

                    _patchesApplied += ring.ApplyPendingPatchesUnderLock(segment);

                    if (!ring.HasPendingPatches) _patchedRings.RemoveAt(i);
                }

                _segment = segment;
            }
        }
    }
}
