using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PRESENT BOUNDARY, AND DECISION V-W4's FOUR QUESTIONS, driven through the surface and swapchain seams
    /// with no Vulkan loader and no window.
    /// <para>
    /// <b>THIS FILE IS THE ONLY AUTOMATED COVERAGE THIS ROW WILL EVER HAVE (MV9).</b> A headless Vulkan device
    /// enables no surface extension at all, which is what lets the golden suite run on a machine with no display
    /// server and is also why not one line of the present path runs in CI on any leg, ever. A green golden leg is
    /// not evidence about anything here. What IS decidable without a driver is the ORDERING: when the recreate
    /// runs, how many retries follow it, what an imageless frame binds, what is destroyed after what, and when the
    /// acquire-wait counter ticks. All of that is asserted below.
    /// </para>
    /// </summary>
    public sealed class VulkanPresentBoundaryTests
    {
        // ---- the harness ------------------------------------------------------------------------------------

        sealed class Rig : IDisposable
        {
            internal FakeVulkanSurfaceApi Surfaces { get; } = new();
            internal FakeVulkanSwapchainApi Swapchains { get; } = new();
            internal FakeVulkanOrphanTarget Orphan { get; } = new();
            internal VulkanAcquireWaits Waits { get; } = new();
            internal int Drains { get; private set; }
            internal VulkanPresentBoundary Boundary { get; }

            internal Rig(VulkanAcquireMode mode = VulkanAcquireMode.Semaphore, bool vsync = true,
                int framesInFlight = 3)
            {
                Boundary = new VulkanPresentBoundary(
                    Surfaces, Swapchains, FakeVulkanSurfaceApi.Handle, new VulkanExtent(1280, 720), vsync, mode,
                    framesInFlight, new object(), () => Drains++, Orphan, Waits);
            }

            /// <summary>One whole frame: a submit takes the frame's semaphore pair, then the boundary runs. The
            /// take is what a real frame's first submit does, and without it the boundary correctly refuses to
            /// present an image nothing rendered into.</summary>
            internal VulkanFrameSemaphores Frame()
            {
                VulkanFrameSemaphores pair = Boundary.TakeFrameSemaphores();
                Boundary.Present();
                return pair;
            }

            public void Dispose() => Boundary.Dispose();
        }

        // ---- construction -----------------------------------------------------------------------------------

        /// <summary>
        /// THE FIRST SWAPCHAIN AND THE FIRST ACQUIRE BOTH HAPPEN AT CONSTRUCTION, which is what makes
        /// <c>SwapchainFramebuffer</c> valid from the moment the device exists rather than from the first present,
        /// and what makes the image index known before the first recording starts.
        /// </summary>
        [Fact]
        public void ConstructionCreatesASwapchainAndHoldsAnImage()
        {
            using var rig = new Rig();

            Assert.Single(rig.Swapchains.LiveSwapchains);
            Assert.True(rig.Boundary.HasImage);
            Assert.NotNull(rig.Boundary.Framebuffer);
            Assert.Equal(1280u, rig.Boundary.Framebuffer!.Width);
            Assert.False(rig.Boundary.IsOrphanBound);
            Assert.Equal(0, rig.Orphan.Created);
        }

        /// <summary>One view and one render-finished semaphore per image, and the acquire ring on top of
        /// them.</summary>
        [Fact]
        public void EachImageGetsAViewAndARenderFinishedSemaphore()
        {
            using var rig = new Rig();

            Assert.Equal(3, rig.Swapchains.LiveViews.Count);
            Assert.Equal(3 + VulkanAcquireRing.CapacityFor(3, 3), rig.Swapchains.LiveSemaphores.Count);
        }

        // ---- the ordinary boundary --------------------------------------------------------------------------

        /// <summary>The steady state: present the frame just submitted, then acquire for the next one.</summary>
        [Fact]
        public void TheBoundaryPresentsThenAcquires()
        {
            using var rig = new Rig();

            VulkanFrameSemaphores pair = rig.Frame();

            Assert.Single(rig.Swapchains.Presents);
            Assert.Equal(pair.Signal, rig.Swapchains.Presents[0].Wait);
            Assert.True(rig.Boundary.HasImage);
            Assert.Equal(1L, rig.Boundary.FramesBegun);
        }

        /// <summary>
        /// THE PAIR IS TAKEN EXACTLY ONCE PER FRAME. A binary semaphore may be waited once per signal, so a second
        /// submit in one frame carrying the same wait semaphore waits for a signal nothing will ever produce,
        /// which is a hang rather than an error.
        /// </summary>
        [Fact]
        public void TheFrameSemaphorePairIsTakenExactlyOnce()
        {
            using var rig = new Rig();

            VulkanFrameSemaphores first = rig.Boundary.TakeFrameSemaphores();
            VulkanFrameSemaphores second = rig.Boundary.TakeFrameSemaphores();

            Assert.False(first.IsEmpty);
            Assert.True(second.IsEmpty);
        }

        /// <summary>
        /// A FRAME NOTHING RENDERED INTO KEEPS ITS IMAGE AND ACQUIRES NOTHING. Presenting it would need a
        /// render-finished semaphore nothing signalled, which hangs the presentation engine, or no wait semaphore
        /// at all, which leaves the acquire semaphore pending for a ring slot that comes round again.
        /// </summary>
        [Fact]
        public void AFrameWithNoSubmitKeepsItsImageAndDoesNotAcquireAgain()
        {
            using var rig = new Rig();
            int acquiresBefore = rig.Swapchains.AcquireSemaphores.Count;

            rig.Boundary.Present();

            Assert.Empty(rig.Swapchains.Presents);
            Assert.Equal(acquiresBefore, rig.Swapchains.AcquireSemaphores.Count);
            Assert.True(rig.Boundary.HasImage);
            Assert.Equal(1L, rig.Boundary.FramesBegun);
        }

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
        /// A CREATION THAT FAILED AT A CREATABLE EXTENT KEEPS THE OLD GENERATION AND NOTHING IS DESTROYED. The
        /// framebuffer still points at views that are still alive, the pending flag goes back, and the next
        /// boundary tries again. That is the only response that leaves no window in which the wrapper names a dead
        /// view.
        /// </summary>
        [Fact]
        public void AFailedCreationKeepsTheOldSwapchain()
        {
            using var rig = new Rig();
            ulong first = rig.Swapchains.LiveSwapchains[0];

            rig.Swapchains.FailNextCreate = "VK_ERROR_OUT_OF_DEVICE_MEMORY";
            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.Contains(first, rig.Swapchains.LiveSwapchains);
            Assert.True(rig.Boundary.HasPendingRecreate);
            Assert.False(rig.Boundary.IsOrphanBound);
        }

        // ---- the checked present result ---------------------------------------------------------------------

        /// <summary>
        /// <c>vkQueuePresentKHR</c>'s RESULT IS CHECKED (V-W7). The incumbent ignores it entirely, so it can never
        /// learn that the surface it presents to changed underneath it.
        /// </summary>
        [Fact]
        public void AnOutOfDatePresentQueuesARecreate()
        {
            using var rig = new Rig();

            rig.Swapchains.ScriptPresents(VulkanPresentOutcome.OutOfDate);
            rig.Frame();

            // The recreate ran at this same boundary, between the present and the acquire.
            Assert.Equal(2, rig.Drains);
            Assert.True(rig.Boundary.HasImage);
        }

        /// <summary>
        /// <c>VK_SUBOPTIMAL_KHR</c> IS A SUCCESS, which is the result that catches people out. An acquire under it
        /// really did hold an image, so the image is kept and the recreate is queued for the NEXT boundary rather
        /// than run underneath a frame that is about to be recorded.
        /// </summary>
        [Fact]
        public void ASuboptimalAcquireKeepsItsImageAndQueuesTheRecreate()
        {
            using var rig = new Rig();
            int drainsBefore = rig.Drains;

            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.Suboptimal);
            rig.Frame();

            Assert.True(rig.Boundary.HasImage);
            Assert.True(rig.Boundary.HasPendingRecreate);
            Assert.Equal(drainsBefore, rig.Drains);
        }

        /// <summary>A lost surface stops the boundary presenting rather than spinning on a recreate that will fail
        /// the same way. Frames still record, submit and complete into the orphan target.</summary>
        [Fact]
        public void ALostSurfaceStopsTheBoundaryRatherThanSpinning()
        {
            using var rig = new Rig();

            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.SurfaceLost);
            rig.Frame();

            int creations = rig.Swapchains.Events.Count(
                e => e.StartsWith("CreateSwapchain", StringComparison.Ordinal));

            rig.Frame();
            rig.Frame();

            Assert.Equal(creations, rig.Swapchains.Events.Count(
                e => e.StartsWith("CreateSwapchain", StringComparison.Ordinal)));
        }

        // ---- the acquire-wait counters ----------------------------------------------------------------------

        /// <summary>
        /// THE COUNTER TICKS ONLY WHEN THE CPU ACTUALLY BLOCKED, established by a zero-timeout PROBE before the
        /// blocking call. A counter that ticked on a non-wait could never answer "was the CPU ever blocked here"
        /// with a zero, which is the only answer MV2's exit criterion accepts.
        /// </summary>
        [Fact]
        public void AnAcquireThatDidNotBlockIsNotCounted()
        {
            using var rig = new Rig();

            rig.Frame();
            rig.Frame();

            Assert.Equal(0L, rig.Waits.Totals.Count);
            Assert.Equal(0, rig.Swapchains.BlockingAcquires);
        }

        /// <summary>A probe that came back NOT_READY is followed by a blocking call, and THAT is what the counter
        /// records. It acquired nothing and signalled nothing, so reusing the same semaphore for the blocking call
        /// is legal.</summary>
        [Fact]
        public void AProbeThatFoundNoImageIsFollowedByACountedBlock()
        {
            using var rig = new Rig();

            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.NotReady, VulkanPresentOutcome.Success);
            rig.Frame();

            Assert.Equal(1L, rig.Waits.Totals.Count);
            Assert.Equal(1, rig.Swapchains.BlockingAcquires);
            Assert.True(rig.Boundary.HasImage);
        }

        // ---- the kill switch --------------------------------------------------------------------------------

        /// <summary>
        /// <c>KE_VULKAN_ACQUIRE=stall</c> RESTORES THE INCUMBENT'S SHAPE EXACTLY: a blocking acquire with no
        /// semaphore, a submit carrying nothing, and a present with NO WAIT SEMAPHORE. That last part is the
        /// specification violation a validation layer rejects, which is why the mode and the validation knob are
        /// documented as not usable together.
        /// </summary>
        [Fact]
        public void TheStallModeBlocksAndPresentsWithNoWaitSemaphore()
        {
            using var rig = new Rig(VulkanAcquireMode.Stall);

            VulkanFrameSemaphores pair = rig.Frame();

            Assert.True(pair.IsEmpty);
            Assert.Single(rig.Swapchains.Presents);
            Assert.Equal(0UL, rig.Swapchains.Presents[0].Wait);
        }

        /// <summary>Every stall-mode acquire is counted, because the call is a blocking wait by construction. That
        /// is what makes the stall side of MV2's A/B read as a substantial fraction of the frame interval while
        /// the semaphore side reads near zero.</summary>
        [Fact]
        public void EveryStallModeAcquireIsCounted()
        {
            using var rig = new Rig(VulkanAcquireMode.Stall);

            rig.Frame();
            rig.Frame();

            Assert.Equal(3L, rig.Waits.Totals.Count);
        }

        /// <summary>The stall mode builds no acquire ring at all, because it hands no semaphore to any
        /// acquire.</summary>
        [Fact]
        public void TheStallModeBuildsNoAcquireRing()
        {
            using var rig = new Rig(VulkanAcquireMode.Stall);

            Assert.Equal(0, rig.Boundary.AcquireRing.Capacity);
            Assert.All(rig.Swapchains.AcquireSemaphores, handle => Assert.Equal(0UL, handle));
        }

        // ---- identity and teardown --------------------------------------------------------------------------

        /// <summary>
        /// THE FRAMEBUFFER'S IDENTITY SURVIVES EVERY RECREATE (V-W5), which matters more here than on the other
        /// native backend because every image view object is replaced. Its size and its attachment move, and the
        /// object a consumer cached does not.
        /// </summary>
        [Fact]
        public void TheFramebufferIdentitySurvivesEveryRecreate()
        {
            using var rig = new Rig();
            VulkanSwapchainFramebuffer framebuffer = rig.Boundary.Framebuffer!;
            ulong id = framebuffer.Id;

            rig.Boundary.QueueResize(800, 600);
            rig.Frame();
            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Swapchains.ScriptAcquires(VulkanPresentOutcome.OutOfDate);
            rig.Frame();

            Assert.Same(framebuffer, rig.Boundary.Framebuffer);
            Assert.Equal(id, rig.Boundary.Framebuffer!.Id);
        }

        /// <summary>Teardown drains, destroys the generation and the ring, and destroys the surface. Nothing is
        /// left alive on either seam.</summary>
        [Fact]
        public void TeardownLeavesNothingAlive()
        {
            var rig = new Rig();
            int drainsBefore = rig.Drains;

            rig.Dispose();

            Assert.Equal(drainsBefore + 1, rig.Drains);
            Assert.Empty(rig.Swapchains.LiveSwapchains);
            Assert.Empty(rig.Swapchains.LiveViews);
            Assert.Empty(rig.Swapchains.LiveSemaphores);
            Assert.Equal(new[] { FakeVulkanSurfaceApi.Handle }, rig.Surfaces.Destroyed);
        }

        /// <summary>Every recreate RE-READS the surface, because a window that changed can change any of what it
        /// reports: its extent, its transform, its formats and its present modes.</summary>
        [Fact]
        public void EveryRecreateReReadsTheSurface()
        {
            using var rig = new Rig();
            int queriesAfterConstruction = rig.Surfaces.Queries;

            rig.Boundary.QueueResize(800, 600);
            rig.Frame();

            Assert.Equal(queriesAfterConstruction + 1, rig.Surfaces.Queries);
        }
    }
}
