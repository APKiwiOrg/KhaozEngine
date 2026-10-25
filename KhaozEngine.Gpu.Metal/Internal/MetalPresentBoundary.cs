using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE FRAME BOUNDARY, IN ONE PLACE: present the drawable the frame just rendered into, apply anything a
    /// resize or a vsync change queued. The current frame acquires at its first swapchain bind. Work-breakdown
    /// row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>NOTHING IN THIS TYPE TOUCHES OBJECTIVE-C, WHICH IS HOW MM7 IS ANSWERED AS FAR AS IT CAN BE.</b>
    /// Native layer tests need macOS and a GPU. Cross-platform contract tests instead put every call behind
    /// <see cref="IMetalSwapchainApi"/>, and everything that DECIDES anything is here, driven by a fake on every
    /// leg: the order of the boundary, the skipped present, the orphan target, the counters, the coalescing and
    /// the drain before an apply.</para>
    ///
    /// <para><b>THE ORDER IS FIXED AND EACH STEP IS WHERE IT IS FOR A DIFFERENT REASON.</b> The PRESENT goes
    /// first, under the submit lock, because it commits to the queue and submit order is the observable order only
    /// while every commit to that queue goes through one lock (M-N2). The APPLY goes next, still under the lock
    /// and after a drain, because that is the one instant the boundary provably owns the queue and no recording is
    /// in flight (M-W7). The CAPTURE boundary goes after the present and outside the lock, because a capture
    /// brackets whole frames and the arm is consumed at present N to close at present N+1 (M-G5). The next
    /// frame stays unresolved until its first swapchain bind, after FramePhases has run preparation. Acquisition
    /// holds NOTHING, because <c>nextDrawable</c> BLOCKS and must not stop other threads' submits (M-W8).
    /// A present-only frame resolves its own drawable before taking the present buffer.</para>
    ///
    /// <para><b>THE PRESENT BUFFER IS TAKEN BEFORE THE LOCK FOR THE SAME REASON, AND ON THIS ONE THE FAILURE IS
    /// WORSE THAN A STALL.</b> M-W6's buffer comes from <c>-commandBuffer</c>, which BLOCKS once the queue's own
    /// maximum of uncommitted buffers is reached (<see cref="MetalUncommittedBuffers"/> is the instrument that
    /// says how close the backend gets). Every commit that could release one goes through this same lock, so a
    /// blocked <c>-commandBuffer</c> inside it cannot be cleared by anything: unlike the acquire, that is a
    /// deadlock rather than a wait. So <see cref="Present"/> takes the buffer first, holding nothing, and the
    /// lock covers the encode and the commit alone. Reading <c>_drawable</c> to decide whether to take one is
    /// safe because this whole member runs on the submit thread, which is the only writer it has.</para>
    ///
    /// <para><b>A SKIPPED PRESENT IS NOT A SKIPPED FRAME (M-W5).</b> <see cref="FramesBegun"/> counts every
    /// boundary, including one whose drawable came back nil, because it is the denominator every per-frame figure
    /// in <c>GpuDeviceCounters</c> is divided by and a frame that recorded and submitted is a frame however it
    /// ended. The incumbent counted nothing at all here and logged nothing either, which is the regression this row
    /// exists for: <see cref="SkippedPresents"/> is the count, and the first one WARNs once with the reason.</para>
    ///
    /// <para><b>THE ACQUIRE IS TIMED RATHER THAN PROBED, and that is forced (M-W4).</b> Vulkan can ask
    /// <c>vkAcquireNextImageKHR</c> for an image with a zero timeout and learn whether waiting was necessary.
    /// <c>-nextDrawable</c> has no such form, no timeout knob and no readiness query, so the block is not
    /// removable and the only instrument available is a stopwatch around the call. So the acquire
    /// <see cref="WaitAccumulator"/> this boundary owns is the one caller in the engine that records a call which
    /// did NOT block, and <c>GpuDeviceCounters.AcquireWaitCount</c> already documents that reading: a backend that
    /// blocks the CPU on the acquire reports one per frame.</para>
    ///
    /// <para><b>THE PRESENTED DRAWABLE IS RELEASED THE MOMENT ITS PRESENT IS COMMITTED.</b> The framebuffer's
    /// native binding data is invalidated first. Committed command buffers retain their referenced resources
    /// until completion (M-H3). Publication changes only on the single ordered frame owner, with no recording
    /// across Present, because bound records share the framebuffer's attachment array.</para>
    ///
    /// <para><b>A DRAWABLE IS COPYABLE ONLY IF ITS OWN TEXTURE REPORTS <c>framebufferOnly</c> FALSE.</b> A
    /// swapchain capture request queues the layer write and the boundary applies it before the acquire, but the
    /// layer may still hand back a pooled drawable made under the old value. So the acquire reads each texture's
    /// own <c>-isFramebufferOnly</c>, and that is what <see cref="CurrentDrawableCopyable"/> answers from. The
    /// drawable held when the request arrives stays uncopyable, and the orphan target never is.</para>
    /// </summary>
    internal sealed class MetalPresentBoundary : IDisposable
    {
        static readonly ILogger log = Log.For<MetalPresentBoundary>();

        readonly IMetalSwapchainApi _api;
        readonly IMetalOrphanTarget _orphan;
        readonly MetalUncommittedBuffers _uncommitted;
        readonly WaitAccumulator _waits;
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
        bool _acquired;
        bool _syncRequested;
        bool _syncApplied;
        bool _drawableFramebufferOnly = true;
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
            MetalUncommittedBuffers uncommitted, WaitAccumulator waits, object submitLock, Action drain,
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
            // format, and the layer answers no error for it. It writes framebufferOnly true. This is the one
            // Configure, so no capture request can be standing yet and there is nothing to re-apply after it.
            _api.Configure(_size, colourSrgb, syncToVerticalBlank, maximumDrawableCount);

            // Metadata is available immediately, but native binding data is resolved only when needed.
            Framebuffer = new MetalSwapchainFramebuffer(_colourFormat, _size, AcquireAndPublish);
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
        /// presented. The number the incumbent did not keep.</summary>
        internal long SkippedPresents => Volatile.Read(ref _skippedPresents);

        /// <summary>M-W4's pair, cumulative since the device was created.
        /// <c>GpuDeviceCounters.AcquireWaitCount</c> and <c>AcquireWaitMs</c>.</summary>
        internal WaitTotals AcquireTotals => _waits.Totals;

        /// <summary>Whether the framebuffer currently points at the orphan target rather than at a drawable's
        /// texture.</summary>
        internal bool IsOrphanBound => _acquired && _orphanBound;

        /// <summary>Whether a drawable is held for the current frame. False after a nil acquire, which is the state
        /// the next present skips in.</summary>
        internal bool HasDrawable => _drawable != IntPtr.Zero;

        /// <summary>Whether the held drawable can be a blit source: a real drawable is held and its texture
        /// reported <c>framebufferOnly</c> false when it was acquired. Always false while the orphan target is
        /// bound.</summary>
        internal bool CurrentDrawableCopyable => _drawable != IntPtr.Zero && !_drawableFramebufferOnly;

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
        /// THE BOUNDARY. Resolve if unused, present, apply, service the capture. See the type remarks for the
        /// ordering and lock boundaries.
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

            // Preserve present-without-draw: resolve this frame, never a future frame. A nil result is also
            // an attempted acquisition, so a bound orphan is not retried at Present.
            AcquireAndPublish();

            // M-W6's BUFFER IS TAKEN BEFORE THE LOCK, AND THAT IS THE SECOND BLOCKING CALL KEPT OUT OF IT.
            // -commandBuffer blocks once the queue's own maximum of uncommitted buffers is reached
            // (MetalUncommittedBuffers), and every commit that could release one goes through this same lock, so
            // taking it inside would be a deadlock rather than the acquire's mere stall. Reading _drawable here
            // is safe for the reason the type remarks give: Present runs on the submit thread, which is the only
            // thread that writes it.
            IntPtr presentBuffer = _drawable != IntPtr.Zero ? _api.AcquirePresentBuffer() : IntPtr.Zero;
            if (presentBuffer != IntPtr.Zero) _uncommitted.Acquired();

            Framebuffer.Reset(_size);
            lock (_submitLock)
            {
                PresentHeldDrawable(presentBuffer);
                ApplyPending();
            }
            _acquired = false;

            // AFTER THE PRESENT AND OUTSIDE THE LOCK (M-G5, row 16's handoff). A capture brackets whole frames,
            // so the arm is consumed at this present and the trace closes at the next one, which is what makes it
            // catch the offscreen model pass as well as the composite. Nothing armed is one flag read and a
            // return, so an ordinary frame pays nothing. Outside the lock because a capture START and a capture
            // STOP both call into Metal and the stop drains, and a drain inside the submit lock is exactly what
            // the drawable acquire is kept out of it for.
            _afterPresent();
        }

        /// <summary>
        /// Release the held drawable and the orphan target, then the layer. Called from the device's teardown
        /// AFTER its drain, so nothing released here is still referenced by work in flight.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _acquired = false;
            Framebuffer.Reset(_size);

            if (_drawable != IntPtr.Zero)
            {
                _api.ReleaseDrawable(_drawable);
                _drawable = IntPtr.Zero;
            }

            _orphan.Release();
            _api.Dispose();
        }

        // M-W6's PRESENT: its own command buffer, taken by the caller BEFORE the lock and committed here, and
        // counted into the device's uncommitted total for exactly the window it is held.
        //
        // THE BRACKET IS EXACTLY THE BUFFER'S LIFE NOW, which is what moving the acquire out of the lock also
        // bought. Row 7 asked for Acquired() when the present buffer is taken and Released() after its commit.
        // While the acquire lived inside this method the bracket wrapped the whole api call, which could only
        // report the peak HIGHER than the true one and never lower, so it was correct in the conservative
        // direction. With the acquire above the lock the increment sits directly after it and this release
        // directly after the commit, so the count is the true one and the same assertion still holds: the peak
        // is read device-free, on every leg, against a bound whose PLUS ONE exists for this buffer.
        //
        // A BUFFER THE QUEUE WOULD NOT MAKE IS ZERO, and the present is skipped while the drawable is still
        // released, which is what the single-call version did when its own acquire answered nil.
        void PresentHeldDrawable(IntPtr presentBuffer)
        {
            if (_drawable == IntPtr.Zero)
            {
                RecordSkippedPresent();
                return;
            }

            if (presentBuffer != IntPtr.Zero)
            {
                try
                {
                    _api.PresentDrawable(presentBuffer, _drawable);
                }
                finally
                {
                    _uncommitted.Released();
                }
            }

            // RELEASED HERE rather than at the next acquire, which is where the incumbent released it. See the
            // type remarks: the committed present buffer retains the drawable until it completes, so this is
            // never the last reference to a texture the committed frame still uses.
            _api.ReleaseDrawable(_drawable);
            _drawable = IntPtr.Zero;
        }

        // M-W7's APPLY, under the submit lock and after a drain. Nothing queued means no drain at all, which is
        // what keeps an ordinary boundary free: a drain per frame would be M-F5's counted wait on every single
        // frame and would report as a drain cost the design never intended to spend.
        void ApplyPending()
        {
            // NO DRAIN FOR THIS ONE. A framebufferOnly write releases and recreates nothing: a drawable already
            // vended keeps its usage, and only the drawables the layer makes afterwards change. It runs before the
            // next frame's acquire, and the acquire reads the texture's own flag either way.
            if (_pending.TakeFramebufferOnly(out bool? framebufferOnly) && framebufferOnly is { } only)
            {
                _api.SetFramebufferOnly(only);
            }

            if (!_pending.Take(out MetalDrawableSize? size, out bool? sync)) return;

            // THE DRAIN IS THE WHOLE POINT OF DEFERRING TO THE BOUNDARY. The incumbent applied a resize inline on
            // the calling thread with no drain anywhere, releasing a depth texture in-flight frames may still be
            // reading. There is no depth texture here (the seam cannot ask for one), so what this protects is the
            // drawable swap and the layer reconfiguration, and it is the seat a depth rebuild would need the day
            // the seam grows one.
            _drain();

            if (size is { } requested)
            {
                _size = requested.AtLeastOnePixel;
                _api.SetDrawableSize(_size);
                Framebuffer.Reset(_size);
            }

            if (sync is { } enabled)
            {
                // UNCONDITIONALLY (M-W2). The incumbent wrote this only inside three values of an enum
                // deprecated since macOS 10.15, so a machine outside that set loses its vsync toggle silently.
                _api.SetDisplaySyncEnabled(enabled);
                _syncApplied = enabled;
            }
        }

        void AcquireAndPublish()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acquired) return;

            // READ BEFORE THE ACQUIRE OVERWRITES IT, so the release below is a TRANSITION rather than a question
            // asked every frame. An unconditional call would be a harmless no-op on the real target and would put
            // a release in the log of every ordinary boundary, which makes "the orphan dies at the next
            // successful acquire" true only in the sense that the call happens.
            bool wasOrphanBound = _orphanBound;

            MetalAttachment attachment = Acquire();
            Framebuffer.Adopt(attachment, _size);
            _acquired = true;

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
            _drawableFramebufferOnly = acquired.FramebufferOnly;
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
