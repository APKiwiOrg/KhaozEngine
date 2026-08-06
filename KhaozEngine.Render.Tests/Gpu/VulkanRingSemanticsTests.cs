using System;
using System.Threading;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE VULKAN RING'S SEQUENCING: section 9.4's LOCK LEGALITY row, which is this backend's own because
    /// each backend has its own lock and its own deadlock to not have, plus the parts of the gate and the
    /// pending-patch queue whose MECHANISM is this backend's even where the policy is shared.
    ///
    /// <para><b>WHAT THE SHARED ROWS ALREADY COVER, and is not repeated here.</b>
    /// <see cref="GpuUniformRingSharedTests"/> runs segment selection, fence gating, backpressure counting, the
    /// off-timeline every-segment reach, its gating, its never-blocking queue and record-time writes staying
    /// current-segment, against BOTH backends' rings. What is left here is what the shared interface deliberately
    /// cannot see: which timeline high-water the owner is read from, that the read happens under the submit lock,
    /// that the wait does not, and the counters that report the queue.</para>
    ///
    /// <para><b>9.4'S ORDERING ROW IS A BUILD-ORDER FACT and has no test at all.</b> The ring cannot recycle
    /// safely before the completion primitive exists, and what enforces that is this row depending on row 5 rather
    /// than anything observable at run time. The nearest thing to an assertion is that
    /// <see cref="VulkanRingAllocator"/> takes a <see cref="VulkanTimeline"/> and has no other way to learn what
    /// the GPU has finished, which the constructor signature states and no test can add to.</para>
    /// </summary>
    public sealed class VulkanRingSemanticsTests
    {
        // ---- 9.4's Lock legality row ---------------------------------------------------------------------

        /// <summary>
        /// A CALLER ALREADY HOLDING THE SUBMIT LOCK MAY WRITE OFF-TIMELINE, and that is the case the waiting draft
        /// would have deadlocked on. The write never waits for anything, so re-entering the lock is free and there
        /// is nothing inside that could wait for work only another thread could do.
        /// </summary>
        [Fact]
        public void AnOffTimelineWrite_FromInsideTheSubmitLock_Completes()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            // Two segments in flight, so the write has real deferring to do rather than taking the trivial path.
            harness.Submit(5);
            harness.Allocator.BeginFrame();
            harness.Submit(6);
            harness.Allocator.BeginFrame();

            var payload = new byte[] { 1, 2, 3, 4 };

            lock (harness.SubmitLock)
            {
                harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
            }

            Assert.Equal(2, harness.Ring.PendingPatchCount);
            Assert.Equal(payload, harness.Ring.ReadSegment(harness.Allocator.CurrentSegment, 0, payload.Length));
        }

        /// <summary>
        /// THE FRAME BOUNDARY REFUSES A CALLER HOLDING THE SUBMIT LOCK, BY NAME. It is the one member that can
        /// BLOCK, for up to a frame, and V-W8 caps that lock at microseconds. Holding it across the wait would also
        /// shut out the submission that would end the wait.
        /// </summary>
        [Fact]
        public void BeginFrame_UnderTheSubmitLock_IsRefusedByName()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            InvalidOperationException ex;
            lock (harness.SubmitLock)
            {
                ex = Assert.Throws<InvalidOperationException>(() => harness.Allocator.BeginFrame());
            }

            Assert.Contains("submit lock", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>The wait itself happens with the lock FREE, which is the other half of the same rule. The gate
        /// polls and blocks outside any hold, and only the short close and the short publish take the lock.
        /// </summary>
        [Fact]
        public void TheSegmentWait_HappensWithTheSubmitLockFree()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            bool? heldDuringTheWait = null;
            harness.Semaphore.OnWait = () => heldDuringTheWait = Monitor.IsEntered(harness.SubmitLock);

            harness.Submit(9);
            for (int frame = 0; frame < 3; frame++) harness.Allocator.BeginFrame();

            Assert.False(heldDuringTheWait);
        }

        // ---- the gate's own mechanism, which the shared interface cannot see -----------------------------

        /// <summary>
        /// THE OWNER IS THE REGISTERED SUBMIT HIGH-WATER, NOT THE ALLOCATION ONE, and the difference matters in
        /// exactly one direction. A submit that failed with a non-loss result took a value nothing will ever
        /// signal, so a segment gated on the allocation high-water would block forever. The deferred-disposal
        /// retire list gates on the allocation high-water instead, for the opposite reason.
        /// </summary>
        [Fact]
        public void TheSegmentOwner_IsTheRegisteredHighWaterAndNotTheAllocatedOne()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(4);

            // A submission that took value 5 and then FAILED: allocated, never registered.
            lock (harness.SubmitLock) harness.Timeline.NextSubmitValue();

            Assert.Equal(5ul, harness.Timeline.LastAllocated);
            Assert.Equal(4ul, harness.Timeline.LastSubmitted);

            harness.Allocator.BeginFrame();

            Assert.Equal(4ul, harness.Allocator.SegmentOwner(0));
        }

        /// <summary>A segment nothing has ever been closed against is handed out with no wait and no poll at all,
        /// which is every segment for the first pass round the ring.</summary>
        [Fact]
        public void ASegmentNothingWasSubmittedAgainst_CostsNoPoll()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();

            Assert.Equal(0, harness.Semaphore.ReadCount);
            Assert.Equal(0, harness.Semaphore.WaitCount);
            Assert.Equal(0, harness.Allocator.StallCount);
        }

        /// <summary>
        /// A DEAD DEVICE RELEASES THE WAIT without the allocator knowing what a device is. The timeline answers
        /// with everything ever allocated once liveness has flipped, so a segment wait during teardown finds its
        /// target already reached and returns rather than blocking on a counter nothing can advance.
        /// </summary>
        [Fact]
        public void AfterDeviceDeath_ASegmentWaitFindsEverythingComplete()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(9);
            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();

            harness.Liveness.MarkDead();
            harness.Allocator.BeginFrame();

            Assert.Equal(0, harness.Allocator.StallCount);
            Assert.Equal(0, harness.Semaphore.WaitCount);
        }

        /// <summary>A stall lands in the DEVICE's one backpressure accumulator, not a second one, so MV3's exit
        /// criterion reads as a single zero across both of that accumulator's sources rather than as two numbers a
        /// reader has to add up first.</summary>
        [Fact]
        public void ASegmentStall_LandsInTheDevicesOneBackpressureAccumulator()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(9);
            for (int frame = 0; frame < 3; frame++) harness.Allocator.BeginFrame();

            Assert.Equal(1, harness.Allocator.StallCount);
            Assert.Equal(1, harness.Backpressure.Totals.Count);
        }

        // ---- the pending-patch queue's counters ----------------------------------------------------------

        /// <summary>
        /// A PATCH THAT FULLY COVERS AN EARLIER ONE REPLACES IT, which is the bound on the storage: one caller
        /// rewriting the same range off-timeline stays at ONE entry per segment forever rather than growing without
        /// limit. A PARTIAL overlap is not coalesced, because there the older bytes outside the newer range still
        /// have to land.
        /// </summary>
        [Fact]
        public void APatchThatFullyCoversAnEarlierOne_ReplacesItAndIsCounted()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(5);
            harness.Allocator.BeginFrame();
            harness.Submit(6);
            harness.Allocator.BeginFrame();

            harness.Allocator.UpdateBuffer(harness.Ring, 8, new byte[4]);
            Assert.Equal(2, harness.Ring.PendingPatchCount);

            // Fully covers the first: same start, longer.
            harness.Allocator.UpdateBuffer(harness.Ring, 8, new byte[16]);
            Assert.Equal(2, harness.Ring.PendingPatchCount);
            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Coalesced);

            // Overlaps but does not cover: both have to land.
            harness.Allocator.UpdateBuffer(harness.Ring, 20, new byte[16]);
            Assert.Equal(4, harness.Ring.PendingPatchCount);
        }

        /// <summary>
        /// FORGETTING A RING DROPS ITS PATCHES AND COUNTS THEM. A queued write names memory that is about to stop
        /// existing, so replaying it after the buffer was released would write through a pointer into a freed
        /// chunk. Dropping them SILENTLY would leave them counted as deferred and never as resolved, so
        /// <c>Outstanding</c> would sit permanently high in any program that streams uniform buffers in and out.
        /// </summary>
        [Fact]
        public void ForgettingARing_DropsItsPendingPatchesAndReconcilesTheCount()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(5);
            harness.Allocator.BeginFrame();
            harness.Submit(6);
            harness.Allocator.BeginFrame();

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[4]);
            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Outstanding);

            harness.Allocator.Forget(harness.Ring);

            Assert.Equal(0, harness.Ring.PendingPatchCount);
            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Dropped);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Outstanding);
        }

        /// <summary>
        /// THE PATCH COUNTERS ARE THEIR OWN READING AND ARE NOT THE BACKPRESSURE NUMBER. A deferred patch is not a
        /// stall: it says a caller wrote a uniform buffer off-timeline while an earlier frame was still reading a
        /// segment of it, which costs nobody a wait. Folding the two would turn a load-time write into evidence
        /// against the frames-in-flight knob and make MV3's zero-stall criterion unreachable for a reason unrelated
        /// to pipeline depth.
        /// </summary>
        [Fact]
        public void ThePatchCounters_AreNotTheBackpressureNumber()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(5);
            harness.Allocator.BeginFrame();
            harness.Submit(6);
            harness.Allocator.BeginFrame();

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[4]);

            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Deferred);
            Assert.Equal(0, harness.Allocator.StallCount);
            Assert.Equal(0, harness.Backpressure.Totals.Count);
        }

        /// <summary>The whole deferral ledger reconciles: everything deferred is eventually applied, coalesced or
        /// dropped, which is what makes <c>Outstanding</c> a reading rather than a running total. A number that
        /// keeps climbing means patches are being recorded for a segment nothing ever acquires, which on a running
        /// device means frames stopped.</summary>
        [Fact]
        public void EveryDeferral_LeavesTheQueueExactlyOnce()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(5);
            harness.Allocator.BeginFrame();
            harness.Submit(6);
            harness.Allocator.BeginFrame();

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[4]);
            harness.Complete(6);

            for (int frame = 0; frame < 3; frame++) harness.Allocator.BeginFrame();

            VulkanRingPatchStats stats = harness.Allocator.OffTimelinePatches;
            Assert.Equal(2, stats.Deferred);
            Assert.Equal(2, stats.Applied);
            Assert.Equal(0, stats.Outstanding);
        }

        /// <summary>An empty off-timeline write records nothing and defers nothing, because a copy of no bytes is a
        /// patch queued for no reason.</summary>
        [Fact]
        public void AnEmptyOffTimelineWrite_DefersNothing()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Submit(5);
            harness.Allocator.BeginFrame();

            harness.Allocator.UpdateBuffer(harness.Ring, 0, ReadOnlySpan<byte>.Empty);

            Assert.Equal(0, harness.Ring.PendingPatchCount);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Deferred);
        }

        /// <summary>An off-timeline write past the logical end is refused BEFORE anything is copied or queued.
        /// Without the check it would spill each of the segments into the next, turning one overrun into
        /// <c>FramesInFlight</c> of them.</summary>
        [Fact]
        public void AnOffTimelineWritePastTheEnd_IsRefusedBeforeAnythingLands()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => harness.Allocator.UpdateBuffer(harness.Ring, 250, new byte[8]));

            Assert.Equal(new byte[harness.Bytes.Length], harness.Bytes);
        }

        /// <summary>An allocator outside the segment range is refused, matching the command pool ring's own floor
        /// and ceiling: one number, two indexes, and the same clamp in front of both.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(17)]
        public void AnAllocatorOutsideTheSegmentRange_IsRefused(int framesInFlight)
        {
            using var timeline = new VulkanTimeline(new FakeVulkanTimelineSemaphore());

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new VulkanRingAllocator(framesInFlight, timeline, new VulkanBackpressure(), new object()));
        }
    }
}
