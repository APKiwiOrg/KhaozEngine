using System;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PENDING-PATCH MECHANISM, which is what the off-timeline write leaves behind instead of waiting for a
    /// segment the GPU has not finished with. The write's own surface is <see cref="D3D11RingOffTimelineTests"/>.
    /// <para>
    /// WHAT IT HAS TO GET RIGHT, and it is all ordering. A patch is applied at the frame boundary that opens its
    /// segment, so a write that was deferred lands LATER than one that was copied, and the two have to end up
    /// resolving the same way a pair of direct copies would: last write wins. That is why a segment already
    /// carrying a patch takes every later write as a patch too, why the list is replayed oldest first, and why
    /// coalescing is allowed only when the newer range fully covers the older one.
    /// </para>
    /// <para>
    /// THE GUARANTEE THE WHOLE THING EXISTS FOR: after an off-timeline write returns, every segment either holds
    /// the write or holds a patch its next acquire applies, so any segment BOUND after that call carries it. The
    /// one-frame-slot window in which an in-flight segment still holds the old bytes is unobservable through the
    /// seam, because that segment is not bound again until it has been acquired.
    /// </para>
    /// </summary>
    public sealed class D3D11RingPendingPatchTests
    {
        const int Segments = 3;

        /// <summary>
        /// THE DEFERRED BYTES LAND AT THAT SEGMENT'S NEXT FRAME BOUNDARY, BYTE FOR BYTE, and not before. The
        /// intermediate boundary is asserted too: opening a DIFFERENT segment must not drain this one, or the
        /// apply is happening on a schedule rather than against the gate that proved the GPU was done.
        /// </summary>
        [Fact]
        public void ADeferredPatch_LandsAtThatSegmentsNextBeginFrameByteForByte()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            byte[] payload = D3D11RingOffTimelineTests.Pattern(32, seed: 0x50);

            harness.Allocator.OnSubmitted(9);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Allocator.UpdateBuffer(harness.Ring, 0, payload);
            Assert.Equal(1, harness.Ring.PendingPatchCountFor(0));

            harness.Completion.Completed = 9;
            harness.Allocator.BeginFrame();     // opens 2, which owes nothing
            Assert.Equal(1, harness.Ring.PendingPatchCountFor(0));
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Applied);

            harness.Allocator.BeginFrame();     // opens 0, which drains
            Assert.Equal(0, harness.Ring.PendingPatchCount);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Applied);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Outstanding);
            D3D11RingOffTimelineTests.AssertSegmentCarries(harness, segment: 0, offsetBytes: 0, payload);
        }

        /// <summary>
        /// TWO PATCHES TO ONE SEGMENT REPLAY IN ARRIVAL ORDER, so overlapping ranges resolve last-write-wins
        /// exactly as two direct copies into mapped memory would. Written with a PARTIAL overlap, because that is
        /// the case where order is observable: the first write's bytes survive outside the second's range and are
        /// gone inside it, and replaying the pair backwards would give the opposite picture.
        /// </summary>
        [Fact]
        public void TwoPatchesToOneSegment_ReplayInArrivalOrderSoTheLastWriteWins()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            byte[] first = D3D11RingOffTimelineTests.Pattern(8, seed: 0xA0);
            byte[] second = D3D11RingOffTimelineTests.Pattern(8, seed: 0xB0);

            harness.Allocator.OnSubmitted(9);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated

            harness.Allocator.UpdateBuffer(harness.Ring, 0, first);
            harness.Allocator.UpdateBuffer(harness.Ring, 4, second);
            Assert.Equal(2, harness.Ring.PendingPatchCountFor(0));
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Coalesced);

            harness.Completion.Completed = 9;
            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();     // opens 0

            uint segmentBase = harness.Ring.FrameBaseBytes(0);
            Assert.True(harness.Memory.Segment(segmentBase, 4).SequenceEqual(first.AsSpan(0, 4)),
                "The first patch's bytes outside the second's range did not survive, so the two were replayed "
                + "out of order or one of them was dropped.");
            Assert.True(harness.Memory.Segment(segmentBase + 4, 8).SequenceEqual(second),
                "The second patch did not win the overlap, so the replay order is not arrival order.");
        }

        /// <summary>
        /// A PATCH THAT FULLY COVERS AN EARLIER ONE REPLACES IT, which is what bounds the storage. Every byte of
        /// the older entry is overwritten by the newer one, so replaying both and replaying only the newer leave
        /// identical memory, and the repeated case of one caller rewriting the same range off-timeline stays at a
        /// single entry per segment forever.
        /// <para>
        /// THE DROPPED ENTRY IS REPORTED RATHER THAN LOST. It was deferred and never applied, so counting it as
        /// applied would make the pair stop adding up, and leaving it out of both would make the outstanding
        /// number climb forever. It has its own count.
        /// </para>
        /// </summary>
        [Fact]
        public void APatchThatFullyCoversAnEarlierOne_ReplacesItAndIsCounted()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            byte[] inner = D3D11RingOffTimelineTests.Pattern(4, seed: 0xC0);
            byte[] outer = D3D11RingOffTimelineTests.Pattern(16, seed: 0xD0);

            harness.Allocator.OnSubmitted(9);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated

            harness.Allocator.UpdateBuffer(harness.Ring, 8, inner);     // covers bytes 8 to 12
            harness.Allocator.UpdateBuffer(harness.Ring, 4, outer);     // covers bytes 4 to 20, so it swallows it

            Assert.Equal(1, harness.Ring.PendingPatchCountFor(0));
            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Deferred);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Coalesced);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Outstanding);

            harness.Completion.Completed = 9;
            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();     // opens 0

            D3D11RingOffTimelineTests.AssertSegmentCarries(harness, segment: 0, offsetBytes: 4, outer);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Applied);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Outstanding);
        }

        /// <summary>
        /// A SEGMENT THAT ALREADY CARRIES A PATCH TAKES THE NEXT WRITE AS A PATCH TOO, even once its fence has
        /// completed and a direct copy would have been legal. Copying directly there would put the newer bytes in
        /// first and let the frame boundary replay the OLDER ones over them, which is a last-write-wins violation
        /// with no thread race in it at all.
        /// <para>
        /// The two ranges are chosen so neither covers the other, so the coalescing rule cannot hide the ordering
        /// and the older write's tail is what proves the replay ran in the right order.
        /// </para>
        /// </summary>
        [Fact]
        public void AWriteToASegmentWithAPatchQueued_JoinsTheQueueEvenAfterItsFenceCompletes()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            byte[] older = D3D11RingOffTimelineTests.Pattern(8, seed: 0x10);
            byte[] newer = D3D11RingOffTimelineTests.Pattern(4, seed: 0x80);

            harness.Allocator.OnSubmitted(9);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Allocator.UpdateBuffer(harness.Ring, 0, older);

            harness.Completion.Completed = 9;   // segment 0 is free now, and it still owes a patch
            harness.Allocator.UpdateBuffer(harness.Ring, 0, newer);

            Assert.Equal(2, harness.Ring.PendingPatchCountFor(0));
            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Deferred);

            // The free segments took the newer write on the spot, as they always do.
            D3D11RingOffTimelineTests.AssertSegmentCarries(harness, segment: 1, offsetBytes: 0, newer);
            D3D11RingOffTimelineTests.AssertSegmentCarries(harness, segment: 2, offsetBytes: 0, newer);

            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();     // opens 0

            uint segmentBase = harness.Ring.FrameBaseBytes(0);
            Assert.True(harness.Memory.Segment(segmentBase, 4).SequenceEqual(newer),
                "The queued segment did not end on the NEWER write, so the later write was copied straight in and "
                + "the older patch was replayed over the top of it.");
            Assert.True(harness.Memory.Segment(segmentBase + 4, 4).SequenceEqual(older.AsSpan(4, 4)),
                "The older write's tail is missing, so it never reached the segment at all.");
        }

        /// <summary>
        /// THE CURRENT SEGMENT NEVER CARRIES A PENDING PATCH, which is the invariant that lets the write copy
        /// into it unconditionally. Patches are recorded for non-current segments only, and the frame boundary
        /// drains the segment it is opening inside the same hold of the submit lock that publishes it as current,
        /// so there is no instant at which a writer can see it as current and still queued.
        /// </summary>
        [Fact]
        public void TheSegmentAFrameOpens_CarriesNoPendingPatchOnceItIsCurrent()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);
            byte[] deferred = D3D11RingOffTimelineTests.Pattern(8, seed: 0x22);
            byte[] direct = D3D11RingOffTimelineTests.Pattern(8, seed: 0x66);

            harness.Allocator.OnSubmitted(9);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Allocator.UpdateBuffer(harness.Ring, 0, deferred);

            harness.Completion.Completed = 9;
            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();     // opens 0, which is now current

            Assert.Equal(0, harness.Allocator.CurrentSegment);
            Assert.Equal(0, harness.Ring.PendingPatchCountFor(0));

            // And a write made now lands in it on the spot rather than joining a queue that no longer exists.
            harness.Allocator.UpdateBuffer(harness.Ring, 0, direct);
            Assert.Equal(0, harness.Ring.PendingPatchCountFor(0));
            D3D11RingOffTimelineTests.AssertSegmentCarries(harness, segment: 0, offsetBytes: 0, direct);
        }

        /// <summary>
        /// FORGETTING A RING DROPS ITS PENDING PATCHES, because they name memory that is about to stop existing.
        /// A patch left behind would be replayed at the next frame boundary into a mapping the runtime has taken
        /// back, which is a write through a dangling pointer on the one path nobody is looking at.
        /// </summary>
        [Fact]
        public void ForgettingARing_DropsItsPendingPatches()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(9);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 0x77, 0x78 });
            Assert.Equal(1, harness.Ring.PendingPatchCount);

            harness.Allocator.Forget(harness.Ring);
            Assert.Equal(0, harness.Ring.PendingPatchCount);

            int mapsBefore = harness.Memory.MapCount;
            harness.Completion.Completed = 9;
            harness.Allocator.BeginFrame();
            harness.Allocator.BeginFrame();     // opens 0, and there is nothing left to replay into it

            Assert.Equal(mapsBefore, harness.Memory.MapCount);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Applied);
        }

        /// <summary>
        /// THE PATCH COUNTERS ARE THEIR OWN CUMULATIVE READING AND NOT THE FRAME BACKPRESSURE. They are the same
        /// kind of number and not the same measurement. M3's exit criterion is that the per-frame backpressure
        /// count is ZERO across a soak window, which reads as "three segments are enough on this machine". A
        /// deferred patch says nothing about that: it says a caller wrote a uniform buffer off-timeline while an
        /// earlier frame was still reading a segment of it, and it costs nobody a stall. Folding the two together
        /// would turn a load-time write into evidence against the segment count.
        /// <para>
        /// CUMULATIVE RATHER THAN ROLLED, because these writes are typically LOAD-TIME and happen before any
        /// frame has begun, so a per-frame roll would discard exactly the ones worth seeing.
        /// </para>
        /// </summary>
        [Fact]
        public void ThePatchCounters_AreTheirOwnCumulativeReadingAndNotTheFrameBackpressure()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: Segments);

            harness.Allocator.OnSubmitted(3);   // segment 0
            harness.Allocator.BeginFrame();     // current is 1, so segment 0 is gated
            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 1 });

            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Deferred);
            Assert.Equal(0, harness.Allocator.OffTimelinePatches.Applied);

            // The frame roll neither picks the deferral up nor clears it, and the frame itself never stalled.
            harness.Completion.Completed = 3;
            harness.Allocator.BeginFrame();
            Assert.Equal(0, harness.Allocator.LastFrameBackpressure.Count);
            Assert.Equal(0d, harness.Allocator.LastFrameBackpressure.TotalMs);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Deferred);

            // A second deferral accumulates onto the first rather than replacing it.
            harness.Allocator.OnSubmitted(8);   // segment 2, which is current
            harness.Allocator.BeginFrame();     // opens 0 and drains it, so the first patch is applied here
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Applied);

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 2 });
            Assert.Equal(2, harness.Allocator.OffTimelinePatches.Deferred);
            Assert.Equal(1, harness.Allocator.OffTimelinePatches.Outstanding);
            Assert.Equal(0, harness.Allocator.LastFrameBackpressure.Count);
        }
    }
}
