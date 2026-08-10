using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE ROW 6 AND ROW 7 JOIN AT THE SUBMIT, device-free: a submit COMMITS THE PENDING SETUP BATCH BEFORE it
    /// hands the recording's command buffer over to be committed (M-M9).
    ///
    /// <para><b>THE FAILURE THIS PINS IS A WRONG PIXEL RATHER THAN A CRASH.</b> A Metal queue runs its buffers in
    /// enqueue order and <c>-commit</c> enqueues, so a frame that samples a texture uploaded through
    /// <c>IGpuDevice.UpdateTexture</c> sees the uploaded bytes only if the batch carrying that upload was
    /// committed first. Flush it after the commit and the blit lands behind the frame that reads it, which
    /// renders stale content with nothing failing anywhere.</para>
    ///
    /// <para><b>IT RUNS EVERYWHERE BECAUSE THE ORDER IS A STATIC.</b>
    /// <see cref="MetalGpuDevice.PrepareForCommit"/> is the whole pre-lock phase and takes liveness as a plain
    /// bool, so a fake command-buffer source and a fake setup batch are all this needs. The commit itself lives
    /// in the device's macOS half and cannot run until that member has returned, which is what makes "before" an
    /// assertion here rather than a comment: the buffer to commit is this member's RETURN VALUE, so nothing can
    /// have committed it while the batch was still open. The same split
    /// <see cref="MetalCompletionHandler.Deliver"/> and <c>MetalGpuDevice.RequireList</c> already take.</para>
    ///
    /// <para><b>WHAT THIS CANNOT REACH</b> is that Metal honours the enqueue order it is relying on, which is a
    /// driver property, and that the whole path is wired through <c>IGpuDevice.Submit</c>.
    /// <c>MetalCommandListGpuTests</c> owns both, under a <c>[GpuFact]</c> against a real device.</para>
    /// </summary>
    public sealed class MetalSubmitSetupOrderTests
    {
        /// <summary>
        /// THE ORDER. An upload is pending, the list is sealed, and the pre-lock phase returns the buffer to
        /// commit with the batch already committed behind it.
        /// </summary>
        [Fact]
        public void ASubmitCommitsThePendingSetupBatchBeforeItYieldsTheBufferToCommit()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness());
            (MetalCommandList list, FakeMetalCommandBufferSource buffers) = NewList();

            Upload(setup);
            Assert.True(setup.HasPendingWork);
            Assert.Empty(native.Committed);

            list.Begin();
            list.End();

            IntPtr buffer = MetalGpuDevice.PrepareForCommit(list, setup, alive: true);

            // The batch is committed and the recording's buffer is only now being handed back for its own commit,
            // which is the ordering claim stated as the two things that are true at this instant.
            Assert.Single(native.Committed);
            Assert.Equal(1, setup.FlushCount);
            Assert.False(setup.HasPendingWork);

            Assert.Equal(buffers.Acquired[0], buffer);
            Assert.NotEqual(IntPtr.Zero, buffer);
        }

        /// <summary>
        /// A SUBMIT WITH NOTHING PENDING COMMITS NOTHING, which is every frame after a load. The flush is a lock
        /// acquisition and a flag read on that path and must stay one.
        /// </summary>
        [Fact]
        public void ASubmitWithNoPendingUploadCommitsNoBatch()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness());
            (MetalCommandList list, _) = NewList();

            list.Begin();
            list.End();

            Assert.NotEqual(IntPtr.Zero, MetalGpuDevice.PrepareForCommit(list, setup, alive: true));

            Assert.Empty(native.Batches);
            Assert.Empty(native.Committed);
            Assert.Equal(0, setup.FlushCount);
        }

        /// <summary>
        /// A SUBMIT ON A DEAD DEVICE CONSUMES THE SEAL, RELEASES THE BUFFER AND FLUSHES NOTHING. Committing to a
        /// queue whose device has gone is the call the whole dead-device posture exists to avoid, and the batch
        /// stays open because its own teardown is what releases it.
        /// </summary>
        [Fact]
        public void ASubmitOnADeadDeviceDiscardsTheRecordingAndCommitsNothing()
        {
            var native = new FakeMetalSetupNative();
            var liveness = new FakeMetalDeviceLiveness();
            using var setup = new MetalSetupCommands(native, liveness);
            (MetalCommandList list, FakeMetalCommandBufferSource buffers) = NewList();

            Upload(setup);
            list.Begin();
            list.End();

            Assert.Equal(IntPtr.Zero, MetalGpuDevice.PrepareForCommit(list, setup, alive: false));

            Assert.Empty(native.Committed);
            Assert.False(list.IsSealed);
            Assert.Equal(0, buffers.Outstanding);
        }

        /// <summary>A list with no sealed recording is refused by name BEFORE anything is flushed, so a
        /// sequencing error at the call site cannot commit a batch as a side effect.</summary>
        [Fact]
        public void AnUnsealedListIsRefusedBeforeTheBatchIsTouched()
        {
            var native = new FakeMetalSetupNative();
            using var setup = new MetalSetupCommands(native, new FakeMetalDeviceLiveness());
            (MetalCommandList list, _) = NewList();

            Upload(setup);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => MetalGpuDevice.PrepareForCommit(list, setup, alive: true));

            Assert.Contains("without a sealed recording", thrown.Message, StringComparison.Ordinal);
            Assert.Empty(native.Committed);
            Assert.True(setup.HasPendingWork);
        }

        static (MetalCommandList List, FakeMetalCommandBufferSource Buffers) NewList()
        {
            FakeMetalCommandBufferSource buffers = new();
            MetalCommandList list = new(buffers,
                new MetalUncommittedBuffers(MetalFramesInFlight.Default, new RecordingLogger()),
                new FakeMetalEncoderSink(new FakeMetalEncoderCalls()),
                new object());

            return (list, buffers);
        }

        static void Upload(MetalSetupCommands setup)
        {
            var shape = new MetalStagingShape(8, 8, 1, 1, GpuPixelFormat.R8G8B8A8UNorm);
            var upload = new MetalTextureUpload(0, 0, 0, 0, 8, 8);

            setup.Upload(default, shape, upload, new byte[8 * 8 * 4]);
        }
    }
}
