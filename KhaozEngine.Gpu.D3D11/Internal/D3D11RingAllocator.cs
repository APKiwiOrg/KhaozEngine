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
    /// same lock around its copies, so it cannot land in the middle of a replay, and the submit path holds it
    /// from its unmap through its replay.
    /// </para>
    /// <para>
    /// THE ONE PLACE THAT LOOPS ON THE LOCK is the off-timeline write, and it never holds the lock while it
    /// waits. It can find a segment the GPU has not finished with, and the segment gate is the same one
    /// <see cref="AcquireSegment"/> applies, so it reads the target under the lock, RELEASES it, spins, retakes it
    /// and re-checks (see <see cref="UpdateBuffer"/>). That is the same rule <see cref="BeginFrame"/> refuses a
    /// caller by name for breaking, expressed as a retry rather than as a refusal, because this call has no
    /// boundary of its own to be moved outside the lock.
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
    /// Not thread-safe for its FRAME counters, the same contract <see cref="D3D11FenceSubsystem"/> and
    /// <c>RetiredResourcePool</c> already have: they are driven from the frame thread. The off-timeline wait
    /// counters behind <see cref="OffTimelineWaits"/> are the exception and are interlocked, because that path is
    /// any-thread by contract.
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

        // The off-timeline write's waits, cumulative since the device was created and deliberately NOT rolled per
        // frame. Interlocked because that path is any-thread by contract while the two counters above are the
        // frame thread's. See OffTimelineWaits.
        int _offTimelineWaits;
        long _offTimelineWaitTicks;

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
        /// M3 measurement, and it counts frame-boundary segment stalls ALONE (see
        /// <see cref="OffTimelineWaits"/>).</summary>
        internal D3D11BackpressureStats LastFrameBackpressure => _lastFrame;

        /// <summary>
        /// THE OFF-TIMELINE WRITE'S WAITS, CUMULATIVE SINCE THE DEVICE WAS CREATED, and a SEPARATE number from
        /// <see cref="LastFrameBackpressure"/> on purpose.
        /// <para>
        /// They are not frame-boundary stalls. M3's exit criterion is that
        /// <see cref="LastFrameBackpressure"/> is ZERO across a soak window, which reads as "three segments are
        /// enough for this machine", and an off-timeline wait says nothing about that: it says a caller wrote a
        /// uniform buffer off-timeline while an earlier frame was still reading a segment of it. Folding the two
        /// together would turn a load-time write into evidence against the segment count and make the M3 criterion
        /// unreachable for reasons unrelated to pipeline depth.
        /// </para>
        /// <para>
        /// CUMULATIVE RATHER THAN ROLLED PER FRAME, because the writes this counts are typically LOAD-TIME and
        /// happen before any frame has begun. A per-frame roll would discard exactly the ones worth seeing. Same
        /// <see cref="D3D11BackpressureStats"/> shape, so a diagnostic reports the pair the same way.
        /// </para>
        /// </summary>
        internal D3D11BackpressureStats OffTimelineWaits => new D3D11BackpressureStats(
            Volatile.Read(ref _offTimelineWaits),
            Interlocked.Read(ref _offTimelineWaitTicks) * 1000d / Stopwatch.Frequency);

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
        /// off-timeline write: no recording is required, one may be open, and the caller may be any thread. This
        /// is the resolution of https://github.com/APKiwiOrg/KhaozEngine/issues/484.
        /// <para>
        /// IT WRITES EVERY SEGMENT, so a value written ONCE persists for the buffer's life exactly as the same
        /// call on the Veldrid backend persists it, where the buffer has one copy. Writing only the current
        /// segment was the shipped shape for one release and it was a defect rather than a documentation problem:
        /// a load-time write reached one segment out of <see cref="FramesInFlight"/>, so two frames out of every
        /// three bound memory nothing had ever written, intermittently, with nothing thrown and nothing logged.
        /// <c>ModelRenderer</c>'s splat-params tail is the shipped consumer that did exactly that.
        /// </para>
        /// <para>
        /// A RECORD-TIME WRITE IS UNCHANGED and still reaches the current segment alone (see
        /// <see cref="D3D11UniformRing.Write"/>). The split is the CALL rather than a usage hint on the buffer,
        /// because the call is what knows whether it happens once: every shipped record-time uniform write is
        /// unconditional per frame, and replicating those would be <see cref="FramesInFlight"/> memcpys for a
        /// value the next frame overwrites, on the hot path this whole design exists to make cheap.
        /// </para>
        /// <para>
        /// A SEGMENT STILL IN FLIGHT IS WAITED FOR, WITH THE LOCK RELEASED, and that is a real semantic change:
        /// this call could previously never block. It is the same gate <see cref="AcquireSegment"/> applies,
        /// because writing a segment the GPU is reading is the silent corruption decision U5 exists to prevent,
        /// and it is a RETRY LOOP rather than a wait in place: under the lock the highest outstanding completion
        /// value across the target segments is read, the lock is RELEASED, that value is spun for, and the lock is
        /// retaken and the check redone, until one hold finds every target free and does all the copies inside it.
        /// Waiting under the lock is refused by name elsewhere in this type for good reason (see
        /// <see cref="BeginFrame"/>), and on the event-query fence mechanism it would also shut out the submission
        /// that would end the wait. At load time nothing has been submitted, so the first iteration writes every
        /// segment with no wait at all. Mid-frame the wait is bounded by <see cref="FramesInFlight"/> frames of
        /// GPU work, which is what the incumbent already blocked for on this exact call: Veldrid's pooled staging
        /// write maps with no <c>DO_NOT_WAIT</c> and blocks until the GPU releases the buffer being recycled
        /// (section 6.1).
        /// </para>
        /// <para>
        /// THE CURRENT SEGMENT IS COPIED WITHOUT THAT GATE, deliberately, and it is the only ungated one. Gating
        /// it would change the documented semantic that the write lands when it is called and the next list
        /// submitted reads it, and it would block on the GPU on every off-timeline write made after this frame
        /// slot's first submit, which is the pathology the ring deletes. The exposure it leaves is exactly the
        /// exposure the shipped call already had and is unchanged by this fix, and it is the forbidden case named
        /// below. What this fix adds is the OTHER segments, and those are the ones that can newly corrupt a frame,
        /// so those are the ones gated.
        /// </para>
        /// <para>
        /// AGAINST A CONCURRENT RECORD-TIME WRITE TO THE SAME RANGE, THE CURRENT SEGMENT IS LAST-WRITE-WINS AND
        /// THAT IS OUTSIDE THE v1 CONTRACT, unchanged by this fix. Under
        /// <see cref="D3D11RingMapScope.AcrossRecording"/> a record-time copy runs with no lock, so the two copies
        /// are not ordered against each other and a torn result is possible: decision W5's boundary already places
        /// a record-time write racing a device-level write outside the contract, and it stays there. The other
        /// <see cref="FramesInFlight"/> minus one segments are NOT contended, because a record-time write never
        /// touches them. Under <see cref="D3D11RingMapScope.PerWrite"/> both shapes take the lock for the whole
        /// write, so there the overlap is serialized.
        /// </para>
        /// <para>
        /// IT MAPS IDEMPOTENTLY. The ring is unmapped at the start of every submit, so a write arriving between
        /// two frames finds it unmapped, maps <c>NO_OVERWRITE</c>, writes, and under the deferred driver leaves it
        /// mapped for the next record phase to reuse. There is no refcount, just the one flag, checked under the
        /// same lock as the write. Under <see cref="D3D11RingMapScope.PerWrite"/> the map, every segment's copy
        /// and the unmap are one critical section, the same atomicity <see cref="WriteUnderPerWriteScope"/> holds
        /// for a record-time write.
        /// </para>
        /// <para>
        /// THE LOCK IS SHORT AND SCOPED TO THE COPIES, never to a frame and never across the wait. It is the
        /// submit lock, so an off-timeline write cannot land in the middle of a replay.
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
            ring.ValidateWriteRange(offsetBytes, data.Length);

            if (data.Length == 0) return;

            while (true)
            {
                ulong target;
                lock (_submitLock)
                {
                    if (!TryFindSegmentStillInFlight(out target))
                    {
                        WriteEverySegmentUnderLock(ring, offsetBytes, data);
                        return;
                    }
                }

                WaitOffTimeline(target);
            }
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
        /// <para>
        /// THIS IS THE RECORD-TIME WRITE, so it copies the CURRENT segment alone. An off-timeline
        /// <see cref="UpdateBuffer"/> under the same scope copies every segment and still holds the map, all the
        /// copies and the unmap as one critical section, which is the same discipline over a wider write.
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

        /// <summary>
        /// THE OFF-TIMELINE WRITE'S GATE, ASKED ONCE PER LOCK HOLD: the highest completion value any segment other
        /// than the current one is still waiting on, or false when all of them are free. Called under the submit
        /// lock, and it polls the timeline AT MOST ONCE, because a poll on the event-query fence mechanism is a
        /// call that re-enters this same lock and a loop of them under the lock is the shape
        /// <see cref="BeginFrame"/> refuses by name.
        /// <para>
        /// THE MAXIMUM RATHER THAN THE FIRST is what makes one wait enough. The timeline is monotonic, so reaching
        /// the highest outstanding target has reached every lower one with it, and the retry that follows finds
        /// every segment free in one more hold instead of one hold per segment.
        /// </para>
        /// <para>
        /// A ZERO OWNER IS SKIPPED WITHOUT A POLL, and at load time every segment is zero, so a one-shot write
        /// before anything has been submitted costs no completion read at all.
        /// </para>
        /// </summary>
        bool TryFindSegmentStillInFlight(out ulong target)
        {
            target = 0;

            ulong highest = 0;
            for (int i = 0; i < _segmentOwner.Length; i++)
            {
                if (i == _segment) continue;
                if (_segmentOwner[i] > highest) highest = _segmentOwner[i];
            }

            if (highest == 0) return false;
            if (_completion.CompletedValue >= highest) return false;

            target = highest;
            return true;
        }

        /// <summary>
        /// SPIN FOR AN OFF-TIMELINE TARGET, WITH NO LOCK HELD, and count it. Same discipline as
        /// <see cref="AcquireSegment"/> and for the same reason: the plain <see cref="SpinWait.SpinOnce()"/> starts
        /// sleeping a millisecond after 20 iterations, which is longer than the frame this can be inside.
        /// <para>
        /// THE COUNTERS ARE INTERLOCKED WHILE THE FRAME ONES ARE NOT, because the two paths have different
        /// callers. <see cref="BeginFrame"/> runs on the frame thread and its counters inherit that contract, while
        /// an off-timeline write is callable from any thread by design, so two of them waiting at once would
        /// otherwise lose a count. It costs an interlocked pair only when a wait actually happened.
        /// </para>
        /// </summary>
        void WaitOffTimeline(ulong target)
        {
            long start = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            while (_completion.CompletedValue < target) spin.SpinOnce(sleep1Threshold: -1);

            Interlocked.Increment(ref _offTimelineWaits);
            Interlocked.Add(ref _offTimelineWaitTicks, Stopwatch.GetTimestamp() - start);
        }

        /// <summary>
        /// THE COPIES THEMSELVES, under the submit lock, with every target segment already known to be free.
        /// Mapping the ring if it is not mapped, copying into all <see cref="FramesInFlight"/> segments, and under
        /// <see cref="D3D11RingMapScope.PerWrite"/> releasing the mapping again before the lock is dropped, so that
        /// scope keeps the atomicity it exists for across the whole replicated write rather than around one
        /// segment of it.
        /// <para>
        /// THE SEGMENT ORDER IS NOT OBSERVABLE. The segments are disjoint memory and every one of them is free of
        /// the GPU for the duration of this hold, so index order is the arbitrary choice it looks like.
        /// </para>
        /// </summary>
        void WriteEverySegmentUnderLock(D3D11UniformRing ring, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            if (!ring.IsMapped)
            {
                ring.MapUnderLock();
                _mappedRings.Add(ring);
            }

            for (int i = 0; i < _segmentOwner.Length; i++) ring.CopyIntoSegmentUnderLock(i, offsetBytes, data);

            if (MapScope != D3D11RingMapScope.PerWrite) return;

            ring.UnmapUnderLock();
            _mappedRings.Remove(ring);
        }
    }
}
