using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE OFF-TIMELINE WRITE UNDER THE RING (decision U5, section 6.4), which is the device-level
    /// <c>UpdateBuffer</c> on a ring-backed uniform buffer, and the resolution of
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/484. The mechanism it defers WITH is
    /// <see cref="D3D11RingPendingPatchTests"/>.
    /// <para>
    /// WHAT #484 WAS. The write reached the CURRENT segment alone, so a value written once at load time survived
    /// until the frame index wrapped back round and no further, and two frames out of every three bound memory
    /// nothing had ever written. Nothing threw, nothing logged, and the wrong pixels were several frames from
    /// their cause. <c>ModelRenderer</c>'s splat-params tail is the shipped consumer that writes exactly that way,
    /// and <see cref="TheSplatParamsShape_WrittenOnceAtLoad_ReadsBackInEveryFrame"/> is that shape pinned here.
    /// </para>
    /// <para>
    /// WHAT IT COSTS, AND WHY NOTHING HERE WAITS. Writing every segment means writing segments an earlier frame
    /// may still be executing, which is the silent corruption decision U5's fence gate exists to prevent. The
    /// first cut waited for those segments, and a reviewer's probe showed the wait STARVES: in the GPU-bound
    /// steady state at least one non-current segment is always in flight, so "all of them are free at once" is
    /// never true and the writer chases the pipeline forever. The shipped shape defers instead. A segment still
    /// in flight gets a PENDING PATCH the next acquire of that segment applies, and the writer returns
    /// immediately, always. <see cref="TheGpuBoundSteadyState_ReturnsImmediatelyAndReachesEverySegment"/> is that
    /// probe kept as a test.
    /// </para>
    /// <para>
    /// Device-free on every operating system, like the rest of the ring's tests: the completion timeline is an
    /// interface and the ring's two native calls are another, so the fake memory is a pinned array a test reads
    /// the actual bytes back out of.
    /// </para>
    /// </summary>
    public sealed class D3D11RingOffTimelineTests
    {
        // ModelRenderer's shape, scaled to the smallest thing that still has both halves: a head rewritten every
        // frame and a tail written once at load, in one uniform buffer. The real one is a 9472-byte frame block
        // followed by the splat params, and nothing about the defect depends on the sizes.
        const uint HeadBytes = 256;
        const uint TailBytes = 256;
        const uint BufferBytes = HeadBytes + TailBytes;
        const int Segments = 3;

        // A wall-clock smoke bound on a call that is supposed to be a bounded pass over three segments. Absurdly
        // generous for the work involved, on purpose: it is here to catch a return to WAITING, which does not
        // take milliseconds, it takes forever. A loaded runner cannot fail it.
        static readonly TimeSpan ImmediateBudget = TimeSpan.FromSeconds(5);
        static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(30);

        // ---- the write reaches every segment ---------------------------------------------------------------

        /// <summary>
        /// A LOAD-TIME WRITE LANDS IN EVERY SEGMENT, BYTE FOR BYTE, at the same offset within each, and it costs
        /// no completion read at all because nothing has been submitted yet. That is the whole fix in one fact:
        /// the same call on the Veldrid backend wrote the buffer's only copy, and here it writes all of them, so
        /// the value persists for the buffer's life either way.
        /// </summary>
        [Fact]
        public void ALoadTimeWrite_LandsInEverySegmentByteForByte()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: BufferBytes, framesInFlight: Segments);
            byte[] payload = Pattern(64, seed: 0x11);

            harness.Allocator.UpdateBuffer(harness.Ring, HeadBytes, payload);

            for (int segment = 0; segment < Segments; segment++)
            {
                ReadOnlySpan<byte> landed = harness.Memory.Segment(
                    harness.Ring.FrameBaseBytes(segment) + HeadBytes, (uint)payload.Length);
                Assert.True(landed.SequenceEqual(payload),
                    "Segment " + segment.ToString(CultureInfo.InvariantCulture)
                    + " did not receive the load-time write byte for byte.");
            }

            Assert.Equal(0, harness.Completion.PollCount);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Deferred);
        }

        /// <summary>
        /// THE #484 REGRESSION, NAMED FOR THE SHAPE THAT FOUND IT. <c>ModelRenderer.CreateSplatParamsUbo</c>
        /// creates one uniform buffer of frame block plus splat params, writes the params ONCE at load through
        /// the device-level <c>UpdateBuffer</c>, and rewrites only the frame block each frame. Under the ring as
        /// it shipped, the params were read as never-written memory on two frames out of every three.
        /// <para>
        /// Seven frames is more than two full wraps of three segments, so a value that survived only until the
        /// index came back round would be caught on the fourth frame rather than passing by luck. The per-frame
        /// head write is here rather than being left out because it is what proves the two halves coexist: a
        /// record-time write to the head must not disturb the tail in any segment.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSplatParamsShape_WrittenOnceAtLoad_ReadsBackInEveryFrame()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: BufferBytes, framesInFlight: Segments);
            byte[] tail = Pattern((int)TailBytes, seed: 0x40);
            byte[] head = Pattern((int)HeadBytes, seed: 0x90);

            // Load time: one device-level write of the params tail, exactly as CreateSplatParamsUbo does it.
            harness.Allocator.UpdateBuffer(harness.Ring, HeadBytes, tail);

            for (int frame = 0; frame < 7; frame++)
            {
                // The per-frame refresh, which touches the head alone (WriteFrameUniformsTo).
                harness.Ring.Write(0, head);

                AssertCurrentSegmentCarriesTail(harness, tail, frame);

                harness.Allocator.UnmapMappedRings();   // a submit would do this
                harness.Allocator.BeginFrame();
            }
        }

        /// <summary>
        /// THE SAME REGRESSION WITH THE PIPELINE ALREADY FULL, so the value reaches two of its three segments as
        /// a PATCH rather than as a copy and the reader cannot tell. A load-time write meets no in-flight segment
        /// at all, so on its own it never exercises the deferral, and a renderer that creates a buffer mid-run
        /// (a terrain chunk streaming in) is the case that would.
        /// <para>
        /// The frame loop drives the completion timeline two submissions behind, which leaves one segment busy at
        /// every instant, and the assertion is unchanged in meaning: every frame binds a segment whose params are
        /// the ones written once.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSplatParamsShape_WrittenWithThePipelineFull_ReadsBackInEveryFrame()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: BufferBytes, framesInFlight: Segments);
            byte[] tail = Pattern((int)TailBytes, seed: 0x40);
            byte[] head = Pattern((int)HeadBytes, seed: 0x90);
            var pipeline = new SteadyStatePipeline(harness, gpuBehindBy: 2);

            for (int frame = 0; frame < 4; frame++) pipeline.RunFrame();
            Assert.True(pipeline.SegmentsInFlight() >= 1, "The pipeline was not full, so nothing was deferred.");

            harness.Allocator.UpdateBuffer(harness.Ring, HeadBytes, tail);
            Assert.True(harness.Allocator.OffTimelinePatches.Deferred >= 1,
                "No segment was deferred, so this is the load-time case again rather than the mid-run one.");

            for (int frame = 0; frame < 7; frame++)
            {
                harness.Ring.Write(0, head);
                AssertCurrentSegmentCarriesTail(harness, tail, frame);
                pipeline.RunFrame();
            }

            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Outstanding);
        }

        /// <summary>
        /// A RECORD-TIME WRITE IS UNCHANGED AND STILL REACHES THE CURRENT SEGMENT ALONE. That is the other half of
        /// the decision and the reason this is not simply "the ring replicates writes": every shipped record-time
        /// uniform write is unconditional per frame, so replicating it would be three memcpys for a value the next
        /// frame overwrites, on the one path the ring exists to make cheap.
        /// </summary>
        [Fact]
        public void ARecordTimeWrite_StillReachesTheCurrentSegmentAlone()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.BeginFrame();
            Assert.Equal(1, harness.Allocator.CurrentSegment);

            harness.Ring.Write(4, new byte[] { 0xD7, 0xD8 });

            Assert.Equal((byte)0xD7, harness.Memory.Bytes[harness.Ring.FrameBaseBytes(1) + 4]);
            Assert.Equal((byte)0, harness.Memory.Bytes[harness.Ring.FrameBaseBytes(0) + 4]);
            Assert.Equal((byte)0, harness.Memory.Bytes[harness.Ring.FrameBaseBytes(2) + 4]);
        }

        /// <summary>A write past the end of the LOGICAL buffer is refused before anything is mapped, and it
        /// matters more on this path than on the record-time one: the off-timeline write walks every segment, so
        /// an overrun would spill each of them into the next rather than only the last.</summary>
        [Fact]
        public void AWritePastTheLogicalEnd_IsRefusedBeforeAnythingIsMapped()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => harness.Allocator.UpdateBuffer(harness.Ring, 250, new byte[8]));

            Assert.Equal(0, harness.Memory.MapCount);
        }

        /// <summary>An empty write maps nothing, copies nothing and queues nothing, the same as the record-time
        /// path. Three segments of zero bytes is still zero bytes.</summary>
        [Fact]
        public void AnEmptyWrite_MapsNothing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.UpdateBuffer(harness.Ring, 0, ReadOnlySpan<byte>.Empty);

            Assert.Equal(0, harness.Memory.MapCount);
            Assert.False(harness.Ring.IsMapped);
            Assert.Equal(0, harness.Ring.PendingPatchCount);
        }

        // ---- the fence gate on the segments this added, and the fact that it never waits -------------------

        /// <summary>
        /// A SEGMENT STILL IN FLIGHT IS DEFERRED AND THE CALL RETURNS, with ONE completion poll and no spinning.
        /// This is the claim the whole redesign rests on, so it is asserted three ways at once: the poll count is
        /// exactly one (a wait would be thousands, and the fake's runaway guard would throw at ten thousand), the
        /// wall clock is inside a budget a spin against a timeline nobody advances could never meet, and the busy
        /// segment is left holding a queued patch rather than the bytes.
        /// <para>
        /// NOTHING EVER ADVANCES THE TIMELINE HERE, deliberately. The first cut's retry loop would have spun in
        /// this exact setup until the fake threw, which is what makes this a mutation detector rather than a
        /// description.
        /// </para>
        /// </summary>
        [Fact]
        public void AWriteMeetingASegmentInFlight_ReturnsImmediatelyAndDefersThatSegment()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(7);   // segment 0's last submission signalled 7, and the GPU is at 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            byte[] payload = Pattern(32, seed: 0x60);

            var clock = Stopwatch.StartNew();
            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
            clock.Stop();

            Assert.True(clock.Elapsed < ImmediateBudget,
                "The off-timeline write took " + clock.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + " seconds against a segment nothing ever frees, so it is waiting rather than deferring.");
            Assert.Equal(1, harness.Completion.PollCount);

            // The two free segments took the bytes, the busy one took a patch.
            AssertSegmentCarries(harness, segment: 1, offsetBytes: 0, payload);
            AssertSegmentCarries(harness, segment: 2, offsetBytes: 0, payload);
            Assert.True(IsAllZero(harness.Memory.Segment(harness.Ring.FrameBaseBytes(0), (uint)payload.Length)),
                "Segment 0 was written while the GPU was still reading it, which is the corruption the gate exists "
                + "to prevent.");

            Assert.Equal(1, harness.Ring.PendingPatchCountFor(0));
            Assert.Equal(1, harness.Ring.PendingPatchCount);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Deferred);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Applied);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Outstanding);
        }

        /// <summary>
        /// THE GPU-BOUND STEADY STATE, which is the shape a reviewer's probe used to prove the first cut starved.
        /// A frame loop runs at pipeline depth with the completion timeline exactly <c>FramesInFlight</c> minus one
        /// submissions behind, which is the deepest it goes without the frame boundary itself stalling, so every
        /// segment other than the current one is in flight at EVERY instant and the
        /// "all of them are free at once" condition the retry loop waited for is never satisfiable. The writer
        /// still returns, in one poll, and the value is then visible in every segment the frame loop goes on to
        /// acquire.
        /// <para>
        /// DRIVEN SYNCHRONOUSLY RATHER THAN FROM A SECOND THREAD, on purpose. A frozen timeline is the WORST case
        /// for the writer rather than a weaker one, since waiting could not have made progress even in principle,
        /// and driving it here buys an exact poll count and a deterministic verdict instead of a race that has to
        /// be sampled. The old code fails this by spinning until the fake's runaway guard throws, so the failure
        /// is named rather than a hang.
        /// </para>
        /// </summary>
        [Fact]
        public void TheGpuBoundSteadyState_ReturnsImmediatelyAndReachesEverySegment()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            var pipeline = new SteadyStatePipeline(harness, gpuBehindBy: Segments - 1);
            byte[] payload = Pattern(48, seed: 0x7C);

            for (int frame = 0; frame < 12; frame++) pipeline.RunFrame();
            Assert.Equal(Segments - 1, pipeline.SegmentsInFlight());

            int pollsBefore = harness.Completion.PollCount;
            var clock = Stopwatch.StartNew();
            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
            clock.Stop();

            Assert.True(clock.Elapsed < ImmediateBudget,
                "The off-timeline write took " + clock.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)
                + " seconds in the steady state, where every non-current segment is in flight at every instant. "
                + "That is the starvation the pending-patch design exists to delete.");
            Assert.Equal(1, harness.Completion.PollCount - pollsBefore);
            Assert.Equal(Segments - 1, harness.Allocator.OffTimelinePatches.Deferred);

            // Every segment the loop goes on to open carries it, from the first wrap onwards. Two more wraps are
            // run so a value that survived only until the index came back round would be caught.
            for (int frame = 0; frame < 2 * Segments; frame++)
            {
                pipeline.RunFrame();
                AssertSegmentCarries(harness, harness.Allocator.CurrentSegment, offsetBytes: 0, payload);
            }

            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Outstanding);
        }

        /// <summary>
        /// THE CURRENT SEGMENT IS COPIED WITHOUT THE GATE, and it is the only ungated one. Gating it would change
        /// the documented semantic that the write lands when it is called and the next submitted list reads it,
        /// and deferring it would be worse still, because the current segment is bound by the very next submit
        /// and its own next acquire is a whole wrap away. The exposure that leaves is the one the call already had
        /// and is not what #484 was about.
        /// <para>
        /// Asserted with a completion value the fake never reaches, and with a poll count of zero: the other two
        /// segments have never been submitted with, so nothing here even asks the timeline a question.
        /// </para>
        /// </summary>
        [Fact]
        public void TheCurrentSegment_IsCopiedWithoutGatingOnItsOwnSubmission()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(11);   // the CURRENT segment, and the GPU never reaches 11
            byte[] payload = { 0xC0, 0xC1 };

            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);

            Assert.Equal(0, harness.Completion.PollCount);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Deferred);
            AssertEverySegmentCarries(harness, 0, payload);
        }

        /// <summary>
        /// A CALLER THAT ALREADY HOLDS THE SUBMIT LOCK IS LEGAL, and this is the case the first cut DEADLOCKED on:
        /// its retry loop released the lock, spun and retook it, and a reentrant caller's release freed nothing,
        /// so it spun forever holding the lock against the submission that would have ended the wait. With no wait
        /// there is nothing to deadlock: the acquisition inside is a free <see cref="Monitor"/> re-entry.
        /// <para>
        /// A WATCHDOG RATHER THAN A PLAIN CALL, because the failure this pins is a hang and a hung test tells
        /// nobody anything. The work runs on a background thread with a segment deliberately in flight, and a
        /// join budget turns the deadlock into a named failure.
        /// </para>
        /// </summary>
        [Fact]
        public void AReentrantCaller_HoldingTheSubmitLock_Completes()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(4);   // segment 0, and the GPU never reaches 4
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated

            byte[] payload = Pattern(16, seed: 0xA0);
            Exception? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    lock (harness.SubmitLock)
                    {
                        harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            })
            { IsBackground = true, Name = "reentrant-off-timeline-writer" };

            worker.Start();
            Assert.True(worker.Join(JoinBudget),
                "A device-level UpdateBuffer made while already holding the submit lock never returned, so the "
                + "off-timeline path is waiting for something a reentrant caller can never let happen.");
            Assert.Null(failure);

            AssertSegmentCarries(harness, segment: 1, offsetBytes: 0, payload);
            Assert.Equal(1, harness.Ring.PendingPatchCountFor(0));
        }

        // ---- the PerWrite scope (the immediate driver's fallback lever) ------------------------------------

        /// <summary>
        /// UNDER <see cref="D3D11RingMapScope.PerWrite"/> THE WHOLE WRITE IS ONE CRITICAL SECTION: one map, every
        /// segment's copy, one unmap, all inside a single hold of the submit lock. That scope exists because it is
        /// the only one holding the map, the copy and the unmap atomically, and a write that mapped and unmapped
        /// per segment would hand that property back three times over.
        /// <para>
        /// The record-time write under the same scope is unchanged and still current-segment only, which is the
        /// second half here: the scope decides how long the mapping is held, and the CALL decides how many
        /// segments it reaches.
        /// </para>
        /// <para>
        /// The fake's submit lock is deliberately NOT wired up: its recursion probe briefly exits and re-enters
        /// the monitor, which would punch a hole in exactly the critical section this test is about.
        /// </para>
        /// </summary>
        [Fact]
        public void UnderPerWriteScope_AnOffTimelineWrite_CoversEverySegmentInOneCriticalSection()
        {
            using var harness = new D3D11RingHarness(
                sizeInBytes: 256, framesInFlight: Segments, mapScope: D3D11RingMapScope.PerWrite);
            byte[] payload = Pattern(16, seed: 0x20);

            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);

            Assert.Equal(1, harness.Memory.MapCount);
            Assert.Equal(1, harness.Memory.UnmapCount);
            Assert.False(harness.Ring.IsMapped);
            Assert.Equal(0, harness.Allocator.MappedRingCount);
            AssertEverySegmentCarries(harness, 0, payload);

            // And the record-time write beside it is still one segment, still one map and unmap pair.
            harness.Allocator.BeginFrame();
            harness.Ring.Write(64, new byte[] { 0x99 });

            Assert.Equal(2, harness.Memory.MapCount);
            Assert.Equal(2, harness.Memory.UnmapCount);
            Assert.Equal((byte)0x99, harness.Memory.Bytes[harness.Ring.FrameBaseBytes(1) + 64]);
            Assert.Equal((byte)0, harness.Memory.Bytes[harness.Ring.FrameBaseBytes(0) + 64]);
            Assert.Equal((byte)0, harness.Memory.Bytes[harness.Ring.FrameBaseBytes(2) + 64]);
        }

        /// <summary>
        /// AND A DEFERRED PATCH KEEPS THAT SCOPE'S BARGAIN TOO: the frame boundary that replays one maps, copies
        /// and unmaps inside its own single hold, so the mapping is never left outstanding on a driver that
        /// cannot tolerate one across a draw. The patch path is the newest way to reach a mapping, so it is the
        /// one most likely to leak that property without a test saying so.
        /// </summary>
        [Fact]
        public void UnderPerWriteScope_ADeferredPatch_IsAppliedInItsOwnMapAndUnmapPair()
        {
            using var harness = new D3D11RingHarness(
                sizeInBytes: 256, framesInFlight: Segments, mapScope: D3D11RingMapScope.PerWrite);
            byte[] payload = Pattern(16, seed: 0x30);

            harness.Allocator.OnSubmitted(6);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
            int mapsBeforeApply = harness.Memory.MapCount;

            harness.Completion.Completed = 6;
            harness.Allocator.BeginFrame();     // 2, nothing queued there
            Assert.Equal(mapsBeforeApply, harness.Memory.MapCount);

            harness.Allocator.BeginFrame();     // 0, which drains
            Assert.Equal(mapsBeforeApply + 1, harness.Memory.MapCount);
            Assert.Equal(harness.Memory.MapCount, harness.Memory.UnmapCount);
            Assert.False(harness.Ring.IsMapped);
            Assert.Equal(0, harness.Allocator.MappedRingCount);
            AssertSegmentCarries(harness, segment: 0, offsetBytes: 0, payload);
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        static void AssertCurrentSegmentCarriesTail(D3D11RingHarness harness, byte[] tail, int frame)
        {
            ReadOnlySpan<byte> paramsNow = harness.Memory.Segment(
                harness.Ring.CurrentFrameBaseBytes + HeadBytes, TailBytes);
            Assert.True(paramsNow.SequenceEqual(tail),
                "Frame " + frame.ToString(CultureInfo.InvariantCulture) + " bound segment "
                + harness.Allocator.CurrentSegment.ToString(CultureInfo.InvariantCulture)
                + ", whose splat params were not the ones written once at load. That is #484.");
        }

        internal static void AssertEverySegmentCarries(D3D11RingHarness harness, uint offsetBytes, byte[] expected)
        {
            for (int segment = 0; segment < harness.Allocator.FramesInFlight; segment++)
                AssertSegmentCarries(harness, segment, offsetBytes, expected);
        }

        internal static void AssertSegmentCarries(
            D3D11RingHarness harness, int segment, uint offsetBytes, byte[] expected)
        {
            ReadOnlySpan<byte> landed = harness.Memory.Segment(
                harness.Ring.FrameBaseBytes(segment) + offsetBytes, (uint)expected.Length);
            Assert.True(landed.SequenceEqual(expected),
                "Segment " + segment.ToString(CultureInfo.InvariantCulture)
                + " does not carry the off-timeline write.");
        }

        static bool IsAllZero(ReadOnlySpan<byte> bytes)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != 0) return false;
            }

            return true;
        }

        // A payload whose every byte differs from its neighbours, so a copy that landed at the wrong offset or
        // ran short shows up as a mismatch rather than as one repeated value that happens to line up.
        internal static byte[] Pattern(int length, byte seed)
        {
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i);
            return bytes;
        }
    }

    /// <summary>
    /// A FRAME LOOP AT PIPELINE DEPTH, driven synchronously, which is what makes "the GPU is behind" a thing a
    /// device-free test can assert against rather than describe. Each frame submits, moves the completion timeline
    /// to <c>submitted - gpuBehindBy</c>, unmaps the way a real submit does, and opens the next frame.
    /// <para>
    /// <c>gpuBehindBy</c> OF <c>FramesInFlight</c> MINUS ONE is the interesting setting, and it is the deepest
    /// the pipeline goes without the frame boundary itself stalling. The segment being opened was last submitted
    /// exactly that many frames ago, so its gate is satisfied with nothing to spare, and every OTHER segment is
    /// still in flight at that instant. That is the steady state in which "wait until they are all free" never
    /// becomes true, which is what starved the first cut of the off-timeline write.
    /// </para>
    /// </summary>
    internal sealed class SteadyStatePipeline
    {
        readonly D3D11RingHarness _harness;
        readonly int _gpuBehindBy;
        ulong _submitted;

        internal SteadyStatePipeline(D3D11RingHarness harness, int gpuBehindBy)
        {
            _harness = harness;
            _gpuBehindBy = gpuBehindBy;
        }

        /// <summary>How many segments other than the current one are still owned by a submission the GPU has not
        /// reached. The number the whole construction exists to keep above zero.</summary>
        internal int SegmentsInFlight()
        {
            ulong completed = _harness.Completion.Completed;
            int busy = 0;
            for (int segment = 0; segment < _harness.Allocator.FramesInFlight; segment++)
            {
                if (segment == _harness.Allocator.CurrentSegment) continue;
                if (_harness.Allocator.SegmentOwner(segment) > completed) busy++;
            }

            return busy;
        }

        /// <summary>Submit this frame, advance the GPU to its lagging position, and open the next frame.</summary>
        internal void RunFrame()
        {
            _submitted++;
            _harness.Allocator.OnSubmitted(_submitted);
            _harness.Completion.Completed =
                _submitted > (ulong)_gpuBehindBy ? _submitted - (ulong)_gpuBehindBy : 0UL;

            _harness.Allocator.UnmapMappedRings();
            _harness.Allocator.BeginFrame();
        }
    }
}
