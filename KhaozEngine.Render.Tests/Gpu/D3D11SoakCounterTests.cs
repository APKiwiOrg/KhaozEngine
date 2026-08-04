using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE CUMULATIVE HALF OF THE M2 AND M3 COUNTERS: the drain and segment-stall totals that never roll, which
    /// are what a telemetry session actually carries. The per-frame rolls beside them are covered by
    /// <see cref="D3D11DrainTests"/> and <see cref="D3D11RingRecyclingTests"/>, and this file asserts the property
    /// those cannot: that a reader sampling on its own cadence still sees every wait that happened.
    /// <para>
    /// WHY THE PAIR EXISTS AT ALL. A soak samples a telemetry row a few times a second while the game runs at
    /// 125 fps, so a row carrying "the frame that just ended" describes one frame in sixty and says nothing about
    /// the rest. M3's exit criterion is that the stall count is ZERO across the whole capture window, which the
    /// per-frame reading cannot establish and a cumulative one settles by subtracting the first row from the last.
    /// The tests below make that difference concrete: the same run reports 1 per frame and 2 for the window.
    /// </para>
    /// <para>
    /// Device-free on every operating system, over the same fakes the neighbouring ring and drain tests use.
    /// </para>
    /// </summary>
    public sealed class D3D11SoakCounterTests
    {
        static D3D11FenceSubsystem Subsystem(ID3D11FenceTimeline timeline, bool realDrain = true)
            => new(timeline, new object(), null, realDrain);

        // ---- M2, the drain totals -------------------------------------------------------------------------

        /// <summary>A device that has never drained reports a zero total, and zero milliseconds with it. This is
        /// the reading a passing soak produces, which is why the seam has to keep it apart from "this backend
        /// counts nothing".</summary>
        [Fact]
        public void AFreshSubsystemHasDrainedNothing()
        {
            using D3D11FenceSubsystem fences = Subsystem(new FakeD3D11FenceTimeline());

            Assert.Equal(0L, fences.TotalDrain.Count);
            Assert.Equal(0d, fences.TotalDrain.TotalMs);
        }

        /// <summary>
        /// THE TOTAL SURVIVES THE FRAME ROLL AND THE PER-FRAME NUMBER DOES NOT, which is the whole reason both
        /// exist. Three drains spread over two frames read as 1 on the last frame and 3 for the session, and a
        /// sampler that only ever saw the first number would report a third of the drains that happened.
        /// </summary>
        [Fact]
        public void DrainsAccumulateAcrossFramesWhileThePerFrameRollResets()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            fences.WaitForIdle();
            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(2, fences.LastFrameDrain.Count);
            Assert.Equal(2L, fences.TotalDrain.Count);

            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(1, fences.LastFrameDrain.Count);   // the frame just ended
            Assert.Equal(3L, fences.TotalDrain.Count);      // the session so far
            Assert.True(fences.TotalDrain.TotalMs >= fences.LastFrameDrain.TotalMs);
        }

        /// <summary>
        /// THE KILL SWITCH COUNTS NOTHING, in the total exactly as in the per-frame roll.
        /// <c>KE_D3D11_REAL_DRAIN=0</c> makes <c>WaitForIdle</c> return without draining, and counting those would
        /// report a soak run with the switch down as having drained hundreds of times for zero milliseconds, which
        /// reads as a drain that costs nothing rather than as a drain that never ran.
        /// </summary>
        [Fact]
        public void TheKillSwitchLeavesTheTotalAtZero()
        {
            var timeline = new FakeD3D11FenceTimeline { AutoCompleteAfterPolls = 1 };
            using D3D11FenceSubsystem fences = Subsystem(timeline, realDrain: false);

            fences.WaitForIdle();
            fences.WaitForIdle();
            fences.BeginFrame();

            Assert.Equal(0L, fences.TotalDrain.Count);
            Assert.Equal(0d, fences.TotalDrain.TotalMs);
        }

        // ---- M3, the segment-stall totals ------------------------------------------------------------------

        /// <summary>The pipeline that never stalls, which is what M3 bets three segments buys. Zero is the passing
        /// reading and it has to be reachable, so a run with no stall reports a total of zero rather than an
        /// absence.</summary>
        [Fact]
        public void AFreshAllocatorHasStalledNothing()
        {
            var completion = new FakeD3D11Completion();
            var allocator = new D3D11RingAllocator(3, completion, new object());

            allocator.BeginFrame();
            allocator.BeginFrame();

            Assert.Equal(0L, allocator.TotalBackpressure.Count);
            Assert.Equal(0d, allocator.TotalBackpressure.TotalMs);
        }

        /// <summary>
        /// TWO STALLS IN A RUN READ AS 2 FOR THE WINDOW AND 1 FOR THE LAST FRAME. This is the case the M3
        /// criterion turns on: a sampler landing on the final frame would see one stall, a sampler landing one
        /// frame later would see none at all, and only the total says the window was not clean.
        /// </summary>
        [Fact]
        public void SegmentStallsAccumulateAcrossFramesWhileThePerFrameRollResets()
        {
            var completion = new FakeD3D11Completion { CompleteAfterPolls = 3, CompleteTo = 7 };
            var allocator = new D3D11RingAllocator(2, completion, new object());

            allocator.OnSubmitted(7);
            allocator.BeginFrame();   // segment 1, never submitted with, no wait

            allocator.OnSubmitted(9);
            allocator.BeginFrame();   // segment 0, owned by 7, reached on the third poll

            // Stage the second stall: raise the trigger BEFORE the value, so the next poll does not release the
            // wait before it has begun.
            completion.CompleteAfterPolls = completion.PollCount + 3;
            completion.CompleteTo = 9;

            allocator.BeginFrame();   // segment 1, owned by 9, reached three polls later
            allocator.BeginFrame();   // rolls the second stall into the per-frame reading

            Assert.Equal(1, allocator.LastFrameBackpressure.Count);
            Assert.Equal(2L, allocator.TotalBackpressure.Count);
            Assert.True(allocator.TotalBackpressure.TotalMs >= allocator.LastFrameBackpressure.TotalMs);
        }

        /// <summary>
        /// THE OFF-TIMELINE COUNTER IS NOT THE STALL COUNTER
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/499), asserted at the source rather than only at the
        /// seam. A load-time write against segments nothing has submitted with defers nothing and stalls nothing,
        /// and a write against an in-flight segment defers without stalling. Neither ever moves the M3 number,
        /// which is what keeps its zero-across-the-window criterion reachable.
        /// </summary>
        [Fact]
        public void AnOffTimelineDeferralIsNotCountedAsASegmentStall()
        {
            using var harness = new D3D11RingHarness(sizeInBytes: 256, framesInFlight: 3);

            harness.Allocator.OnSubmitted(5);   // segment 0 is in flight, the GPU has reached nothing
            harness.Allocator.BeginFrame();     // segment 1, never submitted with, so no wait
            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[16]);

            Assert.True(harness.Allocator.OffTimelinePatches.Deferred > 0);
            Assert.Equal(0L, harness.Allocator.TotalBackpressure.Count);
            Assert.Equal(0, harness.Allocator.LastFrameBackpressure.Count);
        }
    }
}
