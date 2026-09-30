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
    /// </summary>
    public sealed class VulkanStagingCopyOrderTests
    {
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

            List<string> transfers = trace.FindAll(t => t.StartsWith("MemoryBarrier", StringComparison.Ordinal)
                || t.StartsWith("CopyImageToBuffer", StringComparison.Ordinal));
            Assert.Equal(4, transfers.Count);
            Assert.Equal("MemoryBarrier(toTransfer)", transfers[0]);
            Assert.StartsWith("CopyImageToBuffer", transfers[1], StringComparison.Ordinal);
            Assert.Equal("MemoryBarrier(toTransfer)", transfers[2]);
            Assert.StartsWith("CopyImageToBuffer", transfers[3], StringComparison.Ordinal);
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

        // The barrier appears exactly once and before the copy's first region.
        static void AssertOrderedOnce(List<string> trace, string copy)
        {
            int barrier = trace.IndexOf("MemoryBarrier(toTransfer)");
            int region = trace.FindIndex(t => t.StartsWith(copy, StringComparison.Ordinal));

            Assert.True(barrier >= 0, "no barrier before the copy: " + string.Join(", ", trace));
            Assert.True(region > barrier, "the barrier must precede the copy: " + string.Join(", ", trace));
            Assert.Single(trace, t => t.StartsWith("MemoryBarrier", StringComparison.Ordinal));
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
