using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A TEXTURE COPY WITH A STAGING SIDE IS ORDERED BY A GLOBAL BARRIER BEFORE ITS FIRST REGION. A staging texture
    /// is a <c>VkBuffer</c>, so no layout transition orders it, and its memory range goes to the next staging texture
    /// once the frame boundary frees it. Two readbacks in a row then write one range from two submissions, ordered
    /// only by the host's timeline wait between them, which the synchronization validation job reported as a write
    /// after write on every readback of a multi-frame fixture. The barrier puts that dependency in the command
    /// stream.
    /// <para>A COPY INTO STAGING ALSO TAKES ONE GLOBAL BARRIER AFTER ITS LAST REGION, whose second scope names the
    /// HOST. A fence signal covers device access only, so a CPU read of the staging memory after the fence has no
    /// dependency on the copy's write without it. One per copy call, not per region. A copy whose only staging side
    /// is its source, and an image-to-image copy, take none.</para>
    /// </summary>
    public sealed class VulkanStagingCopyOrderTests
    {
        [Fact]
        public void AWholeReadbackIntoStaging_IsMadeHostVisibleOnceAfterItsLastRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTexture(
                Texture(fixture, owned, GpuTextureUsage.RenderTarget, mipLevels: 3, arrayLayers: 2),
                Texture(fixture, owned, GpuTextureUsage.Staging, mipLevels: 3, arrayLayers: 2)));

            List<string> transfers = Transfers(trace);
            Assert.Equal(8, transfers.Count);
            Assert.Equal(ToTransfer, transfers[0]);
            Assert.All(transfers.GetRange(1, 6),
                t => Assert.StartsWith("CopyImageToBuffer", t, StringComparison.Ordinal));
            Assert.Equal(ToHost, transfers[7]);
        }

        [Fact]
        public void ASubresourceReadbackIntoStaging_IsMadeHostVisibleAfterItsRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.RenderTarget), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Staging), 8, 8));

            List<string> transfers = Transfers(trace);
            Assert.Equal(3, transfers.Count);
            Assert.Equal(ToTransfer, transfers[0]);
            Assert.StartsWith("CopyImageToBuffer", transfers[1], StringComparison.Ordinal);
            Assert.Equal(ToHost, transfers[2]);
        }

        [Fact]
        public void AStagingToStagingCopy_IsMadeHostVisibleAfterItsRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTexture(
                Texture(fixture, owned, GpuTextureUsage.Staging),
                Texture(fixture, owned, GpuTextureUsage.Staging)));

            List<string> transfers = Transfers(trace);
            Assert.Equal(3, transfers.Count);
            Assert.Equal(ToTransfer, transfers[0]);
            Assert.StartsWith("CopyBuffer(", transfers[1], StringComparison.Ordinal);
            Assert.Equal(ToHost, transfers[2]);
        }

        [Fact]
        public void AnUploadOutOfStaging_TakesNoBarrierAfterItsRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.Staging), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Sampled), 0, 0, 8, 8));

            Assert.DoesNotContain(ToHost, trace);
        }

        [Fact]
        public void AReadbackIntoStaging_IsOrderedBeforeItsFirstRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTexture(
                Texture(fixture, owned, GpuTextureUsage.RenderTarget),
                Texture(fixture, owned, GpuTextureUsage.Staging)));

            AssertOrderedOnce(trace, "CopyImageToBuffer");
        }

        [Fact]
        public void ASubresourceReadbackIntoStaging_IsOrderedBeforeItsRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.RenderTarget), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Staging), 8, 8));

            AssertOrderedOnce(trace, "CopyImageToBuffer");
        }

        [Fact]
        public void AnUploadOutOfStaging_IsOrderedBeforeItsRegion()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.Staging), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Sampled), 0, 0, 8, 8));

            AssertOrderedOnce(trace, "CopyBufferToImage");
        }

        /// <summary>Two copies into one list, each ordered by its own barrier: the second write into staging
        /// memory the first also wrote is never left without a dependency on it.</summary>
        [Fact]
        public void EachOfTwoReadbacks_CarriesItsOwnBarrier()
        {
            List<string> trace = Record((fixture, owned, list) =>
            {
                IGpuTexture source = Texture(fixture, owned, GpuTextureUsage.RenderTarget);
                IGpuTexture staging = Texture(fixture, owned, GpuTextureUsage.Staging);
                list.CopyTexture(source, staging);
                list.CopyTexture(source, staging);
            });

            List<string> transfers = Transfers(trace);
            Assert.Equal(6, transfers.Count);
            Assert.Equal(ToTransfer, transfers[0]);
            Assert.StartsWith("CopyImageToBuffer", transfers[1], StringComparison.Ordinal);
            Assert.Equal(ToHost, transfers[2]);
            Assert.Equal(ToTransfer, transfers[3]);
            Assert.StartsWith("CopyImageToBuffer", transfers[4], StringComparison.Ordinal);
            Assert.Equal(ToHost, transfers[5]);
        }

        /// <summary>Two real images are ordered by their layout transitions, so the copy between them takes no
        /// global barrier.</summary>
        [Fact]
        public void AnImageToImageCopy_TakesNoGlobalBarrier()
        {
            List<string> trace = Record((fixture, owned, list) => list.CopyTextureSubresource(
                Texture(fixture, owned, GpuTextureUsage.Storage), 0, 0,
                Texture(fixture, owned, GpuTextureUsage.Sampled), 0, 0, 8, 8));

            Assert.DoesNotContain(trace, t => t.StartsWith("MemoryBarrier", StringComparison.Ordinal));
            Assert.Contains(trace, t => t.StartsWith("CopyImage(", StringComparison.Ordinal));
        }

        const string ToTransfer = "MemoryBarrier(toTransfer)";
        const string ToHost = "HostReadBarrier";

        // The before-copy barrier appears exactly once and before the copy's first region.
        static void AssertOrderedOnce(List<string> trace, string copy)
        {
            int barrier = trace.IndexOf(ToTransfer);
            int region = trace.FindIndex(t => t.StartsWith(copy, StringComparison.Ordinal));

            Assert.True(barrier >= 0, "no barrier before the copy: " + string.Join(", ", trace));
            Assert.True(region > barrier, "the barrier must precede the copy: " + string.Join(", ", trace));
            Assert.Single(trace, t => t == ToTransfer);
        }

        // The global barriers and the copy regions, in recorded order, without the layout transitions.
        static List<string> Transfers(List<string> trace)
            => trace.FindAll(t => t.StartsWith("MemoryBarrier", StringComparison.Ordinal)
                || t.StartsWith("Copy", StringComparison.Ordinal) || t == ToHost);

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

        static IGpuTexture Texture(VulkanResourceFixture fixture, List<IDisposable> owned, GpuTextureUsage usage,
            uint mipLevels = 1, uint arrayLayers = 1)
        {
            IGpuTexture texture = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, usage, mipLevels: mipLevels, arrayLayers: arrayLayers));
            owned.Add(texture);
            return texture;
        }
    }
}
