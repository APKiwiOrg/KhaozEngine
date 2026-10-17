using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <see cref="IGpuFencedReadback.MapForRead"/> without a GPU. The device-level facts drive a real
    /// <see cref="VulkanGpuDevice"/> over the fakes in <see cref="VulkanDeviceFixture"/>, which is the only native
    /// device with a device-over-fakes hook. Native Metal needs a real <c>MTLDevice</c> to create a staging
    /// texture, and the Direct3D 11 device is Windows only, so neither can be driven here. Both are covered by
    /// <see cref="FencedReadbackGpuTests"/> on their own legs, and the Direct3D 11 map path underneath is
    /// <c>D3D11StagingAccess</c>, which <see cref="D3D11StagingMapTests"/> already drives.
    /// </summary>
    public sealed class FencedReadbackTests
    {
        [Fact]
        public void An_unsignaled_fence_is_refused()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture staging = fixture.StagingTexture();
            using IGpuFence submitted = fixture.Device.Factory.CreateFence();
            using IGpuFence unarmed = fixture.Device.Factory.CreateFence();
            using VulkanCommandList list = fixture.RecordedList();
            fixture.Device.Submit(list, submitted);

            Assert.False(submitted.Signaled);
            Assert.Throws<InvalidOperationException>(() => fixture.Device.MapForRead(staging, submitted));
            Assert.Throws<InvalidOperationException>(() => fixture.Device.MapForRead(staging, unarmed));

            // Nothing was mapped: an Unmap of the texture is refused as unmapped, and nothing waited.
            Assert.Throws<InvalidOperationException>(() => fixture.Device.Unmap(staging));
            Assert.Equal(0, fixture.Semaphore.WaitCount);
        }

        [Fact]
        public void A_non_staging_texture_is_refused()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture target = fixture.OpenSetupWork();
            IGpuFence fence = SignaledFence(fixture);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => fixture.Device.MapForRead(target, fence));
            Assert.Equal("staging", ex.ParamName);
        }

        /// <summary>
        /// The Vulkan device is the one covered here, for the reason in the type summary. fenceA has signaled and
        /// fenceB has not, so any wait on the device would wait for fenceB's submission and show on the fake
        /// semaphore. Setup work opened after both submits would reach the queue if the map flushed.
        /// </summary>
        [Fact]
        public void MapForRead_never_drains_the_device()
        {
            using var fixture = new VulkanDeviceFixture();
            using IGpuTexture staging = fixture.StagingTexture();
            using IGpuFence fenceA = fixture.Device.Factory.CreateFence();
            using IGpuFence fenceB = fixture.Device.Factory.CreateFence();
            using VulkanCommandList first = fixture.RecordedList();
            using VulkanCommandList second = fixture.RecordedList();

            fixture.Device.Submit(first, fenceA);
            fixture.Semaphore.Completed = fixture.CommandApi.Submissions[^1].Value;
            fixture.Device.Submit(second, fenceB);
            using IGpuTexture opened = fixture.OpenSetupWork();
            int submissions = fixture.CommandApi.Submissions.Count;

            MappedData map = fixture.Device.MapForRead(staging, fenceA);
            fixture.Device.Unmap(staging);

            Assert.NotEqual(IntPtr.Zero, map.Data);
            Assert.True(fenceA.Signaled);
            Assert.False(fenceB.Signaled);
            Assert.Equal(0, fixture.Semaphore.WaitCount);
            Assert.Equal(0, fixture.Timeline.WaitForIdleCalls);
            Assert.Equal(0, fixture.Device.Counters.DrainCount);
            Assert.Equal(submissions, fixture.CommandApi.Submissions.Count);
        }

        /// <summary>A readback staging texture on a cached, non-coherent memory type has its range invalidated
        /// before the pointer is returned, which is what makes the host's cached view current.</summary>
        [Fact]
        public void A_non_coherent_staging_texture_is_invalidated_before_the_pointer_is_returned()
        {
            using var fixture = new VulkanDeviceFixture(nonCoherentReadback: true);
            using IGpuTexture staging = fixture.StagingTexture();
            IGpuFence fence = SignaledFence(fixture);
            int before = fixture.MemoryApi.Invalidates.Count;

            fixture.Device.MapForRead(staging, fence);
            fixture.Device.Unmap(staging);

            Assert.Equal(before + 1, fixture.MemoryApi.Invalidates.Count);
            Assert.Equal(0, fixture.Semaphore.WaitCount);
        }

        /// <summary>A fence from another device names a value on another timeline, so it would read signaled for
        /// work this device never ran.</summary>
        [Fact]
        public void A_fence_from_another_device_is_refused()
        {
            using var fixture = new VulkanDeviceFixture();
            using var other = new VulkanDeviceFixture();
            using IGpuTexture staging = fixture.StagingTexture();
            IGpuFence foreign = SignaledFence(other);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => fixture.Device.MapForRead(staging, foreign));
            Assert.Equal("fence", ex.ParamName);
        }

        [Fact]
        public void Every_native_device_offers_the_capability()
        {
            Assert.True(typeof(IGpuFencedReadback).IsAssignableFrom(typeof(VulkanGpuDevice)));
            Assert.True(typeof(IGpuFencedReadback).IsAssignableFrom(typeof(MetalGpuDevice)));
            Assert.True(typeof(IGpuFencedReadback).IsAssignableFrom(typeof(D3D11GpuDevice)));
        }

        // ---- Vulkan host visibility in the copy path ----------------------------------------------------------

        /// <summary>A copy whose destination is a staging texture ends with the transfer-to-host barrier after its
        /// last region. A host wait on the timeline is not a memory dependency for host reads, so this barrier is
        /// what makes the copy's writes available to the host.</summary>
        [Fact]
        public void A_readback_copy_into_staging_ends_with_the_host_barrier()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTexture(
                Texture(fixture, owned, GpuTextureUsage.RenderTarget),
                Texture(fixture, owned, GpuTextureUsage.Staging)));

            AssertHostBarrierLast(trace, "CopyImageToBuffer");
        }

        [Fact]
        public void A_subresource_readback_into_staging_ends_with_the_host_barrier()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.RenderTarget), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Staging), 8, 8));

            AssertHostBarrierLast(trace, "CopyImageToBuffer");
        }

        [Fact]
        public void A_staging_to_staging_copy_ends_with_the_host_barrier()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTexture(
                Texture(fixture, owned, GpuTextureUsage.Staging),
                Texture(fixture, owned, GpuTextureUsage.Staging)));

            AssertHostBarrierLast(trace, "CopyBuffer");
        }

        /// <summary>Only a staging destination is read by the host, so an upload and an image copy take none.
        /// </summary>
        [Fact]
        public void A_copy_into_an_image_takes_no_host_barrier()
        {
            List<string> upload = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.Staging), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Sampled), 0, 0, 8, 8));
            List<string> image = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.Storage), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Sampled), 0, 0, 8, 8));

            Assert.DoesNotContain("HostReadBarrier", upload);
            Assert.DoesNotContain("HostReadBarrier", image);
        }

        [Fact]
        public void The_host_barrier_orders_transfer_writes_before_host_reads()
        {
            MemoryBarrier2 barrier = VulkanTransferBarrier.ToHost;

            Assert.Equal(PipelineStageFlags2.AllTransferBit, barrier.SrcStageMask);
            Assert.Equal(AccessFlags2.TransferWriteBit, barrier.SrcAccessMask);
            Assert.Equal(PipelineStageFlags2.HostBit, barrier.DstStageMask);
            Assert.Equal(AccessFlags2.HostReadBit, barrier.DstAccessMask);
        }

        // A fence armed by a submission on the fixture's device and then signaled through the fake semaphore.
        static IGpuFence SignaledFence(VulkanDeviceFixture fixture)
        {
            IGpuFence fence = fixture.Device.Factory.CreateFence();
            using VulkanCommandList list = fixture.RecordedList();
            fixture.Device.Submit(list, fence);
            fixture.Semaphore.Completed = fixture.CommandApi.Submissions[^1].Value;
            Assert.True(fence.Signaled);
            return fence;
        }

        static void AssertHostBarrierLast(List<string> trace, string copy)
        {
            int region = trace.FindLastIndex(t => t.StartsWith(copy, StringComparison.Ordinal));
            int barrier = trace.IndexOf("HostReadBarrier");

            Assert.True(region >= 0, "no copy region: " + string.Join(", ", trace));
            Assert.True(barrier > region, "the host barrier must follow the last region: " + string.Join(", ", trace));
            Assert.Single(trace, t => t == "HostReadBarrier");
        }

        static List<string> Record(Action<VulkanResourceFixture, List<IDisposable>, VulkanCommandList> copy)
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                fixture.Trace.Clear();

                copy(fixture, owned, list);

                return new List<string>(fixture.Trace);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        static IGpuTexture Texture(VulkanResourceFixture fixture, List<IDisposable> owned, GpuTextureUsage usage)
        {
            IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(8, 8, usage));
            owned.Add(texture);
            return texture;
        }
    }
}
