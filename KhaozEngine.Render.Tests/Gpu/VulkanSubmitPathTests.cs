using System;
using System.Threading;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SUBMIT PATH, device-free: one <c>vkQueueSubmit</c> per submission (V-F3), the timeline value allocated
    /// inside the lock that orders it, the fence armed with the value it signals, and the failure shape that stops
    /// a failed submit from stranding <c>WaitForIdle</c> forever.
    /// <para>
    /// THE FAILURE ROWS ARE THE POINT OF THIS FILE. The hazard is exact: the two out-of-memory results do NOT flip
    /// liveness, and the spec requires the implementation to leave every referenced synchronisation primitive
    /// unaffected on them, so the value that submission took will never be signalled by anything. A drain that
    /// targeted the allocation high-water would then block forever on the very next call. That cannot be found by
    /// a golden, cannot be found by a soak that never runs out of memory, and is asserted here.
    /// </para>
    /// </summary>
    public sealed class VulkanSubmitPathTests
    {
        /// <summary>A submit is ONE vkQueueSubmit carrying the sealed slot's buffer and the value it signals, and
        /// the value is the timeline's next. The incumbent's second empty submit signalling an internal tracking
        /// fence has nowhere to be expressed on this seam.</summary>
        [Fact]
        public void ASubmit_IsOneQueueSubmitCarryingTheSealedBufferAndItsValue()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            list.Begin();
            list.End();
            ulong buffer = list.SealedBuffer;
            fixture.Submits.Submit(list, null);

            Assert.Single(fixture.Api.Submissions);
            Assert.Equal(buffer, fixture.Api.Submissions[0].Buffer);
            Assert.Equal(1UL, fixture.Api.Submissions[0].Value);
        }

        /// <summary>A list that was never sealed is refused rather than queued half recorded, which is what the
        /// seam says a backend is free to do and what the driver would require anyway: a buffer
        /// vkEndCommandBuffer never saw cannot legally be named in a submit.</summary>
        [Fact]
        public void AnUnsealedList_IsRefusedAndNothingIsQueued()
        {
            using var fixture = new VulkanCommandListTests.Fixture();
            using VulkanCommandList list = fixture.CreateList();

            Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, null));

            list.Begin();
            Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, null));

            Assert.Empty(fixture.Api.Submissions);
        }

        /// <summary>Values are taken in submit order and never skipped, which is the property the whole
        /// one-timeline theorem rests on. Every submit takes one, including the ones with no fence.</summary>
        [Fact]
        public void EverySubmitTakesAValue_InSubmitOrder()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            Assert.Equal(1UL, fixture.RecordAndSubmit(list));
            Assert.Equal(2UL, fixture.RecordAndSubmit(list));
            Assert.Equal(3UL, fixture.RecordAndSubmit(list));

            Assert.Equal(3UL, fixture.Timeline.LastSubmitted);
            Assert.Equal(3UL, fixture.Timeline.LastAllocated);
        }

        /// <summary>The submitted value goes back into the SLOT that was sealed, so the next wrap onto it waits
        /// for that submission rather than for whatever the device submitted most recently.</summary>
        [Fact]
        public void TheSubmittedValue_IsRecordedIntoTheSlotThatWasSealed()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            ulong first = fixture.RecordAndSubmit(list);
            ulong second = fixture.RecordAndSubmit(list);

            Assert.Equal(first, list.Ring.SubmittedAt(0));
            Assert.Equal(second, list.Ring.SubmittedAt(1));
            Assert.Equal(0UL, list.Ring.SubmittedAt(2));
        }

        // ---- Fences ----

        /// <summary>A fence handed to a submit is armed with the value that submission signals, and reads
        /// signalled once the counter passes it and not before.</summary>
        [Fact]
        public void AFence_IsArmedWithTheSubmittedValue()
        {
            using var fixture = new VulkanCommandListTests.Fixture();
            using VulkanCommandList list = fixture.CreateList();
            VulkanGpuFence fence = fixture.Timeline.CreateFence();

            Assert.False(fence.Signaled);

            list.Begin();
            list.End();
            ulong value = fixture.Submits.Submit(list, fence);

            Assert.Equal(value, fence.Target);
            Assert.False(fence.Signaled);

            fixture.Semaphore.Completed = value;
            Assert.True(fence.Signaled);
        }

        /// <summary>
        /// AN ALREADY ARMED FENCE IS REFUSED BEFORE THE NATIVE CALL, not after. The seam requires a fence to be
        /// unsignalled when it is submitted, and arming ahead of the submit means a caller who forgot to Reset is
        /// told so without work having been queued against a fence that cannot observe it.
        /// </summary>
        [Fact]
        public void AnAlreadyArmedFence_IsRefusedBeforeAnythingIsQueued()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();
            VulkanGpuFence fence = fixture.Timeline.CreateFence();

            list.Begin();
            list.End();
            fixture.Submits.Submit(list, fence);

            list.Begin();
            list.End();
            Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, fence));

            Assert.Single(fixture.Api.Submissions);
        }

        // ---- The failure shape ----

        /// <summary>
        /// A FAILED SUBMIT THROWS, LEAVES NO WORK QUEUED, AND LEAVES THE DRAIN TARGET REACHABLE. The value the
        /// failed submission took is never registered, so <c>WaitForIdle</c> still waits for the last SUCCESSFUL
        /// value, which the GPU can still reach. That is the whole structural fix, and the alternative
        /// (host-signalling the taken value to close the gap) was declined because a host signal has to respect
        /// the strictly-increasing rule against signals still pending on the queue.
        /// </summary>
        [Fact]
        public void AFailedSubmit_ThrowsAndLeavesTheDrainTargetReachable()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            ulong good = fixture.RecordAndSubmit(list);

            fixture.Api.FailNextSubmit(VulkanSubmitStatus.Failed);
            list.Begin();
            list.End();

            InvalidOperationException failed =
                Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, null));
            Assert.Contains("vkQueueSubmit failed", failed.Message, StringComparison.Ordinal);

            // The gap: allocated, never registered, never signalled.
            Assert.Equal(2UL, fixture.Timeline.LastAllocated);
            Assert.Equal(good, fixture.Timeline.LastSubmitted);

            // And the drain therefore has a target the GPU can still reach.
            fixture.Timeline.WaitForIdle();
            Assert.Equal(good, fixture.Semaphore.LastWaitValue);
        }

        /// <summary>
        /// AND THE DRAIN ACTUALLY TERMINATES, which is the failure this shape exists to prevent stated as the
        /// thing a caller experiences. The fake semaphore only advances to the value it was asked to wait for, so
        /// a target above anything the GPU will signal would hang here rather than pass.
        /// </summary>
        [Fact]
        public void AfterAFailedSubmit_WaitForIdleStillCompletes()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            ulong good = fixture.RecordAndSubmit(list);

            fixture.Api.FailNextSubmit(VulkanSubmitStatus.Failed);
            list.Begin();
            list.End();
            Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, null));

            fixture.Timeline.WaitForIdle();

            Assert.True(fixture.Timeline.CompletedValue >= good);
            Assert.Equal(1, fixture.Semaphore.WaitCount);
        }

        /// <summary>A fence handed to a submit that then failed is UNARMED again, so it neither reads signalled
        /// over work that never ran nor blocks the next submit as still armed.</summary>
        [Fact]
        public void AFenceOnAFailedSubmit_IsUnarmedAgain()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();
            VulkanGpuFence fence = fixture.Timeline.CreateFence();

            fixture.Api.FailNextSubmit(VulkanSubmitStatus.Failed);
            list.Begin();
            list.End();
            Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, fence));

            Assert.Equal(0UL, fence.Target);
            Assert.False(fence.Signaled);

            // And it can be handed to the retry without a Reset the caller has no reason to make.
            list.Begin();
            list.End();
            ulong retried = fixture.Submits.Submit(list, fence);
            Assert.Equal(retried, fence.Target);
        }

        /// <summary>A submit that failed records nothing into the slot, so the next wrap onto it does not wait for
        /// a value nothing will signal. A slot whose record never reached the queue is safe to reset
        /// immediately.</summary>
        [Fact]
        public void AFailedSubmit_RecordsNoValueIntoItsSlot()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 2);
            using VulkanCommandList list = fixture.CreateList();

            fixture.Api.FailNextSubmit(VulkanSubmitStatus.Failed);
            list.Begin();
            list.End();
            Assert.Throws<InvalidOperationException>(() => fixture.Submits.Submit(list, null));

            Assert.Equal(0UL, list.Ring.SubmittedAt(0));

            // Two more records wrap back onto slot 0, which waits for nothing.
            list.Begin();
            list.End();
            list.Begin();
            list.End();

            Assert.Equal(0, fixture.Semaphore.WaitCount);
        }

        // ---- Device loss ----

        /// <summary>
        /// A SUBMIT THAT LOST THE DEVICE DOES NOT THROW, and does not register. The loss was latched, logged and
        /// put in the telemetry session header at the submit's own site, and after death this backend's posture is
        /// quiet safe answers everywhere (V-F10): WaitForIdle returns, every fence reads signalled, and every
        /// destroy is skipped.
        /// </summary>
        [Fact]
        public void ASubmitThatLostTheDevice_ReturnsQuietlyAndRegistersNothing()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            fixture.Api.FailNextSubmit(VulkanSubmitStatus.DeviceLost);
            fixture.Api.OnSubmit = fixture.Liveness.MarkDead;

            list.Begin();
            list.End();

            Assert.Equal(0UL, fixture.Submits.Submit(list, null));
            Assert.Equal(0UL, fixture.Timeline.LastSubmitted);
        }

        /// <summary>A device that was ALREADY dead submits nothing at all and never reaches the driver, because
        /// every native call against a destroyed device aborts the process through the Vulkan loader.</summary>
        [Fact]
        public void ASubmitOnAnAlreadyDeadDevice_NeverReachesTheDriver()
        {
            using var fixture = new VulkanCommandListTests.Fixture(depth: 3);
            using VulkanCommandList list = fixture.CreateList();

            list.Begin();
            list.End();
            fixture.Liveness.MarkDead();

            Assert.Equal(0UL, fixture.Submits.Submit(list, null));
            Assert.Empty(fixture.Api.Submissions);
        }

        /// <summary>
        /// THE FRAME'S SEMAPHORE PAIR IS TAKEN UNDER THE DEVICE'S SUBMIT LOCK, which is what makes "exactly once"
        /// a fact rather than an assumption about how many threads call <c>IGpuDevice.Submit</c>. The seam nowhere
        /// says that is one thread, and V-W8 says recording is lock-free and per-list on any number of them, so an
        /// unserialised read-modify-write over the pair let two first-submits of one frame both carry the same
        /// wait semaphore. Two submits waiting on one binary semaphore is a HANG rather than an error.
        /// <para>
        /// Asserted by holding the lock and watching the take BLOCK, which is deterministic in the direction that
        /// matters: if the take is under the lock this can never pass early, and if it is not, it completes
        /// immediately and the test fails.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFrameSemaphorePair_IsTakenUnderTheSubmitLock()
        {
            var submitLock = new object();
            using var boundary = new VulkanPresentBoundary(
                new FakeVulkanSurfaceApi(), new FakeVulkanSwapchainApi(), FakeVulkanSurfaceApi.Handle,
                new VulkanExtent(1280, 720), true, VulkanAcquireMode.Semaphore, 3, submitLock, () => { },
                new FakeVulkanOrphanTarget(), new VulkanAcquireWaits());

            using var reached = new ManualResetEventSlim();
            VulkanFrameSemaphores taken = default;

            // A RAW THREAD RATHER THAN A TASK, because the assertion is that the take BLOCKS and every way of
            // waiting on a Task for that is a blocking task operation the xUnit analyzers reject outright.
            var worker = new Thread(() =>
            {
                reached.Set();
                taken = boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true);
            });

            lock (submitLock)
            {
                worker.Start();

                Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
                Assert.False(worker.Join(TimeSpan.FromMilliseconds(200)));
            }

            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.False(taken.IsEmpty);
            Assert.True(boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true).IsEmpty);
        }

        /// <summary>
        /// AND THE PAIR RIDES THE SUBMIT THAT RENDERED THE SWAPCHAIN IMAGE, NOT THE ONE THAT ARRIVED FIRST
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/557). The present waits on a semaphore the frame's
        /// submit signals, and that is only the right semaphore if the submit signalling it is the one that drew
        /// the backbuffer and restored it to <c>PRESENT_SRC_KHR</c> at <c>End</c>.
        ///
        /// <para><b>THE SHIPPED FRAME THAT BREAKS ARRIVAL ORDER IS THE OCEAN'S.</b> Its FFT producer submits and
        /// drains a list of its own before the scene renders, so under the old rule that list took the pair and
        /// the scene list submitted with none at all, leaving the present waiting on a semaphore signalled by a
        /// submission that never touched the image.</para>
        /// </summary>
        [Fact]
        public void ThePair_RidesTheFirstSubmitThatBoundTheSwapchainFramebuffer()
        {
            using VulkanPresentBoundary boundary = Boundary(out FakeVulkanSwapchainApi swapchains);

            // THE PRIMING SUBMIT: a list that never bound the swapchain framebuffer.
            Assert.True(boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: false).IsEmpty);

            // THE SCENE SUBMIT, which is the one that rendered the image.
            VulkanFrameSemaphores scene = boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true);
            Assert.False(scene.IsEmpty);

            boundary.Present();

            (ulong Swapchain, uint Image, ulong Wait) presented = Assert.Single(swapchains.Presents);
            Assert.Equal(scene.Signal, presented.Wait);
        }

        /// <summary>
        /// AND A FRAME WHOSE SUBMITS NEVER BOUND IT PRESENTS NOTHING
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/563). A frame that submitted work is not the same
        /// thing as a frame that rendered the backbuffer: both halves of the resting-layout ruling (the pass
        /// begin's discard and <c>End</c>'s restore) are recorded by the list that BINDS the framebuffer, so a
        /// frame with no such list leaves the image exactly as the acquire found it, which on a freshly created
        /// generation is <c>UNDEFINED</c> rather than <c>PRESENT_SRC_KHR</c>.
        /// </summary>
        [Fact]
        public void AFrameThatSubmittedWithoutBindingTheFramebuffer_PresentsNothingAndKeepsItsImage()
        {
            using VulkanPresentBoundary boundary = Boundary(out FakeVulkanSwapchainApi swapchains);

            boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: false);
            boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: false);
            boundary.Present();

            Assert.Empty(swapchains.Presents);
            Assert.True(boundary.HasImage);
            Assert.Equal(1L, boundary.FramesBegun);
        }

        // A boundary on the present harness's fakes, in the shipped semaphore mode. The whole present state
        // machine belongs to VulkanPresentBoundaryTests. What is here is the ROUTING of the frame's pair, which
        // is a submit-path question and is asserted where the other submit-path facts are.
        static VulkanPresentBoundary Boundary(out FakeVulkanSwapchainApi swapchains)
        {
            swapchains = new FakeVulkanSwapchainApi();

            return new VulkanPresentBoundary(
                new FakeVulkanSurfaceApi(), swapchains, FakeVulkanSurfaceApi.Handle,
                new VulkanExtent(1280, 720), true, VulkanAcquireMode.Semaphore, 3, new object(), () => { },
                new FakeVulkanOrphanTarget(), new VulkanAcquireWaits());
        }
    }
}
