using System;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SEGMENT RECYCLING UNDER FENCE PRESSURE (decision U5), which is the half of the constant-buffer ring that
    /// makes the other half safe: which segment a frame writes, when a segment may be handed back out, what the
    /// wait costs, and where the value it waits for comes from.
    /// <para>
    /// THIS IS THE DEPENDENCY WORK-BREAKDOWN ROW 8 WAITED ON ROW 13a FOR, and the reason is worth stating once in
    /// executable form. A ring recycles a segment when the submission that last used it has FINISHED, which is a
    /// completion read. Veldrid's Direct3D 11 fence was a <c>ManualResetEvent</c> set the instant
    /// <c>ExecuteCommandList</c> returns, so a ring built on one would hand a segment back the moment the CPU
    /// finished asking for the work rather than when the GPU finished doing it, and the next frame would overwrite
    /// uniforms a draw in flight is still reading. Nothing throws and nothing logs. The frame is just wrong,
    /// intermittently, several frames from the cause.
    /// </para>
    /// <para>
    /// Device-free on every operating system: the completion timeline is an interface and the ring's two native
    /// calls are another, so what is exercised here is the ordering and the arithmetic, which is all of what can
    /// be wrong.
    /// </para>
    /// </summary>
    public sealed class D3D11RingRecyclingTests
    {
        // ---- which segment a frame gets -------------------------------------------------------------------

        /// <summary>
        /// FRAME N USES SEGMENT <c>N % FramesInFlight</c>, and the wrap is the whole mechanism rather than an
        /// implementation detail: it is what makes a fixed allocation carry an unbounded number of frames.
        /// </summary>
        [Fact]
        public void FramesWalkTheSegmentsAndWrap()
        {
            var completion = new FakeD3D11Completion();
            var allocator = new D3D11RingAllocator(3, completion, new object());

            Assert.Equal(0, allocator.CurrentSegment);

            int[] walked = new int[7];
            for (int i = 0; i < walked.Length; i++)
            {
                allocator.BeginFrame();
                walked[i] = allocator.CurrentSegment;
            }

            Assert.Equal(new[] { 1, 2, 0, 1, 2, 0, 1 }, walked);
            Assert.Equal(7UL, allocator.FrameIndex);
        }

        /// <summary>One segment is legal and means no pipelining at all: every frame comes back to the same
        /// segment and therefore waits for the previous frame's submission. It exists so a soak can prove the
        /// backpressure counter counts something real.</summary>
        [Fact]
        public void OneSegment_IsLegalAndAlwaysComesBackToItself()
        {
            var completion = new FakeD3D11Completion();
            var allocator = new D3D11RingAllocator(1, completion, new object());

            allocator.BeginFrame();
            allocator.BeginFrame();

            Assert.Equal(0, allocator.CurrentSegment);
        }

        // ---- the gate -------------------------------------------------------------------------------------

        /// <summary>A segment nothing has ever been submitted with is handed out without asking the timeline at
        /// all. That is every segment of the first few frames of a process, and asking would cost a poll per
        /// frame to learn that zero is complete.</summary>
        [Fact]
        public void ASegmentNothingHasSubmittedWith_IsHandedOutWithNoPoll()
        {
            var completion = new FakeD3D11Completion();
            var allocator = new D3D11RingAllocator(3, completion, new object());

            allocator.BeginFrame();
            allocator.BeginFrame();

            Assert.Equal(0, completion.PollCount);
            Assert.Equal(0, allocator.LastFrameBackpressure.Count);
        }

        /// <summary>
        /// THE COMMON CASE: one poll, no wait, no count. With three segments the GPU is normally at most two
        /// frames behind, so by the time a segment comes round again its submission is long finished. A count that
        /// rose on every frame would say nothing, which is why the poll is not what is counted.
        /// </summary>
        [Fact]
        public void ASegmentWhoseSubmissionHasFinished_CostsOnePollAndNoStall()
        {
            var completion = new FakeD3D11Completion { Completed = 9 };
            var allocator = new D3D11RingAllocator(2, completion, new object());

            allocator.OnSubmitted(4);
            allocator.BeginFrame();   // segment 1, never used
            allocator.BeginFrame();   // segment 0, owned by value 4, already complete

            Assert.Equal(1, completion.PollCount);
            allocator.BeginFrame();
            Assert.Equal(0, allocator.LastFrameBackpressure.Count);
            Assert.Equal(0d, allocator.LastFrameBackpressure.TotalMs);
        }

        /// <summary>
        /// THE STALL, WHICH IS THE M3 MEASUREMENT. A segment whose submission is still running blocks the frame
        /// until the GPU reaches its value, and that is counted. M3's exit criterion is this count being ZERO
        /// across a whole soak window: a non-zero count means three segments are too few for that machine, and
        /// <c>KE_D3D11_FRAMES_IN_FLIGHT</c> is the lever, not the design.
        /// <para>
        /// The stall is accumulated into the frame it is paid ON THE WAY INTO, so it is reported by the next roll.
        /// A frame pays for the segment it starts on.
        /// </para>
        /// </summary>
        [Fact]
        public void ASegmentStillInFlight_BlocksAndIsCounted()
        {
            var completion = new FakeD3D11Completion { CompleteAfterPolls = 4, CompleteTo = 7 };
            var allocator = new D3D11RingAllocator(2, completion, new object());

            allocator.OnSubmitted(7);
            allocator.BeginFrame();   // segment 1, never used, no wait
            Assert.Equal(0, allocator.LastFrameBackpressure.Count);

            allocator.BeginFrame();   // segment 0, owned by 7, which the GPU reaches on the fourth poll

            Assert.Equal(4, completion.PollCount);
            Assert.Equal(0, allocator.LastFrameBackpressure.Count);   // still accumulating, this frame owns it

            allocator.BeginFrame();
            Assert.Equal(1, allocator.LastFrameBackpressure.Count);
            Assert.True(allocator.LastFrameBackpressure.TotalMs >= 0d);

            // And it rolls off: the next frame reports its own backpressure, not the previous frame's.
            allocator.BeginFrame();
            Assert.Equal(0, allocator.LastFrameBackpressure.Count);
        }

        /// <summary>
        /// A DEAD DEVICE RELEASES A SEGMENT WAIT, without the allocator knowing what a device is. The fence
        /// subsystem answers a completion read with everything it ever issued once the liveness latch has flipped
        /// (decision X3), so a frame that reaches a segment during teardown finds its target already reached. This
        /// is the wiring between the two rows rather than either one alone, so it is asserted against the real
        /// subsystem.
        /// </summary>
        [Fact]
        public void AfterDeviceDeath_ASegmentWaitFindsEverythingComplete()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new FakeD3D11DeviceLiveness();
            object submitLock = new();
            using var fences = new D3D11FenceSubsystem(timeline, submitLock, liveness);
            var allocator = new D3D11RingAllocator(2, fences, submitLock);

            allocator.OnSubmitted(fences.SignalEndOfReplay(null));
            allocator.BeginFrame();   // segment 1

            // The GPU never reaches the value. Without the liveness answer below, the next acquisition would spin
            // until the fake timeline's runaway guard threw.
            liveness.IsDead = true;
            allocator.BeginFrame();   // segment 0, owned by a value the GPU never reached

            Assert.Equal(0, allocator.CurrentSegment);
            allocator.BeginFrame();
            Assert.Equal(0, allocator.LastFrameBackpressure.Count);
        }

        // ---- where the gate's value comes from -------------------------------------------------------------

        /// <summary>
        /// THE SEGMENT'S TARGET IS THE VALUE ITS SUBMISSION SIGNALLED, taken from the submit path rather than
        /// invented. That is the join between this row and the fence row: one submit is one point on the
        /// timeline, and the segment that submit used remembers it.
        /// </summary>
        [Fact]
        public void ASubmit_RecordsItsCompletionValueAgainstTheCurrentSegment()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new D3D11SubmitSignalTests.FakeD3D11SubmitSignal();
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(1);
            list.End();

            D3D11CommandDrivers.Submit(harness.SubmitLock, list, ref emitter, signal, null, harness.Allocator);
            Assert.Equal(1UL, harness.Allocator.SegmentOwner(0));

            harness.Allocator.BeginFrame();
            D3D11CommandDrivers.Submit(harness.SubmitLock, list, ref emitter, signal, null, harness.Allocator);

            Assert.Equal(2UL, harness.Allocator.SegmentOwner(1));
            Assert.Equal(1UL, harness.Allocator.SegmentOwner(0));   // the earlier frame's target is untouched
            Assert.Equal(0UL, harness.Allocator.SegmentOwner(2));
        }

        /// <summary>Several submits in one frame all belong to the same segment, and the segment remembers the
        /// LAST of them, which is the one that has to finish before the segment is safe.</summary>
        [Fact]
        public void SeveralSubmitsInOneFrame_LeaveTheLastValueOnTheSegment()
        {
            var completion = new FakeD3D11Completion();
            var allocator = new D3D11RingAllocator(3, completion, new object());

            allocator.OnSubmitted(4);
            allocator.OnSubmitted(5);
            allocator.OnSubmitted(6);

            Assert.Equal(6UL, allocator.SegmentOwner(0));
        }

        /// <summary>
        /// THE RINGS ARE UNMAPPED BEFORE THE REPLAY, not after it and not at some later boundary. Direct3D 11
        /// forbids a mapped resource being bound to the pipeline, and the replay is about to bind every one of
        /// them, so this ordering is what makes "zero Map or Unmap during replay" a structural invariant rather
        /// than a hope. Asserted against the emitter call log: the unmap records how many calls had been made when
        /// it arrived, and that has to be none.
        /// </summary>
        [Fact]
        public void ASubmit_UnmapsTheRingsBeforeItReplaysAnything()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new D3D11SubmitSignalTests.FakeD3D11SubmitSignal(log);
            using var harness = new D3D11RingHarness(
                sizeInBytes: 256, framesInFlight: 3, log: log);
            harness.Memory.SubmitLock = harness.SubmitLock;

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(1);
            list.End();
            harness.Ring.Write(0, new byte[] { 1, 2, 3, 4 });
            Assert.True(harness.Ring.IsMapped);

            D3D11CommandDrivers.Submit(harness.SubmitLock, list, ref emitter, signal, null, harness.Allocator);

            Assert.Equal(1, harness.Memory.UnmapCount);
            Assert.Equal(0, harness.Memory.EmitterCallsAtLastUnmap);
            Assert.Equal(3, signal.EmitterCallsAtLastSignal);   // Begin, Draw, End: the signal is at the far end
            Assert.False(harness.Ring.IsMapped);
            Assert.True(harness.Memory.LastUnmapHeldTheSubmitLock);
        }

        /// <summary>
        /// AND IT ALREADY HOLDS THE SUBMIT LOCK WHEN IT UNMAPS, which is a separate claim from the ordering above
        /// and the one that makes the ordering worth anything. An unmap taken OUTSIDE the lock takes the lock for
        /// itself and releases it on the way out, and a device-level <c>UpdateBuffer</c> arriving on any thread in
        /// the gap before the replay acquires it maps the ring straight back (it maps idempotently, and under
        /// <see cref="D3D11RingMapScope.AcrossRecording"/> it leaves the mapping in place), so the replay binds a
        /// mapped constant buffer, which Direct3D 11 forbids.
        /// <para>
        /// The window is nanoseconds wide, so racing a thread into it proves nothing either way. What is exact is
        /// the lock's RECURSION at the unmap: nested means the submit was already holding it and there is no gap
        /// at all, outermost means there is one. That is what the fake reports.
        /// </para>
        /// </summary>
        [Fact]
        public void ASubmit_AlreadyHoldsTheSubmitLockWhenItUnmapsTheRings()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            var signal = new D3D11SubmitSignalTests.FakeD3D11SubmitSignal(log);
            using var harness = new D3D11RingHarness(
                sizeInBytes: 256, framesInFlight: 3, log: log);
            harness.Memory.SubmitLock = harness.SubmitLock;

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.Draw(1);
            list.End();
            harness.Ring.Write(0, new byte[] { 1, 2, 3, 4 });

            D3D11CommandDrivers.Submit(harness.SubmitLock, list, ref emitter, signal, null, harness.Allocator);

            Assert.True(harness.Memory.LastUnmapHeldTheSubmitLock);
            Assert.True(harness.Memory.LastUnmapWasNestedInTheCallersLock,
                "A submit unmapped the rings without already holding the submit lock, so it releases the lock "
                + "between the unmap and the replay and an off-timeline write can re-map a ring the replay is "
                + "about to bind.");
        }

        /// <summary>
        /// A RING ALLOCATOR HANDED TO A SUBMIT WITH NO SIGNAL SINK IS REFUSED, and the failure it prevents is
        /// worse than the fence case beside it. The segment would carry no completion value, so it would be handed
        /// back out with no wait and the CPU would write uniforms into memory the GPU is still reading. That is a
        /// corrupted frame rather than a hang, it is intermittent, and it looks like a rendering bug several frames
        /// from its cause.
        /// </summary>
        [Fact]
        public void RingsWithoutASignalSink_AreRefused()
        {
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            using IGpuCommandList list = D3D11CommandDrivers.CreateDeferred();
            list.Begin();
            list.End();

            ArgumentException ex = Assert.Throws<ArgumentException>(() => D3D11CommandDrivers.Submit(
                harness.SubmitLock, list, ref emitter, null, null, harness.Allocator));
            Assert.Contains("segment", ex.Message, StringComparison.Ordinal);
        }

        // ---- the off-timeline write (6.4) -----------------------------------------------------------------

        /// <summary>
        /// THE DEVICE-LEVEL <c>UpdateBuffer</c> REACHES EVERY SEGMENT, including the one the next submit will
        /// bind, so the write lands when it is called AND persists for the buffer's life the way the same call
        /// persists on the Veldrid leg. Reaching the current segment alone was #484, and the whole off-timeline
        /// surface, including the fence gate this added to the other segments, is
        /// <see cref="D3D11RingOffTimelineTests"/>.
        /// </summary>
        [Fact]
        public void AnOffTimelineWrite_ReachesEverySegmentIncludingTheOneTheNextSubmitWillBind()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Allocator.BeginFrame();
            Assert.Equal(1, harness.Allocator.CurrentSegment);

            harness.Allocator.UpdateBuffer(harness.Ring, 8, new byte[] { 0xE1, 0xE2 });

            for (int segment = 0; segment < 3; segment++)
            {
                uint at = harness.Ring.FrameBaseBytes(segment) + 8;
                Assert.Equal((byte)0xE1, harness.Memory.Bytes[at]);
                Assert.Equal((byte)0xE2, harness.Memory.Bytes[at + 1]);
            }
        }

        /// <summary>
        /// IT MAPS IDEMPOTENTLY WHEN IT FINDS THE RING UNMAPPED BETWEEN FRAMES. The ring is unmapped at the start
        /// of every submit, so an off-timeline write arriving between two frames is exactly the case that has to
        /// take a mapping of its own, and under the deferred driver it leaves that mapping in place for the next
        /// record phase to reuse. There is no refcount, just the one flag.
        /// </summary>
        [Fact]
        public void AnOffTimelineWriteBetweenFrames_MapsOnceAndLeavesItMapped()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, new byte[] { 1 });
            harness.Allocator.UnmapMappedRings();
            Assert.Equal(1, harness.Memory.MapCount);

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 2 });
            harness.Allocator.UpdateBuffer(harness.Ring, 16, new byte[] { 3 });

            Assert.Equal(2, harness.Memory.MapCount);
            Assert.True(harness.Ring.IsMapped);

            // And the record phase that follows reuses it rather than taking a third.
            harness.Ring.Write(32, new byte[] { 4 });
            Assert.Equal(2, harness.Memory.MapCount);
        }

        /// <summary>
        /// THE LOCK IS THE SUBMIT LOCK AND IT IS SCOPED TO THE WRITE, never to a frame. An off-timeline write can
        /// arrive on any thread, and it must not land in the middle of a replay, so it waits for one that is in
        /// progress and holds the lock only while it copies.
        /// <para>
        /// Asserted by holding the lock and watching a write on another thread fail to finish, which is the only
        /// way to observe a lock scope from outside. The wait after the release is what proves it was blocked
        /// rather than broken.
        /// </para>
        /// </summary>
        [Fact]
        public void AnOffTimelineWrite_WaitsForTheSubmitLock()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);
            harness.Memory.SubmitLock = harness.SubmitLock;

            using var started = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var writer = new Thread(() =>
            {
                started.Set();
                harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 0x7F });
                finished.Set();
            }) { IsBackground = true };

            bool blocked;
            lock (harness.SubmitLock)
            {
                writer.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "The writing thread never started.");
                blocked = !finished.Wait(TimeSpan.FromMilliseconds(250));
                Assert.Equal((byte)0, harness.Memory.Bytes[0]);
            }

            Assert.True(finished.Wait(TimeSpan.FromSeconds(10)),
                "The off-timeline write never completed after the lock released.");
            Assert.True(blocked,
                "An off-timeline UpdateBuffer wrote a ring without taking the submit lock, so it can land in the "
                + "middle of a replay.");
            Assert.Equal((byte)0x7F, harness.Memory.Bytes[0]);
            Assert.True(harness.Memory.LastMapHeldTheSubmitLock);
        }

        // ---- disposal -------------------------------------------------------------------------------------

        /// <summary>
        /// A DISPOSED RING IS UNMAPPED AND FORGOTTEN, in that order. Releasing a mapped resource leaves the
        /// runtime holding a pointer into memory nobody owns, and a disposed ring left in the registry would be
        /// unmapped a second time at the next submit, which the fake refuses by name.
        /// </summary>
        [Fact]
        public void ADisposedRing_IsUnmappedAndDroppedFromTheRegistry()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Ring.Write(0, new byte[] { 1 });
            Assert.Equal(1, harness.Allocator.MappedRingCount);

            harness.Allocator.Forget(harness.Ring);

            Assert.Equal(1, harness.Memory.UnmapCount);
            Assert.Equal(0, harness.Allocator.MappedRingCount);

            // The next submit finds nothing to unmap, which is what stops the second release.
            harness.Allocator.UnmapMappedRings();
            Assert.Equal(1, harness.Memory.UnmapCount);
        }

        /// <summary>Forgetting a ring that never mapped costs nothing, which is every uniform buffer disposed
        /// without having been written this frame.</summary>
        [Fact]
        public void ForgettingAnUnmappedRing_ReleasesNothing()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Allocator.Forget(harness.Ring);

            Assert.Equal(0, harness.Memory.UnmapCount);
            Assert.Equal(0, harness.Memory.MapCount);
        }
    }
}
