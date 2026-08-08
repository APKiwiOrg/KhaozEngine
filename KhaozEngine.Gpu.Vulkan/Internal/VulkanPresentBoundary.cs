using System;
using System.Diagnostics;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE PRESENT BOUNDARY, AND THE ONLY PLACE ANY OF THIS HAPPENS: it presents the frame just submitted if an
    /// image is held, applies any pending recreate, and acquires for the NEXT frame. It never throws, never
    /// reports failure upward, and the frame loop above it is unchanged.
    ///
    /// <para><b>THE ACQUIRE TIMING IS THE INCUMBENT'S AND THE SYNCHRONISATION IS NOT (V-W3, 2.6).</b> Acquiring
    /// at present time for the next frame is a genuinely good property, because it makes the image index known
    /// BEFORE recording starts, so nothing about record-time framebuffer resolution has to change. What the
    /// incumbent does on top of that is block the CPU on a fence with an infinite timeout, submit with no
    /// image-availability wait semaphore, and present with no wait semaphore either. That last part is a
    /// specification violation a validation layer flags, and a design cannot gate on validation while
    /// deliberately reproducing a configuration validation rejects. So the timing is kept and the
    /// synchronisation is replaced: the acquire signals a BINARY semaphore the frame's submit waits on at
    /// <c>COLOR_ATTACHMENT_OUTPUT</c>, the submit signals a render-finished semaphore, and the present waits on
    /// it. <c>KE_VULKAN_ACQUIRE=stall</c> restores the incumbent's shape exactly, for the A/B, and is not usable
    /// with the validation knob.</para>
    ///
    /// <para><b>THE <c>OUT_OF_DATE</c> BOUNDARY IS FOUR QUESTIONS RATHER THAN ONE (V-W4), and all four are
    /// answered here.</b> The recreate runs at THAT SAME boundary rather than being queued to a later one, so
    /// the semaphore handed to the failed acquire is retired by the recreate's unconditional drain instead of
    /// being reused while pending (the reuse bug) or destroyed while pending (undefined behaviour). ONE fresh
    /// acquire follows the recreate before the boundary returns, so an ordinary boundary and a recreating
    /// boundary leave the device in the SAME state and the imageless case exists in exactly one place instead of
    /// two. The retry is ONE: a second failure returns with the pending flag still set, so a surface mid-resize
    /// cannot spin the boundary. And an imageless frame binds the ORPHAN TARGET, records, submits and completes
    /// exactly like any other frame, with only its present skipped.</para>
    ///
    /// <para><b>A SKIPPED PRESENT IS NOT A SKIPPED FRAME.</b> <see cref="FramesBegun"/> counts every boundary,
    /// including the imageless ones: the device opened the frame, the recording and the submit really happened,
    /// and this is the denominator every per-frame figure is divided by. Leaving them out would understate
    /// per-frame costs on exactly the frames that were unusual.</para>
    ///
    /// <para><b>WHAT HOLDS THE SUBMIT LOCK AND WHAT DOES NOT (V-W8).</b> The present and the recreate hold it,
    /// because both go through the one queue. The surface query, the policy decision, the orphan creation and the
    /// acquire CALL hold NOTHING: none of them touches the queue, and the orphan in particular must not, because
    /// creating a texture takes the SETUP lock and the setup lock is taken before the submit lock and never after
    /// it. Every one of those is on the frame's own thread regardless.</para>
    ///
    /// <para><b>THE FRAME'S SEMAPHORE PAIR IS THE ONE PIECE OF STATE THAT IS NOT.</b> A submit can arrive on any
    /// thread (the seam nowhere says otherwise, and V-W8 says recording is lock-free and per-list), so
    /// <see cref="TakeFrameSemaphores"/> takes the submit lock and every write of the pair is made under it: the
    /// acquire's publication of it, the present's clearing of it, and every retirement's. A take that was not
    /// serialised let two first-submits of one frame both carry the same wait semaphore, which is a hang rather
    /// than an error.</para>
    ///
    /// <para><b>NOT ONE LINE OF THIS RUNS IN CI, ON ANY LEG, EVER (MV9).</b> A headless Vulkan device enables no
    /// surface extension at all, which is what lets the golden suite run on a machine with no display server, and
    /// it is also why a green golden leg is not evidence about anything in this file. That is the whole reason
    /// the state machine sits above a two-interface seam: the ordering, the retry count, the retirement rule and
    /// the counter are decidable with no loader, and a human at a window is the only instrument for the rest.</para>
    /// </summary>
    internal sealed class VulkanPresentBoundary : IDisposable
    {
        static readonly ILogger log = Log.For<VulkanPresentBoundary>();

        const int NoImage = -1;

        readonly IVulkanSurfaceApi _surfaces;
        readonly IVulkanSwapchainApi _swapchains;
        readonly IVulkanOrphanTarget _orphan;
        readonly VulkanAcquireRing _ring;
        readonly VulkanAcquireWaits _waits;
        readonly VulkanPresentPending _pending = new();
        readonly VulkanAcquireMode _mode;
        readonly object _submitLock;
        readonly Action _drain;
        readonly ILogger _log;
        readonly ulong _surface;

        VulkanSwapchainGeneration? _generation;
        VulkanExtent _requested;
        GpuPixelFormat _seamFormat = GpuPixelFormat.B8G8R8A8UNorm;
        VulkanFrameSemaphores _frame;
        long _framesBegun;
        int _heldImage = NoImage;
        bool _waitConsumed;
        bool _syncToVerticalBlank;
        bool _surfaceLost;
        bool _orphanBound;
        // WHETHER THE ORPHAN TEXTURE EXISTS, which is a different question from whether the framebuffer is
        // currently pointing at it. A recreate that succeeds repoints the framebuffer at a real image inside the
        // submit lock, and the orphan must survive that instant: destroying it there would free the image the
        // framebuffer was on until one statement ago. It goes at the next successful ACQUIRE instead, which is
        // the point at which a real image is provably bound.
        bool _orphanLive;
        bool _disposed;
        bool _saidNothingRendered;
        bool _saidNoPresentTransition;
        bool _saidUndecidable;

        /// <param name="surfaces">The surface seam, for the capability re-read every recreate does.</param>
        /// <param name="swapchains">The swapchain seam.</param>
        /// <param name="surface">The <c>VkSurfaceKHR</c>, which outlives every generation made against it.</param>
        /// <param name="requested">The backbuffer size the device was created at, used only while the surface
        /// dictates none.</param>
        /// <param name="syncToVerticalBlank">The initial vsync setting, which selects the present-mode ladder.</param>
        /// <param name="mode">Which of MV2's two acquire models this run uses.</param>
        /// <param name="framesInFlight">The device's pipeline depth, one half of the acquire ring's capacity.</param>
        /// <param name="submitLock">The device's ONE submit lock.</param>
        /// <param name="drain">Drains the timeline to the last submitted value. Called with the submit lock held,
        /// unconditionally, before every retirement, which is what makes destroying a possibly pending binary
        /// semaphore safe.</param>
        /// <param name="orphan">What an imageless frame binds.</param>
        /// <param name="waits">The device's acquire-wait accumulator, which is what MV2's gate reads.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal VulkanPresentBoundary(IVulkanSurfaceApi surfaces, IVulkanSwapchainApi swapchains, ulong surface,
            VulkanExtent requested, bool syncToVerticalBlank, VulkanAcquireMode mode, int framesInFlight,
            object submitLock, Action drain, IVulkanOrphanTarget orphan, VulkanAcquireWaits waits,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(surfaces);
            ArgumentNullException.ThrowIfNull(swapchains);
            ArgumentNullException.ThrowIfNull(submitLock);
            ArgumentNullException.ThrowIfNull(drain);
            ArgumentNullException.ThrowIfNull(orphan);
            ArgumentNullException.ThrowIfNull(waits);

            _surfaces = surfaces;
            _swapchains = swapchains;
            _surface = surface;
            _requested = requested;
            _syncToVerticalBlank = syncToVerticalBlank;
            _mode = mode;
            _submitLock = submitLock;
            _drain = drain;
            _orphan = orphan;
            _waits = waits;
            _log = logger ?? log;
            _ring = new VulkanAcquireRing(swapchains, framesInFlight);

            // THE FIRST GENERATION AND THE FIRST ACQUIRE BOTH HAPPEN HERE, which is what makes the swapchain
            // framebuffer valid from the moment the device exists rather than from the first present, and what
            // makes the image index known before the first recording starts.
            Recreate(firstGeneration: true);
            AcquireOnce();
            PublishAttachment();

            if (Framebuffer is null)
            {
                throw new InvalidOperationException(
                    "The native Vulkan present boundary finished construction with no framebuffer to publish, "
                    + "which is unreachable: either a swapchain was created or the orphan target was. This is a "
                    + "bug in the boundary rather than in the driver.");
            }
        }

        /// <summary>
        /// The swapchain's framebuffer, which is the SAME object for the whole life of the device (V-W5). This is
        /// what <c>IGpuDevice.SwapchainFramebuffer</c> hands back and it may be cached by anything: a recreate and
        /// an acquire both change what it points at and never its identity.
        /// </summary>
        internal VulkanSwapchainFramebuffer? Framebuffer { get; private set; }

        /// <summary>Frames this boundary has opened, including the ones whose present was skipped. The
        /// denominator every per-frame figure is divided by.</summary>
        internal long FramesBegun => _framesBegun;

        /// <summary>Whether an image is currently held, which is true in the steady state because the boundary
        /// acquires for the next frame before returning.</summary>
        internal bool HasImage => _heldImage != NoImage;

        /// <summary>The current generation's image count, or 0 while imageless. For the tests.</summary>
        internal int ImageCount => _generation?.ImageCount ?? 0;

        /// <summary>The acquire ring, for the tests that assert V-F5's counter indexing.</summary>
        internal VulkanAcquireRing AcquireRing => _ring;

        /// <summary>Whether the framebuffer currently points at the orphan target rather than at a swapchain
        /// image.</summary>
        internal bool IsOrphanBound => _orphanBound;

        /// <summary>Whether a recreate is queued and not yet applied.</summary>
        internal bool HasPendingRecreate => _pending.HasWork;

        /// <summary>
        /// Whether presentation syncs to the vertical blank. Settable live from any thread, which is what the
        /// settings screen needs, and unlike Direct3D 11 it reconfigures something: Vulkan cannot change a
        /// swapchain's present mode in place, so this QUEUES a recreate for the next boundary exactly as a resize
        /// does (V-W6).
        /// </summary>
        internal bool SyncToVerticalBlank
        {
            get => _syncToVerticalBlank;
            set
            {
                if (_syncToVerticalBlank == value) return;

                _syncToVerticalBlank = value;
                _pending.QueueRecreate();
            }
        }

        /// <summary>Queue a resize. Takes no lock, makes no native call and never blocks, so a window callback on
        /// any thread is safe. See <see cref="VulkanPresentPending"/>.</summary>
        internal void QueueResize(uint width, uint height)
        {
            if (_disposed) return;

            _pending.QueueResize(width, height);
        }

        /// <summary>
        /// THE PAIR THE FIRST SUBMIT OF THIS FRAME CARRIES, taken exactly once. Every later submit in the same
        /// frame gets the default, because a binary semaphore may be waited once per signal and a second wait on
        /// the same one is a hang rather than an error.
        ///
        /// <para><b>UNDER THE DEVICE'S SUBMIT LOCK, AND THAT IS STRUCTURAL RATHER THAN CAUTIOUS.</b> "Exactly
        /// once" was enforced by a plain read-modify-write over non-volatile fields, evaluated as an argument
        /// BEFORE <see cref="VulkanSubmitQueue"/> took the lock, so two threads reaching
        /// <c>IGpuDevice.Submit</c> for the first submit of one frame could both see <c>_waitConsumed</c> false
        /// and both take the pair. Two submits waiting on one binary semaphore is a hang rather than an error,
        /// which is precisely what the once contract exists to prevent. The seam does not say <c>Submit</c> is
        /// single-threaded anywhere, and V-W8 says the opposite of what would be needed to assume it: recording is
        /// lock-free and per-list on any number of threads. So the take is serialised against every other take and
        /// against the boundary's own publication of the pair, which happens under the same lock.</para>
        ///
        /// <para>Taking the lock here and again inside the submit queue is not a nesting hazard: it is the same
        /// monitor on the same thread, sequentially, and the setup lock is already released by then, so the one
        /// ordering rule V-W8 pins (setup before submit, never the reverse) is untouched.</para>
        /// </summary>
        internal VulkanFrameSemaphores TakeFrameSemaphores()
        {
            lock (_submitLock)
            {
                if (_waitConsumed || _frame.IsEmpty) return default;

                _waitConsumed = true;
                return _frame;
            }
        }

        /// <summary>
        /// PRESENT, APPLY, ACQUIRE. The whole boundary, in that order, on the submit thread.
        /// </summary>
        internal void Present()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _framesBegun++;

            PresentHeldImage();

            // A HELD IMAGE NOTHING RENDERED INTO IS KEPT RATHER THAN PRESENTED, and it is the one path that does
            // not acquire. Presenting it would either need its render-finished semaphore, which nothing signalled
            // and which would hang the presentation engine, or no wait semaphore at all, which leaves the acquire
            // semaphore pending for a ring slot that comes round again. Keeping it costs one frame of nothing
            // happening on a frame where nothing happened. A pending recreate still applies below, which is what
            // stops a window resize from being held hostage by an idle frame.
            bool keptUnrendered = _heldImage != NoImage;
            if (keptUnrendered) SayNothingRenderedOnce();

            if (_pending.HasWork) ApplyPending();

            if (_heldImage == NoImage && !_surfaceLost)
            {
                // THE ONE FRESH ACQUIRE, then THE ONE RETRY, and no more.
                if (!AcquireOnce() && _pending.HasWork)
                {
                    ApplyPending();
                    AcquireOnce();
                }

                // A BOUNDARY THAT ENDS IMAGELESS ALWAYS LEAVES THE FLAG SET, which is what "tries again next
                // time" means and what stops the imageless state from being terminal. The retry above can end
                // with no generation at all and no flag, since an acquire against nothing sets none, and a
                // boundary that then never recreated would leave the window on the orphan target for the rest of
                // the run with no error anywhere. One flag, checked once, at the one place the state is known.
                if (_heldImage == NoImage && !_surfaceLost) _pending.QueueRecreate();
            }

            PublishAttachment();
        }

        /// <summary>
        /// Destroy the generation, the acquire ring, the orphan target and the surface. The framebuffer wrapper
        /// survives holding a view that no longer exists, which is safe for the same reason every other wrapper's
        /// post-death state is: the device is going away with it and nothing will bind it again.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_submitLock)
            {
                _drain();
                _generation?.Dispose();
                _generation = null;
                _ring.Dispose();

                // THE SEAM ITSELF CAN OWN A DRIVER OBJECT, and exactly one implementation does: the stall mode's
                // VkFence, which is the only VkFence in this backend and lives entirely below this line so
                // nothing above it has to know the completion model has an exception in it. A fence still alive
                // at vkDestroyDevice is a leaked child object a validation layer reports, so the seam is asked
                // whether it has anything to release. The fakes do not, and answer by not implementing it.
                (_swapchains as IDisposable)?.Dispose();
            }

            // OUTSIDE THE LOCK, because releasing the orphan disposes an engine texture and a resource destroy is
            // a deferred entry on the retire list rather than a queue operation.
            _orphan.Release();
            _orphanLive = false;
            _surfaces.DestroySurface(_surface);
        }

        // ---- The three phases ----

        void PresentHeldImage()
        {
            if (_heldImage == NoImage || _generation is null) return;

            // THE STALL MODE PRESENTS WITH NO WAIT SEMAPHORE, which is the incumbent's shape exactly and is the
            // specification violation the mode exists to reproduce. On the shipped path the wait is the image's
            // own render-finished semaphore, and it is only there when a submit actually took the pair.
            bool rendered = _mode == VulkanAcquireMode.Stall || _waitConsumed;
            if (!rendered) return;

            ulong wait = _mode == VulkanAcquireMode.Stall ? 0 : _frame.Signal;
            SayNoPresentTransitionOnce();

            VulkanPresentOutcome outcome;
            lock (_submitLock)
            {
                outcome = _swapchains.Present(_generation.Handle, (uint)_heldImage, wait);

                // INSIDE THE LOCK WITH THE PRESENT ITSELF, so the pair a submit on another thread could take is
                // cleared in the same critical section that consumed it.
                ForgetHeldImage();
            }

            // CHECKED (V-W7). The incumbent ignores this result entirely, so it can never learn that the surface
            // it presents to has changed underneath it, which is how a window that was resized while occluded
            // comes back stretched.
            Interpret(outcome, "vkQueuePresentKHR");
        }

        void ApplyPending()
        {
            if (!_pending.Take(out VulkanExtent? size)) return;
            if (size is not null) _requested = size.Value;
            if (_surfaceLost) return;

            Recreate(firstGeneration: false);
        }

        // THE ONE ACQUIRE, in both of MV2's shapes. Answers whether an image is now held.
        bool AcquireOnce()
        {
            if (_generation is null || _surfaceLost) return false;

            VulkanPresentOutcome outcome;
            uint index;
            ulong semaphore = 0;

            if (_mode == VulkanAcquireMode.Stall)
            {
                // THE CPU BLOCKS BY CONSTRUCTION HERE, so every one of these is recorded. That is what makes the
                // stall side of the A/B read as a substantial fraction of the frame interval while the semaphore
                // side reads near zero.
                long started = Stopwatch.GetTimestamp();
                outcome = _swapchains.AcquireNextImageStalling(_generation.Handle, out index);
                _waits.Record(Stopwatch.GetTimestamp() - started);
            }
            else
            {
                semaphore = _ring.Next();

                // THE ZERO-TIMEOUT PROBE FIRST, which is what turns AcquireWaitCount into a reading rather than a
                // count of calls. A probe that comes back NotReady acquired nothing and signalled nothing, so the
                // blocking call that follows may legally reuse the same semaphore, and the time it spends is the
                // only time the CPU was actually blocked at the acquire.
                outcome = _swapchains.AcquireNextImage(_generation.Handle, semaphore, blockUntilReady: false,
                    out index);

                if (outcome == VulkanPresentOutcome.NotReady)
                {
                    long started = Stopwatch.GetTimestamp();
                    outcome = _swapchains.AcquireNextImage(_generation.Handle, semaphore, blockUntilReady: true,
                        out index);
                    _waits.Record(Stopwatch.GetTimestamp() - started);
                }
            }

            if (outcome is VulkanPresentOutcome.Success or VulkanPresentOutcome.Suboptimal)
            {
                // THE ACQUIRE CALL HELD NOTHING AND THIS PUBLICATION DOES, which is not a contradiction of V-W8:
                // what needs the lock is not the driver call, which touches no queue, but the three fields
                // TakeFrameSemaphores reads under the same lock from whatever thread a submit arrives on.
                lock (_submitLock)
                {
                    _heldImage = (int)index;
                    _frame = _mode == VulkanAcquireMode.Stall
                        ? default
                        : new VulkanFrameSemaphores(semaphore, _generation.RenderFinishedAt(_heldImage));
                    _waitConsumed = false;
                }

                // SUBOPTIMAL REALLY DID ACQUIRE, so the image is kept and the recreate is queued for the next
                // boundary rather than run underneath a frame that is about to be recorded.
                if (outcome == VulkanPresentOutcome.Suboptimal) _pending.QueueRecreate();
                return true;
            }

            Interpret(outcome, "vkAcquireNextImageKHR");
            return false;
        }

        // ---- Recreation ----

        void Recreate(bool firstGeneration)
        {
            if (!TryDecide(firstGeneration, out VulkanSwapchainSpec spec))
            {
                // THE SURFACE COULD NOT BE READ, OR WHAT IT SAID CANNOT BE TURNED INTO A SWAPCHAIN. There is
                // nothing to create against and nothing to throw at, so the frame goes on the orphan target at
                // the last size the framebuffer carried.
                FallToOrphan(LastKnownExtent());
                return;
            }

            // A ZERO-EXTENT SURFACE, which is what a minimised window reports and what the extent clamp turns into
            // a spec that reads as not creatable. There is nothing to call vkCreateSwapchainKHR with.
            if (!spec.IsCreatable)
            {
                FallToOrphan(spec.Extent.AtLeastOnePixel);
                return;
            }

            string? failure;
            lock (_submitLock)
            {
                // UNCONDITIONAL, and that is the whole retirement rule (V-W6). A binary semaphore an acquire or a
                // submit signalled that nothing waited on is left PENDING, there is no way to ask one whether it
                // is, and destroying a pending semaphore is undefined behaviour drivers mostly tolerate until they
                // do not. A drained queue is the only state in which the answer is knowable.
                _drain();

                VulkanSwapchainGeneration? made = VulkanSwapchainGeneration.TryCreate(
                    _swapchains, _surface, spec, _generation?.Handle ?? 0, out failure);

                if (made is not null)
                {
                    Adopt(made, imageIndex: 0);
                    RetireGeneration();
                    _generation = made;
                    ForgetHeldImage();

                    if (_mode == VulkanAcquireMode.Semaphore) _ring.Rebuild(made.ImageCount);

                    // The orphan is released only once a real image is bound again, which is the next successful
                    // acquire. Doing it here would destroy the image the framebuffer is pointing at until one
                    // statement ago.
                    return;
                }
            }

            // A FAILED CREATION TAKES THE OLD GENERATION WITH IT, and that is a specification fact rather than a
            // policy choice here. vkCreateSwapchainKHR RETIRES the swapchain handed to it as oldSwapchain whether
            // or not it succeeds, and a retired swapchain may already have had the images nothing acquired freed
            // underneath it. Keeping the old generation therefore does not keep a framebuffer pointing at views
            // that are still alive: it keeps one pointing at images the driver may have taken back, and it hands
            // the same now-retired handle to the next attempt, which the specification forbids
            // (VUID-VkSwapchainCreateInfoKHR-oldSwapchain-01933). So the old generation is retired here and the
            // frame binds the orphan target, exactly as a zero-extent surface does.
            //
            // THE FIRST GENERATION REFUSES BEFORE ANY OF THAT IS SAID, because there is no old generation to
            // retire and no frame to bind anything for: that is the device constructor, and its own message is
            // the whole of what a reader needs.
            if (firstGeneration) throw NoFirstSwapchain(spec, failure);

            _log.Warn("The native Vulkan backend could not create a swapchain at "
                + spec.Extent.Width + "x" + spec.Extent.Height + ": " + (failure ?? "no reason reported")
                + ". vkCreateSwapchainKHR retires the old swapchain even when it fails, so the previous "
                + "generation is retired here rather than kept, the frame binds the orphan target, and the "
                + "recreate is retried at the next present boundary with no old swapchain to pass.");
            _pending.QueueRecreate();

            FallToOrphan(spec.Extent.AtLeastOnePixel);
        }

        /// <summary>
        /// WHAT THE SURFACE SAYS AND WHAT THE POLICY MAKES OF IT, with every failure routed exactly the way an
        /// acquire's or a present's is. Answers false when there is nothing to create against, having already
        /// logged and latched whatever it found.
        /// <para>
        /// THIS EXISTS BECAUSE ALL THREE OF THESE USED TO THROW OUT OF <c>IGpuDevice.Present</c>, which is a
        /// contradiction of this type's first sentence: the capability query through
        /// <c>VulkanResultCodes.Require</c>, <c>ChooseFormat</c> on an empty format list, and
        /// <c>SeamFormatFor</c> on a format the seam cannot name. The first is the one that matters most, because
        /// <c>VK_ERROR_SURFACE_LOST_KHR</c> shows up THERE first when a window dies under a running frame loop,
        /// and the boundary had a surface-lost path for the acquire and the present and none for the query that
        /// runs before both.
        /// </para>
        /// <para>
        /// THE FIRST GENERATION STILL THROWS, on all three, because it is the device constructor rather than a
        /// frame boundary: a windowed device that cannot describe its own surface has nothing to hand back, and
        /// refusing at creation is what the failed-first-swapchain path already does.
        /// </para>
        /// </summary>
        bool TryDecide(bool firstGeneration, out VulkanSwapchainSpec spec)
        {
            spec = default;

            VulkanPresentOutcome queried = _surfaces.Query(_surface, out VulkanSurfaceReport report);
            if (queried != VulkanPresentOutcome.Success)
            {
                if (firstGeneration) throw NoFirstSurface(queried.ToString());

                // THE SAME DISCIPLINE THE ACQUIRE AND THE PRESENT GET (V-W7), through the same method: a lost
                // surface latches and stops the boundary rather than spinning on a recreate that cannot succeed,
                // and anything else queues a recreate and tries again at the next boundary.
                Interpret(queried, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
                return false;
            }

            if (report.Formats.Count == 0)
            {
                // READ AS A FAILED QUERY RATHER THAN AS AN EMPTY ANSWER. The specification requires a surface the
                // device can present to to report at least one format, and the surface seam answers an empty list
                // on ANY failed format query, so there is no case where this is a surface that simply has none.
                if (firstGeneration) throw NoFirstSurface("vkGetPhysicalDeviceSurfaceFormatsKHR reported no format");

                SayUndecidableOnce("the surface reports no format at all, which means its format query failed");
                _pending.QueueRecreate();
                return false;
            }

            spec = VulkanSwapchainPolicy.Decide(
                report, _requested, _syncToVerticalBlank, srgb: false, out string? warning);

            if (warning != null) _log.Warn(warning);

            try
            {
                // RESOLVED BEFORE ANYTHING IS CREATED, so the orphan and the swapchain framebuffer always agree
                // about the format a pipeline is validated against, on every path including the one where no
                // swapchain is ever made.
                _seamFormat = VulkanSwapchainPolicy.SeamFormatFor(spec.Format);
            }
            catch (NotSupportedException unnameable)
            {
                // CAUGHT RATHER THAN LEFT TO ESCAPE, because it is REACHABLE rather than unreachable by
                // construction: ChooseFormat's last arm takes the surface's FIRST format when the surface offers
                // no BGRA8 at all, and that can be any format the surface happens to have.
                if (firstGeneration) throw;

                SayUndecidableOnce(unnameable.Message);
                _pending.QueueRecreate();
                spec = default;
                return false;
            }

            return true;
        }

        // THE IMAGELESS TARGET, and the ONE way this boundary ever ends up with no generation: a zero-extent
        // surface, a surface that could not be read or described, and a creation that failed after the driver had
        // already retired the old swapchain. All three leave the framebuffer on the orphan, the generation retired
        // and the frame's semaphores forgotten.
        void FallToOrphan(VulkanExtent extent)
        {
            // ENSURED BEFORE THE LOCK, because creating a texture takes the SETUP lock and the setup lock is taken
            // before the submit lock and never after it (V-W8).
            VulkanAttachment attachment = _orphan.Ensure(extent, _seamFormat);

            lock (_submitLock)
            {
                // UNCONDITIONAL AND BEFORE THE RETIREMENT, for the reason the creating path's is.
                _drain();

                // PUBLISHED BEFORE THE OLD VIEWS DIE, which is the ordering rule that makes a use-after-free
                // unreachable rather than merely unlikely. A minimised window reaches exactly this.
                AdoptOrphan(attachment, extent);
                RetireGeneration();
                ForgetHeldImage();
            }

            // THE ACQUIRE RING IS LEFT EXACTLY AS IT IS, which is what the creatable path does NOT do and is
            // deliberate rather than an omission. A ring semaphore is only ever handed out by an acquire, an
            // acquire needs a generation, and there is none now, so nothing can reach a semaphore an earlier
            // acquire left signalled. The next successful creation rebuilds the whole set inside the lock before
            // any acquire can run. Rebuilding here instead would destroy the set while the queue has just drained
            // for no reason, on the one path where the device is already in trouble.
        }

        // The old generation's views, semaphores and swapchain, destroyed inside the lock and after the drain.
        void RetireGeneration()
        {
            VulkanSwapchainGeneration? dying = _generation;
            _generation = null;
            dying?.Dispose();
        }

        // THE HELD IMAGE AND THE FRAME'S SEMAPHORE PAIR, ABANDONED, and EVERY path that retires a generation goes
        // through this. The one that did not was reachable and its failure was silent: a frame that opens and
        // closes without drawing KEEPS its image (nothing rendered into it, so nothing may present it), and a
        // recreate underneath that frame left _heldImage naming an image of a destroyed generation and
        // _frame.Signal naming a destroyed VkSemaphore, which the next submit would then wait on. It also STRANDED
        // the boundary, because the imageless block in Present is guarded on _heldImage and could never run, so
        // there was no generation, no pending flag and no route back to one.
        //
        // ABANDONING THE IMAGE IS SAFE AND PRESENTING IT IS NOT. Its swapchain is being destroyed and the
        // presentation engine takes every one of its images back with it, so there is nothing left to hand back.
        void ForgetHeldImage()
        {
            _heldImage = NoImage;
            _frame = default;
            _waitConsumed = false;
        }

        // ---- Publishing ----

        void PublishAttachment()
        {
            if (_heldImage != NoImage && _generation is not null)
            {
                Adopt(_generation, _heldImage);

                // THE ORPHAN GOES ONLY ONCE A REAL IMAGE IS BOUND AGAIN, which is this line and nowhere else.
                // Releasing it at the recreate would destroy the image the framebuffer was pointing at on exactly
                // the path that needed it.
                if (_orphanLive)
                {
                    _orphan.Release();
                    _orphanLive = false;
                }

                return;
            }

            // A LIVE GENERATION WITH NO IMAGE HELD keeps whatever the framebuffer already points at, which is a
            // view of that generation and is therefore alive. Nothing to publish and nothing to destroy.
            if (_orphanBound || _generation is not null) return;

            // IMAGELESS WITH NO GENERATION AT ALL.
            VulkanExtent extent = LastKnownExtent();
            AdoptOrphan(_orphan.Ensure(extent, _seamFormat), extent);
        }

        // The last size the framebuffer carried, clamped to at least one pixel, or the size the device was created
        // at while there is no framebuffer yet. What the orphan is sized on when no surface reading says otherwise.
        VulkanExtent LastKnownExtent()
            => (Framebuffer is null
                ? _requested
                : new VulkanExtent(Framebuffer.Width, Framebuffer.Height)).AtLeastOnePixel;

        void Adopt(VulkanSwapchainGeneration generation, int imageIndex)
        {
            VulkanAttachment attachment = generation.AttachmentAt(imageIndex);
            _orphanBound = false;

            if (Framebuffer is null)
            {
                Framebuffer = new VulkanSwapchainFramebuffer(
                    generation.SeamFormat, attachment, generation.Extent);
                return;
            }

            Framebuffer.Adopt(attachment, generation.Extent);
        }

        void AdoptOrphan(VulkanAttachment attachment, VulkanExtent extent)
        {
            _orphanBound = true;
            _orphanLive = true;

            if (Framebuffer is null)
            {
                Framebuffer = new VulkanSwapchainFramebuffer(attachment.Format, attachment, extent);
                return;
            }

            Framebuffer.Adopt(attachment, extent);
        }

        // ---- Results ----

        void Interpret(VulkanPresentOutcome outcome, string call)
        {
            switch (outcome)
            {
                case VulkanPresentOutcome.Success:
                case VulkanPresentOutcome.NotReady:
                    return;

                case VulkanPresentOutcome.Suboptimal:
                case VulkanPresentOutcome.OutOfDate:
                    _pending.QueueRecreate();
                    return;

                case VulkanPresentOutcome.SurfaceLost:
                    _surfaceLost = true;
                    _log.Error($"{call} reported the surface lost on the native Vulkan backend. Recreating the "
                        + "swapchain against a lost surface fails the same way, so the boundary stops presenting "
                        + "rather than spinning on a recreate that cannot succeed. Frames still record, submit and "
                        + "complete into the orphan target, and the window will not update again.");
                    return;

                case VulkanPresentOutcome.DeviceLost:
                    // Latched, logged and put in the telemetry session header at the call's own site (V-G4).
                    // Nothing more to say and nothing to throw: a lost device is not something this boundary can
                    // be retried past.
                    return;

                default:
                    _log.Warn($"{call} failed on the native Vulkan backend, which in practice means the process or "
                        + "the device is out of memory. The boundary queues a recreate and tries again next "
                        + "frame.");
                    _pending.QueueRecreate();
                    return;
            }
        }

        void SayNothingRenderedOnce()
        {
            if (_saidNothingRendered) return;
            _saidNothingRendered = true;

            _log.Debug("A native Vulkan present boundary found a swapchain image nothing had rendered into, so it "
                + "kept the image and skipped both the present and the next acquire. That is a frame with no "
                + "submit in it, which is legal and usually means the consumer opened a frame and closed it "
                + "without drawing.");
        }

        void SayNoPresentTransitionOnce()
        {
            if (_saidNoPresentTransition) return;
            _saidNoPresentTransition = true;

            _log.Info("The native Vulkan backend is presenting swapchain images that nothing has transitioned to "
                + "PRESENT_SRC_KHR. The barrier tracker that owns that transition is row 14 "
                + "(https://github.com/APKiwiOrg/KhaozEngine/issues/524) and the draws that would fill the image "
                + "are row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525), so until both land a "
                + "validation layer will report the layout on every present and the window will show whatever the "
                + "driver left in the image. The swapchain itself, the acquire, the resize and the teardown are "
                + "real.");
        }

        // BOTH OF THE RECREATE'S DECISION FAILURES ARE PERSISTENT CONDITIONS a boundary re-reads at every present,
        // so they are said ONCE rather than once per frame. One flag covers the pair because either one leaves the
        // window on the orphan target and neither is more actionable once the other has been reported.
        void SayUndecidableOnce(string detail)
        {
            if (_saidUndecidable) return;
            _saidUndecidable = true;

            _log.Error("The native Vulkan backend cannot decide a swapchain for its surface, so the window will "
                + "not update: " + detail + ". Frames still record, submit and complete into the orphan target, "
                + "and the boundary re-reads the surface at every present in case what it reports changes.");
        }

        static InvalidOperationException NoFirstSurface(string reason)
            => new("The native Vulkan backend could not describe the surface it was asked to present to: "
                + reason + ". A windowed device whose surface cannot be read has nothing to create a swapchain "
                + "against, so creation fails here rather than handing back a device that renders into a window "
                + "that never updates. Select GpuBackendKind.Vulkan, which goes through Veldrid, for a working "
                + "windowed device on this machine.");

        static InvalidOperationException NoFirstSwapchain(in VulkanSwapchainSpec spec, string? failure)
            => new("The native Vulkan backend could not create its FIRST swapchain at "
                + spec.Extent.Width + "x" + spec.Extent.Height + ": " + (failure ?? "no reason reported")
                + ". A windowed device with no swapchain has nothing to present to, so creation fails here rather "
                + "than handing back a device that renders into a window that never updates. Select "
                + "GpuBackendKind.Vulkan, which goes through Veldrid, for a working windowed device on this "
                + "machine.");
    }
}
