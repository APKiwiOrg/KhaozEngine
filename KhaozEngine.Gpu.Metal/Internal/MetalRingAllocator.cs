using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DEVICE'S ONE RING ALLOCATOR (M-M3, M-M5, section 9.2): the segment a recording writes into, the
    /// completion gate that decides when a segment may be reused, the off-timeline write's pending-patch queue,
    /// and the backpressure MM4 measures.
    ///
    /// <para><b>THE MODEL IS SEGMENT-PER-RECORDING VERSIONING, AND IT IS DELIBERATELY NOT PER FRAME.</b> A ring
    /// segment is the uniform VERSION that one recording writes and that recording's submission reads. Rotation
    /// happens at <c>MetalCommandList.Begin</c>, so the depth buys <see cref="FramesInFlight"/> RECORDINGS of
    /// headroom rather than that many frames, and a typical engine frame opens several recordings (the scene
    /// list, <c>Render3DPreview.Capture</c>, each <c>OceanFftProducer</c> prime pass, and one per
    /// <c>RetireBarrier.Submit</c> whenever completion fences are supported, which on this backend is always).
    /// The knob keeps the name <c>KE_METAL_FRAMES_IN_FLIGHT</c> for consumer familiarity and
    /// <see cref="MetalFramesInFlight"/> carries the division that turns it back into frames. Segment-per-
    /// recording is the invariant the M-M7 race closure actually needs, and it is frame-shape independent, which
    /// is why the correction is a vocabulary correction rather than a design change.</para>
    ///
    /// <para><b>EACH RECORDING CAPTURES ITS SEGMENT ONCE AND READS THE CAPTURE FROM THEN ON.</b>
    /// <see cref="BeginRecording"/> returns the segment the caller has claimed, the command list holds it for the
    /// life of that recording, and every record-time ring write (and row 13's bind-offset composition when it
    /// lands) is composed against THAT number rather than against <see cref="CurrentSegment"/>. Reading the live
    /// index at record time would mean a second list's <c>Begin</c> could move the segment under a recording in
    /// progress, so its writes and its binds would straddle two versions. This is NOT a second per-list index in
    /// M-R2's sense: nothing advances per list, and a list does not rotate anything of its own. It simply
    /// remembers which version its recording targets.</para>
    ///
    /// <para><b>WHAT <see cref="CurrentSegment"/> IS STILL FOR.</b> Exactly one thing: the DEVICE-level notion of
    /// current, used by <see cref="UpdateBuffer"/>'s every-segment write (which has no recording to belong to and
    /// must copy one segment ungated) and by <c>Map</c> on a ring-backed buffer, which answers the same segment
    /// for the same reason. No recording reads it.</para>
    ///
    /// <para><b>AND THE BOUNDARY IS <c>MetalCommandList.Begin</c>, WHICH IS WHERE THIS BACKEND DIVERGES FROM BOTH
    /// SIBLINGS (M-R2).</b> On Direct3D 11 and on Vulkan the equivalent <c>BeginFrame</c> is called from
    /// <c>Present</c>, and each of those backends has a SECOND per-list index (a mapped-ring scope there, a
    /// command-pool slot here) that advances at the list's own <c>Begin</c>. This backend has no second index at
    /// all, because an <c>MTLCommandBuffer</c> is single-use and the queue owns its memory, so the depth exists
    /// for exactly one reason and lives on exactly one acquire. Hanging that acquire off <c>Present</c> would
    /// leave it unreached on the HEADLESS path, where nothing presents and where the 36 goldens and every
    /// <c>[GpuFact]</c> run: the ring would never rotate, every recording would write segment 0, and the gate this
    /// row exists to build would be dead code on the only path CI can drive. Row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/581) therefore does NOT add a second call at the present
    /// boundary. A windowed frame reaches <c>Begin</c> exactly as a headless one does.</para>
    ///
    /// <para><b>THE GATE IS A COMPLETION READ AND NOTHING ELSE.</b> Before handing out a segment the allocator
    /// reads <see cref="MetalTimeline.CompletedValue"/>, which is the <c>MTLSharedEvent</c>'s own
    /// <c>signaledValue</c>, and blocks while the GPU has not reached the value the submission that last read that
    /// segment signals. A ring gated on a submit RECEIPT would recycle a segment the moment the CPU finished
    /// ASKING for the work rather than when the GPU finished doing it, which is a silent, intermittent corruption
    /// several frames from its cause. That dependency is why this row waits on row 5
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571).</para>
    ///
    /// <para><b>THE OWNER VALUE IS RECORDED AT THE SUBMIT, WITH THAT SUBMISSION'S OWN VALUE.</b>
    /// <see cref="RecordSegmentOwner"/> is called from <c>MetalCommandList.MarkSubmitted</c> inside the submit
    /// lock, carrying the value <c>MetalTimeline.EncodeSignalForSubmit</c> just encoded into the buffer being
    /// committed, so a segment's owner names the submission that actually READS it. The rejected alternative was
    /// reading <see cref="MetalTimeline.LastSubmitted"/> when the segment stopped being current, which
    /// UNDER-RECORDS on an ordinary interleaving: a list that ends its recording, lets another list's <c>Begin</c>
    /// rotate past it, and only then submits, takes a value ABOVE the one its segment was closed at, so a later
    /// wrap would wait for the wrong value and overwrite a segment a live submission was still reading. A
    /// recording ABANDONED without a submit leaves the owner untouched, which is the direction that cannot hang:
    /// the gate never waits on a value nothing will ever signal.</para>
    ///
    /// <para><b>A DEAD DEVICE RELEASES THE WAIT</b>, without this type knowing what a device is:
    /// <see cref="MetalTimeline.CompletedValue"/> answers with everything ever allocated once liveness has
    /// flipped, so a segment wait during teardown finds its target already reached and returns. The sliced wait
    /// underneath it observes the same flip between slices, which is why that slice exists at all (M-F5).</para>
    ///
    /// <para><b>THE OFF-TIMELINE WRITE NEVER WAITS FOR ANYTHING, AT EVERY DEPTH BUT THE FLOOR</b>, which is what
    /// keeps <see cref="BeginRecording"/> the only member here that can block, and what makes a caller who
    /// already holds the submit lock LEGAL: the lock is a <see cref="Monitor"/>, so the acquisition inside is a
    /// free re-entry, and there is nothing inside that could wait for work only another thread could do. That is
    /// section 9.4's Lock legality row, which is this backend's own. AT A DEPTH OF ONE it does wait, because
    /// there its current-segment copy is the whole write and there is no other segment to defer to
    /// (<see cref="CurrentSegmentIsGatedAtDepthOne"/>). The lock legality survives that: what it waits for is a
    /// submission already made, so the completion that ends the wait comes from the GPU rather than from a thread
    /// that would need this lock.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE.</b> The timeline is behind <see cref="IMetalSharedEvent"/> and
    /// there is no other native surface at all: the writes are memcpys into a pointer <c>MetalBuffer</c> took
    /// once. So the rotation, the gate, the deferral rule, the replay order and the counters all run under
    /// <c>dotnet test</c> on a machine with no Metal.</para>
    /// </summary>
    internal sealed class MetalRingAllocator
    {
        readonly MetalTimeline _timeline;
        readonly WaitAccumulator _backpressure;
        readonly object _submitLock;

        // The timeline value the submission that last READ each segment signals, raised at that submit by
        // RecordSegmentOwner. Zero means no submission has ever read the segment, which is the only case that
        // needs no wait: the timeline's first value is 1, so zero cannot be a real target.
        readonly ulong[] _segmentOwner;

        // Every ring currently owing at least one segment a deferred off-timeline write, so a rotation can apply
        // exactly those. A device may hold hundreds of rings and a walk over all of them at every rotation would
        // put an O(buffers) cost on the one path that has to stay cheap, for a list that is empty in most programs
        // and one entry long in the rest.
        readonly List<MetalUniformRing> _patchedRings = new();

        // Recordings begun since the device was created, and the whole of the rotation. Advanced under the submit
        // lock rather than with a plain increment, because M-R3 documents concurrent Begin as supported and two
        // threads reading one value would claim the same segment for two live recordings.
        ulong _recordingIndex;

        // The DEVICE-level current segment: what the off-timeline write treats as current and what Map answers.
        // Written under the submit lock, and read lock-free only by those two, whose stale read is the same window
        // a device-level write racing a Begin already has.
        int _segment;

        int _stallCount;

        // The off-timeline write's deferrals, their replays, the ones a later write superseded and the ones that
        // went away with a disposed ring, cumulative since the device was created and deliberately NOT rolled per
        // frame. All four are mutated under the submit lock alone, and read volatile because a diagnostic may be
        // on any thread. See RingPatchStats.
        int _patchesDeferred;
        int _patchesApplied;
        int _patchesCoalesced;
        int _patchesDropped;

        /// <param name="framesInFlight">Segments per ring, from <see cref="MetalFramesInFlight"/>. Resolved by
        /// the caller rather than read from the environment here, so the behaviour is testable without touching
        /// process state.</param>
        /// <param name="timeline">The device's one completion timeline. Read for the gate, waited on when the
        /// gate is not satisfied, and never signalled.</param>
        /// <param name="backpressure">The device's ONE backpressure accumulator. On this backend it has exactly
        /// one source and this is it (M-R2).</param>
        /// <param name="submitLock">The device's single submit lock. Not created here, because the same lock has
        /// to order <c>-commit</c>, the timeline value it encodes and the segment owner that value becomes.</param>
        internal MetalRingAllocator(int framesInFlight, MetalTimeline timeline, WaitAccumulator backpressure,
            object submitLock)
        {
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentNullException.ThrowIfNull(backpressure);
            ArgumentNullException.ThrowIfNull(submitLock);

            if (framesInFlight < MetalFramesInFlight.Minimum || framesInFlight > MetalFramesInFlight.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    "A native Metal uniform ring runs between " + MetalFramesInFlight.Minimum + " and "
                    + MetalFramesInFlight.Maximum + " frame segments. " + MetalFramesInFlight.EnvVarName
                    + " clamps to that range before it gets here.");
            }

            _timeline = timeline;
            _backpressure = backpressure;
            _submitLock = submitLock;
            _segmentOwner = new ulong[framesInFlight];
            FramesInFlight = framesInFlight;
        }

        /// <summary>How many segments every ring in this device is cut into, which is how many RECORDINGS of
        /// headroom the depth buys. See <see cref="MetalFramesInFlight"/> for what that is in frames.</summary>
        internal int FramesInFlight { get; }

        /// <summary>
        /// THE DEVICE-LEVEL NOTION OF CURRENT: the segment the most recent <see cref="BeginRecording"/> claimed.
        /// Read by <see cref="UpdateBuffer"/> and by <c>Map</c> on a ring-backed buffer, and by NOTHING that
        /// serves a recording, which reads its own captured segment instead.
        /// </summary>
        internal int CurrentSegment => _segment;

        /// <summary>Recordings begun since the device was created. The segment one claims is this modulo
        /// <see cref="FramesInFlight"/>, and the wrap is the whole mechanism.</summary>
        internal ulong RecordingIndex => _recordingIndex;

        /// <summary>
        /// Segment acquisitions that ACTUALLY BLOCKED, since the device was created. One entry per
        /// <see cref="BeginRecording"/> that waited, however many times it had to revalidate, because the caller
        /// blocked once. The same entries are recorded into the device's BACKPRESSURE
        /// <see cref="WaitAccumulator"/>, which is what the seam reports, and this is the ring's own half of that
        /// reading so a test can say which source produced a count. On this backend they are the same number,
        /// because that accumulator has no second source (M-R2).
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

        /// <summary>The completion value the gate reads, exposed so the list's staging arena can recycle against
        /// the same reading rather than polling the timeline a second time in the same
        /// <c>Begin</c>.</summary>
        internal ulong CompletedValue => _timeline.CompletedValue;

        /// <summary>The timeline value the submission that last read <paramref name="segment"/> signals, or 0 for
        /// a segment no submission has ever read. Present so a test and a diagnostic can see the gate's
        /// input.</summary>
        internal ulong SegmentOwner(int segment)
        {
            lock (_submitLock) return _segmentOwner[segment];
        }

        /// <summary>
        /// CLAIM THE NEXT SEGMENT FOR ONE RECORDING: advance the rotation, block there if the submission that last
        /// read that segment has not completed, replay anything queued for it, and publish it as the device's
        /// current. Returns the segment claimed, which the caller CAPTURES for the life of its recording and which
        /// its staging arena rotates onto.
        /// <para>
        /// THE CALLER IS <c>MetalCommandList.Begin</c>, immediately after it has taken its command buffer and
        /// before the recording flag flips. See the class note for why the boundary is there rather than at
        /// <c>Present</c>, and for why the segment is captured rather than re-read.
        /// </para>
        /// <para>
        /// THE WHOLE SEQUENCE IS ONE LOCK HOLD, RELEASED ONLY ACROSS THE WAIT AND REVALIDATED AFTER IT. Advancing
        /// the index, reading the owner, claiming the segment, replaying its patches and publishing it all happen
        /// under <c>_submitLock</c>, so two threads beginning at once cannot claim the same segment and cannot
        /// skip one: the rotation is not hot, and one hold per <c>Begin</c> costs nothing measurable. The wait is
        /// the one thing that cannot happen inside it, for two reasons that both bite. It is up to a whole
        /// submission long, and that lock is the one serialised point in the frame. And the value being waited for
        /// is REGISTERED under this very lock now, so holding it across the wait would shut out the submission
        /// whose completion is the only thing that could end the wait, which is a deadlock rather than a slow
        /// frame. So the hold is dropped for the wait and re-taken afterwards, and the owner is READ AGAIN: a
        /// recording that captured this segment earlier and had not submitted yet can submit during the wait and
        /// raise the owner past the value that was waited for, and re-reading is what turns that into a second
        /// wait instead of a segment handed out under a live submission.
        /// </para>
        /// <para>
        /// A CALLER ALREADY HOLDING THE SUBMIT LOCK IS REFUSED BY NAME, which is also what makes the release above
        /// a real release rather than one level off a re-entrant count.
        /// </para>
        /// </summary>
        internal int BeginRecording()
        {
            if (Monitor.IsEntered(_submitLock))
            {
                throw new InvalidOperationException(
                    "BeginRecording was called on the native Metal ring allocator while the caller held the "
                    + "submit lock. Claiming a segment waits for the GPU to finish with the submission that last "
                    + "read it, and the value it waits for is registered under that same lock (M-N2), so waiting "
                    + "while holding it would shut out the only thing that could end the wait. Begin takes it "
                    + "nowhere, so reaching this means a new caller was added inside the lock.");
            }

            bool blocked = false;
            long waitedTicks = 0;

            bool taken = false;
            Monitor.Enter(_submitLock, ref taken);
            try
            {
                _recordingIndex++;
                int segment = (int)(_recordingIndex % (ulong)FramesInFlight);

                while (true)
                {
                    ulong target = _segmentOwner[segment];
                    if (target == 0) break;
                    if (_timeline.CompletedValue >= target) break;

                    blocked = true;

                    Monitor.Exit(_submitLock);
                    taken = false;

                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        // THE BACKPRESSURE WAIT, and MM4's only source. It blocks on the shared event rather than
                        // spinning, like the Vulkan sibling and unlike the Direct3D 11 one, and the difference is
                        // the primitive rather than a preference. There a completion poll on the fallback
                        // mechanism re-enters the submit lock, so a sleeping wait would shut out the submission
                        // that would end it. Here the wait holds no lock at all and
                        // waitUntilSignaledValue:timeoutMS: is a real blocking form, so burning a core to avoid a
                        // syscall would trade the one resource a stalled frame still needs.
                        _timeline.WaitForValue(target);
                    }
                    finally
                    {
                        waitedTicks += Stopwatch.GetTimestamp() - start;
                        Monitor.Enter(_submitLock, ref taken);
                    }
                }

                // THE PATCHES AND THE PUBLISH ARE ONE STEP, and were one hold even before the rest of the sequence
                // joined them. An off-timeline write that observed the new segment as current would copy into it
                // directly, so observing that before the replay would have its bytes overwritten by older queued
                // ones.
                ApplyPendingPatchesUnderLock(segment);
                _segment = segment;

                // ONLY THE BLOCK IS RECORDED, and once per acquisition however many revalidations it took: a
                // counter that ticks on a non-wait cannot answer "was anything ever blocked" with a zero, and a
                // counter that ticked per revalidation would report two stalls for one blocked caller.
                if (blocked)
                {
                    Interlocked.Increment(ref _stallCount);
                    _backpressure.Record(waitedTicks);
                }

                return segment;
            }
            finally
            {
                if (taken) Monitor.Exit(_submitLock);
            }
        }

        /// <summary>
        /// A SUBMISSION READ <paramref name="segment"/> AND SIGNALS <paramref name="signalledValue"/>, so that
        /// segment may not be handed out again until the timeline reaches it. Called by
        /// <c>MetalCommandList.MarkSubmitted</c> with the value <c>MetalTimeline.EncodeSignalForSubmit</c> encoded
        /// into the buffer just committed, from inside the submit lock, and by nothing else.
        /// <para>
        /// AT THE SUBMIT RATHER THAN AT THE ROTATION, which is the correction row 8 shipped without. Recording the
        /// owner when a segment stopped being current, from
        /// <see cref="MetalTimeline.LastSubmitted"/>, under-records an ordinary interleaving: a list that ends,
        /// lets another list's <c>Begin</c> rotate past it and only then submits takes a value ABOVE the one its
        /// segment was closed at, so the wrap back to that segment would wait for the earlier value and start
        /// writing while the later submission was still reading. Taking the submission's OWN value removes the
        /// window entirely, because the recording and the value are joined at the only point that knows both.
        /// </para>
        /// <para>
        /// THE HIGHEST VALUE WINS, because a segment can carry more than one submission: with more open recordings
        /// than <see cref="FramesInFlight"/> two of them share a segment, and a list re-Begun after a wrap lands
        /// back on one it has used. Taking the maximum is what keeps one gate sufficient for all of them.
        /// </para>
        /// </summary>
        internal void RecordSegmentOwner(int segment, ulong signalledValue)
        {
            if (segment < 0 || segment >= _segmentOwner.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(segment), segment,
                    "A native Metal uniform ring has " + FramesInFlight + " segments, so a submission cannot have "
                    + "read segment " + segment + ". The segment a submission carries is the one its recording "
                    + "captured at Begin.");
            }

            if (signalledValue == 0) return;

            lock (_submitLock)
            {
                if (signalledValue > _segmentOwner[segment]) _segmentOwner[segment] = signalledValue;
            }
        }

        /// <summary>
        /// THE DEVICE-LEVEL <c>UpdateBuffer</c> ON A RING-BACKED BUFFER (M-M5, section 9.2): the off-timeline
        /// write. No recording is required, one may be open, and the caller may be any thread. This is
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484's correction, ADOPTED WHOLESALE rather than
        /// re-derived, because it cost a consumer defect to learn once.
        /// <para>
        /// IT WRITES EVERY SEGMENT, so a value written ONCE persists for the buffer's life exactly as the same
        /// call on the incumbent Metal leg persists it, where the buffer has one copy. Writing only the current
        /// segment was the shipped shape on another backend for one release and it was a defect rather than a
        /// documentation problem: a load-time write reached one segment out of <see cref="FramesInFlight"/>, so
        /// two frames out of every three bound memory nothing had ever written, intermittently, with nothing
        /// thrown and nothing logged.
        /// </para>
        /// <para>
        /// A SEGMENT STILL IN FLIGHT IS PATCHED RATHER THAN WAITED FOR, AND THIS CALL NEVER BLOCKS. Writing a
        /// segment the GPU is reading is the data race M-M7 names in the incumbent's shipped code and the gate
        /// exists to prevent, so a segment whose owner value has not been reached does not receive the copy: the
        /// byte range and a private copy of the data are queued on the ring, and the <see cref="BeginRecording"/>
        /// that next claims that segment applies them, right after the gate has proved the GPU is done with it.
        /// The writer returns immediately, always, on every thread, at any pipeline depth. A RETRY LOOP WAITING
        /// FOR EVERY NON-CURRENT SEGMENT AT ONCE NEVER TERMINATES in the GPU-bound steady state.
        /// </para>
        /// <para>
        /// EVENTUAL CONSISTENCY IS THE GUARANTEE, and it is exactly what the incumbent's single copy gives. When
        /// this returns, every segment either already holds the write or holds a pending patch its next claim
        /// applies, so ANY segment BOUND after this call carries the value. The window in which an in-flight
        /// segment still holds the old bytes is unobservable through the seam, because that segment is not bound
        /// again until it has been claimed, and claiming it applies the patch first.
        /// </para>
        /// <para>
        /// ONE COMPLETION POLL PER CALL AT MOST. The gate reads the timeline once and compares that one value
        /// against every segment's owner, since the timeline is monotonic and a second read inside one hold could
        /// only make more segments look free. A segment with a zero owner has never been read by a submission and
        /// is skipped without a poll at all, so a load-time write, when every owner is zero, costs no completion
        /// read whatsoever.
        /// </para>
        /// <para>
        /// A SEGMENT THAT ALREADY CARRIES A PATCH TAKES THIS WRITE AS A PATCH TOO, even when its owner has since
        /// completed. That is what keeps arrival order intact: copying directly into a segment with an older
        /// patch still queued would let the next claim replay the OLDER bytes over the newer ones.
        /// </para>
        /// <para>
        /// THE CURRENT SEGMENT IS COPIED, and above a depth of one it is the only ungated copy. Gating it there
        /// would change the documented semantic that the write lands when it is called and the next recording
        /// submitted reads it, and deferring it would be worse still, since the current segment is the one the
        /// recording in progress is writing and its next claim is a whole wrap away. AT A DEPTH OF ONE there is
        /// no other segment at all, so that copy is the whole write and there would be nothing at all between it
        /// and the GPU: see <see cref="CurrentSegmentIsGatedAtDepthOne"/>.
        /// </para>
        /// </summary>
        internal void UpdateBuffer(MetalUniformRing ring, uint offsetBytes, ReadOnlySpan<byte> data)
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
                    // _segment is read and written under this lock alone, so this comparison is exact rather than
                    // a benign stale read.
                    if (segment != _segment && DeferralIsOwed(ring, segment, ref completed, ref polled))
                    {
                        _patchesCoalesced += ring.RecordPendingPatchUnderLock(segment, offsetBytes, data);
                        _patchesDeferred++;
                        continue;
                    }

                    // AND THE CURRENT SEGMENT IS GATED AT THE FLOOR, where it is the only segment there is.
                    if (CurrentSegmentIsGatedAtDepthOne) WaitForCurrentSegmentUnderLock();

                    ring.CopyIntoSegmentUnderLock(segment, offsetBytes, data);
                }

                if (!wasPatched && ring.HasPendingPatches) _patchedRings.Add(ring);
            }
        }

        /// <summary>
        /// Drop <paramref name="ring"/>, for a ring-backed buffer being disposed.
        /// <para>
        /// ITS PENDING PATCHES GO WITH IT: a queued off-timeline write names memory that is about to stop
        /// existing, so a rotation replaying it after the <c>MTLBuffer</c> was released would write through a
        /// dangling <c>contents()</c> pointer. Objective-C reference counting keeps the ALLOCATION alive while
        /// a submitted command buffer references it (M-H3), which is what makes disposal safe, and it says
        /// nothing at all about a CPU write arriving after the engine dropped its own reference.
        /// </para>
        /// <para>
        /// AND THEY ARE COUNTED ON THE WAY OUT, into <see cref="RingPatchStats.Dropped"/>. Dropping them
        /// silently would leave them counted as deferred and never as resolved, so
        /// <see cref="RingPatchStats.Outstanding"/> would sit permanently high in any program that streams
        /// uniform buffers in and out, and the reading that number is FOR would be wrong for exactly the programs
        /// most likely to consult it.
        /// </para>
        /// </summary>
        internal void Forget(MetalUniformRing ring)
        {
            ArgumentNullException.ThrowIfNull(ring);

            lock (_submitLock)
            {
                _patchesDropped += ring.DropPendingPatchesUnderLock();
                _patchedRings.Remove(ring);
            }
        }

        /// <summary>
        /// WHETHER THE EVERY-SEGMENT WRITE HAS TO GATE ITS CURRENT-SEGMENT COPY, which is true at a depth of ONE
        /// and false at every other depth.
        /// <para>
        /// AT DEPTH ONE THE LOOP'S OTHER-SEGMENT BRANCH NEVER RUNS. There is exactly one segment, it is always the
        /// current one, so the ungated copy that is correct at depth three (because the next claim of that segment
        /// is a whole wrap away) becomes a CPU write into the one segment the GPU may be reading right now. That
        /// is M-M7 exactly, in the backend built to close it, reachable through a documented setting:
        /// <c>KE_METAL_FRAMES_IN_FLIGHT=1</c> is what the floor paragraph offers as a measuring depth.
        /// </para>
        /// <para>
        /// SO AT DEPTH ONE, AND ONLY THERE, THE WRITE WAITS for the submission that last read the segment to
        /// complete before copying: the same completion read the claim gate uses, a poll and then the same sliced
        /// wait. That breaks this type's never-blocks property for the device-level write, deliberately and only
        /// at the floor. Correct but slow is the right trade there, because depth one is a MEASURING depth (one
        /// recording of headroom, one stall per recording, the configuration that proves the backpressure counter
        /// counts something real) rather than one anything ships on. The two alternatives are both worse:
        /// deferring the write leaves the value unreachable until that segment is claimed again, which at depth
        /// one is the next recording rather than a wrap, so the write would not be visible to the recording that
        /// asked for it, and skipping the gate is the race.
        /// </para>
        /// </summary>
        internal bool CurrentSegmentIsGatedAtDepthOne => FramesInFlight == 1;

        // The depth-one gate itself. Called under the submit lock, which is where it has to be: the owner it reads
        // is written under that lock. It CAN block, which is the one exception to this type's never-waits property
        // for the off-timeline write, and is why the property above is spelled out rather than inlined.
        //
        // The lock is HELD across this wait, unlike the claim gate's, and the claim gate's deadlock argument does
        // not apply: the value being waited for was registered by a submission that has already been made, so
        // nothing that could end this wait is being shut out. What a caller pays is that no other submit proceeds
        // while it waits, at a depth nothing ships on.
        void WaitForCurrentSegmentUnderLock()
        {
            ulong target = _segmentOwner[_segment];
            if (target == 0) return;
            if (_timeline.CompletedValue >= target) return;

            long start = Stopwatch.GetTimestamp();
            _timeline.WaitForValue(target);
            long elapsed = Stopwatch.GetTimestamp() - start;

            Interlocked.Increment(ref _stallCount);
            _backpressure.Record(elapsed);
        }

        // Whether one non-current segment has to take this write as a patch rather than as a copy. Called under
        // the submit lock, once per segment, sharing ONE completion read across the whole pass.
        //
        // TWO REASONS, AND ORDER IS THE SECOND ONE. A segment whose owner value has not been reached is still
        // being read by the GPU. A segment that already has a patch queued takes this write as a patch too even
        // though it is free, because a direct copy would be overwritten by the older queued bytes when the next
        // claim replays them.
        bool DeferralIsOwed(MetalUniformRing ring, int segment, ref ulong completed, ref bool polled)
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

        // Apply one segment's pending patches. It walks the patched rings alone, not every ring in the device, and
        // a ring leaves that registry as soon as it owes nothing anywhere.
        void ApplyPendingPatchesUnderLock(int segment)
        {
            for (int i = _patchedRings.Count - 1; i >= 0; i--)
            {
                MetalUniformRing ring = _patchedRings[i];
                if (!ring.HasPendingPatchesFor(segment)) continue;

                _patchesApplied += ring.ApplyPendingPatchesUnderLock(segment);

                if (!ring.HasPendingPatches) _patchedRings.RemoveAt(i);
            }
        }
    }
}
