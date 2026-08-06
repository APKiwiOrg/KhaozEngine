using System.Collections.Concurrent;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Decisions V-F1 to V-F4: the device's one completion timeline, its monotonic value allocation, the
    /// dead-device answers V-F10 requires of it, and the counted <c>WaitForIdle</c> drain. All device-free, over
    /// <see cref="FakeVulkanTimelineSemaphore"/> and the shipped <see cref="VulkanDeviceLiveness"/>, so every rule
    /// here runs on a machine with no Vulkan loader.
    /// <para>
    /// WHAT THESE ROWS DO NOT COVER, and it is the boundary worth naming: no value here is signalled by real GPU
    /// work, because nothing submits on this backend yet. Row 7
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517) owns the submit path and is where a counter first
    /// advances because a queue drained. What is asserted here is everything that decides what the backend DOES
    /// with such a value once it has one.
    /// </para>
    /// </summary>
    public sealed class VulkanTimelineTests
    {
        /// <summary>A fresh timeline has handed out nothing, so there is no point on it to wait for and every
        /// question about completion answers 0.</summary>
        [Fact]
        public void AFreshTimeline_HasIssuedNothing()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            Assert.Equal(0UL, timeline.LastSubmitted);
            Assert.Equal(0UL, timeline.CompletedValue);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        /// <summary>Values are handed out one at a time, strictly increasing, starting at 1. The first value being
        /// 1 rather than 0 is what lets 0 stay the fence's unarmed marker.</summary>
        [Fact]
        public void ValuesAreHandedOut_StrictlyIncreasingFromOne()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            Assert.Equal(1UL, timeline.NextSubmitValue());
            Assert.Equal(2UL, timeline.NextSubmitValue());
            Assert.Equal(3UL, timeline.NextSubmitValue());
            Assert.Equal(3UL, timeline.LastSubmitted);
        }

        /// <summary>
        /// THE ALLOCATION IS THREAD-SAFE, which is the one property of this type that a single-threaded test
        /// cannot see at all. Recording is lock-free on this backend and submits arrive from whatever thread
        /// recorded, so two submissions must never share a value: a shared value would make a fence on the first
        /// read signalled when only the second had finished, which frees resources the GPU is still reading.
        /// <para>
        /// The assertion is on DISTINCTNESS and on the high-water mark together, because either alone passes a
        /// broken implementation. Distinct values with a low maximum would mean values were skipped, and the right
        /// maximum with duplicates is exactly the defect being hunted.
        /// </para>
        /// </summary>
        [Fact]
        public void ValueAllocation_HandsNoValueOutTwiceUnderConcurrency()
        {
            const int threads = 8;
            const int perThread = 500;

            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);
            var taken = new ConcurrentBag<ulong>();

            Parallel.For(0, threads, _ =>
            {
                for (int i = 0; i < perThread; i++) taken.Add(timeline.NextSubmitValue());
            });

            var distinct = new System.Collections.Generic.HashSet<ulong>(taken);
            Assert.Equal(threads * perThread, taken.Count);
            Assert.Equal(threads * perThread, distinct.Count);
            Assert.Equal((ulong)(threads * perThread), timeline.LastSubmitted);
        }

        /// <summary>The completed value is the semaphore's, read fresh every time, because a cached one would let
        /// a fence read unsignalled forever after the GPU had passed it.</summary>
        [Fact]
        public void CompletedValue_ReadsTheSemaphoreEveryTime()
        {
            var semaphore = new FakeVulkanTimelineSemaphore { Completed = 4 };
            using var timeline = new VulkanTimeline(semaphore);

            Assert.Equal(4UL, timeline.CompletedValue);
            semaphore.Completed = 9;
            Assert.Equal(9UL, timeline.CompletedValue);
            Assert.Equal(2, semaphore.ReadCount);
        }

        /// <summary>
        /// AFTER DEVICE DEATH THE COMPLETED VALUE IS THE LAST ONE ISSUED, and the semaphore is not touched at all
        /// (V-F10). Both halves matter: the value is what makes every outstanding fence read signalled so a retire
        /// pool is released, and the untouched semaphore is what stops a call reaching a destroyed device's
        /// objects.
        /// </summary>
        [Fact]
        public void AfterDeviceDeath_CompletedValueIsWhatWasIssuedAndTheSemaphoreIsNotTouched()
        {
            var liveness = new VulkanDeviceLiveness();
            var semaphore = new FakeVulkanTimelineSemaphore { Completed = 1 };
            using var timeline = new VulkanTimeline(semaphore, liveness);

            timeline.NextSubmitValue();
            timeline.NextSubmitValue();
            liveness.MarkDead();

            Assert.Equal(2UL, timeline.CompletedValue);
            Assert.Equal(0, semaphore.ReadCount);
        }

        /// <summary>
        /// A DEVICE LOSS DISCOVERED BY THE READ ITSELF does not let the driver's number through. The real
        /// semaphore latches a loss at its own site, which flips liveness underneath the caller, so the value it
        /// hands back means nothing. Asking liveness again after the read is what catches that, and this row is
        /// the only thing pinning the second check in place.
        /// </summary>
        [Fact]
        public void ALossDiscoveredInsideTheRead_AnswersFromWhatWasIssued()
        {
            var liveness = new VulkanDeviceLiveness();
            var semaphore = new FakeVulkanTimelineSemaphore { Completed = 0 };
            using var timeline = new VulkanTimeline(semaphore, liveness);

            timeline.NextSubmitValue();
            timeline.NextSubmitValue();
            timeline.NextSubmitValue();
            semaphore.OnRead = liveness.MarkDead;

            Assert.Equal(3UL, timeline.CompletedValue);
        }

        /// <summary>Nothing has ever been submitted, so there is no point to wait for. The drain does not reach
        /// the semaphore and counts nothing, which is the state this backend is in until the submit path
        /// lands.</summary>
        [Fact]
        public void WaitForIdle_WithNothingSubmitted_DoesNotWaitAndCountsNothing()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            timeline.WaitForIdle();

            Assert.Equal(0, semaphore.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        /// <summary>
        /// A DRAIN THAT FOUND THE GPU ALREADY CAUGHT UP IS NOT A DRAIN, and is not counted. The seam's own
        /// <c>DrainCount</c> doc says so in as many words, and honouring it here costs one non-blocking read. The
        /// other backend counts every drain past its early returns because it signals a fresh point per drain and
        /// therefore always has something outstanding, which is a different situation rather than a different
        /// rule.
        /// </summary>
        [Fact]
        public void WaitForIdle_WithTheGpuCaughtUp_DoesNotWaitAndCountsNothing()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            ulong submitted = timeline.NextSubmitValue();
            semaphore.Completed = submitted;

            timeline.WaitForIdle();

            Assert.Equal(0, semaphore.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        /// <summary>
        /// THE DRAIN WAITS FOR THE LAST SUBMITTED VALUE AND IS COUNTED. The value is the whole of V-F2's theorem
        /// in practice: waiting for the last one waits for every earlier one, because a timeline's signals execute
        /// in submission order.
        /// </summary>
        [Fact]
        public void WaitForIdle_WithWorkOutstanding_WaitsForTheLastSubmittedValueAndCountsIt()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            timeline.NextSubmitValue();
            timeline.NextSubmitValue();
            ulong last = timeline.NextSubmitValue();

            timeline.WaitForIdle();

            Assert.Equal(1, semaphore.WaitCount);
            Assert.Equal(last, semaphore.LastWaitValue);

            VulkanWaitTotals drain = timeline.TotalDrain;
            Assert.Equal(1, drain.Count);
        }

        /// <summary>
        /// THE COUNTERS ACCUMULATE AND ARE NEVER ROLLED, which is what lets a telemetry session bracket a window
        /// by subtracting two sampled rows. A second drain with a fresh submission behind it counts a second time,
        /// and a third with nothing new behind it counts nothing at all.
        /// </summary>
        [Fact]
        public void DrainTotals_AccumulateAcrossDrainsAndAreNeverRolled()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore);

            timeline.NextSubmitValue();
            timeline.WaitForIdle();
            Assert.Equal(1, timeline.TotalDrain.Count);

            timeline.NextSubmitValue();
            timeline.WaitForIdle();
            Assert.Equal(2, timeline.TotalDrain.Count);

            // Nothing new was submitted, so the third drain has nothing to wait for.
            timeline.WaitForIdle();
            Assert.Equal(2, timeline.TotalDrain.Count);
            Assert.Equal(2, semaphore.WaitCount);
        }

        /// <summary>A drain on a dead device returns immediately, counts nothing and never reaches the semaphore
        /// (V-F10). A destroyed device has nothing to wait for, and waiting would wait on a counter nothing can
        /// advance.</summary>
        [Fact]
        public void WaitForIdle_OnADeadDevice_DoesNothing()
        {
            var liveness = new VulkanDeviceLiveness();
            var semaphore = new FakeVulkanTimelineSemaphore();
            using var timeline = new VulkanTimeline(semaphore, liveness);

            timeline.NextSubmitValue();
            liveness.MarkDead();

            timeline.WaitForIdle();

            Assert.Equal(0, semaphore.WaitCount);
            Assert.Equal(0, timeline.TotalDrain.Count);
        }

        /// <summary>
        /// A WAIT THAT ENDED BECAUSE THE DEVICE DIED IS STILL COUNTED. It blocked, for the time recorded, and
        /// dropping it would under-report exactly the drains a post-mortem cares about. What is never counted is a
        /// wait that did not happen.
        /// </summary>
        [Fact]
        public void WaitForIdle_ThatEndedInADeviceLoss_IsStillCounted()
        {
            var liveness = new VulkanDeviceLiveness();
            var semaphore = new FakeVulkanTimelineSemaphore { WaitReachesTheValue = false };
            using var timeline = new VulkanTimeline(semaphore, liveness);

            semaphore.OnWait = liveness.MarkDead;
            timeline.NextSubmitValue();

            timeline.WaitForIdle();

            Assert.Equal(1, semaphore.WaitCount);
            Assert.Equal(1, timeline.TotalDrain.Count);
        }

        /// <summary>The timeline owns the semaphore and destroys it exactly once, however many times it is
        /// disposed.</summary>
        [Fact]
        public void Dispose_DestroysTheSemaphoreOnce()
        {
            var semaphore = new FakeVulkanTimelineSemaphore();
            var timeline = new VulkanTimeline(semaphore);

            timeline.Dispose();
            timeline.Dispose();

            Assert.Equal(1, semaphore.DisposeCount);
        }

        /// <summary>
        /// A DEAD DEVICE SKIPS THE NATIVE DESTROY. <c>vkDestroyDevice</c> (or the loss that killed it) already
        /// destroyed every object made from the device, so <c>vkDestroySemaphore</c> afterwards is a call against
        /// freed memory, which aborts the process through the Vulkan loader rather than failing quietly.
        /// </summary>
        [Fact]
        public void Dispose_OnADeadDevice_SkipsTheNativeDestroy()
        {
            var liveness = new VulkanDeviceLiveness();
            var semaphore = new FakeVulkanTimelineSemaphore();
            var timeline = new VulkanTimeline(semaphore, liveness);

            liveness.MarkDead();
            timeline.Dispose();

            Assert.False(semaphore.Disposed);
        }
    }
}
