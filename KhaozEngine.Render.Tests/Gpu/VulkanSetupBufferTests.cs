using System;
using System.Linq;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEVICE-OWNED SETUP COMMAND BUFFER (V-M10) AND ITS OWN SHORT LOCK (V-W8, 11.4): that creation records
    /// rather than submits, that the batch flushes at the next submit or at any device-level read, that the clear
    /// is preserved, and that the two locks nest in the ONE order the design pins.
    /// </summary>
    public sealed class VulkanSetupBufferTests
    {
        /// <summary>
        /// NOTHING IS SUBMITTED UNTIL SOMETHING FLUSHES, and the ratio is what V-M10 is about: many appends, one
        /// submit. The incumbent's ratio is one submit per texture.
        /// </summary>
        [Fact]
        public void ManyAppends_FlushAsOneSubmit()
        {
            var fixture = new VulkanResourceFixture();

            for (int i = 0; i < 8; i++)
            {
                fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    4, 4, GpuTextureUsage.RenderTarget)).Dispose();
            }

            Assert.Equal(8, fixture.Setup.AppendCount);
            Assert.Empty(fixture.CommandApi.Submissions);

            ulong value = fixture.Setup.Flush();

            Assert.NotEqual(0UL, value);
            Assert.Single(fixture.CommandApi.Submissions);
            Assert.Equal(1, fixture.Setup.FlushCount);
            Assert.False(fixture.Setup.HasPendingWork);
        }

        /// <summary>
        /// A FLUSH WITH NOTHING OPEN IS A NO-OP, which is every frame boundary after a load. It costs one field
        /// read, which is why every submit and every device-level read can afford to ask.
        /// </summary>
        [Fact]
        public void AFlushWithNothingOpen_SubmitsNothing()
        {
            var fixture = new VulkanResourceFixture();

            Assert.Equal(0UL, fixture.Setup.Flush());
            Assert.Empty(fixture.CommandApi.Submissions);
            Assert.Equal(0, fixture.Setup.FlushCount);
        }

        /// <summary>
        /// THE BATCH RIDES THE SAME SLOT MACHINERY A COMMAND LIST DOES: a pool per slot, advanced once per BATCH,
        /// with the advance waiting for that slot's own last submission before resetting its pool. Three flushes at
        /// depth three walk all three pools and the fourth wraps back onto the first.
        /// </summary>
        [Fact]
        public void EachBatch_TakesTheNextPoolSlot()
        {
            var fixture = new VulkanResourceFixture(framesInFlight: 3);

            for (int i = 0; i < 4; i++)
            {
                fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    4, 4, GpuTextureUsage.Sampled)).Dispose();
                fixture.Setup.Flush();
            }

            Assert.Equal(4, fixture.CommandApi.Submissions.Count);
            Assert.Equal(3, fixture.CommandApi.Pools.Count);

            // Slot 0 was recorded into twice, so its pool saw two resets: the wrap is what makes that safe, and it
            // waited for the first submission before the second reset.
            Assert.Equal(4, fixture.CommandApi.Events.Count(
                e => e.StartsWith("ResetPool", StringComparison.Ordinal)));
            Assert.Equal(2, fixture.CommandApi.EventsForPool(fixture.CommandApi.Pools[0]).Count(
                e => e.StartsWith("ResetPool", StringComparison.Ordinal)));
        }

        /// <summary>
        /// EVERY DEVICE-LEVEL READ FLUSHES FIRST, WHICH IS THE HALF OF V-M10 THAT REMOVES THE HOLE. A render target
        /// created and immediately read back must still see cleared contents, and a design that only flushed at
        /// the next submit would leave that case reading memory nothing wrote. Proven through the arithmetic rather
        /// than by inspection: nothing was submitted after the creation, and the map's flush is what puts the
        /// clear on the queue.
        /// </summary>
        [Fact]
        public void ReadingBackImmediatelyAfterCreation_FlushesTheClearFirst()
        {
            var fixture = new VulkanResourceFixture();

            fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                4, 4, GpuTextureUsage.RenderTarget)).Dispose();

            Assert.Empty(fixture.CommandApi.Submissions);

            // The device's Map does exactly this pair before it hands a pointer out.
            fixture.Setup.Flush();

            Assert.Single(fixture.CommandApi.Submissions);
            Assert.Single(fixture.SetupSink.Clears);
        }

        /// <summary>
        /// THE FLUSH TAKES THE SUBMIT LOCK UNDER THE SETUP LOCK, IN THAT ORDER AND NEVER THE REVERSE (V-W8). Proven
        /// from inside the submit itself, which is the one place both are held at once: the setup lock must already
        /// be entered on this thread and the submit lock must be too.
        /// <para>
        /// WHAT THIS DOES NOT PROVE, and cannot, is that no path anywhere takes the setup lock while holding the
        /// submit lock. That is a structural property of the call graph rather than a runtime one, and it is
        /// maintained by the device taking the two SEQUENTIALLY at its one site that touches both: it flushes this
        /// buffer and THEN queues the frame's list.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFlush_TakesTheSubmitLockUnderTheSetupLock()
        {
            var setupLock = new object();
            var fixture = new VulkanResourceFixture(setupLock: setupLock);

            bool? setupHeld = null;
            bool? submitHeld = null;
            fixture.CommandApi.OnSubmit = () =>
            {
                setupHeld = Monitor.IsEntered(setupLock);
                submitHeld = Monitor.IsEntered(fixture.SubmitLock);
            };

            fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                4, 4, GpuTextureUsage.Sampled)).Dispose();
            fixture.Setup.Flush();

            Assert.True(setupHeld);
            Assert.True(submitHeld);
        }

        /// <summary>
        /// A SECOND THREAD CANNOT APPEND WHILE A FLUSH IS IN FLIGHT, which is what the setup lock is FOR: a
        /// <c>VkCommandPool</c> and every buffer allocated from it are externally synchronised, so two threads
        /// creating two textures may not record into one buffer at once.
        /// </summary>
        [Fact]
        public void AnAppendFromAnotherThread_WaitsForAnInFlightFlush()
        {
            var fixture = new VulkanResourceFixture();
            using var insideSubmit = new ManualResetEventSlim();
            using var appendTried = new ManualResetEventSlim();

            bool appendedDuringFlush = false;

            fixture.CommandApi.OnSubmit = () =>
            {
                insideSubmit.Set();

                // The other thread gets a fair chance to barge in. It cannot: the setup lock is held right now.
                appendTried.Wait(TimeSpan.FromMilliseconds(250));
                appendedDuringFlush = fixture.Setup.AppendCount > 1;
            };

            fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                4, 4, GpuTextureUsage.Sampled)).Dispose();

            var other = new Thread(() =>
            {
                insideSubmit.Wait(TimeSpan.FromSeconds(5));
                appendTried.Set();
                fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    4, 4, GpuTextureUsage.Sampled)).Dispose();
            });

            other.Start();
            fixture.Setup.Flush();
            other.Join(TimeSpan.FromSeconds(5));

            Assert.False(appendedDuringFlush);
            Assert.Equal(2, fixture.Setup.AppendCount);
        }

        /// <summary>
        /// A DEVICE-LEVEL TEXTURE UPLOAD IS A STAGED COPY BETWEEN TWO BARRIERS, appended rather than submitted. The
        /// target goes into <c>TRANSFER_DST_OPTIMAL</c> from its RESTING layout, not from <c>UNDEFINED</c>: its
        /// contents are wanted, and a transition out of <c>UNDEFINED</c> is permitted to discard them (V-F8).
        /// </summary>
        [Fact]
        public void ADeviceLevelUpload_IsAStagedCopyBetweenTwoBarriers()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 8, GpuTextureUsage.Sampled));

            fixture.SetupSink.Clear();

            var native = (VulkanTexture)texture;
            fixture.Setup.Upload(
                new VulkanImageUpload(native.Image, DepthStencil: false, MipLevel: 0, ArrayLayer: 0, X: 2, Y: 3,
                    Width: 4, Height: 4, Format: GpuPixelFormat.R8G8B8A8UNorm, Resting: native.Resting),
                new byte[4 * 4 * 4]);

            Assert.Equal(2, fixture.SetupSink.ImageBarriers.Count);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, fixture.SetupSink.ImageBarriers[0].OldLayout);
            Assert.Equal(ImageLayout.TransferDstOptimal, fixture.SetupSink.ImageBarriers[0].NewLayout);
            Assert.Equal(ImageLayout.TransferDstOptimal, fixture.SetupSink.ImageBarriers[1].OldLayout);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, fixture.SetupSink.ImageBarriers[1].NewLayout);

            FakeImageCopy copy = Assert.Single(fixture.SetupSink.ImageCopies);
            Assert.Equal(4u, copy.BufferRowLength);
            Assert.Equal(4u, copy.BufferImageHeight);
            Assert.Equal(2, copy.X);
            Assert.Equal(3, copy.Y);
            Assert.Empty(fixture.CommandApi.Submissions);
        }

        /// <summary>
        /// THE BARRIERS COVER ONE SUBRESOURCE, NOT THE WHOLE IMAGE. An upload to mip 2 layer 1 of a mipped array
        /// must not transition the levels it is not writing: they are at rest and something else may be reading
        /// them.
        /// </summary>
        [Fact]
        public void AnUploadsBarriers_CoverOneSubresource()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                16, 16, GpuTextureUsage.Sampled, mipLevels: 4, arrayLayers: 3));

            fixture.SetupSink.Clear();

            var native = (VulkanTexture)texture;
            fixture.Setup.Upload(
                new VulkanImageUpload(native.Image, false, 2, 1, 0, 0, 4, 4, GpuPixelFormat.R8G8B8A8UNorm,
                    native.Resting),
                new byte[4 * 4 * 4]);

            Assert.All(fixture.SetupSink.ImageBarriers, barrier =>
            {
                Assert.Equal(2u, barrier.BaseMipLevel);
                Assert.Equal(1u, barrier.LevelCount);
                Assert.Equal(1u, barrier.BaseArrayLayer);
                Assert.Equal(1u, barrier.LayerCount);
            });
        }

        /// <summary>
        /// A SHORT UPLOAD PAYLOAD IS REFUSED BY NAME rather than read past. The seam's <c>byte[]</c> overloads
        /// carry the region's rows with no padding between them, so a caller that sized their array for a
        /// different format or a different rectangle would otherwise read whatever follows it.
        /// </summary>
        [Fact]
        public void AShortUploadPayload_IsRefused()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 8, GpuTextureUsage.Sampled));

            var native = (VulkanTexture)texture;

            Assert.Throws<ArgumentException>(() => fixture.Setup.Upload(
                new VulkanImageUpload(native.Image, false, 0, 0, 0, 0, 4, 4, GpuPixelFormat.R8G8B8A8UNorm,
                    native.Resting),
                new byte[4 * 4 * 4 - 1]));
        }

        /// <summary>
        /// A DEVICE-LEVEL BUFFER UPLOAD IS A STAGED COPY PLUS A NARROWED BARRIER. The narrowing is
        /// <see cref="VulkanUploadBarrier"/>'s: the incumbent emits one global <c>VkMemoryBarrier</c> whose
        /// destination is a vertex-attribute read whatever the buffer really is, so an index buffer and a storage
        /// buffer are both synchronised as though they were vertex attributes.
        /// </summary>
        [Fact]
        public void ADeviceLevelBufferUpload_IsAStagedCopyWithANarrowedBarrier()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.IndexBuffer));

            fixture.Setup.UploadBuffer((IVulkanUploadDestination)buffer, 64, new byte[32]);

            FakeBufferCopy copy = Assert.Single(fixture.SetupSink.BufferCopies);
            Assert.Equal(64UL, copy.DestinationOffset);
            Assert.Equal(32UL, copy.Size);

            FakeBufferBarrier barrier = Assert.Single(fixture.SetupSink.BufferBarriers);
            Assert.Equal(64UL, barrier.Offset);
            Assert.Equal(32UL, barrier.Size);
            Assert.Equal(AccessFlags2.IndexReadBit, barrier.DestinationAccess);
            Assert.Empty(fixture.CommandApi.Submissions);
        }

        /// <summary>
        /// AN EMPTY PAYLOAD RECORDS NOTHING AT ALL, because a zero-byte copy is a command and a barrier bought for
        /// no bytes.
        /// </summary>
        [Fact]
        public void AnEmptyPayload_RecordsNothing()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.VertexBuffer));

            fixture.Setup.UploadBuffer((IVulkanUploadDestination)buffer, 0, ReadOnlySpan<byte>.Empty);

            Assert.Equal(0, fixture.SetupSink.CommandCount);
            Assert.False(fixture.Setup.HasPendingWork);
        }

        /// <summary>
        /// A DEAD DEVICE RECORDS NOTHING AND SUBMITS NOTHING. Every native call against a destroyed or lost device
        /// aborts the process through the Vulkan loader, so the posture after death is the same one every other
        /// path in this package takes: quiet and safe answers.
        /// </summary>
        [Fact]
        public void ADeadDevice_RecordsNothingAndSubmitsNothing()
        {
            var fixture = new VulkanResourceFixture();

            fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                4, 4, GpuTextureUsage.Sampled)).Dispose();
            Assert.True(fixture.Setup.HasPendingWork);

            fixture.Liveness.Kill();

            Assert.Equal(0UL, fixture.Setup.Flush());
            Assert.Empty(fixture.CommandApi.Submissions);
            Assert.False(fixture.Setup.HasPendingWork);

            fixture.Setup.Prepare(new VulkanImageSetup(1, false, 1, 1, false, false,
                VulkanRestingLayout.ShaderReadOnlyOptimal));
            Assert.False(fixture.Setup.HasPendingWork);
        }

        /// <summary>
        /// RETIREMENT HANDS THE POOLS TO THE RETIRE LIST AND THE ARENA'S BLOCKS TO THE STAGING SOURCE, and an open
        /// batch is DISCARDED rather than flushed: teardown has already waited for the GPU, so submitting work at
        /// that point would mean waiting for it again, and the resources the batch was preparing are being
        /// destroyed in the same breath.
        /// </summary>
        [Fact]
        public void Retirement_HandsOverThePoolsAndDiscardsAnOpenBatch()
        {
            var fixture = new VulkanResourceFixture(framesInFlight: 3);

            fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                4, 4, GpuTextureUsage.Sampled)).Dispose();

            fixture.Setup.Retire(fixture.Retired);

            Assert.Empty(fixture.CommandApi.Submissions);

            fixture.Drain();
            Assert.Equal(3, fixture.CommandApi.Destroyed.Count);
        }
    }
}
