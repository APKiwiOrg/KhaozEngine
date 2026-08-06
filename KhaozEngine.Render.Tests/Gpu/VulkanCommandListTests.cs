using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE LIST'S LIFECYCLE, device-free: the recording state machine, the per-slot pools, the pool-reset
    /// discipline, the slot wait and its backpressure accounting, and the disposal handover to the retire list.
    /// Decisions V-R2, V-R3 and V-F9, over <see cref="FakeVulkanCommandApi"/> and
    /// <see cref="FakeVulkanTimelineSemaphore"/>.
    /// <para>
    /// WHAT IS DELIBERATELY NOT HERE is recording CONTENT, because there is none yet: rows 11, 12, 14 and 15 own
    /// the drawing, binding, clearing and copying members, and every one of them refuses by naming its row. The
    /// row that pins the refusals is here; the rows that replace them are theirs.
    /// </para>
    /// </summary>
    public sealed class VulkanCommandListTests
    {
        // ---- The state machine ----

        /// <summary>A fresh list has recorded nothing and has nothing sealed, so a submit path asking for its
        /// buffer is refused rather than handed slot 0's empty one.</summary>
        [Fact]
        public void AFreshList_HasNothingSealed()
        {
            using var fixture = new Fixture();
            using VulkanCommandList list = fixture.CreateList();

            Assert.False(list.IsRecording);
            Assert.False(list.IsSealed);
            Assert.Throws<InvalidOperationException>(() => list.SealedBuffer);
        }

        /// <summary>
        /// A SECOND <c>Begin</c> IS REFUSED rather than silently restarting the recording. The driver would refuse
        /// it too, since the buffer is already in the recording state, and refusing here names the sequencing
        /// error instead of surfacing it as a bare result from a call the caller did not make.
        /// </summary>
        [Fact]
        public void ASecondBeginWithoutAnEnd_IsRefusedAndRecordsNothing()
        {
            using var fixture = new Fixture();
            using VulkanCommandList list = fixture.CreateList();

            list.Begin();
            int events = fixture.Api.Events.Count;

            Assert.Throws<InvalidOperationException>(list.Begin);

            Assert.Equal(events, fixture.Api.Events.Count);
            Assert.True(list.IsRecording);
        }

        /// <summary><c>End</c> without a <c>Begin</c> is refused, and so is a second <c>End</c> on a sealed list.
        /// The two messages differ, because "you never began" and "you already sealed it" send the reader to
        /// different places.</summary>
        [Fact]
        public void EndOutsideARecording_IsRefusedAndSaysWhichCaseItWas()
        {
            using var fixture = new Fixture();
            using VulkanCommandList list = fixture.CreateList();

            InvalidOperationException never = Assert.Throws<InvalidOperationException>(list.End);
            Assert.Contains("not recording", never.Message, StringComparison.Ordinal);

            list.Begin();
            list.End();

            InvalidOperationException twice = Assert.Throws<InvalidOperationException>(list.End);
            Assert.Contains("twice", twice.Message, StringComparison.Ordinal);
        }

        /// <summary>A disposed list answers ObjectDisposed on both lifecycle members, rather than reporting a
        /// sequencing error that sends the reader looking for a missing Begin that was never the problem.</summary>
        [Fact]
        public void ADisposedList_RefusesBeginAndEndAsObjectDisposed()
        {
            using var fixture = new Fixture();
            VulkanCommandList list = fixture.CreateList();
            list.Begin();
            list.Dispose();

            Assert.Throws<ObjectDisposedException>(list.Begin);
            Assert.Throws<ObjectDisposedException>(list.End);
        }

        // ---- The pools ----

        /// <summary>
        /// EVERY POOL AND EVERY BUFFER IS CREATED UP FRONT, one per slot, and no record path ever creates a driver
        /// object. A Begin that could allocate is a Begin that can fail on frame 4000, which is the shape all 25
        /// DEVICE_REMOVED stacks in #423 came out of on the other backend.
        /// </summary>
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        public void ConstructionAllocatesOnePoolAndOneBufferPerSlot(int depth)
        {
            using var fixture = new Fixture(depth);
            using VulkanCommandList list = fixture.CreateList();

            Assert.Equal(depth, fixture.Api.Pools.Count);
            Assert.Equal(depth, fixture.Api.Events.Count(e => e.StartsWith("CreatePool", StringComparison.Ordinal)));
            Assert.Equal(depth,
                fixture.Api.Events.Count(e => e.StartsWith("AllocateBuffer", StringComparison.Ordinal)));
            Assert.Equal(depth, list.Ring.Depth);
        }

        /// <summary>The ring is bounded by the same constants the env knob clamps to, so a depth that got past the
        /// parse cannot get past the ring either.</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(17)]
        public void ADepthOutsideTheKnobsRange_IsRefused(int depth)
        {
            var api = new FakeVulkanCommandApi();
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new VulkanCommandPoolRing(api, depth, timeline, new VulkanBackpressure()));
        }

        /// <summary>
        /// THE SLOTS ADVANCE IN ORDER AND WRAP, and each record resets its OWN pool before beginning its own
        /// buffer. The whole-pool reset is decision V-R2: the incumbent's one pool with RESET_COMMAND_BUFFER puts
        /// the driver on its slower per-buffer allocator, and there is no parameter on the seam through which that
        /// flag could be asked for.
        /// </summary>
        [Fact]
        public void EachBeginResetsItsOwnPoolAndTheSlotsWrap()
        {
            using var fixture = new Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            for (int i = 0; i < 4; i++)
            {
                list.Begin();
                list.End();
            }

            Assert.Equal(
                new[] { "ResetPool(p0)", "Begin(b0)", "End(b0)", "ResetPool(p1)", "Begin(b1)", "End(b1)",
                    "ResetPool(p2)", "Begin(b2)", "End(b2)", "ResetPool(p0)", "Begin(b0)", "End(b0)" },
                fixture.Api.Events.Where(e => !e.StartsWith("CreatePool", StringComparison.Ordinal)
                    && !e.StartsWith("AllocateBuffer", StringComparison.Ordinal)));
        }

        // ---- The slot wait, and MV3's accounting ----

        /// <summary>
        /// A SLOT NOTHING EVER SUBMITTED IS NOT WAITED FOR AT ALL. The first Depth records of a list's life all
        /// take this path, which is why a freshly created list starts recording without touching the timeline.
        /// </summary>
        [Fact]
        public void TheFirstRecordsOfAListsLife_WaitOnNothing()
        {
            using var fixture = new Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            for (int i = 0; i < 3; i++)
            {
                list.Begin();
                list.End();
            }

            Assert.Equal(0, fixture.Semaphore.WaitCount);
            Assert.Equal(0, fixture.Backpressure.Totals.Count);
        }

        /// <summary>
        /// A WRAP ONTO A SLOT THE GPU HAS ALREADY PASSED COUNTS NOTHING. This is the steady state at depth 3 and
        /// is what MV3's exit criterion asserts about a real capture: the poll returns and the record starts.
        /// </summary>
        [Fact]
        public void AWrapOntoAFinishedSlot_DoesNotBlockAndCountsNothing()
        {
            using var fixture = new Fixture(depth: 2);
            using VulkanCommandList list = fixture.CreateList();

            fixture.RecordAndSubmit(list);
            fixture.RecordAndSubmit(list);

            // The GPU has caught up with everything submitted, so the wrap onto slot 0 finds its value passed.
            fixture.Semaphore.Completed = fixture.Timeline.LastSubmitted;

            list.Begin();
            list.End();

            Assert.Equal(0, fixture.Semaphore.WaitCount);
            Assert.Equal(0, fixture.Backpressure.Totals.Count);
        }

        /// <summary>
        /// A WRAP ONTO A SLOT STILL IN FLIGHT BLOCKS, WAITS FOR THAT SLOT'S OWN VALUE, AND COUNTS ONCE WITH TIME.
        /// This is the backpressure MV3 measures, and the value waited for is the slot's rather than the device's
        /// latest: waiting for the newest submission would over-wait by a whole frame on a device with several
        /// lists.
        /// </summary>
        [Fact]
        public void AWrapOntoASlotStillInFlight_BlocksAndIsCountedOnce()
        {
            using var fixture = new Fixture(depth: 2);
            using VulkanCommandList list = fixture.CreateList();

            ulong first = fixture.RecordAndSubmit(list);
            fixture.RecordAndSubmit(list);

            // Nothing has completed, so wrapping back onto slot 0 has to wait for slot 0's own submission.
            list.Begin();
            list.End();

            Assert.Equal(1, fixture.Semaphore.WaitCount);
            Assert.Equal(first, fixture.Semaphore.LastWaitValue);

            VulkanWaitTotals stalls = fixture.Backpressure.Totals;
            Assert.Equal(1, stalls.Count);
            Assert.True(stalls.Ticks >= 0);
        }

        /// <summary>
        /// THE STALL LANDS IN THE DEVICE COUNTER MV3 READS, on the same accumulator the uniform ring will use.
        /// Splitting them would ask a reader to add two numbers up before the gate meant anything, and the exit
        /// criterion is a single zero.
        /// </summary>
        [Fact]
        public void ASlotStall_IsReportedThroughTheSameAccumulatorTheRingWillUse()
        {
            using var fixture = new Fixture(depth: 2);
            using VulkanCommandList list = fixture.CreateList();

            fixture.RecordAndSubmit(list);
            fixture.RecordAndSubmit(list);
            list.Begin();
            list.End();

            // Exactly what VulkanGpuDevice.Counters reads into BackpressureStallCount and BackpressureStallMs.
            Assert.Equal(1, fixture.Backpressure.Totals.Count);
            Assert.True(fixture.Backpressure.Totals.TotalMs >= 0d);
        }

        /// <summary>A dead device answers its own allocated value, which is at or above anything a slot can hold,
        /// so a wrap returns without blocking rather than waiting on a counter nothing can advance.</summary>
        [Fact]
        public void AWrapOnADeadDevice_DoesNotBlock()
        {
            using var fixture = new Fixture(depth: 2);
            using VulkanCommandList list = fixture.CreateList();

            fixture.RecordAndSubmit(list);
            fixture.RecordAndSubmit(list);
            fixture.Liveness.MarkDead();

            list.Begin();
            list.End();

            Assert.Equal(0, fixture.Semaphore.WaitCount);
            Assert.Equal(0, fixture.Backpressure.Totals.Count);
        }

        // ---- Disposal ----

        /// <summary>
        /// A LIST DISPOSED WITH SUBMISSIONS OUTSTANDING DESTROYS NOTHING YET. Its pools go to the device's retire
        /// list at the HIGHEST value any of its slots was submitted at, and are destroyed once the counter passes
        /// it. No refcount, unlike the incumbent, because the retire list exists for resources anyway.
        /// </summary>
        [Fact]
        public void DisposalInFlight_RetiresThePoolsAtTheHighestSubmittedValue()
        {
            using var fixture = new Fixture(depth: 3);
            VulkanCommandList list = fixture.CreateList();

            fixture.RecordAndSubmit(list);
            ulong highest = fixture.RecordAndSubmit(list);

            list.Dispose();

            Assert.Empty(fixture.Api.Destroyed);
            Assert.Equal(3, fixture.Retired.Count);

            // One short of the value: still held, because that submission may still be reading the pool.
            fixture.Semaphore.Completed = highest - 1;
            Assert.Equal(0, fixture.Retired.Drain(fixture.Timeline.CompletedValue));
            Assert.Empty(fixture.Api.Destroyed);

            fixture.Semaphore.Completed = highest;
            Assert.Equal(3, fixture.Retired.Drain(fixture.Timeline.CompletedValue));
            Assert.Equal(3, fixture.Api.Destroyed.Count);
            Assert.Equal(fixture.Api.Pools.OrderBy(p => p), fixture.Api.Destroyed.OrderBy(p => p));
        }

        /// <summary>A list nothing ever submitted retires at 0, and an entry at 0 is released by the very next
        /// drain. Pools no submission ever referenced are safe to destroy immediately, which is correct rather
        /// than a special case.</summary>
        [Fact]
        public void DisposalWithNothingSubmitted_ReleasesOnTheNextDrain()
        {
            using var fixture = new Fixture(depth: 2);
            VulkanCommandList list = fixture.CreateList();
            list.Begin();
            list.End();

            list.Dispose();

            Assert.Equal(2, fixture.Retired.Drain(fixture.Timeline.CompletedValue));
            Assert.Equal(2, fixture.Api.Destroyed.Count);
        }

        /// <summary>
        /// THE HELD DESTROYS ARE TERMINAL, which is what makes them legal in the teardown drain that runs between
        /// vkDeviceWaitIdle and vkDestroyDevice: each one is a single vkDestroyCommandPool that retires nothing and
        /// allocates nothing, so a drain cannot leave a second generation of entries behind it.
        /// </summary>
        [Fact]
        public void TheHeldDestroysAreTerminal_SoOneDrainEmptiesTheList()
        {
            using var fixture = new Fixture(depth: 3);
            VulkanCommandList list = fixture.CreateList();
            fixture.RecordAndSubmit(list);
            list.Dispose();

            fixture.Retired.DrainAll();

            Assert.Equal(0, fixture.Retired.Count);
            Assert.Equal(3, fixture.Api.Destroyed.Count);
        }

        /// <summary>Disposing twice retires the pools once. A consumer disposing a list twice is a teardown-order
        /// accident rather than a defect, and a double retire would be a double destroy.</summary>
        [Fact]
        public void DisposingTwice_RetiresThePoolsOnce()
        {
            using var fixture = new Fixture(depth: 2);
            VulkanCommandList list = fixture.CreateList();

            list.Dispose();
            list.Dispose();

            Assert.Equal(2, fixture.Retired.Count);
        }

        /// <summary>Disposing mid-recording discards the record and seals nothing. vkDestroyCommandPool frees every
        /// buffer whatever state it is in, so ending a record nobody will submit would be a native call bought for
        /// nothing.</summary>
        [Fact]
        public void DisposingMidRecording_EndsNothingAndStillRetires()
        {
            using var fixture = new Fixture(depth: 2);
            VulkanCommandList list = fixture.CreateList();
            list.Begin();

            list.Dispose();

            Assert.DoesNotContain(fixture.Api.Events, e => e.StartsWith("End(", StringComparison.Ordinal));
            Assert.Equal(2, fixture.Retired.Count);
        }

        // ---- The unbuilt members ----

        /// <summary>
        /// EVERY RECORDING MEMBER REFUSES BY NAMING THE ROW THAT BUILDS IT, as a full URL, because the reader of
        /// that message needs to know whether to wait for a row or file a bug. The list is scanned against the
        /// interface below, so a seam member added later cannot quietly land here unattributed.
        /// </summary>
        [Fact]
        public void EveryUnbuiltRecordingMember_RefusesWithItsOwnRowUrl()
        {
            using var fixture = new Fixture();
            using VulkanCommandList list = fixture.CreateList();
            list.Begin();

            foreach ((string member, Action<IGpuCommandList> call) in EveryRecordingCommand())
            {
                NotSupportedException refused = Assert.Throws<NotSupportedException>(() => call(list));
                Assert.Contains("https://github.com/APKiwiOrg/KhaozEngine/issues/", refused.Message,
                    StringComparison.Ordinal);
                Assert.Contains("not built yet", refused.Message, StringComparison.Ordinal);
                Assert.False(string.IsNullOrEmpty(member));
            }
        }

        /// <summary>The coverage above is a hand-written list, so this is what keeps it honest when the seam grows
        /// a member: every method on the interface is either a lifecycle member this row built or a recording
        /// member the list above refuses.</summary>
        [Fact]
        public void TheRefusalCoverage_NamesEveryRecordingSeamMember()
        {
            string[] covered = EveryRecordingCommand()
                .Select(c => c.Member)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            string[] declared = typeof(IGpuCommandList).GetMethods()
                .Select(m => m.Name)
                .Where(n => n is not ("Begin" or "End" or "Dispose"))
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(declared, covered);
        }

        // ---- Fixtures ----

        static readonly uint OneWord = 1u;

        /// <summary>One call per recording member. Arguments are irrelevant: every call is expected to refuse
        /// before it looks at one.</summary>
        static IEnumerable<(string Member, Action<IGpuCommandList> Call)> EveryRecordingCommand()
            => new (string, Action<IGpuCommandList>)[]
            {
                (nameof(IGpuCommandList.SetFramebuffer), l => l.SetFramebuffer(null!)),
                (nameof(IGpuCommandList.ClearColorTarget), l => l.ClearColorTarget(0, new Color(1f, 1f, 1f, 1f))),
                (nameof(IGpuCommandList.ClearDepthStencil), l => l.ClearDepthStencil(1f)),
                (nameof(IGpuCommandList.SetPipeline), l => l.SetPipeline(null!)),
                (nameof(IGpuCommandList.SetGraphicsResourceSet), l => l.SetGraphicsResourceSet(0, null!)),
                (nameof(IGpuCommandList.SetGraphicsResourceSet), l => l.SetGraphicsResourceSet(0, null!, 256)),
                (nameof(IGpuCommandList.SetVertexBuffer), l => l.SetVertexBuffer(0, null!)),
                (nameof(IGpuCommandList.SetVertexBuffer), l => l.SetVertexBuffer(0, null!, 64)),
                (nameof(IGpuCommandList.SetIndexBuffer), l => l.SetIndexBuffer(null!, GpuIndexFormat.UInt32)),
                (nameof(IGpuCommandList.SetScissorRect), l => l.SetScissorRect(0, 0, 0, 16, 16)),
                (nameof(IGpuCommandList.SetFullScissorRects), l => l.SetFullScissorRects()),
                (nameof(IGpuCommandList.Draw), l => l.Draw(3)),
                (nameof(IGpuCommandList.Draw), l => l.Draw(3, 1, 0, 0)),
                (nameof(IGpuCommandList.DrawIndexed), l => l.DrawIndexed(3, 1, 0, 0, 0)),
                (nameof(IGpuCommandList.UpdateBuffer), l => l.UpdateBuffer(null!, 0, in OneWord)),
                (nameof(IGpuCommandList.UpdateBuffer), l => l.UpdateBuffer<byte>(null!, 0, new byte[] { 1 })),
                (nameof(IGpuCommandList.CopyBuffer), l => l.CopyBuffer(null!, 0, null!, 0, 16)),
                (nameof(IGpuCommandList.CopyTexture), l => l.CopyTexture(null!, null!)),
                (nameof(IGpuCommandList.CopyTextureSubresource),
                    l => l.CopyTextureSubresource(null!, 0, 0, null!, 16, 16)),
                (nameof(IGpuCommandList.CopyTextureSubresource),
                    l => l.CopyTextureSubresource(null!, 0, 0, null!, 0, 0, 16, 16)),
                (nameof(IGpuCommandList.GenerateMipmaps), l => l.GenerateMipmaps(null!)),
                (nameof(IGpuCommandList.ResolveTexture), l => l.ResolveTexture(null!, null!)),
                (nameof(IGpuCommandList.SetComputePipeline), l => l.SetComputePipeline(null!)),
                (nameof(IGpuCommandList.SetComputeResourceSet), l => l.SetComputeResourceSet(0, null!)),
                (nameof(IGpuCommandList.SetComputeResourceSet), l => l.SetComputeResourceSet(0, null!, 256)),
                (nameof(IGpuCommandList.Dispatch), l => l.Dispatch(1, 1, 1)),
            };

        /// <summary>A device's worth of command-path machinery with no device: the seam, the timeline, the retire
        /// list, the backpressure accumulator and the submit queue, wired exactly as
        /// <c>VulkanGpuDevice</c> wires them.</summary>
        internal sealed class Fixture : IDisposable
        {
            readonly int _depth;

            internal Fixture(int depth = 3)
            {
                _depth = depth;
                Timeline = new VulkanTimeline(Semaphore, Liveness);
                Submits = new VulkanSubmitQueue(Api, Timeline);
            }

            internal FakeVulkanCommandApi Api { get; } = new();

            internal FakeVulkanTimelineSemaphore Semaphore { get; } = new();

            internal VulkanDeviceLiveness Liveness { get; } = new();

            internal VulkanTimeline Timeline { get; }

            internal VulkanRetireList Retired { get; } = new();

            internal VulkanBackpressure Backpressure { get; } = new();

            internal VulkanSubmitQueue Submits { get; }

            internal VulkanCommandList CreateList()
                => new(new VulkanCommandPoolRing(Api, _depth, Timeline, Backpressure), Retired);

            /// <summary>One whole record-and-submit cycle, which is what a frame does to one list.</summary>
            internal ulong RecordAndSubmit(VulkanCommandList list)
            {
                list.Begin();
                list.End();
                return Submits.Submit(list, null);
            }

            public void Dispose() => Timeline.Dispose();
        }
    }
}
