using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE WIRING INSIDE THE NATIVE VULKAN DEVICE, driven through the device's own public seam members for the
    /// first time (https://github.com/APKiwiOrg/KhaozEngine/issues/550). Two claims, six call sites:
    ///
    /// <para><b>V-M10: EVERY DEVICE-LEVEL READ FLUSHES THE SETUP COMMAND BUFFER FIRST.</b> The device records a
    /// texture's creation-time clear and its first-ever layout transition into one device-owned command buffer and
    /// submits nothing, so a render target created and immediately read back would see memory nothing wrote unless
    /// the read flushes first. The flush is one call at each of six sites, and until this file existed all six were
    /// carried by inspection: <c>VulkanSetupBufferTests</c> calls <c>Setup.Flush()</c> itself, which is the
    /// subsystem rather than the device path.</para>
    ///
    /// <para><b>V-C8: <c>Map(staging, Read)</c> THEN WAITS ON THE TIMELINE, COUNTED AS A DRAIN.</b> Direct3D 11's
    /// <c>Map(READ)</c> blocks by definition, so this is where Vulkan has to be explicit about what the other API
    /// did implicitly. Getting it wrong hands back a pointer to bytes the copy has not written, which reads as an
    /// intermittently wrong golden rather than as a failure. A WRITE map deliberately does not wait, and that half
    /// is asserted too, because a drain on every map would be indistinguishable from a correct one on a green
    /// suite and would serialise every write behind the queue.</para>
    ///
    /// <para><b>THE ORDER IS READ OFF THE TIMELINE VALUES, not off a call log.</b> Every submission takes the
    /// timeline's next value inside the lock that orders <c>vkQueueSubmit</c>, so a lower value IS an earlier
    /// submission. The setup batch carrying value 1 and the frame's list carrying value 2 is the flush-first rule
    /// stated in the only currency the queue has.</para>
    /// </summary>
    public sealed class VulkanDeviceWiringTests
    {
        // ---- V-M10, the two submit overloads -------------------------------------------------------------

        /// <summary>
        /// <c>Submit</c> puts the setup batch on the queue BEFORE the frame's list, so the creations and
        /// transitions made since the last flush execute before the frame that reads them.
        /// </summary>
        [Fact]
        public void SubmittingAList_FlushesTheSetupBatchAheadOfIt()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture target = fixture.OpenSetupWork();
            using VulkanCommandList list = fixture.RecordedList();
            ulong sealedBuffer = list.SealedBuffer;

            Assert.Empty(fixture.CommandApi.Submissions);

            fixture.Device.Submit(list);

            Assert.Equal(2, fixture.CommandApi.Submissions.Count);
            Assert.Equal(sealedBuffer, fixture.CommandApi.Submissions[1].Buffer);
            Assert.NotEqual(sealedBuffer, fixture.CommandApi.Submissions[0].Buffer);
            Assert.True(fixture.CommandApi.Submissions[0].Value < fixture.CommandApi.Submissions[1].Value);
            Assert.Single(fixture.SetupSink.Clears);
        }

        /// <summary>The fenced overload does the same thing in the same order, which is worth its own row: the two
        /// overloads are separate call sites and a flush lost from one of them would leave the other green.
        /// </summary>
        [Fact]
        public void SubmittingAListWithAFence_FlushesTheSetupBatchAheadOfIt()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture target = fixture.OpenSetupWork();
            using VulkanCommandList list = fixture.RecordedList();
            ulong sealedBuffer = list.SealedBuffer;
            IGpuFence fence = fixture.Device.Factory.CreateFence();

            fixture.Device.Submit(list, fence);

            Assert.Equal(2, fixture.CommandApi.Submissions.Count);
            Assert.Equal(sealedBuffer, fixture.CommandApi.Submissions[1].Buffer);
            Assert.True(fixture.CommandApi.Submissions[0].Value < fixture.CommandApi.Submissions[1].Value);
        }

        // ---- V-M10, the explicit drain -------------------------------------------------------------------

        /// <summary>
        /// <c>WaitForIdle</c> is a device-level read, so it flushes and then waits for what it just queued. The
        /// order is the whole claim: waiting first would return having drained a queue the clear was never on.
        /// </summary>
        [Fact]
        public void WaitingForIdle_FlushesTheSetupBatchAndThenWaitsForIt()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture target = fixture.OpenSetupWork();

            fixture.Device.WaitForIdle();

            Assert.Single(fixture.CommandApi.Submissions);
            Assert.Equal(1, fixture.Semaphore.WaitCount);
            Assert.Equal(fixture.CommandApi.Submissions[0].Value, fixture.Semaphore.LastWaitValue);
            Assert.Single(fixture.SetupSink.Clears);
        }

        /// <summary>A read with nothing open submits nothing, which is every frame boundary after a load. The
        /// flush costs one field read, which is what lets all six sites afford to ask.</summary>
        [Fact]
        public void WaitingForIdleWithNothingOpen_SubmitsNothingAndWaitsForNothing()
        {
            using var fixture = new VulkanDeviceFixture();

            fixture.Device.WaitForIdle();

            Assert.Empty(fixture.CommandApi.Submissions);
            Assert.Equal(0, fixture.Semaphore.WaitCount);
            Assert.Equal(0, fixture.Device.Counters.DrainCount);
        }

        // ---- V-M10 and V-C8, the two map overloads -------------------------------------------------------

        /// <summary>
        /// Mapping a staging TEXTURE for read flushes the setup batch and then waits on the value that batch was
        /// submitted at. Both halves in one row, because the drain is only correct BECAUSE the flush preceded it:
        /// a wait taken first would cover a queue the clear had not reached.
        /// </summary>
        [Fact]
        public void MappingAStagingTextureForRead_FlushesAndThenDrains()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture staging = fixture.StagingTexture();
            using IGpuTexture target = fixture.OpenSetupWork();

            Assert.Empty(fixture.CommandApi.Submissions);

            fixture.Device.Map(staging, GpuMapMode.Read);
            fixture.Device.Unmap(staging);

            Assert.Single(fixture.CommandApi.Submissions);
            Assert.Equal(1, fixture.Semaphore.WaitCount);
            Assert.Equal(fixture.CommandApi.Submissions[0].Value, fixture.Semaphore.LastWaitValue);
            Assert.Equal(1, fixture.Device.Counters.DrainCount);
        }

        /// <summary>The buffer overload is a separate call site and gets its own row, for the reason the fenced
        /// submit does.</summary>
        [Fact]
        public void MappingAStagingBufferForRead_FlushesAndThenDrains()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuBuffer staging = fixture.StagingBuffer();
            using IGpuTexture target = fixture.OpenSetupWork();

            fixture.Device.Map(staging, GpuMapMode.Read);
            fixture.Device.Unmap(staging);

            Assert.Single(fixture.CommandApi.Submissions);
            Assert.Equal(1, fixture.Semaphore.WaitCount);
            Assert.Equal(fixture.CommandApi.Submissions[0].Value, fixture.Semaphore.LastWaitValue);
            Assert.Equal(1, fixture.Device.Counters.DrainCount);
        }

        /// <summary>
        /// A WRITE MAP FLUSHES AND DOES NOT WAIT, which is the half of V-C8 a suite that only checked the read
        /// path could not tell from a device that waits on everything. A write map hands back memory the CALLER is
        /// about to fill, and the seam's contract for an off-timeline write is that it lands when you call it.
        /// </summary>
        [Theory]
        [InlineData(GpuMapMode.Write)]
        [InlineData(GpuMapMode.ReadWrite)]
        public void MappingAStagingTexture_DrainsOnlyWhenTheModeReads(GpuMapMode mode)
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture staging = fixture.StagingTexture();
            using IGpuTexture target = fixture.OpenSetupWork();

            fixture.Device.Map(staging, mode);
            fixture.Device.Unmap(staging);

            // The flush happens on every mode: the batch reached the queue either way.
            Assert.Single(fixture.CommandApi.Submissions);

            bool reads = mode != GpuMapMode.Write;
            Assert.Equal(reads ? 1 : 0, fixture.Semaphore.WaitCount);
            Assert.Equal(reads ? 1 : 0, fixture.Device.Counters.DrainCount);
        }

        // ---- The hook's own contract ---------------------------------------------------------------------

        /// <summary>
        /// A DEVICE BUILT OVER FAKE SEAMS REFUSES A COMMAND LIST BY NAME. The five recording seams are loaded
        /// <c>Vk</c> entry points and this device holds no instance, so the refusal is the honest answer and a
        /// <c>NullReferenceException</c> would not be. Asserted so the hook's one documented limitation stays a
        /// contract rather than a comment.
        /// </summary>
        [Fact]
        public void ADeviceBuiltOverSeams_RefusesToCreateACommandList()
        {
            using var fixture = new VulkanDeviceFixture();

            System.InvalidOperationException ex = Assert.Throws<System.InvalidOperationException>(
                () => fixture.Device.Factory.CreateCommandList());

            Assert.Contains("no VkInstance", ex.Message, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// A DEVICE BUILT OVER FAKE SEAMS TEARS ITSELF DOWN, with nothing killed first. Teardown's native path
        /// reads <c>Instance</c> on its first statement, so a device holding no lease has to be routed past it by
        /// the lease rather than by the liveness token, and the failure this row is written against is what
        /// happens otherwise: <c>Dispose</c> throws with the disposed flag already set, the next call returns
        /// silently, and the maps, the setup buffer, the descriptors, the modules, the pipelines and the timeline
        /// are never released. The semaphore is the one release visible from here, because the timeline owns it
        /// and destroys it through the fake this rig handed in.
        /// </summary>
        [Fact]
        public void ADeviceBuiltOverSeams_TearsItselfDownWithNothingKilledFirst()
        {
            using var fixture = new VulkanDeviceFixture();
            using (fixture.OpenSetupWork()) { }

            Assert.False(fixture.Liveness.IsDead);
            Assert.False(fixture.Semaphore.Disposed);

            fixture.Device.Dispose();

            Assert.Equal(1, fixture.Semaphore.DisposeCount);
            Assert.Single(fixture.PipelineApi.DestroyedCaches);
        }
    }
}
