using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE-OWNED SETUP BATCH'S BOOKKEEPING (M-M9), driven with no Metal in the room: which uploads share a
    /// batch, when the staging budget commits one early, and what the batch holds while it does.
    ///
    /// <para><b>THIS IS THE HALF THE GPU TEST CANNOT REACH, and it used to be the half nothing reached.</b> Before
    /// <see cref="IMetalSetupNative"/> the type held an <c>MTLCommandQueue</c> and made its own Objective-C calls,
    /// so off a device every append returned early on a nil command buffer and none of this ran anywhere except
    /// on a Mac under <c>KE_GPU_TESTS</c>. <c>MetalResourceGpuTests</c> still owns the questions only a driver can
    /// answer: whether Metal accepts the eleven-argument copy, and whether the batch carrying it completes.</para>
    /// </summary>
    public sealed class MetalSetupBatchTests
    {
        readonly ITestOutputHelper _output;

        public MetalSetupBatchTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// M-M9's CLAIM, device-free: many uploads, one batch, one commit. The incumbent's equivalent was one whole
        /// queue submit per upload.
        /// </summary>
        [Fact]
        public void EightUploadsUnderTheBudget_ShareOneBatchAndOneCommit()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            for (uint i = 0; i < 8; i++) Upload(setup, 8, 8);

            Assert.Single(native.Batches);
            Assert.Empty(native.Committed);
            Assert.Equal(8, native.Encoded.Count);
            Assert.Equal(8, setup.AppendCount);
            Assert.True(setup.HasPendingWork);

            setup.Flush();

            Assert.Single(native.Committed);
            Assert.Equal(1, setup.FlushCount);
            Assert.False(setup.HasPendingWork);

            // And the batch's staging goes with the commit rather than living until teardown.
            Assert.Equal(0, native.LiveStagingBytes);
            Assert.Equal(0u, setup.StagedBytes);
        }

        /// <summary>
        /// THE BUDGET. An append that would carry the open batch past the cap commits it first, so the residency
        /// stays bounded across a run of uploads with no device-level read between them.
        /// </summary>
        [Fact]
        public void AnUploadThatWouldCrossTheBudget_CommitsTheOpenBatchFirst()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            // Four 16x16 RGBA8 uploads are 1024 bytes each, so they reach the budget EXACTLY and do not cross it.
            for (uint i = 0; i < 4; i++) Upload(setup, 16, 16);

            Assert.Single(native.Batches);
            Assert.Empty(native.Committed);
            Assert.Equal(Budget, setup.StagedBytes);

            // The fifth crosses.
            Upload(setup, 16, 16);

            Assert.Equal(2, native.Batches.Count);
            Assert.Single(native.Committed);
            Assert.Equal(native.Batches[0], native.Committed[0]);
            Assert.Equal(1024u, setup.StagedBytes);
            Assert.Equal(1, setup.StagingCount);

            // The uploads all landed: the split is where they were committed, not whether they were recorded.
            Assert.Equal(5, native.Encoded.Count);
            Assert.Equal(5, setup.AppendCount);
        }

        /// <summary>
        /// The residency claim itself, checked after every single append rather than at the end, because a budget
        /// that only holds at the boundaries is a budget a burst walks straight through.
        /// </summary>
        [Fact]
        public void TheOpenBatch_NeverHoldsMoreThanTheBudget()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            for (uint i = 0; i < 32; i++)
            {
                Upload(setup, 16, 16);

                Assert.True(setup.StagedBytes <= Budget, "the open batch holds " + setup.StagedBytes + " bytes");
                Assert.True(native.LiveStagingBytes <= (long)Budget,
                    "the staging buffers alive at once total " + native.LiveStagingBytes + " bytes");
            }

            _output.WriteLine($"32 uploads in {native.Committed.Count} committed batches, "
                + $"peak residency {Budget} bytes");
        }

        /// <summary>
        /// A single upload LARGER than the budget still goes through, in a batch of its own. The cap is a ceiling
        /// on what a batch accumulates and not a limit on what the seam can describe, and refusing here would
        /// refuse a texture that is perfectly legal.
        /// </summary>
        [Fact]
        public void AnUploadLargerThanTheBudget_GetsABatchOfItsOwn()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            Upload(setup, 16, 16);
            Upload(setup, 64, 64);

            Assert.Equal(2, native.Batches.Count);
            Assert.Single(native.Committed);
            Assert.Equal(16384u, setup.StagedBytes);
            Assert.Equal(1, setup.StagingCount);
        }

        /// <summary>
        /// THE DEFAULT CAP AGAINST THE CALL SITE IT WAS CHOSEN FOR. <c>Scene3D.LoadSplatMaterial</c> uploads five
        /// albedo and five normal layers at mip 0 with nothing between them, so it is the engine's largest burst
        /// with no drain in it. A 1024-square layer set is 40 MB and must stay ONE batch, which is what M-M9
        /// claims. A 2048-square set is 160 MB, which is the residency the cap exists to refuse, and it splits.
        /// </summary>
        [Fact]
        public void TheDefaultBudget_HoldsAOneKSplatSet_AndSplitsATwoKOne()
        {
            var payload = new byte[2048 * 2048 * 4];

            var oneK = new FakeMetalSetupNative();
            using (var setup = new MetalSetupCommands(oneK, new FakeMetalDeviceLiveness()))
            {
                for (int i = 0; i < 10; i++) Upload(setup, 1024, 1024, payload);

                Assert.Single(oneK.Batches);
                Assert.Empty(oneK.Committed);
                Assert.Equal(40UL * 1024 * 1024, setup.StagedBytes);
            }

            var twoK = new FakeMetalSetupNative();
            using (var setup = new MetalSetupCommands(twoK, new FakeMetalDeviceLiveness()))
            {
                for (int i = 0; i < 10; i++) Upload(setup, 2048, 2048, payload);

                // Four 16 MB uploads reach the 64 MB cap exactly, so the split is after every fourth.
                Assert.Equal(3, twoK.Batches.Count);
                Assert.Equal(2, twoK.Committed.Count);
                Assert.True(setup.StagedBytes <= MetalSetupCommands.DefaultStagingCapBytes);
            }
        }

        /// <summary>
        /// AFTER DISPOSAL AN UPLOAD REFUSES AND OPENS NOTHING, which is the observable half of the check that
        /// moved inside the gate. The race it closes is not reachable from a test: it needs a teardown to
        /// complete between an unsynchronised read of the disposal flag and the lock acquisition that follows it,
        /// and both now happen under one acquisition. What is checkable is that no batch is opened or retained
        /// once the type is disposed, which is the state that race produced.
        /// </summary>
        [Fact]
        public void AnUploadAfterDisposal_RefusesAndOpensNoBatch()
        {
            var native = new FakeMetalSetupNative();
            var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            setup.Dispose();

            Assert.Throws<ObjectDisposedException>(() => Upload(setup, 8, 8));
            Assert.Empty(native.Batches);
            Assert.Empty(native.Staged);

            // A flush after disposal stays a straggler rather than a defect, and commits nothing.
            setup.Flush();
            Assert.Empty(native.Committed);

            // And disposal is idempotent, which matters because the device's own Dispose can be reached twice.
            setup.Dispose();
            Assert.Empty(native.ReleasedBatches);
        }

        /// <summary>A queue that will not make a command buffer is a device already in trouble, and an append
        /// then records nothing rather than encoding into a nil handle.</summary>
        [Fact]
        public void AnAppendAgainstAQueueThatAnswersNil_RecordsNothing()
        {
            var native = new FakeMetalSetupNative { BeginAnswersNil = true };
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            Upload(setup, 8, 8);

            Assert.Empty(native.Encoded);
            Assert.Empty(native.Staged);
            Assert.Equal(0, setup.AppendCount);
            Assert.False(setup.HasPendingWork);
        }

        /// <summary>
        /// THE PRIVATE PATH'S REGION REFUSAL, end to end. The payload is exactly the right length for the region,
        /// so the length check has nothing to say, and the region still lands past the destination's edge. Nothing
        /// is staged and nothing is encoded, which is what says the refusal happens before the allocation rather
        /// than after it.
        /// </summary>
        [Fact]
        public void AnUploadPastTheDestinationsEdge_IsRefusedBeforeAnythingIsStaged()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            var shape = new MetalStagingShape(16, 16, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);

            // In range first, so the refusals below are about the region rather than about the shape.
            setup.Upload(default, shape, new MetalTextureUpload(0, 0, 8, 8, 8, 8), new byte[8 * 8 * 4]);

            Assert.Throws<ArgumentOutOfRangeException>(() => setup.Upload(default, shape,
                new MetalTextureUpload(0, 0, 9, 8, 8, 8), new byte[8 * 8 * 4]));
            Assert.Throws<ArgumentOutOfRangeException>(() => setup.Upload(default, shape,
                new MetalTextureUpload(0, 0, 8, 9, 8, 8), new byte[8 * 8 * 4]));

            Assert.Single(native.Staged);
            Assert.Single(native.Encoded);
            Assert.Equal(1, setup.AppendCount);
        }

        /// <summary>
        /// A FLUSH AFTER THE DEVICE DIES RELEASES NOTHING, which is the posture every wrapper in the package
        /// takes and which the flush path used to contradict: it committed nothing (correctly) and then released
        /// the command buffer and every staging buffer anyway, which is a call into objects the driver has
        /// already given up on. The managed references go, so nothing reaches them later either.
        /// </summary>
        [Fact]
        public void AFlushAfterTheDeviceDies_ReleasesNothing()
        {
            var native = new FakeMetalSetupNative();
            var liveness = new FakeMetalDeviceLiveness();
            using var setup = new MetalSetupCommands(native, liveness, Budget);

            Upload(setup, 8, 8);
            Upload(setup, 8, 8);
            Assert.Equal(2, native.Staged.Count);

            liveness.MarkDead();
            setup.Flush();

            Assert.Empty(native.Committed);
            Assert.Empty(native.ReleasedBatches);
            Assert.Empty(native.ReleasedStaging);
            Assert.Equal(0, setup.FlushCount);
            Assert.False(setup.HasPendingWork);
            Assert.Equal(0u, setup.StagedBytes);
            Assert.Equal(0, setup.StagingCount);
        }

        /// <summary>Disposal takes the same posture, which is where it was already documented. The two paths say
        /// one thing now rather than two.</summary>
        [Fact]
        public void DisposalAfterTheDeviceDies_ReleasesNothing()
        {
            var native = new FakeMetalSetupNative();
            var liveness = new FakeMetalDeviceLiveness();
            var setup = new MetalSetupCommands(native, liveness, Budget);

            Upload(setup, 8, 8);
            setup.Flush();
            Upload(setup, 8, 8);

            int releasedBatches = native.ReleasedBatches.Count;
            int releasedStaging = native.ReleasedStaging.Count;

            liveness.MarkDead();
            setup.Dispose();

            Assert.Equal(releasedBatches, native.ReleasedBatches.Count);
            Assert.Equal(releasedStaging, native.ReleasedStaging.Count);

            // And a dead device answers no outcome, rather than messaging a buffer the driver may have torn down.
            Assert.Null(setup.LastCommittedFault());
        }

        /// <summary>
        /// ON A LIVE DEVICE EVERY HANDLE IS RELEASED EXACTLY ONCE, across a run that commits twice and then tears
        /// down with a batch still open. An over-release of an Objective-C object is a use-after-free somewhere
        /// else entirely, so the counts are what pin the ownership rather than the absence of a crash.
        /// </summary>
        [Fact]
        public void OnALiveDevice_EveryHandleIsReleasedExactlyOnce()
        {
            var native = new FakeMetalSetupNative();
            var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness(), Budget);

            Upload(setup, 8, 8);
            setup.Flush();
            Upload(setup, 8, 8);
            setup.Flush();
            Upload(setup, 8, 8);

            setup.Dispose();

            Assert.Equal(3, native.Batches.Count);
            Assert.Equal(2, native.Committed.Count);

            // Once each, and every one of them. The ORDER is deliberately not asserted: a committed batch is
            // released when its successor commits, so the last teardown releases the open one before the
            // committed one it is still holding.
            Assert.Equal(3, native.ReleasedBatches.Count);
            Assert.Equal(3, native.ReleasedBatches.Distinct().Count());
            Assert.All(native.Batches, batch => Assert.Contains(batch, native.ReleasedBatches));

            Assert.Equal(3, native.ReleasedStaging.Count);
            Assert.Equal(3, native.ReleasedStaging.Distinct().Count());
        }

        // 4096 bytes, which is four 16x16 RGBA8 uploads. Small enough to drive the budget with no allocation
        // worth naming, and the arithmetic stays legible in the assertions.
        const ulong Budget = 4096;

        static void Upload(MetalSetupCommands setup, uint width, uint height, byte[]? payload = null)
        {
            var shape = new MetalStagingShape(width, height, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            var upload = new MetalTextureUpload(0, 0, 0, 0, width, height);

            setup.Upload(default, shape, upload, payload ?? new byte[width * height * 4]);
        }
    }
}
