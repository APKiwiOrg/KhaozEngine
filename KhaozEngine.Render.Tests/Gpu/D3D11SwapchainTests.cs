using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The native Direct3D 11 swapchain's engine half: the present boundary, the queued and coalesced resize of
    /// decision W3, and the stable framebuffer identity of decision W2 (work-breakdown row 14 of
    /// <c>docs/design/D3D11-NATIVE-BACKEND-DESIGN-2026-08-02.md</c>).
    /// <para>
    /// Every test here is a plain <c>[Fact]</c> that runs on macOS and Linux, which is the whole reason
    /// <see cref="ID3D11SwapchainSurface"/> exists: the swapchain is the one area of this backend with no
    /// automated coverage anywhere on the real path (the goldens are headless, the WARP leg never presents), so
    /// everything that CAN be checked without a window has to be, and it has to be checked against the shipped
    /// logic rather than a copy of it.
    /// </para>
    /// <para>
    /// THE CALLER IS THE DEVICE, which builds one on the windowed path and none when headless, and drives the
    /// present boundary through it. Everything asserted here is still device-free, over a fake surface.
    /// </para>
    /// </summary>
    public sealed class D3D11SwapchainTests
    {
        // ---- construction and the framebuffer -------------------------------------------------------------

        /// <summary>
        /// The framebuffer is valid from the moment the swapchain exists rather than from the first present, so a
        /// consumer that reads <c>SwapchainFramebuffer</c> before any frame has run gets a real target.
        /// </summary>
        [Fact]
        public void Construction_PublishesTheFirstAttachments()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);

            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            Assert.Equal(new[] { "CreateAttachments 1280x720" }, surface.Trace);
            Assert.Equal(1280u, swapchain.Framebuffer.Width);
            Assert.Equal(720u, swapchain.Framebuffer.Height);
            Assert.Same(surface.LastRenderTargetView, swapchain.Framebuffer.RenderTargetView);
            Assert.Equal(1UL, swapchain.Framebuffer.Generation);
        }

        /// <summary>
        /// <see cref="IGpuFramebuffer.Outputs"/> is fixed at construction, and that is what keeps every pipeline
        /// built against the swapchain valid across every resize: decision W1 pins the format to
        /// <c>B8G8R8A8_UNorm</c> and the sample count to 1, so a resize changes the size and nothing a pipeline
        /// was validated against.
        /// </summary>
        [Fact]
        public void Outputs_CarryTheSurfaceFormatsAndOneSample_AndSurviveAResize()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720, GpuPixelFormat.D32FloatS8UInt);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            GpuOutputDescription before = swapchain.Framebuffer.Outputs;

            swapchain.QueueResize(1600, 900);
            swapchain.ApplyPendingResize();

            GpuOutputDescription after = swapchain.Framebuffer.Outputs;
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, Assert.Single(after.Colour));
            Assert.Equal(GpuPixelFormat.D32FloatS8UInt, after.Depth);
            Assert.Equal(1, after.SampleCount);
            Assert.Equal(before.Depth, after.Depth);
            Assert.Equal(before.SampleCount, after.SampleCount);
        }

        /// <summary>
        /// DECISION W2, AND THE POINT OF THE WHOLE TYPE: the framebuffer is the SAME OBJECT across a resize, and
        /// the views underneath it are different objects.
        /// <para>
        /// The incumbent disposes the depth texture and the whole framebuffer and builds a new one, which is why
        /// <c>VeldridGpuDevice.ResizeSwapchain</c> re-wraps only on a reference change, a workaround whose comment
        /// names the Windows black screen after going fullscreen, maximising or drag-resizing. Owning the wrapper
        /// is what deletes that workaround's reason to exist.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFramebufferIdentityNeverChanges_AndTheViewsAreSwappedUnderneath()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720, GpuPixelFormat.D32FloatS8UInt);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            IGpuFramebuffer framebuffer = swapchain.Framebuffer;
            object renderTargetBefore = swapchain.Framebuffer.RenderTargetView;
            object? depthBefore = swapchain.Framebuffer.DepthStencilView;

            swapchain.QueueResize(1600, 900);
            swapchain.ApplyPendingResize();

            Assert.Same(framebuffer, swapchain.Framebuffer);
            Assert.NotSame(renderTargetBefore, swapchain.Framebuffer.RenderTargetView);
            Assert.NotSame(depthBefore, swapchain.Framebuffer.DepthStencilView);
            Assert.Same(surface.LastRenderTargetView, swapchain.Framebuffer.RenderTargetView);
            Assert.Same(surface.LastDepthStencilView, swapchain.Framebuffer.DepthStencilView);
            Assert.Equal(1600u, framebuffer.Width);
            Assert.Equal(900u, framebuffer.Height);
            Assert.Equal(2UL, swapchain.Framebuffer.Generation);
        }

        /// <summary>
        /// The framebuffer owns nothing, so disposing the one the device hands out releases nothing and leaves it
        /// working. That matches the incumbent's no-dispose wrapper over the device-owned swapchain framebuffer,
        /// and it is what makes handing this object to a consumer safe at all.
        /// </summary>
        [Fact]
        public void DisposingTheFramebuffer_ReleasesNothingAndLeavesItUsable()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.Framebuffer.Dispose();
            swapchain.QueueResize(800, 600);
            swapchain.ApplyPendingResize();

            Assert.True(swapchain.Framebuffer.IsDisposed);
            Assert.Equal(0, surface.DisposeCount);
            Assert.True(surface.AttachmentsOutstanding);
            Assert.Equal(800u, swapchain.Framebuffer.Width);
        }

        // ---- the queue (decision W3) ----------------------------------------------------------------------

        /// <summary>
        /// DECISION W3's QUEUE: <c>ResizeSwapchain</c> stores the size and returns, touching nothing native. The
        /// foreign-thread resize during a recording that issue #415 records as a cross-thread
        /// <c>Monitor.Exit</c> becomes structurally impossible rather than contractually forbidden.
        /// </summary>
        [Fact]
        public void QueueResize_TouchesNothingNative()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            int callsAfterConstruction = surface.Calls.Count;

            swapchain.QueueResize(1600, 900);

            Assert.Equal(callsAfterConstruction, surface.Calls.Count);
            Assert.True(swapchain.HasPendingResize);
            Assert.Equal(1280u, swapchain.Framebuffer.Width);
        }

        /// <summary>
        /// COALESCED TO THE LAST REQUEST, which is what makes a drag-resize affordable: a burst of size events
        /// between two presents costs one <c>ResizeBuffers</c> at the final size, not one per event.
        /// </summary>
        [Fact]
        public void QueueResize_CoalescesToTheLastRequestedSize()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: false);

            swapchain.QueueResize(1300, 730);
            swapchain.QueueResize(1400, 800);
            swapchain.QueueResize(1600, 900);
            swapchain.Present();

            Assert.Equal(
                new[]
                {
                    "CreateAttachments 1280x720",
                    "Present 0",
                    "ReleaseAttachments",
                    "ResizeBuffers 1600x900",
                    "CreateAttachments 1600x900",
                },
                surface.Trace);
        }

        /// <summary>
        /// The queue is emptied by the apply, so a second present with nothing new queued issues only the present.
        /// A queue that kept its last value would rebuild the backbuffer every frame forever.
        /// </summary>
        [Fact]
        public void APresentWithNothingQueued_IssuesOnlyThePresent()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            swapchain.Present();
            int afterFirstPresent = surface.Calls.Count;
            swapchain.Present();
            swapchain.Present();

            Assert.False(swapchain.HasPendingResize);
            Assert.Equal(afterFirstPresent + 2, surface.Calls.Count);
            Assert.Equal("Present 1", surface.Trace[^1]);
            Assert.Equal(2, surface.CreateCount);
        }

        /// <summary>Each present applies at most one resize, and a second one queued afterwards waits for the next
        /// boundary rather than being folded into the one in flight.</summary>
        [Fact]
        public void TwoResizesAcrossTwoPresents_ApplyOneEach()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            swapchain.Present();
            Assert.Equal(1600u, swapchain.Framebuffer.Width);

            swapchain.QueueResize(800, 600);
            Assert.Equal(1600u, swapchain.Framebuffer.Width);
            swapchain.Present();

            Assert.Equal(800u, swapchain.Framebuffer.Width);
            Assert.Equal(3UL, swapchain.Framebuffer.Generation);
        }

        // ---- the present boundary --------------------------------------------------------------------------

        /// <summary>
        /// THE APPLY LANDS AFTER THE PRESENT, NOT BEFORE IT. <c>ResizeBuffers</c> discards the backbuffer
        /// contents, so a resize applied before presenting would throw away the frame that had just been rendered
        /// and present freshly allocated, undefined buffers instead. That is a black or torn frame on every
        /// drag-resize step, which is the family of defect this row exists to remove.
        /// </summary>
        [Fact]
        public void TheResizeApplies_AfterThePresentRatherThanBeforeIt()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            swapchain.Present();

            Assert.Equal(
                new[]
                {
                    "CreateAttachments 1280x720",
                    "Present 1",
                    "ReleaseAttachments",
                    "ResizeBuffers 1600x900",
                    "CreateAttachments 1600x900",
                },
                surface.Trace);
        }

        /// <summary>
        /// THE VIEWS ARE RELEASED BEFORE THE BUFFERS ARE RESIZED, which is the ordering rule the three-member
        /// surface split exists to make testable at all. <c>IDXGISwapChain::ResizeBuffers</c> fails while any
        /// outstanding reference to a backbuffer survives, and the incumbent depends on that order silently. The
        /// fake refuses the wrong order by name, so this test would fail as a thrown exception rather than as a
        /// mismatched trace.
        /// </summary>
        [Fact]
        public void TheAttachmentsAreReleasedBeforeTheBuffersAreResized()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            bool applied = swapchain.ApplyPendingResize();

            Assert.True(applied);
            Assert.Equal("ReleaseAttachments", surface.Trace[^3]);
            Assert.Equal("ResizeBuffers 1600x900", surface.Trace[^2]);
            Assert.Equal("CreateAttachments 1600x900", surface.Trace[^1]);
        }

        /// <summary>An apply with nothing queued answers false and touches nothing, so the threading row can call
        /// it unconditionally at whatever boundary it owns.</summary>
        [Fact]
        public void ApplyPendingResize_WithNothingQueued_AnswersFalseAndTouchesNothing()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            int callsAfterConstruction = surface.Calls.Count;

            Assert.False(swapchain.ApplyPendingResize());
            Assert.Equal(callsAfterConstruction, surface.Calls.Count);
        }

        /// <summary>
        /// THE SIZE COMES OFF THE BACKBUFFER, NEVER OFF THE REQUEST. DXGI reads a zero width or height as "match
        /// the window's client area", and the Silk framebuffer-resize callback does forward a minimised window as
        /// 0 by 0, so trusting the request would leave the framebuffer claiming 0 by 0 while the backbuffer is
        /// whatever the window is. The viewport decision W6 derives from those numbers would then rasterise
        /// nothing.
        /// </summary>
        [Fact]
        public void TheFramebufferTakesTheBackbufferSize_NotTheRequestedOne()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720) { ZeroMeansKeepTheWindowSize = true };
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(0, 0);
            swapchain.Present();

            Assert.Equal("ResizeBuffers 0x0", surface.Trace[^2]);
            Assert.Equal(1280u, swapchain.Framebuffer.Width);
            Assert.Equal(720u, swapchain.Framebuffer.Height);
        }

        // ---- vsync (decision W1) ---------------------------------------------------------------------------

        /// <summary>
        /// DECISION W1's SYNC INTERVAL, which is 1 or 0 and nothing else: no pacing, no frame-latency object, no
        /// throttling of any other kind. The same two values, from the same question, as the incumbent's
        /// <c>D3D11Util.GetSyncInterval</c>.
        /// </summary>
        [Theory]
        [InlineData(true, "Present 1")]
        [InlineData(false, "Present 0")]
        public void SyncToVerticalBlank_SelectsTheSyncIntervalPresentIsGiven(bool vsync, string expected)
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, vsync);

            Assert.Equal(vsync ? 1 : 0, swapchain.SyncInterval);
            swapchain.Present();
            Assert.Equal(expected, surface.Trace[^1]);
        }

        /// <summary>
        /// Flipping vsync live reconfigures NOTHING, which is the incumbent's behaviour on Direct3D 11: the sync
        /// interval is an argument of <c>Present</c>, so there is no swapchain to recreate, none to leak, and no
        /// size or depth attachment to preserve.
        /// </summary>
        [Fact]
        public void FlippingVsyncLive_RecreatesNothing()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.Present();
            swapchain.SyncToVerticalBlank = false;
            swapchain.Present();

            Assert.False(swapchain.SyncToVerticalBlank);
            Assert.Equal(new[] { "CreateAttachments 1280x720", "Present 1", "Present 0" }, surface.Trace);
            Assert.Equal(1, surface.CreateCount);
        }

        // ---- the submit lock (decisions W3 and W4) ---------------------------------------------------------

        /// <summary>
        /// PRESENT AND THE RESIZE APPLY BOTH RUN UNDER THE SUBMIT LOCK (decision W4), which is where the submit
        /// thread provably owns the context and no replay is in flight.
        /// </summary>
        [Fact]
        public void ThePresentAndTheApply_BothRunUnderTheSubmitLock()
        {
            var submitLock = new object();
            var surface = new FakeD3D11SwapchainSurface(1280, 720) { SubmitLock = submitLock };
            using var swapchain = new D3D11Swapchain(surface, submitLock, 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            swapchain.Present();

            Assert.All(surface.Calls.Skip(1), call => Assert.True(call.HeldTheSubmitLock,
                $"{call} arrived without the submit lock held."));
        }

        /// <summary>
        /// THE QUEUE DOES NOT TAKE THE LOCK, and that is the whole of decision W3's gain rather than a detail. A
        /// window callback arriving while the submit thread is mid-replay must return immediately, because the
        /// alternative is the frame-long blocking the incumbent's resize path produced.
        /// </summary>
        [Fact]
        public async Task QueueResize_DoesNotBlockWhileAnotherThreadHoldsTheSubmitLock()
        {
            var submitLock = new object();
            var surface = new FakeD3D11SwapchainSurface(1280, 720) { SubmitLock = submitLock };
            using var swapchain = new D3D11Swapchain(surface, submitLock, 1280, 720, syncToVerticalBlank: true);
            using var holding = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);

            Task holder = Task.Run(() =>
            {
                lock (submitLock)
                {
                    holding.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            });

            Assert.True(holding.Wait(TimeSpan.FromSeconds(30)), "the holder thread never took the submit lock");
            swapchain.QueueResize(1600, 900);   // must return with the lock still held elsewhere
            Assert.True(swapchain.HasPendingResize);

            release.Set();
            await holder.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // ---- device loss, the shape row 16 consumes (decision G3) ------------------------------------------

        /// <summary>
        /// The present's raw <c>HRESULT</c> reaches the caller, which is what decision G3's device-loss check
        /// needs at the site. The incumbent discards it, and a discarded device removal surfaces frames later as
        /// an unrelated crash. Latching it and calling <c>GetDeviceRemovedReason</c> is row 16's and is not built
        /// here.
        /// </summary>
        [Fact]
        public void Present_ReturnsTheRawHresult()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720) { PresentResult = DeviceRemoved };
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            Assert.Equal(DeviceRemoved, swapchain.Present());
        }

        /// <summary>
        /// A FAILED PRESENT DOES NOT TOUCH THE SWAPCHAIN AGAIN. If the present just reported the device gone,
        /// <c>ResizeBuffers</c> reports the same thing and throws from inside the present call, so the caller
        /// would get an exception instead of the <c>HRESULT</c> it was going to latch. The queued size survives,
        /// because nothing consumed it.
        /// </summary>
        [Fact]
        public void AFailedPresent_SkipsTheResizeAndKeepsItQueued()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720) { PresentResult = DeviceRemoved };
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            Assert.Equal(DeviceRemoved, swapchain.Present());

            Assert.True(swapchain.HasPendingResize);
            Assert.Equal(1280u, swapchain.Framebuffer.Width);
            Assert.Equal(new[] { "CreateAttachments 1280x720", "Present 1" }, surface.Trace);

            // And it is genuinely still queued rather than merely unapplied: the next present that succeeds
            // applies exactly the size the failed one refused to.
            surface.PresentResult = 0;
            swapchain.Present();
            Assert.Equal(1600u, swapchain.Framebuffer.Width);
        }

        /// <summary>An occluded present is a SUCCESS that presented nothing, and a resize behind an occluded
        /// window is exactly as correct as one behind a visible window. The gate is the HRESULT sign bit rather
        /// than an equality against S_OK, which is what makes that fall out rather than needing a special
        /// case.</summary>
        [Fact]
        public void AnOccludedPresent_StillAppliesTheResize()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720) { PresentResult = Occluded };
            using var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.QueueResize(1600, 900);
            Assert.Equal(Occluded, swapchain.Present());

            Assert.False(swapchain.HasPendingResize);
            Assert.Equal(1600u, swapchain.Framebuffer.Width);
        }

        // ---- teardown ---------------------------------------------------------------------------------------

        /// <summary>Disposal releases the views and the swapchain, in that order, and asking twice does it
        /// once.</summary>
        [Fact]
        public void Dispose_ReleasesTheAttachmentsThenTheSurface_AndIsIdempotent()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);

            swapchain.Dispose();
            swapchain.Dispose();

            Assert.Equal(new[] { "CreateAttachments 1280x720", "ReleaseAttachments", "Dispose" }, surface.Trace);
            Assert.Equal(1, surface.DisposeCount);
        }

        /// <summary>
        /// A resize arriving after teardown is DROPPED rather than refused. Teardown order is a consumer's
        /// business and a window can report a size change while it is being destroyed, so throwing here would turn
        /// a normal shutdown into a crash. The incumbent's <c>ResizeSwapchain</c> returns silently when there is
        /// no swapchain, for the same reason.
        /// </summary>
        [Fact]
        public void AResizeQueuedAfterDispose_IsDropped()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            swapchain.Dispose();

            swapchain.QueueResize(1600, 900);

            Assert.False(swapchain.HasPendingResize);
            Assert.Equal(1, surface.DisposeCount);
        }

        /// <summary>A present after teardown is a consumer ordering error rather than a race to absorb, and it is
        /// on the frame loop's critical path, so it says so.</summary>
        [Fact]
        public void PresentAfterDispose_Throws()
        {
            var surface = new FakeD3D11SwapchainSurface(1280, 720);
            var swapchain = new D3D11Swapchain(surface, new object(), 1280, 720, syncToVerticalBlank: true);
            swapchain.Dispose();

            Assert.Throws<ObjectDisposedException>(() => swapchain.Present());
        }

        // ---- the platform boundary -------------------------------------------------------------------------

        /// <summary>
        /// THE CLAIM DECISION P1 RESTS ON, checked for everything this row added: driving the whole swapchain
        /// surface off Windows must not put the Direct3D interop into the process. That is what makes every test
        /// above a plain <c>[Fact]</c> rather than a <c>[GpuFact]</c>, and it holds only while the engine half
        /// stays free of Vortice and every body that names one stays behind the platform guard.
        /// </summary>
        [Fact]
        public void OffWindows_TheWholeSwapchainSurfaceRunsWithoutLoadingTheDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            var submitLock = new object();
            var surface = new FakeD3D11SwapchainSurface(1280, 720, GpuPixelFormat.D32FloatS8UInt)
            {
                SubmitLock = submitLock,
            };
            var swapchain = new D3D11Swapchain(surface, submitLock, 1280, 720, syncToVerticalBlank: true);
            swapchain.QueueResize(1600, 900);
            swapchain.Present();
            swapchain.SyncToVerticalBlank = false;
            swapchain.QueueResize(800, 600);
            swapchain.ApplyPendingResize();
            _ = swapchain.HasPendingResize;
            _ = swapchain.Framebuffer.Outputs;
            _ = swapchain.Framebuffer.RenderTargetView;
            _ = swapchain.Framebuffer.DepthStencilView;
            _ = swapchain.Framebuffer.Generation;
            swapchain.Framebuffer.Dispose();
            swapchain.Dispose();

            D3D11InteropLoad.AssertNotLoaded();
        }

        // DXGI_ERROR_DEVICE_REMOVED and DXGI_STATUS_OCCLUDED, spelled here rather than named through Vortice,
        // because naming them through Vortice would load the interop and break the assertion above.
        const int DeviceRemoved = unchecked((int)0x887A0005);
        const int Occluded = 0x087A0001;
    }
}
