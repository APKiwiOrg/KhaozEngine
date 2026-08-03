using System;
using System.Globalization;
using System.Threading;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE OFF-TIMELINE WRITE UNDER THE RING (decision U5, section 6.4), which is the device-level
    /// <c>UpdateBuffer</c> on a ring-backed uniform buffer, and the resolution of
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/484.
    /// <para>
    /// WHAT #484 WAS. The write reached the CURRENT segment alone, so a value written once at load time survived
    /// until the frame index wrapped back round and no further, and two frames out of every three bound memory
    /// nothing had ever written. Nothing threw, nothing logged, and the wrong pixels were several frames from
    /// their cause. <c>ModelRenderer</c>'s splat-params tail is the shipped consumer that writes exactly that way,
    /// and <see cref="TheSplatParamsShape_WrittenOnceAtLoad_ReadsBackInEveryFrame"/> is that shape pinned here.
    /// </para>
    /// <para>
    /// WHAT IT COSTS, and why half this file is about waiting. Writing every segment means writing segments an
    /// earlier frame may still be executing, which is the silent corruption decision U5's fence gate exists to
    /// prevent, so the write is gated on the completion timeline exactly as <c>AcquireSegment</c> is. The gate can
    /// BLOCK, and this call could previously never block, so the shape of that wait is the thing to get right: it
    /// is a retry loop that reads its target under the submit lock, releases the lock, spins, and retakes. Waiting
    /// while holding that lock is the defect the whole threading contract is built to exclude, and
    /// <see cref="TheRetryLoop_NeverPollsInALoopWhileHoldingTheSubmitLock"/> is what makes that claim checkable.
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

        // The hand-driven wait's budget. Short, because each one is a deliberate pause during which a thread is
        // spinning, and long enough that a loaded runner does not read a scheduling hiccup as a returned write.
        static readonly TimeSpan StepBudget = TimeSpan.FromMilliseconds(200);
        static readonly TimeSpan JoinBudget = TimeSpan.FromSeconds(30);

        // ---- the write reaches every segment ---------------------------------------------------------------

        /// <summary>
        /// A LOAD-TIME WRITE LANDS IN EVERY SEGMENT, BYTE FOR BYTE, at the same offset within each, and it costs
        /// no completion read at all because nothing has been submitted yet. That is the whole fix in one fact:
        /// the same call on the Veldrid backend writes the buffer's only copy, and here it writes all of them, so
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
            Assert.Equal(0, harness.Allocator.OffTimelineWaits.Count);
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

                ReadOnlySpan<byte> paramsNow = harness.Memory.Segment(
                    harness.Ring.CurrentFrameBaseBytes + HeadBytes, TailBytes);
                Assert.True(paramsNow.SequenceEqual(tail),
                    "Frame " + frame.ToString(CultureInfo.InvariantCulture) + " bound segment "
                    + harness.Allocator.CurrentSegment.ToString(CultureInfo.InvariantCulture)
                    + ", whose splat params were not the ones written once at load. That is #484.");

                harness.Allocator.UnmapMappedRings();   // a submit would do this
                harness.Allocator.BeginFrame();
            }
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

        /// <summary>An empty write maps nothing and copies nothing, the same as the record-time path. Three
        /// segments of zero bytes is still zero bytes.</summary>
        [Fact]
        public void AnEmptyWrite_MapsNothing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.UpdateBuffer(harness.Ring, 0, ReadOnlySpan<byte>.Empty);

            Assert.Equal(0, harness.Memory.MapCount);
            Assert.False(harness.Ring.IsMapped);
        }

        // ---- the fence gate on the segments this added -----------------------------------------------------

        /// <summary>
        /// THE WAIT IS FOR THE HIGHEST OUTSTANDING VALUE ACROSS THE GATED SEGMENTS, not for the first one it
        /// finds and not for anything lower. The timeline is monotonic, so reaching the highest has reached every
        /// lower one with it, and one wait is enough where a per-segment wait would be one hold of the lock each.
        /// <para>
        /// PINNED BY DRIVING THE TIMELINE IN STEPS FROM ANOTHER THREAD, which is the only way to say WHICH value
        /// a waiter is waiting for. Segment 0 is owned by 5 and segment 1 by 9, and the write must not return at
        /// 0, must not return at 5 (where a wait for the first busy segment's value would), and must return at 9.
        /// A poll-count trigger cannot express the deliberate pause at 5, so the fake's runaway guard is turned
        /// off for this one test and the <c>finally</c> below is what bounds it instead.
        /// </para>
        /// </summary>
        [Fact]
        public void AnOffTimelineWrite_WaitsForExactlyTheFenceOfTheSegmentStillInFlight()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            harness.Completion.DrivenByHand = true;

            harness.Allocator.OnSubmitted(5);   // segment 0's last submission signalled 5
            harness.Allocator.BeginFrame();
            harness.Allocator.OnSubmitted(9);   // segment 1's signalled 9
            harness.Allocator.BeginFrame();
            Assert.Equal(2, harness.Allocator.CurrentSegment);
            Assert.Equal(0UL, harness.Completion.Completed);

            byte[] payload = Pattern(32, seed: 0x60);
            Exception? failure = null;
            using var finished = new ManualResetEventSlim(false);
            var writer = new Thread(() =>
            {
                try
                {
                    harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                finished.Set();
            })
            { IsBackground = true, Name = "off-timeline-writer" };

            try
            {
                writer.Start();

                Assert.False(finished.Wait(StepBudget),
                    "The off-timeline write returned while both gated segments were still in flight.");

                harness.Completion.Completed = 5;   // segment 0 is free, segment 1 is not
                Assert.False(finished.Wait(StepBudget),
                    "The off-timeline write returned as soon as the LOWEST outstanding value completed, so it "
                    + "would write a segment the GPU is still reading.");

                harness.Completion.Completed = 9;   // both are free
                Assert.True(finished.Wait(JoinBudget),
                    "The off-timeline write never returned after the highest outstanding value completed.");
            }
            finally
            {
                // Whatever happened above, release any waiter so a failing assertion does not leave a thread
                // spinning behind the rest of the suite.
                harness.Completion.Completed = ulong.MaxValue;
                writer.Join(JoinBudget);
            }

            Assert.Null(failure);
            AssertEverySegmentCarries(harness, 0, payload);

            // One wait, counted on the off-timeline counter rather than on the frame's.
            Assert.Equal(1, harness.Allocator.OffTimelineWaits.Count);
            Assert.Equal(0, harness.Allocator.LastFrameBackpressure.Count);
        }

        /// <summary>
        /// THE RETRY LOOP NEVER SPINS UNDER THE SUBMIT LOCK, which is the clause the whole threading contract
        /// rests on (decision W4) and the one <c>D3D11RingAllocator.BeginFrame</c> refuses a caller by name for
        /// breaking. A frame-long hold of that lock is invisible from outside, and on the event-query fence
        /// mechanism it is worse than slow: every poll of the completion value re-enters the same lock, so a wait
        /// under it would shut out the submission that would end it.
        /// <para>
        /// EXACT RATHER THAN APPROXIMATE, because the fake records where each poll happened. The gate is allowed
        /// ONE poll per hold of the lock and the loop here takes two holds (busy, then clear), so two polls owe
        /// the lock and every other poll of the wait owes it not to be held. A wait moved inside the lock turns
        /// the first number into the whole poll count, which no threshold has to be chosen for.
        /// </para>
        /// </summary>
        [Fact]
        public void TheRetryLoop_NeverPollsInALoopWhileHoldingTheSubmitLock()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            harness.Completion.SubmitLock = harness.SubmitLock;

            harness.Allocator.OnSubmitted(7);   // segment 0, still running
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Completion.CompleteAfterPolls = 40;
            harness.Completion.CompleteTo = 7;

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 0x5A });

            Assert.Equal(2, harness.Completion.PollsHoldingTheSubmitLock);
            Assert.True(harness.Completion.PollsWithTheSubmitLockFree >= 30,
                "Only " + harness.Completion.PollsWithTheSubmitLockFree.ToString(CultureInfo.InvariantCulture)
                + " of the wait's polls ran with the submit lock free, so the spin was not outside the lock.");
            Assert.Equal(41, harness.Completion.PollCount);
            Assert.Equal(1, harness.Allocator.OffTimelineWaits.Count);
            AssertEverySegmentCarries(harness, 0, new byte[] { 0x5A });
        }

        /// <summary>
        /// THE CURRENT SEGMENT IS COPIED WITHOUT THE GATE, and it is the only ungated one. Gating it would change
        /// the documented semantic that the write lands when it is called and the next submitted list reads it,
        /// and it would block on the GPU on every off-timeline write made after this frame slot's first submit,
        /// which is the pathology the ring deletes. The exposure that leaves is the one the call already had and
        /// is not what #484 was about: what the fix ADDS is the other segments, so those are what it gates.
        /// <para>
        /// Asserted with a completion value the fake never reaches. A gate on the current segment would spin here
        /// until the runaway guard threw, so this test would report the change rather than passing quietly.
        /// </para>
        /// </summary>
        [Fact]
        public void TheCurrentSegment_IsCopiedWithoutWaitingOnItsOwnSubmission()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(11);   // the CURRENT segment, and the GPU never reaches 11
            byte[] payload = { 0xC0, 0xC1 };

            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);

            Assert.Equal(0, harness.Completion.PollCount);
            Assert.Equal(0, harness.Allocator.OffTimelineWaits.Count);
            AssertEverySegmentCarries(harness, 0, payload);
        }

        // ---- the counter (M3) ------------------------------------------------------------------------------

        /// <summary>
        /// OFF-TIMELINE WAITS ARE COUNTED SEPARATELY FROM THE FRAME BACKPRESSURE, AND CUMULATIVELY. Both are the
        /// same shape and they are not the same measurement. M3's exit criterion is that the per-frame
        /// backpressure count is ZERO across a soak window, which reads as "three segments are enough on this
        /// machine". An off-timeline wait says nothing about that: it says a caller wrote a uniform buffer
        /// off-timeline while an earlier frame was still reading a segment of it. Folding the two together would
        /// turn a load-time write into evidence against the segment count and make M3 unreachable for a reason
        /// that has nothing to do with pipeline depth.
        /// <para>
        /// CUMULATIVE RATHER THAN ROLLED, because these writes are typically LOAD-TIME and happen before any
        /// frame has begun, so a per-frame roll would discard exactly the ones worth seeing.
        /// </para>
        /// </summary>
        [Fact]
        public void OffTimelineWaits_AreTheirOwnCumulativeCounterAndNotTheFrameBackpressure()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(3);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Completion.CompleteAfterPolls = 6;
            harness.Completion.CompleteTo = 3;

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 1 });

            Assert.Equal(1, harness.Allocator.OffTimelineWaits.Count);
            Assert.True(harness.Allocator.OffTimelineWaits.TotalMs >= 0d);

            // The frame roll does not pick it up and does not clear it.
            harness.Allocator.BeginFrame();
            Assert.Equal(0, harness.Allocator.LastFrameBackpressure.Count);
            Assert.Equal(0d, harness.Allocator.LastFrameBackpressure.TotalMs);
            Assert.Equal(1, harness.Allocator.OffTimelineWaits.Count);

            // And a second wait accumulates onto the first rather than replacing it.
            harness.Allocator.OnSubmitted(8);   // segment 2, which is current
            harness.Allocator.BeginFrame();     // current is 0, so segment 2 is now gated at 8
            harness.Completion.CompleteAfterPolls = harness.Completion.PollCount + 5;
            harness.Completion.CompleteTo = 8;

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 2 });

            Assert.Equal(2, harness.Allocator.OffTimelineWaits.Count);
        }

        // ---- the PerWrite scope (the immediate driver's fallback lever) ------------------------------------

        /// <summary>
        /// UNDER <see cref="D3D11RingMapScope.PerWrite"/> THE WHOLE REPLICATED WRITE IS ONE CRITICAL SECTION: one
        /// map, every segment's copy, one unmap, all inside a single hold of the submit lock. That scope exists
        /// because it is the only one holding the map, the copy and the unmap atomically, and a replicated write
        /// that mapped and unmapped per segment would hand that property back three times over.
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

        // ---- helpers ---------------------------------------------------------------------------------------

        static void AssertEverySegmentCarries(D3D11RingHarness harness, uint offsetBytes, byte[] expected)
        {
            for (int segment = 0; segment < harness.Allocator.FramesInFlight; segment++)
            {
                ReadOnlySpan<byte> landed = harness.Memory.Segment(
                    harness.Ring.FrameBaseBytes(segment) + offsetBytes, (uint)expected.Length);
                Assert.True(landed.SequenceEqual(expected),
                    "Segment " + segment.ToString(CultureInfo.InvariantCulture)
                    + " does not carry the off-timeline write.");
            }
        }

        // A payload whose every byte differs from its neighbours, so a copy that landed at the wrong offset or
        // ran short shows up as a mismatch rather than as one repeated value that happens to line up.
        static byte[] Pattern(int length, byte seed)
        {
            byte[] bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = (byte)(seed + i);
            return bytes;
        }
    }
}
