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
        /// The write-scoped fallback: the ring is mapped for the duration of ONE write and unmapped before that
        /// write returns, with the map, the copy and the unmap serialized under the submit lock as one critical
        /// section.
        /// <para>
        /// NO DRIVER SELECTS THIS ANY MORE, and the reason it existed is worth keeping. Under
        /// <c>KE_D3D11_RECORD=immediate</c> draws are issued as the seam is called, and Direct3D 11 does not
        /// permit a draw against a mapped resource, so the immediate driver needs the mapping released before
        /// every command. The spec's phrasing for that degradation is per-FLUSH map and unmap, which needs a flush
        /// point to hang the unmap on, and when this enum shipped the flush point (work-breakdown row 9) did not
        /// exist. Write-scoped was the shape that was correct with no cooperation from any other row. Row 9 built
        /// the flush point, so <see cref="D3D11RingAllocator.MapScopeFor"/> now answers
        /// <see cref="AcrossRecording"/> for both drivers and <see cref="D3D11BindFlush"/> unmaps at every DRAW
        /// and every DISPATCH. Not at a pipeline switch: the hazard is a draw against a mapped resource, the
        /// switch's drain only BINDS constant buffers, and the next draw unmaps before it issues.
        /// </para>
        /// <para>
        /// KEPT RATHER THAN DELETED because it is the only shape that holds the map, the copy and the unmap
        /// ATOMICALLY, which is a property no other scope has: everywhere else a mapping is held with no lock, and
        /// the reason that is safe is decision W5's one-thread rule rather than anything structural. A path that
        /// one day needs a ring write to be safe against a concurrent unmap wants this, and it is constructible,
        /// tested and one constructor argument away.
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
    /// LOCKING. The package README's "Threading: the shipped contract" section is the authoritative statement of
    /// decision W4 and the W5 boundary, and what follows is this type's share of it rather than a second copy.
    /// The device's single submit lock covers the map and the unmap, because both are
    /// immediate-context calls, and it is held for the CALL rather than for a frame. Under
    /// <see cref="D3D11RingMapScope.AcrossRecording"/> the copy is not covered at all, which is what keeps
    /// recording lock-free: acquiring the mapping is once per ring per record phase and writing into it is
    /// thousands of times. Under <see cref="D3D11RingMapScope.PerWrite"/> the map, the copy and the unmap are one
    /// critical section (see <see cref="WriteUnderPerWriteScope"/>). The off-timeline path (section 6.4) takes the
    /// same lock around the whole write, so it cannot land in the middle of a replay, and the submit path holds it
    /// from its unmap through its replay.
    /// </para>
    /// <para>
    /// AND WHAT THE LOCKING DOES NOT COVER, named combination by combination rather than papered over, because
    /// "not thread-safe" is too coarse to act on. SERIALIZED, on both scopes: two device-level
    /// <see cref="UpdateBuffer"/> calls from any threads, a device-level write against a submit's unmap, replay
    /// and signal, a device-level write against another ring's map, and a disposal (<see cref="Forget"/>) against
    /// any of them. Also serialized under <see cref="D3D11RingMapScope.PerWrite"/>: a record-time write against a
    /// device-level write, and a record-time write against a submit, since that scope's whole write is inside the
    /// lock. OUTSIDE THE v1 CONTRACT, and that is decision W5's territory: under
    /// <see cref="D3D11RingMapScope.AcrossRecording"/>, a record-time write concurrent with a submit's unmap or
    /// with a device-level write, and two record-time writes to one ring on two threads. Concurrent RECORDING is
    /// structurally permitted and neither exercised nor supported in v1, where one thread records and submits.
    /// The unmap clears the flag before it releases the mapping, which narrows the record-time window and does not
    /// close it, and closing it would mean taking the submit lock on every uniform write of a deferred recording,
    /// which is the cost the whole design exists to avoid.
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

        /// <summary>
        /// WHICH MAPPING SCOPE A RECORDING DRIVER NEEDS, and the answer is now the same for both.
        /// <para>
        /// The deferred driver keeps the mapping for the whole record phase and the next <c>Submit</c> releases
        /// it, which is two native calls per ring per submit and the floor. The immediate driver issues draws
        /// while the phase is open, so it needs the mapping released before every command, which the spec calls a
        /// per-FLUSH map and unmap. Row 9's <see cref="D3D11BindFlush"/> is that flush point, and it calls
        /// <see cref="UnmapMappedRings"/> before every draw and every dispatch. Not at a pipeline switch: the
        /// hazard is a draw against a mapped resource, the switch's drain only binds constant buffers, and the
        /// next draw unmaps before it issues. That gives the immediate driver one map per run of writes between
        /// two commands instead of one per write.
        /// </para>
        /// <para>
        /// THE MEASUREMENT IS WHY IT MATTERS RATHER THAN THE CALL COUNT. Milestone M1 A/Bs the two drivers on a
        /// real frame and DELETES the loser, so a ring that maps and unmaps per uniform write on one arm and once
        /// per submit on the other is not measuring the recording model, it is measuring a handicap. Per-flush is
        /// the degradation the spec names for exactly that reason.
        /// </para>
        /// <para>
        /// WHAT IT COSTS is the atomicity <see cref="D3D11RingMapScope.PerWrite"/> had: under
        /// <see cref="D3D11RingMapScope.AcrossRecording"/> the copy runs with no lock, so a device-level
        /// <see cref="UpdateBuffer"/> arriving from another thread mid-copy is outside the contract instead of
        /// serialized. That is the exposure the deferred driver has always had, and decision W5 is what covers
        /// both: concurrent recording is structurally permitted, neither exercised nor supported in v1, and one
        /// thread records at a time.
        /// </para>
        /// <para>
        /// <paramref name="mode"/> no longer changes the answer and the method still takes it, on purpose. The
        /// call site reads as the question it is asking, the day a third driver needs a different scope there is
        /// one place to put it, and a test that asserts BOTH modes answer the same thing is what would notice if
        /// the immediate arm quietly went back to per-write while <see cref="D3D11BindFlush"/> was still unmapping
        /// for it.
        /// </para>
        /// </summary>
        internal static D3D11RingMapScope MapScopeFor(D3D11RecordMode mode) => D3D11RingMapScope.AcrossRecording;

        /// <summary>
        /// CLOSE THE FRAME JUST BUILT AND OPEN THE NEXT ONE: roll the backpressure counters, advance to the next
        /// segment, and wait there if the GPU has not finished with it. Called once per frame from the device's
        /// present, the same boundary <see cref="D3D11FenceSubsystem.BeginFrame"/> uses.
        /// <para>
        /// The roll happens BEFORE the wait, so a stall paid on the way into a frame is reported as that frame's
        /// cost rather than the previous one's. That is the reading a soak wants: the number answers "what did
        /// this frame pay", and a frame pays for the segment it starts on.
        /// </para>
        /// <para>
        /// CALL IT WITHOUT THE SUBMIT LOCK, AND A CALLER HOLDING IT IS REFUSED BY NAME (decision W4). This is the
        /// one member here that can BLOCK: <see cref="AcquireSegment"/> spins until the GPU has finished with the
        /// segment being opened, which is up to a frame. Under the lock that would be a frame-long hold of the
        /// exact lock decision W4 caps at microseconds, and on the event-query fence mechanism it is worse than
        /// slow: every poll of the completion value re-enters the same lock, so the wait would also shut out the
        /// submission that would end it. The present boundary calls this AFTER the present has released the lock,
        /// and the check makes wiring it the other way a message rather than a stall nobody can see.
        /// </para>
        /// </summary>
        internal void BeginFrame()
        {
            if (Monitor.IsEntered(_submitLock))
            {
                throw new InvalidOperationException(
                    "BeginFrame was called on the native Direct3D 11 ring allocator while the caller held the "
                    + "submit lock. Opening a frame waits for the GPU to finish with the segment it opens, which "
                    + "is up to a frame, and decision W4 holds the submit lock for microseconds. Call it after "
                    + "the present has released the lock, not inside it.");
            }

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
        /// IT IS ALSO THE IMMEDIATE DRIVER'S PER-FLUSH UNMAP, which is what row 9 built and what
        /// <see cref="MapScopeFor"/> now assumes. <see cref="D3D11BindFlush"/> calls it before every DRAW and
        /// every DISPATCH on that driver, UNCONDITIONALLY rather than only when a bind is pending: a draw with no
        /// dirty slot still draws against the constant buffers an earlier flush bound, and a record-time uniform
        /// write since then has re-mapped the ring underneath them.
        /// </para>
        /// <para>
        /// A PIPELINE SWITCH DOES NOT CALL IT, deliberately. What Direct3D 11 refuses is a DRAW against a mapped
        /// resource, and the switch's drain issues bind calls alone, which are legal while the ring is mapped.
        /// The next draw or dispatch unmaps before it issues, so the ring is released ahead of the one command
        /// that cannot tolerate it, and an unmap at the switch as well would be an extra lock per pipeline change
        /// buying nothing.
        /// </para>
        /// <para>
        /// Idempotent, and an empty registry costs one uncontended lock. The registry is read INSIDE the lock
        /// rather than short-circuiting outside it: an empty count read without the lock can be stale by the time
        /// it is acted on, and being wrong in that direction means a ring stays mapped through the replay that is
        /// about to bind it.
        /// </para>
        /// <para>
        /// CALL IT WITH THE SUBMIT LOCK ALREADY HELD, which is what the submit path does and what the bind flush
        /// will do. The lock is a <see cref="Monitor"/>, so re-entering it on the thread that already owns it is
        /// free, and nothing here assumes it is the outermost holder: it acquires no other lock, waits on
        /// nothing, and the registry is consistent again before it returns. Taking it outside instead would open
        /// a window between this method's release and the caller's acquisition, which is exactly where an
        /// off-timeline write re-maps a ring the caller is about to bind.
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
        /// recording is already writing, deliberately NOT the segment the GPU is executing. So the write lands
        /// when it is called and the next list submitted reads it.
        /// </para>
        /// <para>
        /// IT DOES NOT PERSIST BEYOND THAT SEGMENT, and this is the one place the ring diverges from the
        /// incumbent rather than merely being faster than it. The write reaches one segment out of
        /// <see cref="FramesInFlight"/>, so it survives exactly until the frame index wraps back round, whereas
        /// the same call on the Veldrid backend writes the buffer's only copy and persists for the buffer's life.
        /// The requirement that follows is plain: a ring-backed uniform buffer's FULL contents have to be
        /// re-established every frame, and a one-shot load-time write is not preserved. One shipped consumer does
        /// exactly that (<c>ModelRenderer</c>'s splat-params tail), which is tracked as
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484 and blocks the device row rather than being solved
        /// here.
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
        /// THE WHOLE WRITE UNDER <see cref="D3D11RingMapScope.PerWrite"/>: map, copy and unmap as ONE critical
        /// section. Called by the ring's write path when that scope is in force, so the scope decision lives in
        /// one place rather than at every write site.
        /// <para>
        /// The three steps are atomic rather than a lock around each context call, because the alternative is a
        /// window in which the ring is mapped and no lock is held: a device-level <see cref="UpdateBuffer"/>
        /// arriving on another thread, or a submit's unmap, would withdraw the mapping while the copy is running,
        /// and the copy would write through a pointer the runtime has taken back. Serializing a whole write costs
        /// something, and it costs it only under <c>KE_D3D11_RECORD=immediate</c>, which is the M1 fallback lever
        /// and not a path anything ships on: that driver already maps and unmaps per write, so the lock is the
        /// cheapest thing in the sequence.
        /// </para>
        /// <para>
        /// The mapping still goes through the registry, so the "every mapped ring is in the registry" invariant
        /// holds without a scope test at the places that read it, and an already-mapped ring is written and left
        /// unmapped exactly as a freshly mapped one is.
        /// </para>
        /// </summary>
        internal void WriteUnderPerWriteScope(D3D11UniformRing ring, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            lock (_submitLock)
            {
                if (!ring.IsMapped)
                {
                    ring.MapUnderLock();
                    _mappedRings.Add(ring);
                }

                ring.CopyIntoCurrentSegmentUnderLock(offsetBytes, data);

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
