using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu.Internal;
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
    public sealed partial class VulkanPresentBoundaryTests
    {
        // ---- the harness ------------------------------------------------------------------------------------

        sealed class Rig : IDisposable
        {
            internal FakeVulkanSurfaceApi Surfaces { get; } = new();
            internal FakeVulkanSwapchainApi Swapchains { get; } = new();
            internal FakeVulkanOrphanTarget Orphan { get; } = new();
            internal WaitAccumulator Waits { get; } = new();
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
                VulkanFrameSemaphores pair = Boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true);
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

            VulkanFrameSemaphores first = rig.Boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true);
            VulkanFrameSemaphores second = rig.Boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true);

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

        // ---- the checked present result ---------------------------------------------------------------------

        /// <summary>
        /// <c>vkQueuePresentKHR</c>'s RESULT IS CHECKED (V-W7). The incumbent ignored it entirely, so it could never
        /// learn that the surface it presented to changed underneath it.
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
