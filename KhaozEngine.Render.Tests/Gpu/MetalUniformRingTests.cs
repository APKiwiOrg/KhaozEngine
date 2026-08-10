using System;
using System.Threading;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE THREE ROWS OF SECTION 9.4 THAT ARE THIS BACKEND'S OWN, plus the geometry underneath them, device-free.
    /// The other seven run against all three backends through <see cref="GpuUniformRingSharedTests"/>.
    ///
    /// <para><b>Stride</b> is <see cref="MetalRingStrideTests"/>. <b>Lock legality</b> and <b>Ordering</b> are
    /// here, because each backend has its own lock and its own deadlock to not have, and the shared interface
    /// cannot see either.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Everything here is engine arithmetic over a pointer, so a failure is a
    /// bad segment base, a bounds check that lets a write spill into the next frame's segment, or a lock rule
    /// broken. None of them is visible on a device: they present as another frame's uniforms being subtly wrong,
    /// several frames from the cause.</para>
    /// </summary>
    public sealed class MetalUniformRingTests : IDisposable
    {
        readonly MetalRingHarness _harness = new();

        /// <inheritdoc/>
        public void Dispose() => _harness.Dispose();

        [Fact]
        public void TheAllocationIsTheStrideTimesTheDepthAndTheLogicalSizeIsWhatTheCallerAsked()
        {
            MetalUniformRing ring = _harness.NewRing(200, out byte[] backing);

            Assert.Equal(200u, ring.SizeInBytes);
            Assert.Equal(256u, ring.SegmentStrideBytes);
            Assert.Equal(768ul, ring.TotalBytes);
            Assert.Equal(768, backing.Length);
            Assert.Equal(MetalFramesInFlight.Default, ring.FramesInFlight);
        }

        [Fact]
        public void SegmentBasesAreEvenlySpacedAndSegmentZeroStartsAtZero()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);

            Assert.Equal(0ul, ring.SegmentBaseBytes(0));
            Assert.Equal(256ul, ring.SegmentBaseBytes(1));
            Assert.Equal(512ul, ring.SegmentBaseBytes(2));
        }

        [Fact]
        public void ASegmentThatDoesNotExistIsRefusedByName()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);

            Assert.Throws<ArgumentOutOfRangeException>(() => ring.SegmentBaseBytes(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => ring.SegmentBaseBytes(MetalFramesInFlight.Default));
        }

        /// <summary>
        /// A NULL <c>contents()</c> POINTER IS REFUSED AT CONSTRUCTION. Every buffer this backend creates is
        /// Shared and a Shared buffer always answers a real pointer, so a zero means the storage mode changed
        /// rather than that an allocation failed, and sub-allocating into address zero is the one outcome worth
        /// refusing loudly.
        /// </summary>
        [Fact]
        public void ANullContentsPointerIsRefusedByName()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => new MetalUniformRing(_harness.Rings, IntPtr.Zero, 256));

            Assert.Contains("MTLStorageModeShared", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A WRITE PAST THE LOGICAL SIZE IS REFUSED, and the reason is what makes it worth a check rather than a
        /// comment: without it the bytes spill into the NEXT frame's segment, which the GPU may be reading right
        /// now, so it would present as a different frame's uniforms being wrong. The incumbent's own
        /// <c>UpdateBufferCore</c> has no bound check at all.
        /// </summary>
        [Fact]
        public void AWriteThatLeavesTheLogicalBufferIsRefusedByName()
        {
            MetalUniformRing ring = _harness.NewRing(200, out _);

            ArgumentOutOfRangeException thrown =
                Assert.Throws<ArgumentOutOfRangeException>(() => ring.Write(0, 192, new byte[16]));

            Assert.Contains("next frame's segment", thrown.Message, StringComparison.Ordinal);

            // The last byte the caller owns is still writable, so the refusal is exact rather than conservative.
            ring.Write(0, 192, new byte[8]);
        }

        /// <summary>
        /// THE LOGICAL SIZE IS THE BOUND AND THE STRIDE IS NOT, which is the mistake available here: a
        /// 200-byte buffer has a 256-byte stride, and the 56 bytes of slack belong to nobody. A caller writing
        /// into them would be writing into padding on every segment except at the point a future change made the
        /// stride equal to the size.
        /// </summary>
        [Fact]
        public void TheSlackBetweenTheSizeAndTheStrideIsNotWritable()
        {
            MetalUniformRing ring = _harness.NewRing(200, out _);

            Assert.Throws<ArgumentOutOfRangeException>(() => ring.Write(0, 200, new byte[1]));
        }

        /// <summary>
        /// SECTION 9.4's LOCK LEGALITY ROW: the off-timeline write is legal from a caller who ALREADY HOLDS the
        /// submit lock, and it is legal because it never waits for anything. The lock is a
        /// <see cref="Monitor"/>, so the acquisition inside is a free re-entry, and there is nothing inside that
        /// could wait for work only another thread could do.
        /// <para>
        /// This is a shape a real caller has: <c>MetalGpuDevice</c> holds the submit lock across a commit, and a
        /// future row that wrote a uniform buffer from inside it would deadlock instantly on a design that waited
        /// there. Asserting it costs one test and is the only place the property is written down as behaviour.
        /// </para>
        /// </summary>
        [Fact]
        public void TheOffTimelineWriteIsLegalFromInsideTheSubmitLock()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);
            byte[] payload = { 1, 2, 3, 4 };

            // Two frames closed against work the GPU has not reached, so the gate would have something to wait
            // for if it ever waited.
            SubmitWork(5);
            _harness.Rings.BeginRecording();
            SubmitWork(6);
            _harness.Rings.BeginRecording();

            lock (_harness.SubmitLock)
            {
                _harness.Rings.UpdateBuffer(ring, 0, payload);
            }

            Assert.Equal(payload, ring.ReadSegment(_harness.Rings.CurrentSegment, 0, payload.Length));
            Assert.Equal(MetalFramesInFlight.Default - 1, ring.PendingPatchCount);
        }

        /// <summary>
        /// SECTION 9.4's ORDERING ROW, as far as anything can assert it: the ring reads a COMPLETION value rather
        /// than a submit receipt. The two differ by exactly the window between a commit returning and the GPU
        /// finishing, and a ring gated on the receipt hands a segment back inside that window.
        /// <para>
        /// AND THIS IS THE WIRING PROOF FOR THE STALL COUNTER, which is worth saying here because the obvious
        /// candidate is not. <c>MetalRingGpuTests.TheStallCounterCountsARealSegmentWait</c> runs a real frame loop
        /// ahead of a real GPU and asserts only that the two readings agree, so it passes at zero and cannot tell
        /// "never blocked" from "never wired". This row can: the submission is registered, the completion counter
        /// is left behind it, and the wrap has to block, so the count is asserted at exactly one. Deterministic,
        /// and it runs on every leg.
        /// </para>
        /// </summary>
        [Fact]
        public void TheGateReadsCompletionAndNotTheSubmitReceipt()
        {
            SubmitWork(7);

            // Registered as submitted, and the counter has NOT moved: a receipt-gated ring would already consider
            // this segment free.
            Assert.Equal(7ul, _harness.Timeline.LastSubmitted);
            Assert.Equal(0ul, _harness.Timeline.CompletedValue);

            for (int frame = 0; frame < MetalFramesInFlight.Default - 1; frame++) _harness.Rings.BeginRecording();
            Assert.Equal(0, _harness.Rings.StallCount);

            _harness.Rings.BeginRecording();

            Assert.Equal(1, _harness.Rings.StallCount);
            Assert.Equal(1, _harness.Backpressure.Totals.Count);
        }

        /// <summary>
        /// A RING WHOSE BUFFER IS DISPOSED LEAVES THE ALLOCATOR AND TAKES ITS PATCHES WITH IT, counted as
        /// dropped. Reference counting keeps the ALLOCATION alive while a submitted buffer reads it (M-H3) and
        /// says nothing about a CPU write scheduled for a future frame boundary, which is what a pending patch is.
        /// </summary>
        [Fact]
        public void ForgettingARingDropsItsPatchesAndCountsThem()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);

            SubmitWork(5);
            _harness.Rings.BeginRecording();
            SubmitWork(6);
            _harness.Rings.BeginRecording();

            _harness.Rings.UpdateBuffer(ring, 0, new byte[] { 9, 9, 9, 9 });
            Assert.Equal(MetalFramesInFlight.Default - 1, ring.PendingPatchCount);

            _harness.Rings.Forget(ring);

            Assert.Equal(0, ring.PendingPatchCount);

            MetalRingPatchStats stats = _harness.Rings.OffTimelinePatches;
            Assert.Equal(MetalFramesInFlight.Default - 1, stats.Deferred);
            Assert.Equal(MetalFramesInFlight.Default - 1, stats.Dropped);
            Assert.Equal(0, stats.Outstanding);
        }

        /// <summary>A caller already inside the submit lock cannot open a frame, because that is the ONE member
        /// that blocks: it would hold the frame's one serialised point for up to a frame and shut out the
        /// submission that would end the wait.</summary>
        [Fact]
        public void BeginRecordingFromInsideTheSubmitLockIsRefusedByName()
        {
            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() =>
            {
                lock (_harness.SubmitLock) _harness.Rings.BeginRecording();
            });

            Assert.Contains("held the submit lock", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// AT THE FLOOR THE DEVICE-LEVEL WRITE GATES ITS ONE SEGMENT, which is the one place this backend trades
        /// the never-blocks property for correctness. At a depth of one the loop's other-segment branch never runs
        /// and the current-segment copy is the whole write, so an ungated one would be M-M7's race with nothing
        /// between it and the GPU.
        /// <para>
        /// THE ORDERING IS THE ASSERTION, not merely the stall count: the segment is read from INSIDE the wait,
        /// through the fake event's hook, and it still holds the old bytes there. A gate that ran after the copy
        /// would count a stall and still have raced.
        /// </para>
        /// </summary>
        [Fact]
        public void AtDepthOneTheDeviceLevelWriteWaitsForTheSegmentBeforeCopying()
        {
            using var harness = new MetalRingHarness(framesInFlight: 1);
            MetalUniformRing ring = harness.NewRing(256, out _);
            byte[] payload = { 7, 7, 7, 7 };

            // One submission that read the only segment, and a GPU that has reached nothing.
            harness.Timeline.EncodeSignalForSubmit(IntPtr.Zero);
            harness.Timeline.RegisterSubmitted(1);
            harness.Rings.RecordSegmentOwner(0, 1);

            byte[]? duringTheWait = null;
            harness.Event.OnWait = () => duringTheWait ??= ring.ReadSegment(0, 0, payload.Length);

            harness.Rings.UpdateBuffer(ring, 0, payload);

            Assert.Equal(new byte[payload.Length], duringTheWait);
            Assert.Equal(payload, ring.ReadSegment(0, 0, payload.Length));
            Assert.Equal(1ul, harness.Event.LastWaitValue);
            Assert.Equal(1, harness.Rings.StallCount);
            Assert.Equal(0, ring.PendingPatchCount);
        }

        /// <summary>
        /// AND IT IS THE FLOOR ALONE. At any other depth the current-segment copy stays ungated, because the next
        /// claim of that segment is a whole wrap away and gating it would change the documented semantic that the
        /// write lands when it is called. The control matters: a gate applied at every depth would be a wait on
        /// the one path #484 exists to keep non-blocking.
        /// </summary>
        [Fact]
        public void AboveTheFloorTheDeviceLevelWriteStillNeverWaits()
        {
            MetalUniformRing ring = _harness.NewRing(256, out _);

            SubmitWork(4);

            _harness.Rings.UpdateBuffer(ring, 0, new byte[] { 3, 3, 3, 3 });

            Assert.Equal(0, _harness.Rings.StallCount);
            Assert.Null(_harness.Event.LastWaitValue);
            Assert.False(_harness.Rings.CurrentSegmentIsGatedAtDepthOne);
        }

        /// <summary>The allocator refuses a depth outside the knob's own range, so a caller that read the
        /// environment itself cannot build a ring the env var would have clamped.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(MetalFramesInFlight.Maximum + 1)]
        public void ADepthOutsideTheKnobsRangeIsRefusedByName(int framesInFlight)
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => new MetalRingAllocator(framesInFlight, _harness.Timeline, _harness.Backpressure,
                    new object()));

            Assert.Contains(MetalFramesInFlight.EnvVarName, thrown.Message, StringComparison.Ordinal);
        }

        // The submit path's whole observable effect, in the order MetalGpuDevice.SubmitOnMacOs takes it inside its
        // lock: allocate and encode, register the value the commit accepted, and tell the ring which segment that
        // submission reads. The last step is MetalCommandList.MarkSubmitted's, and the segment is the one the
        // recording captured, which for a test with no list open is the allocator's current.
        void SubmitWork(ulong value)
        {
            while (_harness.Timeline.LastAllocated < value)
            {
                _harness.Timeline.EncodeSignalForSubmit(IntPtr.Zero);
            }

            _harness.Timeline.RegisterSubmitted(value);
            _harness.Rings.RecordSegmentOwner(_harness.Rings.CurrentSegment, value);
        }
    }
}
