using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ONE ROUTING DECISION, AT BOTH LEVELS (sections 9.2 and 9.3). A ring-backed uniform buffer takes a
    /// memcpy into ring memory and records nothing at all. Everything else stages through the arena and records a
    /// copy plus a narrowed barrier, with the render-pass split those copies unavoidably cause.
    ///
    /// <para><b>THE SPLIT IS THE CALL RATHER THAN A USAGE HINT ON THE BUFFER.</b> A RECORD-TIME write reaches the
    /// CURRENT segment alone and a DEVICE-LEVEL one reaches EVERY segment, because the call is what knows whether
    /// it happens once. Every shipped record-time uniform write is unconditional per frame, so replicating those
    /// would be <c>FramesInFlight</c> memcpys for a value the next frame overwrites, on the hot path.</para>
    ///
    /// <para><b>WHY THE NON-UNIFORM LEG STILL REFUSES.</b> A non-uniform buffer cannot EXIST until row 9
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519) builds <c>IGpuResourceFactory</c>, so what row 8 owes
    /// is the DECISION and the arena behind it, both of which are here. The list's implementation of that leg
    /// arrives with the sink that records the copy.</para>
    /// </summary>
    public sealed class VulkanUpdateBufferRoutingTests
    {
        // ---- the record-time level -----------------------------------------------------------------------

        /// <summary>
        /// A RECORD-TIME UNIFORM WRITE IS A MEMCPY AND RECORDS NOTHING: no staging lease, no copy command, no
        /// barrier and no pass split. That is the whole of what the ring buys on this backend, where the shipped
        /// incumbent's same call is a render-pass split plus a full pipeline flush plus a global memory barrier.
        /// </summary>
        [Fact]
        public void ARecordTimeUniformWrite_LandsInTheRingAndRecordsNothing()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);
            using var fixture = new VulkanCommandListTests.Fixture();
            var uploads = new CountingUploads();
            using VulkanCommandList list = Listed(fixture, uploads);
            list.Begin();

            var buffer = new FakeVulkanRingBackedBuffer(harness.Ring);
            list.UpdateBuffer<byte>(buffer, 16, new byte[] { 1, 2, 3, 4 });

            Assert.Equal(0, uploads.Uploads);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, harness.Ring.ReadSegment(0, 16, 4));
        }

        /// <summary>The single-value overload routes the same way, which is what every shipped renderer's per-draw
        /// uniform write actually is.</summary>
        [Fact]
        public void TheSingleValueOverload_RoutesToTheRingToo()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);
            using var fixture = new VulkanCommandListTests.Fixture();
            var uploads = new CountingUploads();
            using VulkanCommandList list = Listed(fixture, uploads);
            list.Begin();

            var buffer = new FakeVulkanRingBackedBuffer(harness.Ring);
            list.UpdateBuffer(buffer, 0, in FourBytes);

            Assert.Equal(0, uploads.Uploads);
            Assert.Equal(BitConverter.GetBytes(FourBytes), harness.Ring.ReadSegment(0, 0, 4));
        }

        /// <summary>A record-time write lands in the segment the CURRENT frame is writing, and in no other, which
        /// is section 9.4's Record-time writes row expressed through the seam rather than through the ring's own
        /// member.</summary>
        [Fact]
        public void ARecordTimeWrite_ReachesTheCurrentSegmentAlone()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);
            using var fixture = new VulkanCommandListTests.Fixture();
            using VulkanCommandList list = Listed(fixture, new CountingUploads());

            harness.Allocator.BeginFrame();
            list.Begin();

            var buffer = new FakeVulkanRingBackedBuffer(harness.Ring);
            list.UpdateBuffer<byte>(buffer, 0, new byte[] { 7, 7, 7, 7 });

            Assert.Equal(1, harness.Allocator.CurrentSegment);
            Assert.Equal(new byte[] { 7, 7, 7, 7 }, harness.Ring.ReadSegment(1, 0, 4));
            Assert.Equal(new byte[4], harness.Ring.ReadSegment(0, 0, 4));
            Assert.Equal(new byte[4], harness.Ring.ReadSegment(2, 0, 4));
        }

        /// <summary>A non-uniform buffer goes to the ARENA when the list has an uploader, which is the other half
        /// of the decision. The bulk path is where the copy, the barrier and the pass split live.</summary>
        [Fact]
        public void ANonUniformWrite_GoesToTheArena()
        {
            using var fixture = new VulkanCommandListTests.Fixture();
            var uploads = new CountingUploads();
            using VulkanCommandList list = Listed(fixture, uploads);
            list.Begin();

            var buffer = new FakeVulkanUploadBuffer(0xBEEF, 256, GpuBufferUsage.VertexBuffer);
            list.UpdateBuffer<byte>(buffer, 32, new byte[] { 5, 6 });

            Assert.Equal(1, uploads.Uploads);
            Assert.Equal(0xBEEFul, uploads.LastDestination);
            Assert.Equal(32ul, uploads.LastOffset);
            Assert.Equal(2, uploads.LastLength);
        }

        /// <summary>With no uploader the non-uniform leg refuses by naming ROW 9, because no such buffer can exist
        /// until that row builds the factory. The refusal moved off row 8 when the ring landed, which is what the
        /// list's refusal-coverage pair records.</summary>
        [Fact]
        public void WithNoUploader_ANonUniformWrite_NamesTheResourcesRow()
        {
            using var fixture = new VulkanCommandListTests.Fixture();
            using VulkanCommandList list = fixture.CreateList();
            list.Begin();

            var buffer = new FakeVulkanUploadBuffer(0xBEEF, 256, GpuBufferUsage.VertexBuffer);

            NotSupportedException ex = Assert.Throws<NotSupportedException>(
                () => list.UpdateBuffer<byte>(buffer, 0, new byte[] { 1 }));

            Assert.Contains("519", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("518", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>A null buffer is an argument error rather than a "not built yet", now that the member is
        /// built.</summary>
        [Fact]
        public void ANullBuffer_IsAnArgumentError()
        {
            using var fixture = new VulkanCommandListTests.Fixture();
            using VulkanCommandList list = fixture.CreateList();
            list.Begin();

            Assert.Throws<ArgumentNullException>(() => list.UpdateBuffer<byte>(null!, 0, new byte[] { 1 }));
        }

        /// <summary>
        /// EVERY <c>Begin</c> OPENS THE ARENA'S SLOT THE POOL RING JUST ADVANCED ONTO, which is the one boundary at
        /// which the staging blocks are provably finished with. Recycling the whole arena instead would hand back
        /// the blocks the previous record's submission is still reading.
        /// </summary>
        [Fact]
        public void EveryBegin_OpensTheArenaSlotThePoolRingAdvancedOnto()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            var uploads = new CountingUploads();
            using VulkanCommandList list = Listed(fixture, uploads);

            for (int record = 0; record < 5; record++)
            {
                list.Begin();
                list.End();
            }

            Assert.Equal(new[] { 0, 1, 2, 0, 1 }, uploads.Slots);
        }

        // ---- the device level ----------------------------------------------------------------------------

        /// <summary>
        /// THE DEVICE-LEVEL WRITE IS THE OFF-TIMELINE ONE and reaches EVERY segment, which is what makes a value
        /// written once persist for the buffer's life. Driven here through the ring allocator, because the device
        /// itself needs a real <c>VkDevice</c> to construct while the routing it performs does not.
        /// </summary>
        [Fact]
        public void TheDeviceLevelWrite_ReachesEverySegment()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            var payload = new byte[] { 3, 1, 4, 1, 5 };
            harness.Allocator.UpdateBuffer(harness.Ring, 8, payload);

            for (int segment = 0; segment < 3; segment++)
            {
                Assert.Equal(payload, harness.Ring.ReadSegment(segment, 8, payload.Length));
            }
        }

        /// <summary>The two levels are DIFFERENT shapes over the same buffer, which is the split V-M8 turns on: the
        /// record-time write is current-segment and the device-level one is every-segment, and a reader who
        /// confuses them reintroduces the two-frames-in-three defect.</summary>
        [Fact]
        public void TheTwoLevels_AreDifferentShapesOverTheSameBuffer()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);
            using var fixture = new VulkanCommandListTests.Fixture();
            using VulkanCommandList list = Listed(fixture, new CountingUploads());
            list.Begin();

            var buffer = new FakeVulkanRingBackedBuffer(harness.Ring);

            harness.Allocator.UpdateBuffer(harness.Ring, 0, new byte[] { 1, 1, 1, 1 });
            list.UpdateBuffer<byte>(buffer, 0, new byte[] { 2, 2, 2, 2 });

            Assert.Equal(new byte[] { 2, 2, 2, 2 }, harness.Ring.ReadSegment(0, 0, 4));
            Assert.Equal(new byte[] { 1, 1, 1, 1 }, harness.Ring.ReadSegment(1, 0, 4));
            Assert.Equal(new byte[] { 1, 1, 1, 1 }, harness.Ring.ReadSegment(2, 0, 4));
        }

        static readonly uint FourBytes = 0x0A0B0C0Du;

        static VulkanCommandList Listed(VulkanCommandListTests.Fixture fixture, IVulkanRecordUploads uploads)
            => new(new VulkanCommandPoolRing(fixture.Api, 3, fixture.Timeline, fixture.Backpressure),
                fixture.Retired, uploads);

        // The list's view of the staging arena, counting what it was asked for. The real implementation lands with
        // the sink that records the copy (see IVulkanRecordUploads).
        sealed class CountingUploads : IVulkanRecordUploads
        {
            internal int Uploads { get; private set; }

            internal ulong LastDestination { get; private set; }

            internal ulong LastOffset { get; private set; }

            internal int LastLength { get; private set; }

            internal List<int> Slots { get; } = new();

            public void Upload(IVulkanUploadDestination destination, ulong destinationOffsetBytes,
                ReadOnlySpan<byte> data)
            {
                Uploads++;
                LastDestination = destination.DeviceBuffer;
                LastOffset = destinationOffsetBytes;
                LastLength = data.Length;
            }

            public void BeginSlot(int slot) => Slots.Add(slot);
        }
    }
}
