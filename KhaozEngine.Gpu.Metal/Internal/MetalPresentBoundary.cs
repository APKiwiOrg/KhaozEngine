using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE FRAME BOUNDARY, IN ONE PLACE: present the drawable the frame just rendered into, apply anything a
    /// resize or a vsync change queued, and acquire the drawable the NEXT frame will render into. Work-breakdown
    /// row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>NOTHING IN THIS TYPE TOUCHES OBJECTIVE-C, WHICH IS HOW MM7 IS ANSWERED AS FAR AS IT CAN BE.</b>
    /// The design records that not one line of the incumbent's swapchain runs in CI on any leg, ever. A headless
    /// runner cannot drive a real <c>CAMetalLayer</c>, so every native call goes behind
    /// <see cref="IMetalSwapchainApi"/> and everything that DECIDES anything is here, driven by a fake on every
    /// leg: the order of the boundary, the skipped present, the orphan target, the counters, the coalescing and
    /// the drain before an apply.</para>
    ///
    /// <para><b>THE ORDER IS FIXED AND EACH STEP IS WHERE IT IS FOR A DIFFERENT REASON.</b> The PRESENT goes
    /// first, under the submit lock, because it commits to the queue and submit order is the observable order only
    /// while every commit to that queue goes through one lock (M-N2). The APPLY goes next, still under the lock
    /// and after a drain, because that is the one instant the boundary provably owns the queue and no recording is
    /// in flight (M-W7). The CAPTURE boundary goes after the present and outside the lock, because a capture
    /// brackets whole frames and the arm is consumed at present N to close at present N+1 (M-G5). The ACQUIRE goes
    /// LAST and holds NOTHING, because <c>nextDrawable</c> BLOCKS and blocking inside the submit lock would stop
    /// every other thread's submit for the length of a display refresh, which is exactly the "held for
    /// microseconds, not a frame" rule M-W8 states.</para>
    ///
    /// <para><b>A SKIPPED PRESENT IS NOT A SKIPPED FRAME (M-W5).</b> <see cref="FramesBegun"/> counts every
    /// boundary, including one whose drawable came back nil, because it is the denominator every per-frame figure
    /// in <c>GpuDeviceCounters</c> is divided by and a frame that recorded and submitted is a frame however it
    /// ended. The incumbent counts nothing at all here and logs nothing either, which is the regression this row
    /// exists for: <see cref="SkippedPresents"/> is the count, and the first one WARNs once with the reason.</para>
    ///
    /// <para><b>THE ACQUIRE IS TIMED RATHER THAN PROBED, and that is forced (M-W4).</b> Vulkan can ask
    /// <c>vkAcquireNextImageKHR</c> for an image with a zero timeout and learn whether waiting was necessary.
    /// <c>-nextDrawable</c> has no such form, no timeout knob and no readiness query, so the block is not
    /// removable and the only instrument available is a stopwatch around the call. See
    /// <see cref="MetalAcquireWaits"/> for why recording every acquire is the reading the seam already
    /// documents.</para>
    ///
    /// <para><b>THE PRESENTED DRAWABLE IS RELEASED THE MOMENT ITS PRESENT IS COMMITTED, and the framebuffer goes
    /// on naming its texture until the acquire republishes.</b> That gap is safe because an
    /// <c>MTLCommandBuffer</c> retains every resource it references until it completes (M-H3), so the committed
    /// present buffer is itself an owner of the drawable and therefore of the texture. It is also a gap nothing
    /// can record into: it is inside <see cref="Present"/>, on the submit thread, at the one instant M-W5's own
    /// rule says no recording is in flight.</para>
    /// </summary>
    internal sealed class MetalPresentBoundary : IDisposable
    {
        static readonly ILogger log = Log.For<MetalPresentBoundary>();

        readonly IMetalSwapchainApi _api;
        readonly IMetalOrphanTarget _orphan;
        readonly MetalUncommittedBuffers _uncommitted;
        readonly MetalAcquireWaits _waits;
        readonly MetalPresentPending _pending = new();
        readonly object _submitLock;
        readonly Action _drain;
        readonly Action _afterPresent;
        readonly GpuPixelFormat _colourFormat;
        readonly ILogger _log;

        IntPtr _drawable;
        MetalDrawableSize _size;
        long _framesBegun;
        long _skippedPresents;
        int _skipReported;
        bool _orphanBound;
        bool _syncRequested;
        bool _syncApplied;
        bool _disposed;

        /// <param name="api">Every native call this boundary makes.</param>
        /// <param name="orphan">What a nil-drawable frame binds (M-W5).</param>
        /// <param name="uncommitted">The device's uncommitted-buffer count, which M-W6's present buffer is the
        /// PLUS ONE of in <see cref="MetalFramesInFlight.UncommittedBufferBound"/>.</param>
        /// <param name="waits">M-W4's acquire accumulator.</param>
        /// <param name="submitLock">The device's ONE submit lock (M-N2, M-W8).</param>
        /// <param name="drain">The device's drain, run before an apply and nowhere else on this path.</param>
        /// <param name="afterPresent">Serviced after the drawable has been presented, which is
        /// <c>MetalGpuDevice.ServiceFrameCaptureAtPresentBoundary</c> (M-G5).</param>
        /// <param name="size">The initial drawable size, clamped here rather than by the caller.</param>
        /// <param name="colourSrgb">Whether the sRGB colour format is wanted. See
        /// <see cref="MetalSwapchainPolicy.ColourSrgbRequested"/>.</param>
        /// <param name="syncToVerticalBlank">The initial vsync value, written unconditionally (M-W2).</param>
        /// <param name="maximumDrawableCount">The drawable queue depth (M-W4).</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal MetalPresentBoundary(IMetalSwapchainApi api, IMetalOrphanTarget orphan,
            MetalUncommittedBuffers uncommitted, MetalAcquireWaits waits, object submitLock, Action drain,
            Action afterPresent, MetalDrawableSize size, bool colourSrgb, bool syncToVerticalBlank,
            int maximumDrawableCount, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(orphan);
            ArgumentNullException.ThrowIfNull(uncommitted);
            ArgumentNullException.ThrowIfNull(waits);
            ArgumentNullException.ThrowIfNull(submitLock);
            ArgumentNullException.ThrowIfNull(drain);
            ArgumentNullException.ThrowIfNull(afterPresent);

            _api = api;
            _orphan = orphan;
            _uncommitted = uncommitted;
            _waits = waits;
            _submitLock = submitLock;
            _drain = drain;
            _afterPresent = afterPresent;
            _colourFormat = MetalSwapchainPolicy.SeamColourFormat(colourSrgb);
            _log = logger ?? log;

            _size = size.AtLeastOnePixel;
            _syncRequested = syncToVerticalBlank;
            _syncApplied = syncToVerticalBlank;

            // THE WHOLE CONFIGURATION IN ONE CALL, before the first acquire, which is the incumbent's own order:
            // it writes device, pixelFormat, framebufferOnly and drawableSize, then vsync, then takes a drawable,
            // then builds its framebuffer. A drawable acquired before the format was written would be the wrong
            // format, and the layer answers no error for it.
            _api.Configure(_size, colourSrgb, syncToVerticalBlank, maximumDrawableCount);

            // THE FIRST ACQUIRE IS A REAL ONE and it is counted like every other. It happens before any frame
            // exists, which is why AcquireWaitCount runs exactly one ahead of FramesBegun on a device that has
            // presented at all, and that offset is a fact rather than an off-by-one.
            Framebuffer = new MetalSwapchainFramebuffer(_colourFormat, Acquire(), _size);
        }

        /// <summary>
        /// THE SAME OBJECT FOR THE WHOLE LIFE OF THE DEVICE (M-W7), so anything may cache it. An acquire and a
        /// resize both change what it points at and never its identity.
        /// </summary>
        internal MetalSwapchainFramebuffer Framebuffer { get; }

        /// <summary>Frames this boundary has opened, which is every <see cref="Present"/> including one whose
        /// present was skipped. <c>GpuDeviceCounters.FramesBegun</c>.</summary>
        internal long FramesBegun => Volatile.Read(ref _framesBegun);

        /// <summary>Boundaries whose drawable was nil, so the frame rendered into the orphan target and was not
        /// presented. The number the incumbent does not keep.</summary>
        internal long SkippedPresents => Volatile.Read(ref _skippedPresents);

        /// <summary>M-W4's pair, cumulative since the device was created.
        /// <c>GpuDeviceCounters.AcquireWaitCount</c> and <c>AcquireWaitMs</c>.</summary>
        internal MetalWaitTotals AcquireTotals => _waits.Totals;

        /// <summary>Whether the framebuffer currently points at the orphan target rather than at a drawable's
        /// texture.</summary>
        internal bool IsOrphanBound => _orphanBound;

        /// <summary>Whether a drawable is held for the next frame. False after a nil acquire, which is the state
        /// the next present skips in.</summary>
        internal bool HasDrawable => _drawable != IntPtr.Zero;

        /// <summary>The size the layer is configured at, after every apply. The framebuffer reports the
        /// same.</summary>
        internal MetalDrawableSize Size => _size;

        /// <summary>Whether anything is queued for the next boundary. Diagnostic, and what the coalescing tests
        /// read.</summary>
        internal bool HasPendingWork => _pending.HasWork;

        /// <summary>The queued state, for the tests that pin the coalescing.</summary>
        internal MetalPresentPending Pending => _pending;

        /// <summary>The vsync value actually written to the layer, which lags <see cref="SyncToVerticalBlank"/>
        /// until the next boundary applies it.</summary>
        internal bool AppliedSyncToVerticalBlank => _syncApplied;

        /// <summary>
        /// The vsync value the consumer last asked for. The SETTER queues rather than writes (M-W7), so it takes
        /// no lock and makes no native call and is safe from any thread, and the getter answers the request rather
        /// than the layer so a consumer reading back what it just set sees what it set.
        /// </summary>
        internal bool SyncToVerticalBlank
        {
            get => Volatile.Read(ref _syncRequested);
            set
            {
                Volatile.Write(ref _syncRequested, value);
                _pending.QueueSyncToVerticalBlank(value);
            }
        }

        /// <summary>
        /// Queue a resize, coalescing onto any earlier one, and apply it at the next boundary (M-W7). Stores a
        /// number and returns: no lock, no native call, nothing that can block, so a window callback arriving on
        /// any thread while the submit thread is inside a commit is safe.
        /// <para>
        /// A ZERO IN EITHER DIMENSION IS SAFE HERE AND SAFE AT THE APPLY. A minimised window reports (0, 0)
        /// through its framebuffer-resize event, the clamp turns it into one by one, and the layer then usually
        /// vends no drawable, so the frame binds the orphan target and skips its present until the window comes
        /// back.
        /// </para>
        /// </summary>
        internal void QueueResize(uint width, uint height) => _pending.QueueResize(width, height);

        /// <summary>
        /// THE BOUNDARY. Present, apply, service the capture, acquire. See the type remarks for why each step is
        /// where it is.
        /// <para>
        /// IT NEVER THROWS AND NEVER REPORTS FAILURE UPWARD. A nil drawable is M-W5's orphan path, and a device
        /// that has gone is answered by the caller before this is reached: the frame loop above is unchanged in
        /// every case.
        /// </para>
        /// </summary>
        internal void Present()
        {
            if (_disposed) return;

            // FIRST, so a boundary that throws nowhere still counts the frame it opened. Every later step is
            // conditional on something and this one is not.
            Interlocked.Increment(ref _framesBegun);

            lock (_submitLock)
            {
                PresentHeldDrawable();
                ApplyPending();
            }

            // AFTER THE PRESENT AND OUTSIDE THE LOCK (M-G5, row 16's handoff). A capture brackets whole frames,
            // so the arm is consumed at this present and the trace closes at the next one, which is what makes it
            // catch the offscreen model pass as well as the composite. Nothing armed is one flag read and a
            // return, so an ordinary frame pays nothing. Outside the lock because a capture START and a capture
            // STOP both call into Metal and the stop drains, and a drain inside the submit lock is exactly what
            // the acquire below is kept out of it for.
            _afterPresent();

            AcquireAndPublish();
        }

        /// <summary>
        /// Release the held drawable and the orphan target, then the layer. Called from the device's teardown
        /// AFTER its drain, so nothing released here is still referenced by work in flight.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_drawable != IntPtr.Zero)
            {
                _api.ReleaseDrawable(_drawable);
                _drawable = IntPtr.Zero;
            }

            _orphan.Release();
            _api.Dispose();
        }

        // M-W6's PRESENT: its own command buffer, taken and committed inside one api call, and counted into the
        // device's uncommitted total for exactly that window.
        //
        // THE BRACKET IS AROUND THE CALL RATHER THAN INSIDE IT, deliberately, and it is the conservative
        // direction. Row 7 asked for Acquired() when the present buffer is taken and Released() after its commit,
        // and the buffer's whole life is inside this one call, so bracketing the call can only report the peak
        // HIGHER than the true one and never lower. What it buys is that the count is asserted device-free, on
        // every leg, against a bound whose PLUS ONE exists for this buffer and had no occupant until this row.
        void PresentHeldDrawable()
        {
            if (_drawable == IntPtr.Zero)
            {
                RecordSkippedPresent();
                return;
            }

            _uncommitted.Acquired();
            try
            {
                _api.PresentDrawable(_drawable);
            }
            finally
            {
                _uncommitted.Released();
            }

            // RELEASED HERE rather than at the next acquire, which is where the incumbent releases it. See the
            // type remarks: the committed present buffer retains the drawable until it completes, so this is
            // never the last reference to a texture the framebuffer still names.
            _api.ReleaseDrawable(_drawable);
            _drawable = IntPtr.Zero;
        }

        // M-W7's APPLY, under the submit lock and after a drain. Nothing queued means no drain at all, which is
        // what keeps an ordinary boundary free: a drain per frame would be M-F5's counted wait on every single
        // frame and would report as a drain cost the design never intended to spend.
        void ApplyPending()
        {
            if (!_pending.Take(out MetalDrawableSize? size, out bool? sync)) return;

            // THE DRAIN IS THE WHOLE POINT OF DEFERRING TO THE BOUNDARY. The incumbent applies a resize inline on
            // the calling thread with no drain anywhere, releasing a depth texture in-flight frames may still be
            // reading. There is no depth texture here (the seam cannot ask for one), so what this protects is the
            // drawable swap and the layer reconfiguration, and it is the seat a depth rebuild would need the day
            // the seam grows one.
            _drain();

            if (size is { } requested)
            {
                _size = requested.AtLeastOnePixel;
                _api.SetDrawableSize(_size);
            }

            if (sync is { } enabled)
            {
                // UNCONDITIONALLY (M-W2). The incumbent writes this only inside three values of an enum
                // deprecated since macOS 10.15, so a machine outside that set loses its vsync toggle silently.
                _api.SetDisplaySyncEnabled(enabled);
                _syncApplied = enabled;
            }
        }

        void AcquireAndPublish()
        {
            // READ BEFORE THE ACQUIRE OVERWRITES IT, so the release below is a TRANSITION rather than a question
            // asked every frame. An unconditional call would be a harmless no-op on the real target and would put
            // a release in the log of every ordinary boundary, which makes "the orphan dies at the next
            // successful acquire" true only in the sense that the call happens.
            bool wasOrphanBound = _orphanBound;

            MetalAttachment attachment = Acquire();
            Framebuffer.Adopt(attachment, _size);

            // THE ORPHAN DIES AT THE NEXT SUCCESSFUL ACQUIRE, and AFTER the publish rather than before it (M-W5).
            // Its lifetime is the DEVICE's precisely so a recording that bound it is not left naming a destroyed
            // texture, and releasing it before the framebuffer had been repointed would be that exact failure
            // narrowed to one statement.
            if (wasOrphanBound && !_orphanBound) _orphan.Release();
        }

        MetalAttachment Acquire()
        {
            long start = Stopwatch.GetTimestamp();
            MetalAcquiredDrawable acquired = _api.NextDrawable();
            _waits.Record(Stopwatch.GetTimestamp() - start);

            _drawable = acquired.Drawable;
            _orphanBound = !acquired.HasDrawable;

            if (acquired.HasDrawable) return new MetalAttachment(acquired.Texture, _colourFormat);

            // THE ORPHAN IS ENSURED WITH NO LOCK HELD, which is the reason it is a seam at all: creating a
            // texture can append to the device's setup command buffer under the SETUP lock, and the setup lock is
            // taken before the submit lock and never after it (M-W8).
            return _orphan.Ensure(_size, _colourFormat);
        }

        // THE NUMBER AND THE LINE THE INCUMBENT HAS NEITHER OF. Counted every time, WARNed once per device: a
        // minimised window produces one of these per frame for as long as it is down, so a line per skip would
        // bury the session log in the one state a reader most wants to search it.
        void RecordSkippedPresent()
        {
            Interlocked.Increment(ref _skippedPresents);
            if (Interlocked.Exchange(ref _skipReported, 1) == 1) return;

            _log.Warn("The native Metal swapchain had no drawable at a present boundary, so that frame rendered "
                + "into the device's orphan target and was not presented. The frame still recorded, submitted, "
                + "completed and counted into FramesBegun (M-W5), which is what separates this from the "
                + "incumbent's behaviour of building a whole frame and discarding every draw in it with nothing "
                + "logged. A minimised or zero-sized window is the ordinary cause and it recovers by itself. "
                + "Reported once per device, and MetalPresentBoundary.SkippedPresents keeps the running count.");
        }
    }
}
