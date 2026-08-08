using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-F5's INDEXING RULE, which is the most common Vulkan swapchain bug and the one that never fails
    /// cleanly.
    /// <para>
    /// The acquire semaphore is handed to <c>vkAcquireNextImageKHR</c> BEFORE the image index is known, so a ring
    /// indexed by image index reuses a semaphore that may still be pending from an acquire that returned a
    /// DIFFERENT image. That is undefined behaviour, and it manifests as a validation error and an intermittent
    /// hang rather than as a failure anybody can attribute. So the indexing is a monotonic acquire counter, and it
    /// is asserted here over a simulated sequence that includes <c>OUT_OF_DATE</c> returns.
    /// </para>
    /// </summary>
    public sealed class VulkanAcquireRingTests
    {
        /// <summary>
        /// THE CAPACITY IS THE MAXIMUM OF THE TWO CLOCKS PLUS ONE. Acquires are paced by the presentation engine
        /// and recording is paced by the frame loop, so a ring sized on either alone is exhausted by the other.
        /// </summary>
        [Theory]
        [InlineData(3, 3, 4)]
        [InlineData(3, 5, 6)]
        [InlineData(5, 2, 6)]
        [InlineData(2, 2, 3)]
        public void TheCapacityClearsBothClocks(int framesInFlight, int imageCount, int expected)
        {
            Assert.Equal(expected, VulkanAcquireRing.CapacityFor(framesInFlight, imageCount));
        }

        /// <summary>
        /// THE HANDOUT FOLLOWS THE ACQUIRE COUNTER, so consecutive acquires never share a semaphore and the reuse
        /// distance is the ring's capacity. This is the assertion the whole type exists for.
        /// </summary>
        [Fact]
        public void ConsecutiveAcquiresNeverShareASemaphore()
        {
            var api = new FakeVulkanSwapchainApi();
            using var ring = new VulkanAcquireRing(api, framesInFlight: 3);
            ring.Rebuild(imageCount: 3);

            var handed = new List<ulong>();
            for (int i = 0; i < 40; i++) handed.Add(ring.Next());

            int capacity = ring.Capacity;
            Assert.Equal(4, capacity);

            for (int i = 1; i < handed.Count; i++) Assert.NotEqual(handed[i - 1], handed[i]);
            for (int i = capacity; i < handed.Count; i++) Assert.Equal(handed[i - capacity], handed[i]);
        }

        /// <summary>
        /// AN ACQUIRE THAT CAME BACK <c>OUT_OF_DATE</c> STILL CONSUMED ITS TURN. Reusing its semaphore for the
        /// retry is precisely the reuse this ring exists to prevent, and the failed acquire is not distinguishable
        /// from a successful one at this level: the ring is asked once per ATTEMPT.
        /// </summary>
        [Fact]
        public void AFailedAcquireStillConsumesItsTurn()
        {
            var api = new FakeVulkanSwapchainApi();
            using var ring = new VulkanAcquireRing(api, framesInFlight: 2);
            ring.Rebuild(imageCount: 2);

            ulong first = ring.Next();
            ulong afterFailure = ring.Next();

            Assert.NotEqual(first, afterFailure);
            Assert.Equal(2UL, ring.AcquireCount);
        }

        /// <summary>
        /// A REBUILD DESTROYS EVERY SEMAPHORE AND MAKES A FRESH SET, and the caller has already drained. A binary
        /// semaphore an acquire signalled that nothing waited on stays PENDING, there is no way to ask one whether
        /// it is, and destroying a pending semaphore is undefined behaviour. A drained queue is the only state in
        /// which the answer is knowable, which is what makes the recreate's drain unconditional.
        /// </summary>
        [Fact]
        public void ARebuildRetiresEveryOldSemaphore()
        {
            var api = new FakeVulkanSwapchainApi();
            using var ring = new VulkanAcquireRing(api, framesInFlight: 3);

            ring.Rebuild(imageCount: 3);
            var firstGeneration = new List<ulong>();
            for (int i = 0; i < ring.Capacity; i++) firstGeneration.Add(ring.At(i));

            ring.Rebuild(imageCount: 4);

            foreach (ulong old in firstGeneration) Assert.DoesNotContain(old, api.LiveSemaphores);
            Assert.Equal(VulkanAcquireRing.CapacityFor(3, 4), api.LiveSemaphores.Count);
        }

        /// <summary>
        /// THE COUNTER IS NOT RESET BY A REBUILD, deliberately. Resetting it would make the first acquire after a
        /// resize take the ring slot the last acquire before it took, which is the reuse bug arriving through the
        /// one path that was supposed to retire it.
        /// </summary>
        [Fact]
        public void ARebuildDoesNotResetTheAcquireCounter()
        {
            var api = new FakeVulkanSwapchainApi();
            using var ring = new VulkanAcquireRing(api, framesInFlight: 3);
            ring.Rebuild(imageCount: 3);

            ring.Next();
            ring.Next();
            ring.Rebuild(imageCount: 3);

            Assert.Equal(2UL, ring.AcquireCount);
        }

        /// <summary>An acquire before any swapchain existed is refused by name rather than handing back a zero
        /// handle the driver would read as "no semaphore".</summary>
        [Fact]
        public void AnAcquireBeforeAnySwapchainIsRefused()
        {
            var api = new FakeVulkanSwapchainApi();
            using var ring = new VulkanAcquireRing(api, framesInFlight: 3);

            Assert.Throws<InvalidOperationException>(() => ring.Next());
        }

        /// <summary>Disposal destroys every semaphore, so a device that goes down leaves none behind.</summary>
        [Fact]
        public void DisposalDestroysEverySemaphore()
        {
            var api = new FakeVulkanSwapchainApi();
            var ring = new VulkanAcquireRing(api, framesInFlight: 3);
            ring.Rebuild(imageCount: 3);

            ring.Dispose();

            Assert.Empty(api.LiveSemaphores);
        }
    }
}
