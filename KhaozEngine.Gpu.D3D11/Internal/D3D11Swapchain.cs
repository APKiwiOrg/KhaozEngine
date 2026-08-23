using System;
using System.Threading;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE ENGINE HALF OF THE SWAPCHAIN: the present boundary, the queued resize of decision W3, and the stable
    /// framebuffer identity of decision W2. Everything that can be wrong about a swapchain lives here, and it is
    /// all driven by plain <c>[Fact]</c>s with a fake <see cref="ID3D11SwapchainSurface"/> behind it, on macOS and
    /// Linux as well as Windows.
    /// <para>
    /// v1 IS THE INCUMBENT'S PRESENT PATH, REPRODUCED RATHER THAN IMPROVED (decision W1). The surface creates an
    /// unversioned <c>IDXGIFactory</c> swapchain with <c>BufferCount = 2</c>, <c>SwapEffect.Discard</c>,
    /// <c>Windowed = true</c>, <c>SampleDescription(1, 0)</c> and <c>B8G8R8A8_UNorm</c>, and this presents it at a
    /// sync interval of 1 or 0 with no other throttling. There is no flip model, no <c>ALLOW_TEARING</c>, no
    /// waitable frame-latency object and no pacing, and their absence is a decision rather than an omission: the
    /// swapchain is the ONE area of this backend with no automated coverage anywhere (the goldens are headless,
    /// the shape tests are device-free, the WARP leg never presents), so a flip model is validated only by a human
    /// looking at a window. Keeping the blit model is what makes the soak measure the recording model and the
    /// memory model and nothing else, which is what makes a regression attributable. All of it is sequenced as one
    /// follow-up with its own manual validation.
    /// </para>
    /// <para>
    /// THAT COSTS ONE MEASUREMENT AND THE DESIGN SAYS SO (M5, an OBSERVATION rather than a bet). Because v1
    /// carries the incumbent's blit present path unchanged, it CANNOT discriminate whether the blit model is the
    /// mechanism behind the frame-pacing defect on issue #380. A native soak that reproduces #380 unchanged is
    /// consistent with "blit causes it" and with "blit does not", so it proves nothing either way. The
    /// discriminating measurement is the same scene on the flip-model prototype, A and B against this path on the
    /// same machine and the same build. Recorded here so a reader of a soak capture does not mistake an unchanged
    /// #380 for evidence.
    /// </para>
    /// <para>
    /// RESIZE IS ENFORCED RATHER THAN DOCUMENTED (decision W3). <see cref="QueueResize"/> stores the size and
    /// returns, taking no lock and touching nothing native, so it is safe from any thread including one that
    /// arrives mid-recording. The submit thread applies it at the next present boundary, where it PROVABLY owns
    /// the context and no replay is in flight. The cost is one frame of resize latency, which is invisible. The
    /// gain is that a foreign-thread resize during recording becomes structurally impossible instead of
    /// contractually forbidden, which is the failure recorded on issue #415 as a cross-thread
    /// <c>Monitor.Exit</c> out of the resize path.
    /// </para>
    /// <para>
    /// THE APPLY LANDS AFTER THE PRESENT, NOT BEFORE IT, and the order is load-bearing. <c>ResizeBuffers</c>
    /// discards the backbuffer contents, so applying a queued resize before presenting would throw away the frame
    /// that had just been rendered and present freshly allocated, undefined buffers instead. That is a black or
    /// torn frame on every drag-resize step, which is the family of defect this row exists to remove. Presenting
    /// first shows the completed frame at the old size, and the resize then lands before anything records again,
    /// so the next frame is the first one at the new size either way.
    /// </para>
    /// <para>
    /// THE SUBMIT LOCK IS A FIELD, taken by this type rather than by its caller, exactly as
    /// <see cref="D3D11RingAllocator"/> takes it around its own map and unmap. Decision W4 puts replay, present
    /// and the resize apply under one lock held for microseconds, and a <c>Monitor</c> is re-entrant, so a device
    /// that already holds it around a whole frame boundary pays nothing to call in here.
    /// </para>
    /// </summary>
    internal sealed class D3D11Swapchain : IDisposable
    {
        // A packed size no window can be, so it doubles as "nothing queued" without a second field a writer would
        // have to publish in the right order. A packed request is NOT always non-negative, since any width from
        // 0x80000000 up sets the top bit, but that is not what this sentinel needs: -1 is every one of the 64 bits
        // set, so the one request that could collide with it is Pack(uint.MaxValue, uint.MaxValue), a 4294967295
        // by 4294967295 pixel backbuffer.
        const long NothingPending = -1L;

        readonly ID3D11SwapchainSurface _surface;
        readonly D3D11SwapchainFramebuffer _framebuffer;
        readonly object _submitLock;

        long _pendingSize = NothingPending;
        volatile bool _syncToVerticalBlank;
        bool _disposed;

        /// <summary>
        /// Wrap <paramref name="surface"/> and publish its first generation of attachments, which is what makes
        /// <see cref="Framebuffer"/> valid from the moment the device exists rather than from the first present.
        /// </summary>
        /// <param name="surface">The native half. Taken over: <see cref="Dispose"/> disposes it.</param>
        /// <param name="submitLock">The device's one submit lock (decision W4).</param>
        /// <param name="width">The backbuffer width the swapchain was created at.</param>
        /// <param name="height">The backbuffer height the swapchain was created at.</param>
        /// <param name="syncToVerticalBlank">The initial vsync setting, which selects the sync interval.</param>
        internal D3D11Swapchain(ID3D11SwapchainSurface surface, object submitLock, uint width, uint height,
            bool syncToVerticalBlank)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _syncToVerticalBlank = syncToVerticalBlank;
            _framebuffer = new D3D11SwapchainFramebuffer(
                surface.ColourFormat, surface.DepthFormat, surface.CreateAttachments(width, height));
        }

        /// <summary>
        /// The swapchain's framebuffer, which is the SAME object for the whole life of the device (decision W2).
        /// This is what <c>IGpuDevice.SwapchainFramebuffer</c> hands back, and it may be cached by anything: a
        /// resize changes its size and its views and never its identity.
        /// </summary>
        internal D3D11SwapchainFramebuffer Framebuffer => _framebuffer;

        /// <summary>
        /// Whether presentation syncs to the vertical blank. Settable live from any thread, which is what the
        /// settings-screen path needs, and it reconfigures NOTHING: on Direct3D 11 vsync is an argument of
        /// <c>Present</c>, so the incumbent's setter also just changes the interval and there is no swapchain
        /// recreate to leak and no size or depth to preserve.
        /// </summary>
        internal bool SyncToVerticalBlank
        {
            get => _syncToVerticalBlank;
            set => _syncToVerticalBlank = value;
        }

        /// <summary>The sync interval <see cref="Present"/> passes, which decision W1 pins to 1 or 0 with no
        /// other throttling. The same two values, from the same question, as the incumbent's
        /// <c>D3D11Util.GetSyncInterval</c>.</summary>
        internal int SyncInterval => _syncToVerticalBlank ? 1 : 0;

        /// <summary>True while a resize is queued and not yet applied. For a diagnostic and for the tests that
        /// pin the coalescing, never needed to decide anything: <see cref="Present"/> takes the queue itself.
        /// </summary>
        internal bool HasPendingResize => Interlocked.Read(ref _pendingSize) != NothingPending;

        /// <summary>
        /// DECISION W3's QUEUE: store the requested size and return. Takes no lock, makes no native call, and
        /// never blocks, so a window callback on any thread is safe even while the submit thread holds the lock
        /// for a replay.
        /// <para>
        /// COALESCED TO THE LAST REQUEST, which is the whole reason a drag-resize is affordable: a burst of
        /// thirty size events between two presents costs one <c>ResizeBuffers</c>, not thirty. The exchange is
        /// atomic, so two threads racing leave one of the two sizes and never a mix of the two halves.
        /// </para>
        /// <para>
        /// A resize arriving after <see cref="Dispose"/> is DROPPED rather than refused. Teardown order is a
        /// consumer's business and a window can report a size change while it is being destroyed, so throwing
        /// here would turn a normal shutdown into a crash. That matches the incumbent, whose
        /// <c>ResizeSwapchain</c> returns silently when there is no swapchain.
        /// </para>
        /// </summary>
        internal void QueueResize(uint width, uint height)
        {
            if (_disposed) return;

            Interlocked.Exchange(ref _pendingSize, Pack(width, height));
        }

        /// <summary>
        /// PRESENT, THEN APPLY WHATEVER RESIZE IS QUEUED, both under the submit lock. This is the per-present
        /// member the device calls, alongside <see cref="D3D11FenceSubsystem.BeginFrame"/> and
        /// <see cref="D3D11RingAllocator.BeginFrame"/>, and it returns the present's raw <c>HRESULT</c> for
        /// decision G3's device-loss check to read at the site.
        /// <para>
        /// A FAILED PRESENT DOES NOT TOUCH THE SWAPCHAIN AGAIN, and that gate is the one interpretation of the
        /// result taken here. If the present just reported the device gone, <c>ResizeBuffers</c> reports the same
        /// thing and throws from inside this call, so the caller would get an exception instead of the
        /// <c>HRESULT</c> it was going to latch. The queued size survives, because nothing consumed it. Naming
        /// the removal, calling <c>GetDeviceRemovedReason</c> at the fault site and latching it are
        /// <see cref="D3D11DeviceLossLatch"/>'s, and the DEVICE is the caller that hands this result to it.
        /// </para>
        /// </summary>
        internal int Present()
        {
            lock (_submitLock)
            {
                // INSIDE the lock, because Dispose flips the flag inside it. Checked outside, a Dispose landing in
                // the gap between the check and the lock would leave this call presenting a surface that has
                // already been released, which on the shipped surface is a Present against a freed IDXGISwapChain.
                // No device-free test can pin that interleaving, since both orderings of the racy version throw
                // whenever the race does not actually happen.
                ObjectDisposedException.ThrowIf(_disposed, this);

                int result = _surface.Present(SyncInterval);
                // The standard HRESULT reading: the sign bit is the failure bit, so a non-negative value is any
                // form of success, S_OK and DXGI_STATUS_OCCLUDED included. An occluded present is a success that
                // presented nothing, and a resize applied behind an occluded window is exactly as correct as one
                // applied behind a visible one.
                if (result >= 0) ApplyPendingResizeCore();
                return result;
            }
        }

        /// <summary>
        /// Apply a queued resize on its own, without presenting, and answer whether there was one. Separate from
        /// <see cref="Present"/> so the threading row has a seam it can drive directly, and so a test can pin the
        /// apply without a present in front of it. Takes the submit lock, which is free re-entry for a caller
        /// that already holds it.
        /// </summary>
        internal bool ApplyPendingResize()
        {
            lock (_submitLock) return ApplyPendingResizeCore();
        }

        /// <summary>
        /// THE THREE-STEP RESIZE, IN THE ONE ORDER THAT WORKS, with the caller holding the submit lock.
        /// <para>
        /// Release every view over the backbuffer, THEN resize the buffers, THEN create the views again.
        /// <c>IDXGISwapChain::ResizeBuffers</c> fails while any outstanding reference to a backbuffer exists, so
        /// the middle step is impossible without the first, and getting it wrong leaves the window presenting a
        /// swapchain whose buffers no longer match it. The incumbent had the same order and stated it nowhere,
        /// which is why the three steps are three members of <see cref="ID3D11SwapchainSurface"/>: the order is
        /// engine logic here, asserted by a device-free test, rather than a detail buried in a Windows-only body
        /// that only a human with a real window could ever catch.
        /// </para>
        /// <para>
        /// The size is taken from the queue ATOMICALLY, so a resize arriving while this runs is queued for the
        /// next boundary rather than lost or half-applied. The new views are then published onto the framebuffer
        /// under its stable identity, and the framebuffer takes its size from the attachments rather than from
        /// the request, because DXGI reads a zero dimension as "match the window".
        /// </para>
        /// <para>
        /// A THROW FROM THE MIDDLE LEAVES THE FRAMEBUFFER POINTING AT RELEASED VIEWS, and that is a known, narrow
        /// open end rather than an oversight (issue #489). In v1 the only way to reach it is device loss, since
        /// the arguments are already validated and the surface makes no other failing call. There is no repair
        /// available that does not require holding the old views across the resize, which is precisely what
        /// <c>ResizeBuffers</c> forbids, so the answer is the latch rather than a rollback: once the device is
        /// known dead, nothing binds again.
        /// </para>
        /// <para>
        /// THE HANDLER IS THE DEVICE'S AND THIS TYPE DELIBERATELY DOES NOT CALL IT.
        /// <see cref="D3D11DeviceLossLatch.CheckAfterFault"/> is that handler, and the device wraps its call to
        /// <see cref="Present"/> in it, for the same reason the other three device-loss check sites are at the
        /// device: the latch is the device's, this type has no reference to one, and giving it one would put a
        /// fourth constructor argument on a class whose entire test surface is device-free. So a throw out of
        /// here propagates to the device's present, which reads the removal reason at that fault before letting
        /// the exception carry on. The ordinary removal path is already clean, because <see cref="Present"/>
        /// reports the removal as an HRESULT and a failed present skips the queued resize entirely.
        /// </para>
        /// </summary>
        bool ApplyPendingResizeCore()
        {
            if (_disposed) return false;

            long pending = Interlocked.Exchange(ref _pendingSize, NothingPending);
            if (pending == NothingPending) return false;

            uint width = WidthOf(pending);
            uint height = HeightOf(pending);

            _surface.ReleaseAttachments();
            _surface.ResizeBuffers(width, height);
            _framebuffer.Adopt(_surface.CreateAttachments(width, height));
            return true;
        }

        /// <summary>
        /// Release the swapchain and its views. The framebuffer wrapper survives, holding views that no longer
        /// exist, which is safe for the same reason every other wrapper's post-death disposal is: the device is
        /// going away with it and nothing will bind them again.
        /// </summary>
        public void Dispose()
        {
            lock (_submitLock)
            {
                if (_disposed) return;

                _disposed = true;
                _surface.Dispose();
            }
        }

        // One long carries both halves so the queue is a single atomic exchange rather than two fields a reader
        // could catch mid-update. Width in the high half, height in the low half.
        static long Pack(uint width, uint height) => ((long)width << 32) | height;

        static uint WidthOf(long packed) => (uint)(packed >> 32);

        static uint HeightOf(long packed) => (uint)packed;
    }
}
