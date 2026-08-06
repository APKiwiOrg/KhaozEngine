using System;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The seam's <c>IGpuFence</c> on the native Vulkan backend (V-F1): a remembered value on the device's one
    /// timeline, and no device object of its own. Device-free over <see cref="FakeVulkanTimelineSemaphore"/>,
    /// which is what lets the target lifecycle, the poll, the reset and the dead-device answer all be asserted on
    /// a machine with no Vulkan loader.
    /// <para>
    /// ARMING IS ROW 7'S CALLER AND THIS ROW'S RULE. Nothing submits yet
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517), so <c>Arm</c> is driven directly here. What row 7
    /// adds is the one call site, not another rule.
    /// </para>
    /// </summary>
    public sealed class VulkanFenceLifecycleTests
    {
        /// <summary>A fresh fence is UNARMED and reads unsignalled, because the seam requires a fence to be
        /// unsignalled when it is submitted.</summary>
        [Fact]
        public void AFreshFence_IsUnarmedAndUnsignalled()
        {
            var semaphore = new FakeVulkanTimelineSemaphore { Completed = 99 };
            using var timeline = new VulkanTimeline(semaphore);

            VulkanGpuFence fence = timeline.CreateFence();

            Assert.Equal(0UL, fence.Target);
            Assert.False(fence.Signaled);
        }

        /// <summary>
        /// THE COMPARISON IS "AT OR ABOVE", not equality, and that is the whole of V-F2 at the poll site. A
        /// timeline value the GPU has passed covers every earlier submission transitively, so a fence armed at 5
        /// reads signalled at 5 and at everything after it.
        /// </summary>
        [Theory]
        [InlineData(0UL, false)]
        [InlineData(4UL, false)]
        [InlineData(5UL, true)]
        [InlineData(6UL, true)]
        [InlineData(500UL, true)]
        public void AnArmedFence_IsSignalledAtOrAboveItsTarget(ulong completed, bool expected)
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            VulkanGpuFence fence = timeline.CreateFence();
            fence.Arm(5);
            semaphore.Completed = completed;

            Assert.Equal(expected, fence.Signaled);
        }

        /// <summary>
        /// RESET UNARMS, so the fence can be handed to a later submission. It cannot unsignal anything and does
        /// not need to: the counter is device-wide and monotonic, so a reset fence is re-armed with a strictly
        /// higher value than the one it just held, which is exactly the fresh target the seam asks for.
        /// </summary>
        [Fact]
        public void Reset_UnarmsSoTheFenceCanBeHandedToALaterSubmission()
        {
            var semaphore = new FakeVulkanTimelineSemaphore { Completed = 5 };
            using var timeline = new VulkanTimeline(semaphore);

            VulkanGpuFence fence = timeline.CreateFence();
            fence.Arm(5);
            Assert.True(fence.Signaled);

            fence.Reset();
            Assert.Equal(0UL, fence.Target);
            Assert.False(fence.Signaled);

            fence.Arm(9);
            Assert.False(fence.Signaled);
            semaphore.Completed = 9;
            Assert.True(fence.Signaled);
        }

        /// <summary>
        /// RE-ARMING AN ARMED FENCE THROWS BY NAME. The seam requires a fence to be unsignalled when it is
        /// submitted, and overwriting the target silently would make the earlier submission's completion
        /// unobservable, so a consumer polling for it would free resources the GPU is still reading.
        /// </summary>
        [Fact]
        public void ArmingAnAlreadyArmedFence_Throws()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            VulkanGpuFence fence = timeline.CreateFence();
            fence.Arm(1);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => fence.Arm(2));
            Assert.Contains("Reset", thrown.Message, StringComparison.Ordinal);
            Assert.Equal(1UL, fence.Target);
        }

        /// <summary>Arming with 0 is the unarmed marker and is refused, because the timeline starts at 0 and the
        /// first submission takes 1, so reaching that call means a fence was armed with no value
        /// allocated.</summary>
        [Fact]
        public void ArmingWithZero_Throws()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            VulkanGpuFence fence = timeline.CreateFence();

            Assert.Throws<ArgumentOutOfRangeException>(() => fence.Arm(0));
        }

        /// <summary>
        /// AFTER DEVICE DEATH EVERY FENCE READS SIGNALLED, armed or not (V-F10). A destroyed device has no
        /// outstanding work, so "is it done" is yes. Getting this wrong is not cosmetic: an unsignalled fence
        /// after death strands <c>RetiredResourcePool</c> forever on a batch it can never free, and teardown is
        /// exactly where a resource wrapper outliving its device is normal rather than a defect.
        /// </summary>
        [Fact]
        public void AfterDeviceDeath_EveryFenceReadsSignalled()
        {
            var liveness = new VulkanDeviceLiveness();
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore, liveness);

            VulkanGpuFence unarmed = timeline.CreateFence();
            VulkanGpuFence armed = timeline.CreateFence();
            armed.Arm(7);

            Assert.False(unarmed.Signaled);
            Assert.False(armed.Signaled);

            // What the live polls above cost, so the assertion below is that death added NONE rather than that
            // nothing ever read: an armed fence on a live device does reach the semaphore, and must.
            int readsWhileAlive = semaphore.ReadCount;
            liveness.MarkDead();

            Assert.True(unarmed.Signaled);
            Assert.True(armed.Signaled);
            Assert.Equal(readsWhileAlive, semaphore.ReadCount);
        }

        /// <summary>
        /// DISPOSAL RELEASES NOTHING AND LATCHES. A disposed fence still polls and still resets, because those are
        /// teardown-order accidents rather than defects, and arming one throws, because that is a submission
        /// against an object the caller has already given up.
        /// </summary>
        [Fact]
        public void Dispose_LatchesArmingAndLeavesPollingAlone()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            VulkanGpuFence fence = timeline.CreateFence();
            fence.Arm(3);
            fence.Dispose();

            semaphore.Completed = 3;
            Assert.True(fence.Signaled);
            fence.Reset();
            Assert.False(fence.Signaled);

            Assert.Throws<ObjectDisposedException>(() => fence.Arm(4));
        }
    }
}
