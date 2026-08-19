using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The RECREATION half of the present boundary's coverage: the OUT_OF_DATE boundary end to end, the resize and
    /// present-mode changes that queue a recreate, the retirement hazard around the old swapchain, and the
    /// recreate's own failures, which must never escape <c>Present</c>. The harness (<c>Rig</c>) and the ordinary
    /// per-frame boundary stay in <c>VulkanPresentBoundaryTests.cs</c>, and the same partial class carries both, so
    /// these still run as one test class against one <c>Rig</c>.
    /// <para>
    /// Split out at 777 of the 800-line cap (#559), on the section boundary the file already drew, and mirroring
    /// the production split into <c>VulkanPresentBoundary.Recreate.cs</c>. MV9 still holds: this is the only
    /// automated coverage the present path has on any leg, so it is the file that should be free to grow.
    /// </para>
    /// </summary>
    public sealed partial class VulkanPresentBoundaryTests
    {
        // ---- the OUT_OF_DATE boundary in full ---------------------------------------------------------------

        /// <summary>
        /// AN ACQUIRE THAT CAME BACK <c>OUT_OF_DATE</c> RECREATES AT THAT SAME BOUNDARY AND TAKES ONE FRESH
        /// ACQUIRE BEFORE RETURNING. Queuing the recreate to a later boundary would leave the semaphore handed to
        /// the failed acquire either reused while pending (the reuse bug) or destroyed while pending (undefined
        /// behaviour), and returning without re-acquiring would force the record path to grow a second "no image
        /// yet" state.
        /// </summary>
        [Fact]
        public void AnOutOfDateAcquireRecreatesAndReAcquiresAtTheSameBoundary()
        {
            using var rig = new Rig();
            ulong firstChain = rig.Swapchains.LiveSwapchains[0];

            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.OutOfDate);
            rig.Frame();

            Assert.Single(rig.Swapchains.LiveSwapchains);
            Assert.NotEqual(firstChain, rig.Swapchains.LiveSwapchains[0]);
            Assert.True(rig.Boundary.HasImage);
            Assert.False(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// THE RETRY IS ONE. A second failure returns with the pending flag still set and tries again at the next
        /// boundary, so a surface mid-resize cannot spin the boundary.
        /// </summary>
        [Fact]
        public void TheRetryIsExactlyOne()
        {
            using var rig = new Rig();

            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.OutOfDate, VulkanPresentOutcome.OutOfDate);
            rig.Frame();

            int recreates = rig.Swapchains.Events.Count(e => e.StartsWith("CreateSwapchain", StringComparison.Ordinal));

            // One at construction, one for the recreate the first failure triggered, and NOT a second recreate
            // for the retry's own failure. The boundary returns instead, with the flag set for the next one.
            Assert.Equal(2, recreates);
            Assert.False(rig.Boundary.HasImage);
            Assert.True(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// AN IMAGELESS FRAME BINDS THE ORPHAN TARGET, records and completes exactly like any other frame, and
        /// only its PRESENT is skipped. Leaving the framebuffer pointing at views the recreate destroyed is a
        /// use-after-free no CI leg in this fleet can see, and making <c>SetFramebuffer</c> illegal for one frame
        /// would put a second "no image yet" state in the recording path.
        /// </summary>
        [Fact]
        public void AnImagelessFrameBindsTheOrphanTargetAndSkipsOnlyItsPresent()
        {
            using var rig = new Rig();

            // Both the recreate's own creation and the retry's fail, which is the double-failure path.
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.OutOfDate);

            rig.Frame();

            Assert.False(rig.Boundary.HasImage);
            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.Equal(1, rig.Orphan.Created);
            Assert.Equal(new VulkanExtent(1, 1), rig.Orphan.Ensured[0].Extent);
            Assert.NotNull(rig.Boundary.Framebuffer);
            Assert.Equal(1u, rig.Boundary.Framebuffer!.Width);

            // The present of the frame BEFORE the failure still happened. It is the next one that is skipped.
            Assert.Single(rig.Swapchains.Presents);
        }

        /// <summary>
        /// A SKIPPED PRESENT IS NOT A SKIPPED FRAME. The device opened it, the recording and the submit really
        /// happened, and <c>FramesBegun</c> is the denominator every per-frame figure is divided by. Leaving them
        /// out would understate per-frame costs on exactly the frames that were unusual.
        /// </summary>
        [Fact]
        public void ASkippedPresentStillCountsIntoFramesBegun()
        {
            using var rig = new Rig();
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.OutOfDate);

            rig.Frame();
            rig.Frame();
            rig.Frame();

            Assert.Equal(3L, rig.Boundary.FramesBegun);
            Assert.True(rig.Boundary.IsOrphanBound);
        }

        /// <summary>The orphan target goes only once a real image is bound again, not at the recreate that needed
        /// it. Releasing it at the recreate would destroy the image the framebuffer was pointing at on exactly the
        /// path that needed one.</summary>
        [Fact]
        public void TheOrphanIsReleasedOnlyWhenARealImageComesBack()
        {
            using var rig = new Rig();
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.OutOfDate);
            rig.Frame();

            Assert.Equal(0, rig.Orphan.Released);

            // The window comes back, and the boundary that follows finds a creatable surface again.
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Desktop(800, 600);
            rig.Frame();

            Assert.False(rig.Boundary.IsOrphanBound);
            Assert.Equal(1, rig.Orphan.Released);
            Assert.Equal(800u, rig.Boundary.Framebuffer!.Width);
        }

        /// <summary>
        /// A RECREATE UNDERNEATH A FRAME THAT DREW NOTHING ABANDONS THE HELD IMAGE, and this is the path every
        /// other minimise test misses because <see cref="Rig.Frame"/> takes the semaphores first. An undrawn frame
        /// KEEPS its image, so a zero-extent recreate at that same boundary used to retire the generation while
        /// <c>_heldImage</c> still named one of its images and the frame pair still named a render-finished
        /// semaphore the retirement had just destroyed. The next submit would have waited on that destroyed
        /// semaphore.
        /// </summary>
        [Fact]
        public void ARecreateUnderAFrameThatDrewNothingAbandonsTheHeldImage()
        {
            using var rig = new Rig();

            // NOT rig.Frame(): the frame opens and closes without a submit, so nothing takes the pair, which is
            // exactly the state that made the image be kept rather than presented.
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Boundary.QueueResize(800, 600);
            rig.Boundary.Present();

            Assert.False(rig.Boundary.HasImage);
            Assert.True(rig.Boundary.IsOrphanBound);

            // THE ASSERTION THAT MATTERS: the next frame's first submit is handed nothing, rather than a pair
            // naming a VkSemaphore the retirement destroyed.
            Assert.True(rig.Boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true).IsEmpty);
        }

        /// <summary>
        /// AND IT LEAVES A ROUTE BACK. The imageless backstop in the boundary is guarded on the held image, so a
        /// recreate that left one set produced a boundary with no generation, no pending flag and nothing that
        /// could ever queue one: the window stayed on the orphan target for the rest of the run with no error
        /// anywhere.
        /// </summary>
        [Fact]
        public void AFrameThatDrewNothingAcrossAMinimiseStillFindsItsWayBack()
        {
            using var rig = new Rig();

            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Boundary.QueueResize(800, 600);
            rig.Boundary.Present();

            Assert.True(rig.Boundary.HasPendingRecreate);

            rig.Surfaces.Report = FakeVulkanSurfaceApi.Desktop(800, 600);
            rig.Boundary.Present();

            Assert.True(rig.Boundary.HasImage);
            Assert.False(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true).IsEmpty);
        }

        // ---- resize, present mode, and the retirement hazard -------------------------------------------------

        /// <summary>A queued resize is applied at the next boundary and coalesced to the LAST request, which is
        /// what makes a drag-resize cost one recreate rather than thirty.</summary>
        [Fact]
        public void AResizeIsCoalescedAndAppliedAtTheNextBoundary()
        {
            using var rig = new Rig();
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Desktop() with
            {
                CurrentExtent = VulkanExtent.SurfaceDecidesNothing,
            };

            rig.Boundary.QueueResize(900, 500);
            rig.Boundary.QueueResize(1000, 600);
            rig.Boundary.QueueResize(1100, 700);

            Assert.True(rig.Boundary.HasPendingRecreate);

            rig.Frame();

            Assert.Equal(new VulkanExtent(1100, 700), rig.Swapchains.LastSpec.Extent);
            Assert.Single(rig.Swapchains.LiveSwapchains);
            Assert.False(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// A RUNTIME VSYNC CHANGE IS A FULL RECREATE HERE, unlike on Direct3D 11 where vsync is an argument of the
        /// present call. Vulkan cannot change a swapchain's present mode in place, so the seam's "no recreate"
        /// wording, which describes Metal, gains a Vulkan sentence.
        /// </summary>
        [Fact]
        public void ARuntimeVsyncChangeQueuesARecreateAndChangesThePresentMode()
        {
            using var rig = new Rig(vsync: true);

            Assert.Equal(Silk.NET.Vulkan.PresentModeKHR.FifoRelaxedKhr, rig.Swapchains.LastSpec.PresentMode);

            rig.Boundary.SyncToVerticalBlank = false;
            Assert.True(rig.Boundary.HasPendingRecreate);

            rig.Frame();

            Assert.Equal(Silk.NET.Vulkan.PresentModeKHR.MailboxKhr, rig.Swapchains.LastSpec.PresentMode);
        }

        /// <summary>Setting vsync to the value it already has queues nothing, so a settings screen writing the
        /// same value every frame does not recreate the swapchain every frame.</summary>
        [Fact]
        public void SettingVsyncToWhatItAlreadyIsQueuesNothing()
        {
            using var rig = new Rig(vsync: true);

            rig.Boundary.SyncToVerticalBlank = true;

            Assert.False(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// THE DRAIN IS UNCONDITIONAL AND RUNS BEFORE EVERY RETIREMENT (V-W6). A binary semaphore an acquire or a
        /// submit signalled that nothing waited on is left PENDING, there is no way to ask one whether it is, and
        /// destroying a pending semaphore is undefined behaviour drivers mostly tolerate until they do not. A
        /// drained queue is the only state in which the answer is knowable.
        /// </summary>
        [Fact]
        public void EveryRecreateDrainsFirst()
        {
            using var rig = new Rig();

            Assert.Equal(1, rig.Drains);

            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.Equal(2, rig.Drains);
        }

        /// <summary>The new swapchain is created with the old one as <c>oldSwapchain</c>, which lets the driver
        /// reuse presentable images rather than tearing the whole chain down, and the old handle is still
        /// destroyed afterwards.</summary>
        [Fact]
        public void ARecreatePassesTheOldSwapchainAndThenDestroysIt()
        {
            using var rig = new Rig();
            ulong first = rig.Swapchains.LiveSwapchains[0];

            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.Equal(first, rig.Swapchains.LastOldSwapchain);
            Assert.DoesNotContain(first, rig.Swapchains.LiveSwapchains);
            Assert.Single(rig.Swapchains.LiveSwapchains);
        }

        /// <summary>
        /// THE ORDERING RULE THAT MAKES A USE-AFTER-FREE UNREACHABLE: the new views are published onto the
        /// framebuffer BEFORE the old ones are destroyed. Recording against views a recreate destroyed is a
        /// use-after-free no CI leg in this fleet can see, so it is designed out rather than tested for at the
        /// driver.
        /// </summary>
        [Fact]
        public void TheNewViewsArePublishedBeforeTheOldOnesAreDestroyed()
        {
            using var rig = new Rig();
            rig.Swapchains.Events.Clear();

            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            int firstNewView = rig.Swapchains.Events.FindIndex(
                e => e.StartsWith("CreateImageView", StringComparison.Ordinal));
            int firstDestroy = rig.Swapchains.Events.FindIndex(
                e => e.StartsWith("DestroyImageView", StringComparison.Ordinal));

            Assert.True(firstNewView >= 0 && firstDestroy > firstNewView,
                "every new view must exist before any old one is destroyed");
        }

        /// <summary>
        /// A CREATION THAT FAILED AT A CREATABLE EXTENT RETIRES THE OLD GENERATION AND FALLS TO THE ORPHAN, and
        /// keeping the old one is exactly what it must not do. <c>vkCreateSwapchainKHR</c> retires the swapchain
        /// handed to it as <c>oldSwapchain</c> as an effect of the CALL rather than of the call succeeding, and a
        /// retired swapchain may already have had the images nothing acquired freed underneath it. So keeping it
        /// does not keep the framebuffer pointing at live views, it keeps it pointing at images the driver may
        /// have taken back.
        /// </summary>
        [Fact]
        public void AFailedCreationRetiresTheOldSwapchainAndBindsTheOrphan()
        {
            using var rig = new Rig();
            ulong first = rig.Swapchains.LiveSwapchains[0];

            // BOTH the recreate's own creation and the one retry's fail, because a single failure is repaired by
            // the retry at this same boundary and leaves nothing to look at.
            rig.Swapchains.FailNextCreate = "VK_ERROR_OUT_OF_DEVICE_MEMORY";
            rig.Swapchains.FailCreateCount = 2;
            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.DoesNotContain(first, rig.Swapchains.LiveSwapchains);
            Assert.Empty(rig.Swapchains.LiveSwapchains);
            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.HasImage);
            Assert.True(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// AND EVERY ATTEMPT AFTER A FAILED ONE PASSES ZERO AS <c>oldSwapchain</c>. Re-passing the handle a failed
        /// creation already retired is a specification violation
        /// (<c>VUID-VkSwapchainCreateInfoKHR-oldSwapchain-01933</c>), and it is the one a boundary that kept its
        /// old generation walks into at the very next attempt, which here is the retry inside the same boundary.
        /// </summary>
        [Fact]
        public void TheAttemptAfterAFailedCreationPassesNoOldSwapchain()
        {
            using var rig = new Rig();
            ulong first = rig.Swapchains.LiveSwapchains[0];

            rig.Swapchains.FailNextCreate = "VK_ERROR_OUT_OF_DEVICE_MEMORY";
            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.Equal(0UL, rig.Swapchains.LastOldSwapchain);
            Assert.DoesNotContain(first, rig.Swapchains.LiveSwapchains);
            Assert.Single(rig.Swapchains.LiveSwapchains);
            Assert.True(rig.Boundary.HasImage);
            Assert.False(rig.Boundary.IsOrphanBound);
        }

        // ---- the recreate's own failures, which must not escape Present ---------------------------------------

        /// <summary>
        /// A SURFACE QUERY THAT FAILED FALLS TO THE ORPHAN RATHER THAN THROWING. The boundary never throws and
        /// never reports failure upward, and the capability query used to go through
        /// <c>VulkanResultCodes.Require</c>, so an out-of-memory result there propagated straight out of
        /// <c>IGpuDevice.Present</c> into a frame loop with no answer for one.
        /// </summary>
        [Fact]
        public void ASurfaceQueryThatFailedBindsTheOrphanRatherThanThrowing()
        {
            using var rig = new Rig();

            rig.Surfaces.QueryOutcome = VulkanPresentOutcome.Failed;
            rig.Boundary.QueueResize(800, 600);

            rig.Frame();

            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.HasImage);
            Assert.Empty(rig.Swapchains.LiveSwapchains);
            Assert.True(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// A SURFACE LOST AT THE QUERY LATCHES EXACTLY AS ONE LOST AT THE ACQUIRE DOES. It is the FIRST place a
        /// window that died under a running frame loop shows up, because the capability re-read is the first thing
        /// a recreate does, and it was the one path with no surface-lost handling at all.
        /// </summary>
        [Fact]
        public void ASurfaceLostAtTheQueryStopsTheBoundaryRatherThanSpinning()
        {
            using var rig = new Rig();

            rig.Surfaces.QueryOutcome = VulkanPresentOutcome.SurfaceLost;
            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.Empty(rig.Swapchains.LiveSwapchains);

            int queries = rig.Surfaces.Queries;
            rig.Frame();
            rig.Frame();

            // Latched: the boundary does not re-read a surface it was told is gone, and creates nothing.
            Assert.Equal(queries, rig.Surfaces.Queries);
            Assert.Empty(rig.Swapchains.LiveSwapchains);
        }

        /// <summary>
        /// A SURFACE REPORTING NO FORMATS IS A FAILED FORMAT QUERY, not a surface with none, and it used to reach
        /// <c>ChooseFormat</c>'s <c>ArgumentException</c>. The seam answers an empty list on ANY failed
        /// <c>vkGetPhysicalDeviceSurfaceFormatsKHR</c>, and the specification requires a presentable surface to
        /// report at least one.
        /// </summary>
        [Fact]
        public void ASurfaceWithNoFormatsBindsTheOrphanRatherThanThrowing()
        {
            using var rig = new Rig();

            rig.Surfaces.Report = FakeVulkanSurfaceApi.NoFormats();
            rig.Boundary.QueueResize(800, 600);

            rig.Frame();

            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.HasImage);
            Assert.True(rig.Boundary.HasPendingRecreate);
        }

        /// <summary>
        /// A FORMAT THE SEAM CANNOT NAME IS REACHABLE RATHER THAN UNREACHABLE BY CONSTRUCTION, which is why its
        /// <c>NotSupportedException</c> is caught at the recreate instead of being left to escape: the format
        /// ladder's last arm takes the surface's FIRST format when the surface offers no BGRA8 at all, and that
        /// can be any format the surface happens to have.
        /// </summary>
        [Fact]
        public void AFormatTheSeamCannotNameBindsTheOrphanRatherThanThrowing()
        {
            using var rig = new Rig();

            rig.Surfaces.Report = FakeVulkanSurfaceApi.UnnameableFormat();
            rig.Boundary.QueueResize(800, 600);

            rig.Frame();

            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.HasImage);
            Assert.Empty(rig.Swapchains.LiveSwapchains);
        }

        /// <summary>
        /// THE FIRST GENERATION STILL REFUSES, on all three, because that is the device CONSTRUCTOR rather than a
        /// frame boundary. A windowed device that cannot describe its own surface has nothing to hand back, which
        /// is the same posture the failed-first-swapchain path already takes.
        /// </summary>
        [Fact]
        public void AFirstGenerationThatCannotReadItsSurfaceRefuses()
        {
            var surfaces = new FakeVulkanSurfaceApi { QueryOutcome = VulkanPresentOutcome.Failed };

            Assert.Throws<InvalidOperationException>(() => new VulkanPresentBoundary(
                surfaces, new FakeVulkanSwapchainApi(), FakeVulkanSurfaceApi.Handle, new VulkanExtent(1280, 720),
                true, VulkanAcquireMode.Semaphore, 3, new object(), () => { }, new FakeVulkanOrphanTarget(),
                new WaitAccumulator()));
        }
    }
}
