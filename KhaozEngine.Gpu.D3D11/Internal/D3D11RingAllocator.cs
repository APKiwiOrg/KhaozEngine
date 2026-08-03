using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>How long a ring keeps its mapping, which is the one thing the two recording drivers force apart.
    /// </summary>
    internal enum D3D11RingMapScope
    {
        /// <summary>The deferred driver's scope (decisions R1 and U2): the first write of a record phase maps, and
        /// the start of the next <c>Submit</c> unmaps. Two native calls per ring per submit, which is the floor,
        /// and it is legal only because no draw happens while the recording is being built.</summary>
        AcrossRecording = 0,

        /// <summary>
        /// The immediate driver's scope (decision R2, and the degradation section 2.1 names): the ring is mapped
        /// for the duration of ONE write and unmapped before that write returns.
        /// <para>
        /// It has to degrade, because under <c>KE_D3D11_RECORD=immediate</c> draws are issued as the seam is
        /// called, and Direct3D 11 forbids a mapped resource being bound to the pipeline. The spec's phrasing for
        /// the degradation is per-FLUSH map and unmap, which is coarser than this and strictly better: it needs a
        /// flush point to hang the unmap on, and the flush point is the bind flush of work-breakdown row 9, which
        /// does not exist yet. Write-scoped is the shape that is correct with no cooperation from any other row,
        /// and <see cref="D3D11RingAllocator.UnmapMappedRings"/> is the one call row 9 needs to batch it up to
        /// per-flush once it owns a draw path.
        /// </para>
        /// </summary>
        PerWrite = 1,
    }

    /// <summary>
    /// THE RING ALLOCATOR (decisions U1, U2 and U5): one per device, owning the frame segment every ring writes
    /// into, the fence gate that decides when a segment may be reused, the mapped-ring registry the next
    /// <c>Submit</c> unmaps, and the M3 backpressure counters.
    /// <para>
    /// SEGMENT ROTATION IS DEVICE-WIDE. Frame N writes segment <c>N % FramesInFlight</c> in EVERY ring at once,
    /// so this type holds the segment index and each <see cref="D3D11UniformRing"/> multiplies it by its own
    /// stride. One index rather than one per buffer is what makes a frame's uniforms consistent: a bind computes
    /// its first constant from the same segment every write in that frame went to.
    /// </para>
    /// <para>
    /// THE GATE IS A COMPLETION READ AND NOTHING ELSE (decision U5, and the dependency work-breakdown row 8 waited
    /// on row 13a for). Before handing out a segment, the allocator reads the completion value the submission that
    /// last used it was signalled under, and blocks while the GPU has not reached it. A ring built against a submit
    /// RECEIPT instead would recycle a segment the GPU is still reading and corrupt a frame silently, which is
    /// exactly what Veldrid's Direct3D 11 fence would have given it.
    /// </para>
    /// <para>
    /// A DEAD DEVICE RELEASES THE WAIT, without this type knowing what a device is.
    /// <see cref="D3D11FenceSubsystem.CompletedValue"/> answers with everything it ever issued once the liveness
    /// latch has flipped, so a segment wait during teardown finds its target already reached and returns.
    /// </para>
    /// <para>
    /// LOCKING. The device's single submit lock (decision W4) covers the map and the unmap, because both are
    /// immediate-context calls, and it is held for the CALL rather than for a frame. The copy is not covered at
    /// all, which is what keeps recording lock-free: acquiring the mapping is once per ring per record phase and
    /// writing into it is thousands of times. The off-timeline path (section 6.4) takes the same lock around the
    /// whole write, so it cannot land in the middle of a replay.
    /// </para>
    /// <para>
    /// AND THE CASE THAT LOCKING DOES NOT COVER, stated rather than papered over. A record-time write does not
    /// take the submit lock, so a recording running on ANOTHER thread while a submit unmaps could have its mapping
    /// withdrawn mid-write. That is decision W5's territory: concurrent recording is structurally permitted and
    /// neither exercised nor supported in v1, where one thread records and submits. The unmap clears the flag
    /// before it releases the mapping, which narrows the window and does not close it, and closing it would mean
    /// taking the submit lock on every uniform write.
    /// </para>
    /// <para>
    /// Not thread-safe for its own counters, the same contract <see cref="D3D11FenceSubsystem"/> and
    /// <c>RetiredResourcePool</c> already have: they are driven from the frame thread.
    /// </para>
    /// </summary>
    internal sealed class D3D11RingAllocator
    {
        readonly ID3D11CompletionRead _completion;
        readonly object _submitLock;

        // The completion value the last submission that used each segment was signalled under. Zero means the
        // segment has never been submitted with, which is the only case that needs no wait: the timeline's first
        // value is 1, so zero cannot be a real target.
        readonly ulong[] _segmentOwner;

        // Every ring currently holding a mapping, so the next submit can unmap exactly those. A list rather than
        // a walk over every buffer in the device: a frame touches a handful of uniform buffers and the device may
        // hold hundreds, and the unmap has to be O(mapped) for "two native calls per ring per submit" to be the
        // floor rather than the average.
        readonly List<D3D11UniformRing> _mappedRings = new();

        ulong _frameIndex;
        int _segment;

        int _stallCount;
        long _stallTicks;
        D3D11BackpressureStats _lastFrame;

        /// <summary>
        /// Build the allocator for one device.
        /// </summary>
        /// <param name="framesInFlight">Segments per ring, from <see cref="D3D11FramesInFlight"/>. Resolved by the
        /// caller rather than read from the environment here, so the behaviour is testable without touching
        /// process state.</param>
        /// <param name="completion">The device's completion timeline, read half only. Never a submit
        /// receipt.</param>
        /// <param name="submitLock">The device's single submit lock (decision W4). Not created here, because the
        /// same lock has to cover replay, present and the resize apply.</param>
        /// <param name="mapScope">How long a mapping is held, which follows the recording driver. See
        /// <see cref="MapScopeFor"/>.</param>
        internal D3D11RingAllocator(int framesInFlight, ID3D11CompletionRead completion, object submitLock,
            D3D11RingMapScope mapScope = D3D11RingMapScope.AcrossRecording)
        {
            if (framesInFlight < D3D11FramesInFlight.Minimum || framesInFlight > D3D11FramesInFlight.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    $"A uniform ring runs between {D3D11FramesInFlight.Minimum} and {D3D11FramesInFlight.Maximum} "
                    + $"frame segments. {D3D11FramesInFlight.EnvVarName} clamps to that range before it gets here.");
            }

            _completion = completion ?? throw new ArgumentNullException(nameof(completion));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _segmentOwner = new ulong[framesInFlight];
            FramesInFlight = framesInFlight;
            MapScope = mapScope;
        }

        /// <summary>How many segments every ring in this device is cut into.</summary>
        internal int FramesInFlight { get; }

        /// <summary>How long a mapping is held, which is the one place the two recording drivers differ about the
        /// ring.</summary>
        internal D3D11RingMapScope MapScope { get; }

        /// <summary>The segment the next submit will bind, which is the one any recording in progress is writing.
        /// Deliberately not the segment the GPU is executing (section 6.4).</summary>
        internal int CurrentSegment => _segment;

        /// <summary>Frames begun since the device was created. <see cref="CurrentSegment"/> is this modulo
        /// <see cref="FramesInFlight"/>, and the wrap is the whole mechanism.</summary>
        internal ulong FrameIndex => _frameIndex;

        /// <summary>How many rings currently hold a mapping, which is how many unmaps the next submit owes.
        /// </summary>
        internal int MappedRingCount => _mappedRings.Count;

        /// <summary>The backpressure of the frame that has ENDED. Rolled by <see cref="BeginFrame"/>. This is the
        /// M3 measurement.</summary>
        internal D3D11BackpressureStats LastFrameBackpressure => _lastFrame;

        /// <summary>The completion value the last submission that used <paramref name="segment"/> was signalled
        /// under, or 0 for a segment nothing has been submitted with. Present so a test and a diagnostic can see
        /// the gate's input.</summary>
        internal ulong SegmentOwner(int segment) => _segmentOwner[segment];

        /// <summary>Which mapping scope a recording driver needs. The deferred driver keeps the mapping for the
        /// record phase, and the immediate one cannot, because it issues draws while the phase is open.</summary>
        internal static D3D11RingMapScope MapScopeFor(D3D11RecordMode mode)
            => mode == D3D11RecordMode.Immediate ? D3D11RingMapScope.PerWrite : D3D11RingMapScope.AcrossRecording;

        /// <summary>
        /// CLOSE THE FRAME JUST BUILT AND OPEN THE NEXT ONE: roll the backpressure counters, advance to the next
        /// segment, and wait there if the GPU has not finished with it. Called once per frame from the device's
        /// present, the same boundary <see cref="D3D11FenceSubsystem.BeginFrame"/> uses.
        /// <para>
        /// The roll happens BEFORE the wait, so a stall paid on the way into a frame is reported as that frame's
        /// cost rather than the previous one's. That is the reading a soak wants: the number answers "what did
        /// this frame pay", and a frame pays for the segment it starts on.
        /// </para>
        /// </summary>
        internal void BeginFrame()
        {
            _lastFrame = new D3D11BackpressureStats(_stallCount, _stallTicks * 1000d / Stopwatch.Frequency);
            _stallCount = 0;
            _stallTicks = 0L;

            _frameIndex++;
            int next = (int)(_frameIndex % (ulong)FramesInFlight);
            AcquireSegment(next);
            _segment = next;
        }

        /// <summary>
        /// RECORD WHICH SUBMISSION THE CURRENT SEGMENT WAS LAST USED BY, from the value that submission signalled.
        /// Called by the submit path right after the end-of-replay signal, inside the submit lock.
        /// <para>
        /// This is the other half of the gate. Without it a segment carries no target, so it is handed back out
        /// with no wait and the ring behaves exactly like the corruption U5 exists to prevent. A submit that
        /// signalled nothing (value 0) records nothing, which is why the drivers refuse a ring allocator handed to
        /// a submit with no signal sink.
        /// </para>
        /// <para>
        /// The value is monotonic, so the last submission of a frame is the highest, and taking the maximum keeps
        /// that true even if a caller records them out of order.
        /// </para>
        /// </summary>
        internal void OnSubmitted(ulong completionValue)
        {
            if (completionValue == 0) return;
            if (completionValue > _segmentOwner[_segment]) _segmentOwner[_segment] = completionValue;
        }

        /// <summary>
        /// UNMAP EVERY RING THAT HOLDS A MAPPING. The start of a <c>Submit</c> calls it under the deferred driver
        /// (decision U2), because a mapped resource cannot be bound to the pipeline and the replay is about to
        /// bind them all.
        /// <para>
        /// It is also the call the bind flush of work-breakdown row 9 needs if it wants the immediate driver's
        /// per-FLUSH mapping rather than the per-write mapping <see cref="D3D11RingMapScope.PerWrite"/> ships
        /// with: unmap here at each flush point, and leave the scope at
        /// <see cref="D3D11RingMapScope.AcrossRecording"/>.
        /// </para>
        /// <para>
        /// Idempotent, and an empty registry costs one uncontended lock. The registry is read INSIDE the lock
        /// rather than short-circuiting outside it: an empty count read without the lock can be stale by the time
        /// it is acted on, and being wrong in that direction means a ring stays mapped through the replay that is
        /// about to bind it.
        /// </para>
        /// </summary>
        internal void UnmapMappedRings()
        {
            lock (_submitLock)
            {
                for (int i = 0; i < _mappedRings.Count; i++) _mappedRings[i].UnmapUnderLock();
                _mappedRings.Clear();
            }
        }

        /// <summary>
        /// THE DEVICE-LEVEL <c>UpdateBuffer</c> ON A RING-BACKED BUFFER (decision U5, section 6.4), which is the
        /// off-timeline write: no recording is required, one may be open, and the caller may be any thread.
        /// <para>
        /// IT WRITES THE CURRENT SEGMENT, meaning the one the next <c>Submit</c> will bind and the one any open
        /// recording is already writing, deliberately NOT the segment the GPU is executing. That is what preserves
        /// the documented semantic: the write lands when it is called, and a later-submitted list reads what the
        /// CPU wrote most recently.
        /// </para>
        /// <para>
        /// IT MAPS IDEMPOTENTLY. The ring is unmapped at the start of every submit, so a write arriving between
        /// two frames finds it unmapped, maps <c>NO_OVERWRITE</c>, writes, and under the deferred driver leaves it
        /// mapped for the next record phase to reuse. There is no refcount, just the one flag, checked under the
        /// same lock as the write.
        /// </para>
        /// <para>
        /// THE LOCK IS SHORT AND SCOPED TO THE WRITE, never to a frame. It is the submit lock, so an off-timeline
        /// write cannot land in the middle of a replay, and it is released the moment the copy is done.
        /// </para>
        /// <para>
        /// WHAT IS STILL FORBIDDEN, restated because the ring makes it quieter rather than because it changed:
        /// writing off-timeline to a range a recording has ALREADY recorded a bind for, and then expecting that
        /// recorded bind to see the old value. It never worked, the seam already documents that the CPU runs
        /// several frames ahead of the GPU, and under the ring the stale read is a plausible-looking value from
        /// this frame's segment rather than an obvious one.
        /// </para>
        /// </summary>
        internal void UpdateBuffer(D3D11UniformRing ring, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ArgumentNullException.ThrowIfNull(ring);

            lock (_submitLock) ring.Write(offsetBytes, data);
        }

        /// <summary>
        /// Give <paramref name="ring"/> a mapping if it does not have one. Called by the ring's own write path,
        /// which is why the fast path is a volatile flag read there and the lock is only reached once per ring per
        /// record phase.
        /// <para>
        /// Double-checked under the submit lock, because a map is a context call and the immediate context is not
        /// free-threaded. The second check is not defensive noise: two threads writing two different offsets of a
        /// ring that has just been unmapped both see it unmapped, and mapping twice would leak a mapping the
        /// runtime never gets back.
        /// </para>
        /// </summary>
        internal void EnsureMapped(D3D11UniformRing ring)
        {
            if (ring.IsMapped) return;

            lock (_submitLock)
            {
                if (ring.IsMapped) return;

                ring.MapUnderLock();
                _mappedRings.Add(ring);
            }
        }

        /// <summary>
        /// What happens to the mapping when a write finishes, which is nothing at all under the deferred driver
        /// and an immediate unmap under <see cref="D3D11RingMapScope.PerWrite"/>. Called by the ring's write path
        /// so the scope decision lives in one place rather than at every write site.
        /// </summary>
        internal void AfterWrite(D3D11UniformRing ring)
        {
            if (MapScope != D3D11RingMapScope.PerWrite || !ring.IsMapped) return;

            lock (_submitLock)
            {
                ring.UnmapUnderLock();
                _mappedRings.Remove(ring);
            }
        }

        /// <summary>
        /// Drop <paramref name="ring"/>, unmapping it first if it is mapped. Called when a ring-backed buffer is
        /// disposed, because releasing a mapped resource leaves the runtime holding a pointer into memory nobody
        /// owns, and because a disposed ring left in the registry would be unmapped again at the next submit.
        /// </summary>
        internal void Forget(D3D11UniformRing ring)
        {
            ArgumentNullException.ThrowIfNull(ring);

            lock (_submitLock)
            {
                ring.UnmapUnderLock();
                _mappedRings.Remove(ring);
            }
        }

        /// <summary>
        /// THE BACKPRESSURE WAIT, and the M3 measurement's source. Reads the completion value the segment's last
        /// owner was submitted under and blocks while the GPU has not reached it, counting the wait and its
        /// duration.
        /// <para>
        /// THE COMMON CASE COSTS ONE POLL AND COUNTS NOTHING. With three segments the GPU is normally two frames
        /// behind at worst, so the target is already reached and this returns without touching the counters. A
        /// non-zero count is the whole signal: it means the pipeline is deeper than the segment count allows on
        /// that machine, and <c>KE_D3D11_FRAMES_IN_FLIGHT</c> is the lever rather than the design being wrong.
        /// </para>
        /// <para>
        /// IT SPINS RATHER THAN SLEEPING, and never escalates to a millisecond. The plain
        /// <see cref="SpinWait.SpinOnce()"/> starts sleeping one millisecond after 20 iterations, which at
        /// Windows' default timer resolution is longer than the frame this wait is inside. The same reasoning the
        /// fence drain is written against (see <see cref="D3D11FenceSubsystem.WaitForIdle"/>), for a wait that is
        /// expected never to happen at all.
        /// </para>
        /// <para>
        /// The blocking wait the primary timeline offers is deliberately NOT used here. It belongs to the drain
        /// alone, it is the one member callable without the submit lock, and a segment wait is meant to be so rare
        /// that arming an event for it would cost more than the spin it replaces.
        /// </para>
        /// </summary>
        void AcquireSegment(int segment)
        {
            ulong target = _segmentOwner[segment];
            if (target == 0) return;
            if (_completion.CompletedValue >= target) return;

            long start = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            while (_completion.CompletedValue < target) spin.SpinOnce(sleep1Threshold: -1);

            _stallCount++;
            _stallTicks += Stopwatch.GetTimestamp() - start;
        }
    }
}
