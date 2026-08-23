using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PER-LIST STAGING ARENA AND THE UPLOAD IT RECORDS (V-M9, section 9.3): the size classes, the
    /// sub-allocation, the per-slot recycling boundary, the retention cap that replaces the incumbent's 512 bytes,
    /// and the barrier narrowed to the destination's actual usage.
    ///
    /// <para><b>THIS IS THE PATH THE RING IS NOT FOR.</b> A bulk payload does cost a copy command, a barrier and
    /// the render-pass split those copies unavoidably cause. What the arena removes is a different cost: the
    /// shipped incumbent destroys any returned staging buffer over 512 bytes, so every real-sized upload creates
    /// and destroys a <c>VkBuffer</c> AND a device memory block per call, and a scene load is thousands of those.
    /// </para>
    ///
    /// <para>Everything here is device-free behind <see cref="IVulkanStagingSource"/> and
    /// <see cref="IVulkanUploadSink"/>, so the policy runs under <c>dotnet test</c> on a machine with no Vulkan
    /// loader. What is left on the far side is a <c>vkCreateBuffer</c>, a destroy and a <c>vkCmdCopyBuffer</c>.
    /// </para>
    /// </summary>
    public sealed class VulkanStagingArenaTests
    {
        // ---- the size classes ----------------------------------------------------------------------------

        /// <summary>
        /// A REQUEST LANDS IN A POWER-OF-TWO CLASS, FLOORED AT THE BLOCK SIZE. A pool keyed on the exact byte count
        /// would hold one block per distinct upload size and reuse almost nothing, which is a different way to
        /// reach the same allocation storm.
        /// </summary>
        [Theory]
        [InlineData(1ul, 64ul)]
        [InlineData(64ul, 64ul)]
        [InlineData(65ul, 128ul)]
        [InlineData(129ul, 256ul)]
        [InlineData(1024ul, 1024ul)]
        [InlineData(1025ul, 2048ul)]
        public void ARequest_LandsInThePowerOfTwoClassAtOrAboveIt(ulong request, ulong expected)
            => Assert.Equal(expected, VulkanStagingArena.BlockSizeFor(request, blockBytes: 64));

        /// <summary>The default block size and retention cap are policy numbers, pinned so moving either is a
        /// deliberate edit rather than a drift. The cap is the whole of V-M9's "real retention cap", against the
        /// incumbent's 512 bytes.</summary>
        [Fact]
        public void ThePolicyNumbers_ArePinned()
        {
            Assert.Equal(64UL * 1024, VulkanStagingArena.DefaultBlockBytes);
            Assert.Equal(8UL * 1024 * 1024, VulkanStagingArena.DefaultRetentionBytes);
            Assert.True(VulkanStagingArena.DefaultRetentionBytes > 512,
                "The retention cap exists to stop every real-sized upload allocating and destroying a buffer and a "
                + "memory block, which is what the incumbent's 512-byte cap causes.");
        }

        // ---- the sub-allocation --------------------------------------------------------------------------

        /// <summary>
        /// A RUN OF SMALL UPLOADS SHARES ONE BLOCK, which is what makes the arena an arena rather than a pool of
        /// one-shot buffers. Each lease names the same buffer at its own offset, and the offsets do not overlap.
        /// </summary>
        [Fact]
        public void ARunOfSmallUploads_SharesOneBlockAtRisingOffsets()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            VulkanStagingLease first = arena.Take(100);
            VulkanStagingLease second = arena.Take(100);
            VulkanStagingLease third = arena.Take(100);

            Assert.Equal(1, arena.BlocksCreated);
            Assert.Equal(first.Buffer, second.Buffer);
            Assert.Equal(first.Buffer, third.Buffer);
            Assert.True(second.OffsetBytes >= first.OffsetBytes + first.SizeBytes);
            Assert.True(third.OffsetBytes >= second.OffsetBytes + second.SizeBytes);
        }

        /// <summary>A lease's mapped address is its block's mapping plus its own offset, which is what a caller
        /// memcpys to and what the copy's <c>srcOffset</c> names. Getting those two out of step would upload the
        /// wrong bytes with nothing thrown.</summary>
        [Fact]
        public void ALeasesMappedAddress_IsItsBlocksMappingPlusItsOwnOffset()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            arena.Take(100);
            VulkanStagingLease second = arena.Take(100);

            VulkanStagingBlock block = source.Live[0];
            Assert.Equal(block.Mapped + (nint)second.OffsetBytes, second.Mapped);
        }

        /// <summary>Bytes written through a lease land at that lease's offset in the block, and nowhere else. The
        /// only way to see that is to read the block's memory back, which is why the fake pins its arrays.</summary>
        [Fact]
        public void BytesWrittenThroughALease_LandAtItsOwnOffset()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            VulkanStagingLease first = arena.Take(4);
            VulkanStagingLease second = arena.Take(4);

            first.Write(new byte[] { 1, 2, 3, 4 });
            second.Write(new byte[] { 9, 8, 7, 6 });

            byte[] bytes = source.BytesOf(first.Buffer);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes.AsSpan((int)first.OffsetBytes, 4).ToArray());
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, bytes.AsSpan((int)second.OffsetBytes, 4).ToArray());
        }

        /// <summary>A lease refuses more bytes than it reserved, because those bytes would run into the next
        /// sub-allocation of the same block.</summary>
        [Fact]
        public void ALease_RefusesMoreBytesThanItReserved()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            VulkanStagingLease lease = arena.Take(4);

            Assert.Throws<ArgumentOutOfRangeException>(() => lease.Write(new byte[5]));
        }

        /// <summary>A request larger than the block size gets a block of its own rather than failing, and the
        /// arithmetic that sizes it cannot be reached in a failing state.</summary>
        [Fact]
        public void ARequestLargerThanTheBlock_GetsItsOwnBlock()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            VulkanStagingLease lease = arena.Take(5000);

            Assert.Equal(1, arena.BlocksCreated);
            Assert.True(source.Live[0].SizeBytes >= 5000);
            Assert.Equal(5000ul, lease.SizeBytes);
        }

        /// <summary>Alignment is honoured, and a zero-byte or non-power-of-two request is refused rather than
        /// producing a lease nothing can be copied out of.</summary>
        [Fact]
        public void AlignmentIsHonoured_AndAnImpossibleRequestIsRefused()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 4096);

            arena.Take(1);
            VulkanStagingLease aligned = arena.Take(16, alignment: 256);

            Assert.Equal(0ul, aligned.OffsetBytes % 256);

            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Take(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.Take(16, alignment: 96));
        }

        // ---- the per-slot recycling boundary -------------------------------------------------------------

        /// <summary>
        /// OPENING A SLOT GIVES BACK ONLY THAT SLOT'S BLOCKS, which is the invariant that keeps the boundary safe.
        /// The list's <c>Begin</c> waited for the slot it is advancing ONTO before it reset that pool, so the
        /// blocks that slot filled last time round are provably finished with. The blocks the PREVIOUS record
        /// filled belong to a submission that may still be in flight, and handing those back is the same class of
        /// corruption the ring's fence gate exists to prevent, arriving through the other path.
        /// </summary>
        [Fact]
        public void OpeningASlot_GivesBackThatSlotsBlocksAlone()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            arena.BeginSlot(0);
            arena.Take(900);
            arena.Take(900);   // does not fit beside the first, so a second block opens

            arena.BeginSlot(1);
            arena.Take(900);

            // Slot 0's two blocks are still held, because slot 0 has not been reopened.
            Assert.Equal(3, arena.OpenBlockCount);
            Assert.Equal(0, arena.FreeBlockCount);

            arena.BeginSlot(0);

            // Now slot 0's two came back, and slot 1's one did not.
            Assert.Equal(1, arena.OpenBlockCount);
            Assert.Equal(2, arena.FreeBlockCount);
        }

        /// <summary>A slot outside the arena's depth is refused, matching the pool ring it shadows.</summary>
        [Fact]
        public void ASlotOutsideTheDepth_IsRefused()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3);

            Assert.Throws<ArgumentOutOfRangeException>(() => arena.BeginSlot(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => arena.BeginSlot(-1));
        }

        // ---- the retention cap ---------------------------------------------------------------------------

        /// <summary>
        /// A RECYCLED BLOCK IS REUSED RATHER THAN RECREATED, which is the whole fix. Walking round the slots
        /// repeatedly with the same upload shape allocates ONCE, where the incumbent allocated and destroyed per
        /// call.
        /// </summary>
        [Fact]
        public void WalkingTheSlots_ReusesBlocksRatherThanRecreatingThem()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 4096);

            for (int frame = 0; frame < 30; frame++)
            {
                arena.BeginSlot(frame % 3);
                arena.Take(1000);
            }

            Assert.Equal(3, arena.BlocksCreated);
            Assert.Equal(0, arena.BlocksDestroyed);
        }

        /// <summary>
        /// THE CAP TURNS BLOCKS AWAY AND DESTROYS THEM, LARGEST FIRST. Retaining everything would let a one-off
        /// texture load pin its peak for the process's life, and the largest block is the one worth giving back
        /// because the small classes are the ones a load revisits thousands of times.
        /// </summary>
        [Fact]
        public void TheRetentionCap_DestroysTheLargestBlocksFirst()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(
                source, framesInFlight: 3, blockBytes: 1024, retentionBytes: 3000);

            arena.BeginSlot(0);
            arena.Take(900);     // a 1024 block
            arena.Take(900);     // another 1024
            arena.Take(4000);    // a 4096 block

            Assert.Equal(3, arena.BlocksCreated);

            arena.BeginSlot(0);

            // 1024 + 1024 + 4096 = 6144 retained, over the 3000 cap, so the 4096 goes and the two 1024s stay.
            Assert.Equal(1, arena.BlocksDestroyed);
            Assert.Equal(2048ul, arena.RetainedBytes);
            Assert.Equal(2, arena.FreeBlockCount);
        }

        /// <summary>A zero cap is the incumbent's shape, kept constructible so the difference is pinned rather than
        /// asserted in prose: nothing is retained, so every upload allocates and destroys.</summary>
        [Fact]
        public void AZeroCap_ReproducesTheIncumbentsAllocationStorm()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(
                source, framesInFlight: 3, blockBytes: 1024, retentionBytes: 0);

            for (int frame = 0; frame < 6; frame++)
            {
                arena.BeginSlot(frame % 3);
                arena.Take(900);
            }

            // Six uploads, six vkCreateBuffer results, and every block that came back was destroyed rather than
            // reused. The same six uploads under the shipped cap allocate THREE, one per slot, and destroy none.
            Assert.Equal(6, arena.BlocksCreated);
            Assert.Equal(3, arena.BlocksDestroyed);
            Assert.Equal(0ul, arena.RetainedBytes);
        }

        /// <summary>A pooled block is taken by SIZE: a small request takes the smallest block that fits rather than
        /// carving up a large one that another request will need.</summary>
        [Fact]
        public void APooledBlock_IsTakenBySize()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            arena.BeginSlot(0);
            arena.Take(900);
            arena.Take(4000);
            arena.BeginSlot(0);

            Assert.Equal(2, arena.FreeBlockCount);

            arena.Take(16);

            // The 1024 block was reused, so the 4096 one is still pooled and nothing new was created.
            Assert.Equal(2, arena.BlocksCreated);
            Assert.Equal(4096ul, arena.RetainedBytes);
        }

        /// <summary>Disposal destroys every block, open and pooled. The arena dies with its list, for the same
        /// reason its command pools do.</summary>
        [Fact]
        public void Disposal_DestroysEveryBlock()
        {
            var source = new FakeStagingSource();
            var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            arena.BeginSlot(0);
            arena.Take(900);
            arena.BeginSlot(1);
            arena.Take(900);
            arena.BeginSlot(0);   // one of them goes to the pool

            arena.Dispose();

            Assert.Equal(2, arena.BlocksCreated);
            Assert.Equal(2, arena.BlocksDestroyed);
            Assert.Empty(source.Live);

            // Idempotent: a consumer disposing twice is a teardown-order accident, not a double destroy.
            arena.Dispose();
            Assert.Equal(2, arena.BlocksDestroyed);
        }

        /// <summary>
        /// DISPOSE REACHES EVERY BLOCK, OPEN AND FREE, EXACTLY ONCE. The fake source counts calls per block, so a
        /// block reachable from two paths (an open slot the loop revisits, or a free-list entry Dispose also walks)
        /// would show a count of two rather than one.
        /// <para>
        /// WHAT THIS DOES NOT PROVE: that a LIVE device defers the native free behind the retire list rather than
        /// running it immediately. The fake source has no timeline to defer against, so an immediate call and a
        /// correctly deferred one look identical here. That half of the contract on
        /// <see cref="IVulkanStagingSource.Destroy"/> is row 9's to prove, against the real source over
        /// <see cref="VulkanRetireList"/>, once it exists.
        /// </para>
        /// </summary>
        [Fact]
        public void Disposal_DestroysOpenAndFreeBlocksExactlyOnceEach()
        {
            var source = new FakeStagingSource();
            var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);

            arena.BeginSlot(0);
            arena.Take(900);   // slot 0's open block, a 1024 class
            arena.BeginSlot(1);
            arena.Take(900);   // slot 1's open block, another 1024 class
            arena.BeginSlot(0);   // slot 0's block returns to the free list
            arena.BeginSlot(2);
            arena.Take(5000);   // too big for the freed 1024 block, so this opens a fresh one rather than reusing it

            List<ulong> handles = source.Live.ConvertAll(block => block.Buffer);
            Assert.Equal(3, handles.Count);
            Assert.Equal(2, arena.OpenBlockCount);
            Assert.Equal(1, arena.FreeBlockCount);

            arena.Dispose();

            foreach (ulong handle in handles)
            {
                Assert.Equal(1, source.DestroyCountOf(handle));
            }
        }

        // ---- the narrowed barrier ------------------------------------------------------------------------

        /// <summary>
        /// THE BARRIER NAMES WHAT ACTUALLY READS THE DESTINATION, which is what the incumbent's one global
        /// <c>VertexAttributeRead</c> at <c>VertexInput</c> gets wrong for every buffer that is not a vertex
        /// buffer.
        /// </summary>
        [Theory]
        [InlineData(GpuBufferUsage.VertexBuffer, PipelineStageFlags2.VertexInputBit,
            AccessFlags2.VertexAttributeReadBit)]
        [InlineData(GpuBufferUsage.IndexBuffer, PipelineStageFlags2.VertexInputBit, AccessFlags2.IndexReadBit)]
        [InlineData(GpuBufferUsage.IndirectBuffer, PipelineStageFlags2.DrawIndirectBit,
            AccessFlags2.IndirectCommandReadBit)]
        public void TheDestinationMasks_NameWhatActuallyReadsTheBuffer(GpuBufferUsage usage,
            PipelineStageFlags2 stage, AccessFlags2 access)
        {
            Assert.Equal(stage, VulkanUploadBarrier.DestinationStage(usage));
            Assert.Equal(access, VulkanUploadBarrier.DestinationAccess(usage));
        }

        /// <summary>A UNIFORM read is <c>UniformRead</c> and not <c>VertexAttributeRead</c>, which is the specific
        /// under-synchronisation the shipped incumbent's global barrier carries for every per-frame uniform buffer
        /// in the engine. The ring is why no uniform write reaches this path at all, and the entry exists so the
        /// table is total over the enum.</summary>
        [Fact]
        public void AUniformDestination_IsSynchronisedAsAUniformRead()
        {
            Assert.Equal(AccessFlags2.UniformReadBit,
                VulkanUploadBarrier.DestinationAccess(GpuBufferUsage.UniformBuffer));
            Assert.NotEqual(AccessFlags2.VertexAttributeReadBit,
                VulkanUploadBarrier.DestinationAccess(GpuBufferUsage.UniformBuffer));
        }

        /// <summary>A buffer created for two things is read at two stages with two access types, and the masks are
        /// UNIONED rather than switched. A switch would have to pick one and would be wrong for the other.</summary>
        [Fact]
        public void TwoUsages_UnionTheirStagesAndAccesses()
        {
            const GpuBufferUsage both = GpuBufferUsage.VertexBuffer | GpuBufferUsage.IndexBuffer;

            Assert.Equal(PipelineStageFlags2.VertexInputBit, VulkanUploadBarrier.DestinationStage(both));
            Assert.Equal(AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit,
                VulkanUploadBarrier.DestinationAccess(both));
        }

        /// <summary>A read-write storage buffer names BOTH storage accesses, because a shader that writes it after
        /// the upload is ordered against the transfer write too.</summary>
        [Fact]
        public void AReadWriteStorageDestination_NamesBothStorageAccesses()
        {
            Assert.Equal(AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit,
                VulkanUploadBarrier.DestinationAccess(GpuBufferUsage.StructuredBufferReadWrite));
        }

        /// <summary>A buffer with no read usage at all is still copied FROM, so the masks fall back to transfer
        /// rather than to NONE. A barrier that orders the write against nothing is the shape that passes review and
        /// synchronises nothing.</summary>
        [Fact]
        public void ADestinationWithNoReadUsage_FallsBackToTransferRatherThanNone()
        {
            Assert.Equal(PipelineStageFlags2.TransferBit,
                VulkanUploadBarrier.DestinationStage(GpuBufferUsage.Staging));
            Assert.Equal(AccessFlags2.TransferReadBit,
                VulkanUploadBarrier.DestinationAccess(GpuBufferUsage.Staging));
        }

        /// <summary>The barrier is over the WRITTEN RANGE of the destination buffer, not a global memory barrier,
        /// and it names no queue-family transfer because this backend has one queue.</summary>
        [Fact]
        public void TheBarrier_IsOverTheWrittenRangeOfOneBuffer()
        {
            BufferMemoryBarrier2 barrier = VulkanUploadBarrier.After(
                destination: 0xB0B0, offsetBytes: 64, sizeBytes: 128, GpuBufferUsage.VertexBuffer);

            Assert.Equal(0xB0B0ul, barrier.Buffer.Handle);
            Assert.Equal(64ul, barrier.Offset);
            Assert.Equal(128ul, barrier.Size);
            Assert.Equal(Vk.QueueFamilyIgnored, barrier.SrcQueueFamilyIndex);
            Assert.Equal(Vk.QueueFamilyIgnored, barrier.DstQueueFamilyIndex);
            Assert.Equal(PipelineStageFlags2.TransferBit, barrier.SrcStageMask);
            Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
        }

        // ---- the barrier BEFORE the copy (#618) ----------------------------------------------------------

        /// <summary>
        /// THE PRE-COPY BARRIER NAMES THE STAGES THAT ALREADY READ THE BUFFER, which is what makes a per-frame
        /// vertex-buffer upload safe against the previous frame's still-running vertex fetch. Without it the sync
        /// validation tier reports <c>SYNC_COPY_TRANSFER_WRITE</c> against a prior
        /// <c>SYNC_VERTEX_ATTRIBUTE_INPUT_VERTEX_ATTRIBUTE_READ</c>
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/618">#618</see>).
        /// </summary>
        [Theory]
        [InlineData(GpuBufferUsage.VertexBuffer, PipelineStageFlags2.VertexInputBit)]
        [InlineData(GpuBufferUsage.IndexBuffer, PipelineStageFlags2.VertexInputBit)]
        [InlineData(GpuBufferUsage.IndirectBuffer, PipelineStageFlags2.DrawIndirectBit)]
        public void ThePriorStages_AreTheReadingStagesPlusTransfer(GpuBufferUsage usage,
            PipelineStageFlags2 reader)
        {
            Assert.Equal(reader | PipelineStageFlags2.TransferBit, VulkanUploadBarrier.PriorStage(usage));
        }

        /// <summary>
        /// THE PRE-COPY BARRIER IS TRANSFER-WRITE ON THE DESTINATION SIDE, over the same range the copy is about to
        /// write. Its source ACCESS is the transfer write alone, because a read needs no availability operation and
        /// the write-after-read half is carried by the source STAGE mask, while an earlier upload's write to the
        /// same range is the one access that does have to be made available.
        /// </summary>
        [Fact]
        public void ThePreCopyBarrier_OrdersPriorReadsAndPriorWritesAgainstTheTransferWrite()
        {
            BufferMemoryBarrier2 barrier = VulkanUploadBarrier.Before(
                destination: 0xB0B0, offsetBytes: 64, sizeBytes: 128, GpuBufferUsage.VertexBuffer);

            Assert.Equal(0xB0B0ul, barrier.Buffer.Handle);
            Assert.Equal(64ul, barrier.Offset);
            Assert.Equal(128ul, barrier.Size);
            Assert.Equal(PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.TransferBit,
                barrier.SrcStageMask);
            Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
            Assert.Equal(PipelineStageFlags2.TransferBit, barrier.DstStageMask);
            Assert.Equal(AccessFlags2.TransferWriteBit, barrier.DstAccessMask);
        }

        /// <summary>A destination with no read usage still gets a pre-copy barrier, and its source stage falls back
        /// to transfer rather than collapsing to NONE, which would order the write against nothing.</summary>
        [Fact]
        public void APreCopyBarrierForANonReadDestination_StillNamesTransfer()
        {
            Assert.Equal(PipelineStageFlags2.TransferBit, VulkanUploadBarrier.PriorStage(GpuBufferUsage.Staging));
        }

        // ---- the recorded upload -------------------------------------------------------------------------

        /// <summary>
        /// THE WHOLE UPLOAD, IN ORDER: the pass ends FIRST because a copy is illegal inside a rendering scope, and
        /// the copy is BRACKETED by the two barriers, because each direction needs its own and the one after the
        /// copy cannot order the reads that came before it
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/618">#618</see>).
        /// </summary>
        [Fact]
        public void ARecordedUpload_EndsThePassThenBracketsTheCopyInTwoBarriers()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);
            var log = new CmdLog();
            var copies = new FakeUploadSink(log);
            var rendering = new FakeRenderingScope();
            var sink = new RecordingCmdSink(log);

            var destination = new FakeVulkanUploadBuffer(0xDEAD, 256, GpuBufferUsage.VertexBuffer);
            var payload = new byte[] { 4, 5, 6, 7 };

            VulkanBufferUpload.Record(sink, copies, arena, rendering, destination, 32, payload);

            Assert.Equal(1, rendering.Ended);
            Assert.Single(copies.Copies);
            Assert.Equal(0xDEADul, copies.Copies[0].Destination);
            Assert.Equal(32ul, copies.Copies[0].DestinationOffset);
            Assert.Equal(4ul, copies.Copies[0].SizeBytes);

            Assert.Equal(new[] { "barrier", "copy", "barrier" }, log.Sequence);

            // The FIRST barrier is the write-after-read half: prior vertex fetches, then this transfer write. The
            // second is the read-after-write half. Reading them off the recorded DependencyInfo is what makes the
            // ordering an assertion rather than a comment.
            Assert.Equal(PipelineStageFlags2.VertexInputBit | PipelineStageFlags2.TransferBit,
                log.BufferBarriers[0].SrcStageMask);
            Assert.Equal(AccessFlags2.TransferWriteBit, log.BufferBarriers[0].DstAccessMask);
            Assert.Equal(32ul, log.BufferBarriers[0].Offset);
            Assert.Equal(4ul, log.BufferBarriers[0].Size);

            Assert.Equal(PipelineStageFlags2.TransferBit, log.BufferBarriers[1].SrcStageMask);
            Assert.Equal(AccessFlags2.VertexAttributeReadBit, log.BufferBarriers[1].DstAccessMask);

            // The bytes went into the lease the copy names.
            byte[] block = source.BytesOf(copies.Copies[0].Source);
            Assert.Equal(payload, block.AsSpan((int)copies.Copies[0].SourceOffset, 4).ToArray());
        }

        /// <summary>An empty upload records NOTHING at all, because a zero-byte copy is a command and a barrier
        /// bought for no bytes, and the pass split is the expensive half of both.</summary>
        [Fact]
        public void AnEmptyUpload_RecordsNothing()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);
            var log = new CmdLog();
            var copies = new FakeUploadSink(log);
            var rendering = new FakeRenderingScope();
            var sink = new RecordingCmdSink(log);

            VulkanBufferUpload.Record(sink, copies, arena, rendering,
                new FakeVulkanUploadBuffer(0xDEAD, 256, GpuBufferUsage.VertexBuffer), 0,
                ReadOnlySpan<byte>.Empty);

            Assert.Equal(0, rendering.Ended);
            Assert.Empty(copies.Copies);
            Assert.Equal(0, sink.Log.Barriers);
            Assert.Equal(0, arena.BlocksCreated);
        }

        /// <summary>A null rendering scope is legal and means there is no pass to end, which is the state on a
        /// list built with no rendering seam. Only a test constructs one: every list the device hands out gets a
        /// schedule and hands itself to its uploader as the scope.</summary>
        [Fact]
        public void ANullRenderingScope_IsLegal()
        {
            var source = new FakeStagingSource();
            using var arena = new VulkanStagingArena(source, framesInFlight: 3, blockBytes: 1024);
            var log = new CmdLog();
            var copies = new FakeUploadSink(log);
            var sink = new RecordingCmdSink(log);

            VulkanBufferUpload.Record(sink, copies, arena, rendering: null,
                new FakeVulkanUploadBuffer(0xDEAD, 256, GpuBufferUsage.IndexBuffer), 0, new byte[] { 1 });

            Assert.Single(copies.Copies);
        }

        // ---- fakes ---------------------------------------------------------------------------------------

        /// <summary>The two native calls a staging block is, over pinned arrays so a test can read back what a
        /// lease wrote. Pinning is what makes that possible: the arena writes through a raw pointer.</summary>
        sealed class FakeStagingSource : IVulkanStagingSource
        {
            readonly Dictionary<ulong, byte[]> _bytes = new();
            readonly Dictionary<ulong, GCHandle> _pins = new();
            readonly Dictionary<ulong, int> _destroyCounts = new();
            ulong _next = 1;

            internal List<VulkanStagingBlock> Live { get; } = new();

            internal byte[] BytesOf(ulong buffer) => _bytes[buffer];

            /// <summary>How many times <see cref="Destroy"/> was called for <paramref name="buffer"/>. What
            /// <see cref="Disposal_DestroysOpenAndFreeBlocksExactlyOnceEach"/> reads: a block reachable from both an
            /// open slot and the free list, or destroyed by two overlapping paths, would show a count over one.
            /// </summary>
            internal int DestroyCountOf(ulong buffer) => _destroyCounts.GetValueOrDefault(buffer);

            public VulkanStagingBlock Create(ulong sizeBytes)
            {
                ulong handle = _next++;
                var bytes = new byte[sizeBytes];
                GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);

                _bytes[handle] = bytes;
                _pins[handle] = pin;

                var block = new VulkanStagingBlock(handle, pin.AddrOfPinnedObject(), sizeBytes);
                Live.Add(block);
                return block;
            }

            public void Destroy(in VulkanStagingBlock block)
            {
                ulong handle = block.Buffer;
                Live.RemoveAll(live => live.Buffer == handle);
                _destroyCounts[handle] = _destroyCounts.GetValueOrDefault(handle) + 1;

                if (!_pins.Remove(handle, out GCHandle pin)) return;

                pin.Free();
                _bytes.Remove(handle);
            }
        }

        /// <summary>The copy side, recorded rather than made. It shares the command log with the barrier sink so
        /// the two streams interleave into ONE sequence, which is the only way the bracket around the copy is an
        /// assertion rather than two independent counts.</summary>
        sealed class FakeUploadSink : IVulkanUploadSink
        {
            readonly CmdLog _log;

            internal FakeUploadSink(CmdLog log) => _log = log;

            internal List<(ulong Source, ulong SourceOffset, ulong Destination, ulong DestinationOffset,
                ulong SizeBytes)> Copies { get; } = new();

            public void CopyBuffer(ulong source, ulong sourceOffsetBytes, ulong destination,
                ulong destinationOffsetBytes, ulong sizeBytes)
            {
                Copies.Add((source, sourceOffsetBytes, destination, destinationOffsetBytes, sizeBytes));
                _log.Sequence.Add("copy");
            }
        }

        /// <summary>The rendering scope, counting the pass ends the upload path owes.</summary>
        sealed class FakeRenderingScope : IVulkanRenderingScope
        {
            internal int Ended { get; private set; }

            public void EndActiveRendering() => Ended++;
        }

        /// <summary>The barrier count, behind the budget seam, because the arena's barriers are one of the three
        /// call classes that seam exists to watch even though the copy between them is not. It also carries the
        /// interleaved sequence and the barrier structs themselves, so the bracket's ORDER and its MASKS are both
        /// readable.</summary>
        sealed class CmdLog
        {
            internal int Barriers { get; set; }

            /// <summary>"barrier" and "copy" in the order they were recorded.</summary>
            internal List<string> Sequence { get; } = new();

            /// <summary>Every buffer memory barrier the sink saw, in order.</summary>
            internal List<BufferMemoryBarrier2> BufferBarriers { get; } = new();
        }

        readonly struct RecordingCmdSink : IVkCmdSink
        {
            internal RecordingCmdSink(CmdLog log) => Log = log;

            internal CmdLog Log { get; }

            public void BindDescriptorSets(PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet,
                ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets)
            {
            }

            public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
            {
            }

            public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset,
                uint firstInstance)
            {
            }

            public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            {
            }

            public unsafe void PipelineBarrier(in DependencyInfo dependency)
            {
                Log.Barriers++;
                Log.Sequence.Add("barrier");

                for (uint i = 0; i < dependency.BufferMemoryBarrierCount; i++)
                {
                    Log.BufferBarriers.Add(dependency.PBufferMemoryBarriers[i]);
                }
            }
        }
    }
}
