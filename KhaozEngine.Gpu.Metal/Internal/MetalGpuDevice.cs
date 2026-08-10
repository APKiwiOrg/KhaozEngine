using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The engine's native Metal device as the GPU seam sees it: a real <c>MTLDevice</c> and a real
    /// <c>MTLCommandQueue</c> that create and tear down cleanly.
    /// <para>
    /// <b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> Rows 4, 6
    /// and 7 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> have landed: the interop layer, the
    /// device, the queue, <c>KE_METAL_DEVICE</c>, the validation report, the command-buffer error latch, the
    /// liveness token and a drain before teardown (row 4), the resource factory, the shared sampler pair, the
    /// device-level uploads, the setup command buffer and <c>Map</c> with its read drain (row 6), and the
    /// completion timeline, the command list and the SUBMIT path (row 7). The swapchain is row 15, so the members
    /// that row owns throw a message saying so rather than returning something that fails later somewhere less
    /// informative, with three deliberate exceptions whose remarks below carry the reasons:
    /// <see cref="Counters"/> returns an absent default (row 16's channels, and absent is not zero),
    /// <see cref="SwapchainFramebuffer"/> returns null (the headless answer is null, not a throw), and
    /// <see cref="SyncToVerticalBlank"/> is a backing value until row 15 gives it a swapchain to reconfigure.
    /// Both sibling backends landed the same way and had this paragraph rewritten at every fill-in, which is the
    /// discipline it is under here too: it is a ledger, and a stale one is worse than none.
    /// </para>
    /// <para>
    /// <b>THE MEMBERS THESE ROWS OWN ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the part
    /// row 4 can read honestly, see below), <see cref="Diagnostics"/> with both of its fields, both
    /// <c>Submit</c> overloads, <see cref="WaitForIdle"/>, <see cref="Dispose"/>, and row 6's whole resource half
    /// in <c>MetalGpuDevice.Resources.cs</c>. The list the submit path takes comes from
    /// <see cref="CreateCommandList"/>, which the seam reaches through
    /// <c>IGpuResourceFactory.CreateCommandList</c>.
    /// </para>
    /// <para>
    /// <b><see cref="Capabilities"/> IS PARTIAL AND SAYS WHICH PART.</b> Row 16 owns the capability read and the
    /// ZERO-permitted-difference parity test against the incumbent (section 14). What this row fills is
    /// everything readable off a device with no renderer on it, and <c>MaxMsaaSampleCount</c> is pinned to 1
    /// rather than guessed, because M-C3 says the incumbent's own computation is what row 16 reproduces and a
    /// formula invented here would be a silent lie <c>AntiAliasing.ResolveFor</c> would act on. Nothing selects
    /// this backend, so a conservative 1 costs nothing and an invented value would cost the parity test its
    /// meaning.
    /// </para>
    /// <para>
    /// <b>TEARDOWN IS M-F6's ORDER AND NOTHING ELSE:</b> drain first, stop the completion route, then flip the
    /// liveness token inside the lifecycle lock, then dispose the timeline and release the queue and the device.
    /// The incumbent already waits first, so the drain half is reproduction rather than repair, which is the one
    /// place this backend inherits a correct teardown instead of fixing one.
    /// </para>
    /// </summary>
    internal sealed partial class MetalGpuDevice : IGpuDevice
    {
        static readonly ILogger log = Log.For<MetalGpuDevice>();

        readonly MetalDeviceLiveness _liveness;
        readonly MetalDeviceLossLatch _loss;
        readonly MTLDevice _device;
        readonly MetalTimeline _timeline;
        readonly MetalSetupCommands _setup;
        readonly MetalResourceFactory _factory;
        readonly object _lifecycle = new();

        // THE ONE SERIALISED POINT IN THE FRAME (M-N2). -commit ENQUEUES, and a Metal queue executes in enqueue
        // order, so submit order is the observable order the GPU seam documents only if commits are serialised.
        // The timeline value is allocated and encoded inside this same lock, or two threads could encode in one
        // order and commit in another, which is the half MetalTimeline.EncodeSignalForSubmit cannot enforce
        // itself. Recording is deliberately NOT under it: N lists record concurrently (M-R3). The setup batch's
        // own gate is never taken under it either, which is why Submit flushes BEFORE it acquires this.
        readonly object _submitLock = new();

        readonly MetalCommandBufferSource _commandBuffers;
        readonly MetalUncommittedBuffers _uncommitted;

        // THE RENDER SEAM, ONE PER DEVICE RATHER THAN ONE PER LIST, which is what MetalRenderApi's own header
        // says of itself: it is a readonly struct carrying nothing per pass and nothing per list, so a second
        // instance could differ from the first in nothing at all. It is held here as the INTERFACE, which is the
        // form a list takes it in, so this is also one box for the device instead of one per CreateCommandList.
        readonly IMetalRenderApi _renderApi;

        // MM4's depth, and the two subsystems that are the only things in this backend sized by it: the uniform
        // ring's segments (M-M3) and each list's staging arena slots (M-M8). No command buffer anywhere is
        // allocated per frame in flight, which is the whole of M-R2.
        readonly int _framesInFlight;
        readonly MetalBackpressure _backpressure;
        readonly MetalRingAllocator _rings;
        readonly IMetalStagingSource _staging;

        MetalSampler _pointSampler = null!;
        MetalSampler _linearSampler = null!;

        bool _disposed;
        bool _syncToVerticalBlank;

        [SupportedOSPlatform("macos")]
        MetalGpuDevice(MTLDevice device, MTLCommandQueue queue, GpuCapabilities capabilities,
            MetalDeviceLiveness liveness, MetalDeviceLossLatch loss, MetalTimeline timeline,
            MetalUncommittedBuffers uncommitted, IMetalSetupNative setupNative, int framesInFlight,
            IMetalStagingSource staging)
        {
            _device = device;
            Queue = queue;
            _liveness = liveness;
            _loss = loss;
            _timeline = timeline;
            _uncommitted = uncommitted;
            _commandBuffers = new MetalCommandBufferSource(queue);
            Capabilities = capabilities;
            _setup = new MetalSetupCommands(setupNative, liveness);
            _framesInFlight = framesInFlight;
            _staging = staging;
            _renderApi = new MetalRenderApi();

            // THE ONE RING ALLOCATOR (M-M3), sharing the submit lock rather than owning one. It has to read
            // MetalTimeline.LastSubmitted under exactly the lock that orders the commit which registers it, or
            // the segment owner it records would name a submission that had not happened yet.
            _backpressure = new MetalBackpressure();
            _rings = new MetalRingAllocator(framesInFlight, timeline, _backpressure, _submitLock);

            _factory = new MetalResourceFactory(this);
        }

        /// <inheritdoc/>
        public GpuBackendKind Backend => GpuBackendKind.MetalNative;

        /// <inheritdoc/>
        public GpuCapabilities Capabilities { get; }

        /// <summary>The ONE queue this device has (M-N2), created once and thread-safe by Metal's own contract.
        /// Held here because it is the device's, and read by the timeline row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571) and the command-list row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), which are the two that submit through it.</summary>
        internal MTLCommandQueue Queue { get; }

        /// <summary>The device handle, for the rows that create objects on it. Internal because nothing outside
        /// this package may name an Objective-C handle, which is what keeps <c>GpuPublicApiTests</c>'s
        /// one-exported-type pin meaningful.</summary>
        internal MTLDevice Handle => _device;

        /// <summary>The liveness token every wrapper this device creates will be handed (M-F6).</summary>
        internal MetalDeviceLiveness Liveness => _liveness;

        /// <summary>The latch every site that can see a command-buffer failure reports through (M-G4). The
        /// completion handler on every submitted buffer is the one that calls it on every frame, through
        /// <see cref="MetalCompletionErrorRoute"/>.</summary>
        internal MetalDeviceLossLatch Loss => _loss;

        /// <summary>The device's one completion timeline (M-F1). Row 6's factory reads it for
        /// <c>CreateFence</c> (https://github.com/APKiwiOrg/KhaozEngine/issues/572) and the uniform ring reads it
        /// for its segment gate.</summary>
        internal MetalTimeline Timeline => _timeline;

        /// <summary>
        /// The device's ONE uniform ring allocator (M-M3). Every ring-backed buffer this device creates rotates
        /// on its segment index, every <c>Begin</c> advances it, and a device-level write to a uniform buffer
        /// goes through its every-segment path (M-M5).
        /// </summary>
        internal MetalRingAllocator Rings => _rings;

        /// <summary>
        /// The ring's segment stalls, cumulative since the device was created. MM4's exit criterion is that this
        /// count is ZERO across a whole capture window at the default depth, and on this backend it has exactly
        /// one source (M-R2), so a non-zero reading is unambiguous. Row 16
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/582) reads it for
        /// <c>GpuDeviceCounters.BackpressureStallCount</c> and <c>BackpressureStallMs</c>.
        /// </summary>
        internal MetalWaitTotals BackpressureTotals => _backpressure.Totals;

        /// <summary>How many uncommitted command buffers this device is holding, against section 6.1's bound.
        /// Read by the device-free assertion and by row 16's counter fill.</summary>
        internal MetalUncommittedBuffers Uncommitted => _uncommitted;

        /// <summary>
        /// A command list against this device's queue (M-R1, M-R2). Internal because the seam hands lists out
        /// through <c>IGpuResourceFactory.CreateCommandList</c>, which is row 6's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/572): that factory calls this, and so does the
        /// <c>[GpuFact]</c> submit path.
        /// </summary>
        /// <param name="clearMode">M-A2's position for this list. Defaults to the ONE reading of
        /// <c>KE_METAL_CLEAR</c> this process took, which is what every real caller gets. A test passes a literal
        /// instead, because reading the environment once per process means a test that mutated it would be racing
        /// every other list in the same collection.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MetalCommandList CreateCommandList(MetalClearMode? clearMode = null)
            => new(_commandBuffers, _uncommitted, new MetalEncoderSink(), this, _rings,
                // A FRESH ARENA PER LIST (M-M8), not one shared by the device. Two lists recording on two threads
                // must not sub-allocate from the same blocks, and the recycling proof is per list too: each slot
                // remembers the timeline value THAT list's submission took. The list disposes it. It takes the
                // liveness token because its block creation is a native allocation on this device (M-F6).
                new MetalStagingArena(_staging, _framesInFlight, liveness: _liveness), new MetalBlitApi(),
                _liveness, _renderApi,
                // M-A2's POSITION, READ ONCE PER PROCESS AND COPIED PER LIST. Reading the environment per pass
                // would let a mid-run change split one frame's clears between two policies, which is a shape the
                // gate-1 A/B could not interpret. See MetalClearPolicy.
                clearMode ?? MetalClearPolicy.Current);

        /// <inheritdoc/>
        /// <remarks>M-G2's <c>softwareAdapter</c> is ALWAYS false with confidence rather than null, because Apple
        /// ships no software Metal rasterizer at all. That is a genuinely different answer from "nobody asked",
        /// which is what the struct documents null as meaning and what the Veldrid Metal path correctly keeps.
        /// <c>deviceLossReason</c> comes from the latch, is null until something is latched, and is the header
        /// field #427 asks for.</remarks>
        public GpuDeviceDiagnostics Diagnostics => new(softwareAdapter: false, _loss.HeaderValue);

        /// <inheritdoc/>
        /// <remarks>Row 16 fills these from the subsystems that count them, none of which exist yet. The default
        /// is the honest answer meanwhile, and the struct's own doc already says absent is not zero, so a capture
        /// from this row reports no channels rather than reporting zeros a reader would take for
        /// measurements.</remarks>
        public GpuDeviceCounters Counters => default;

        /// <inheritdoc/>
        /// <remarks>Null, and correct rather than unbuilt: this row creates HEADLESS devices only, and a headless
        /// device has no swapchain by definition. The windowed path is row 15, and it refuses at creation rather
        /// than handing back a device that cannot present.</remarks>
        public IGpuFramebuffer? SwapchainFramebuffer => null;

        /// <inheritdoc/>
        /// <remarks>A backing value on a headless device, which is what the seam asks for. It reconfigures
        /// nothing because there is no swapchain to reconfigure, and row 15 is where it starts meaning something
        /// (M-W2 makes it an unconditional <c>displaySyncEnabled</c> on the layer rather than the incumbent's
        /// three-value enum test).</remarks>
        public bool SyncToVerticalBlank
        {
            get => _syncToVerticalBlank;
            set => _syncToVerticalBlank = value;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// M-F1's SIGNAL, M-F2's HANDLER AND THE COMMIT, ALL UNDER <c>_submitLock</c>. The value is allocated and
        /// encoded into the buffer inside the lock, the completion handler is attached, the buffer is committed,
        /// and the value is registered as submitted. Every step is inside, because <c>-commit</c> ENQUEUES and a
        /// queue executes in enqueue order: submit order is the observable order only if commits are serialised,
        /// and encoding outside the lock would let two threads encode in one order and commit in another.
        /// <para>
        /// EVERY COMMITTED BUFFER GETS THE HANDLER (M-F2, M-G4), in every configuration and behind no knob. It is
        /// the only place a Metal command-buffer failure is ever reported, which the incumbent does not do at
        /// all, and a latch built on checks that compile away in Release never fires.
        /// </para>
        /// <para>
        /// AND THE PENDING SETUP BATCH IS FLUSHED FIRST, OUTSIDE THAT LOCK (M-M9). It is the third of the batch's
        /// three flush sites, the other two being both <c>Map</c> overloads and <see cref="WaitForIdle"/>. See
        /// <see cref="SubmitCore"/> for the two separate reasons the position matters.
        /// </para>
        /// <para>
        /// A SUBMIT ON A DEAD DEVICE IS A NO-OP that still consumes the seal, rather than a throw. The seam has no
        /// recovery path and the frame loop above it is not written to handle one: the work has already been
        /// discarded by the driver, and a device that has gone reports failures from every later call anyway. The
        /// seal is consumed so the list is reusable, and the buffer is released rather than committed into a
        /// queue nothing can advance.
        /// </para>
        /// </remarks>
        public void Submit(IGpuCommandList cl) => SubmitCore(cl, fence: null);

        /// <inheritdoc/>
        /// <remarks>The same path, with the fence armed at the value THIS submission signals, inside the same
        /// lock. One monotonic shared event is what makes the seam's ordering promise a theorem rather than a
        /// convention: the counter reaching this value requires every earlier signal to have happened, so polling
        /// a later fence transitively covers every earlier submission.</remarks>
        public void Submit(IGpuCommandList cl, IGpuFence fence) => SubmitCore(cl, fence);

        // THE ROUTING IN ONE PLACE rather than at each of the two overloads, so the fenced and unfenced paths
        // cannot drift apart by an edit to one of them. That matters more here than it looks: the two differ by
        // exactly one line inside the submit lock, and a second copy of everything around it is how the two ended
        // up encoding their signal at different points on a sibling backend.
        void SubmitCore(IGpuCommandList cl, IGpuFence? fence)
        {
            ArgumentNullException.ThrowIfNull(cl);

            MetalCommandList list = RequireList(cl, this);
            MetalGpuFence? armed = fence is null ? null : RequireFence(fence, _timeline);

            // THE GUARD IS READ INLINE HERE rather than folded into the argument below, because CA1416 reads the
            // guard property AT THE CALL SITE and a value passed in hides it (M-P1). It is the same reason
            // MetalResourceFactory spells the same line at each of its three creation members.
            if (_liveness.IsDead || !KhaozEngineMetal.IsPlatformSupported)
            {
                PrepareForCommit(list, _setup, alive: false);
                return;
            }

            // Non-zero by construction on this arm: a sealed list holds a real buffer, because Begin throws
            // rather than sealing over a nil one.
            IntPtr buffer = PrepareForCommit(list, _setup, alive: true);

            SubmitOnMacOs(list, buffer, armed);
        }

        /// <summary>
        /// EVERYTHING A SUBMIT DOES BEFORE IT TAKES THE LOCK, in the one order M-M9 and M-N2 fix between them:
        /// read the seal, answer a dead device, then flush the pending setup batch. The
        /// <c>MTLCommandBuffer</c> the caller is to commit, or <see cref="IntPtr.Zero"/> when this submit is a
        /// no-op. A live sealed list always holds a non-zero buffer, because <see cref="MetalCommandList.Begin"/>
        /// throws rather than sealing over a nil one, so zero is unambiguous.
        ///
        /// <para><b>THE SEAL IS READ FIRST</b>, so a list with no sealed recording is refused by name rather than
        /// inside the one serialised point in the frame.</para>
        ///
        /// <para><b>A DEAD DEVICE CONSUMES THE SEAL AND FLUSHES NOTHING.</b> A list whose recording cannot be
        /// submitted is still reusable, and holding the buffer would keep it counted against the queue's own
        /// uncommitted maximum. The flush is skipped because committing to a queue whose device has gone is the
        /// one call this backend's dead-device posture exists to avoid.</para>
        ///
        /// <para><b>AND THE SETUP BATCH FLUSHES HERE, WHICH IS TWO SEPARATE CLAIMS.</b> ORDERING: <c>-commit</c>
        /// ENQUEUES and a Metal queue runs its buffers in enqueue order, so committing the pending uploads before
        /// this list's buffer is what makes a recording that samples a texture uploaded through
        /// <c>UpdateTexture</c> see the uploaded bytes. Flushing after the commit would put the upload behind the
        /// frame that reads it, which is a wrong pixel rather than a failure. LOCKING: the batch has its OWN gate,
        /// and this is two sequential acquisitions rather than a nested pair. Taking that gate while holding
        /// <c>_submitLock</c> would invent an ordering rule between two locks that today have none, and
        /// <see cref="MetalSetupCommands.Flush"/>'s own doc is where row 6 wrote that obligation down.</para>
        ///
        /// <para><b>STATIC, TAKING LIVENESS AS A PLAIN BOOL, so the whole pre-lock decision is device-free</b> and
        /// a plain <c>[Fact]</c> drives it with a fake command-buffer source and a fake setup batch on a machine
        /// with no Metal. That is the same split <see cref="RequireList"/> and
        /// <see cref="MetalCompletionHandler.Deliver"/> already take, and it is what makes the order an assertion
        /// rather than a comment: the commit lives in <see cref="SubmitOnMacOs"/>, which cannot run until this
        /// has returned.</para>
        /// </summary>
        internal static IntPtr PrepareForCommit(MetalCommandList list, MetalSetupCommands setup, bool alive)
        {
            IntPtr buffer = list.SealedCommandBuffer;

            if (!alive)
            {
                list.DiscardRecording();
                return IntPtr.Zero;
            }

            setup.Flush();
            return buffer;
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void SubmitOnMacOs(MetalCommandList list, IntPtr buffer, MetalGpuFence? fence)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            ulong value;

            lock (_submitLock)
            {
                value = _timeline.EncodeSignalForSubmit(buffer);

                // BEFORE THE COMMIT. Metal refuses a handler added to a buffer that has already been committed,
                // and a buffer that completed before its handler was attached would report nothing at all.
                MetalCompletionHandler.AttachTo(buffer);

                new MTLCommandBuffer(buffer).Commit();

                _timeline.RegisterSubmitted(value);
                fence?.Arm(value);

                // INSIDE THE LOCK, which is what this member's own doc has always said and what the ring's gate
                // needs to be true. The value travels with it because two subsystems gate on exactly the value
                // the submission that read them signals: the list's staging arena for its blocks (M-M8), and the
                // uniform ring for the segment this recording captured (M-M3). Doing it after the release of the
                // lock would leave a window in which a concurrent Begin could rotate onto that segment before it
                // had an owner, and at a depth of one that window is a single Begin wide.
                list.MarkSubmitted(value);
            }

            // AFTER the commit and outside the lock: the queue retains a committed buffer until it completes, so
            // this cannot free one the GPU is running, and the release is not something the ordering depends on.
            _commandBuffers.Release(buffer);
        }

        /// <summary>
        /// The list <paramref name="cl"/> is, or a refusal naming <paramref name="device"/> as the one that had to
        /// have created it.
        /// <para>
        /// IDENTITY, NOT TYPE, AND THE DIFFERENCE IS NOT THEORETICAL. A process can hold up to
        /// <see cref="MetalCompletionHandler.MaxRegisteredQueues"/> live native Metal devices at once, and a type
        /// check passes a list belonging to ANOTHER one of them. Committing that list here would take this
        /// device's submit lock and then commit a buffer on the other device's queue, encoding this timeline's
        /// shared event into it: two devices would be committing to that queue in an order neither lock orders, so
        /// <see cref="MetalTimeline.LastSubmitted"/> would stop meaning what it says on both of them. Reference
        /// identity is what makes the message that was always written here true.
        /// </para>
        /// <para>
        /// STATIC AND TAKING THE OWNER, so the whole decision is device-free and a plain <c>[Fact]</c> drives it
        /// with two owners on a machine with no Metal. That is the same split the completion path takes with
        /// <see cref="MetalCompletionHandler.Deliver"/>.
        /// </para>
        /// </summary>
        internal static MetalCommandList RequireList(IGpuCommandList cl, object device)
        {
            if (cl is MetalCommandList list && ReferenceEquals(list.Owner, device)) return list;

            throw new ArgumentException(
                "That command list was not created by this native Metal device, so it holds no MTLCommandBuffer "
                + "this device's queue can commit. Create command lists through the device you submit them to. A "
                + "list from a DIFFERENT native Metal device is refused here too, by reference rather than by "
                + "type: committing it would encode this device's shared event into a buffer belonging to another "
                + "queue, outside the lock that orders that queue's commits, and submit order is the observable "
                + "order only while every commit to a queue goes through one lock.",
                nameof(cl));
        }

        /// <summary>
        /// The fence <paramref name="fence"/> is, or a refusal naming <paramref name="timeline"/> as the one it had
        /// to have been created on. Identity for the same reason as <see cref="RequireList"/>, and each device has
        /// exactly one timeline (M-F1), so timeline identity is device identity.
        /// <para>
        /// THE FAILURE THIS PREVENTS IS SILENT. A fence armed against another device's counter is not a crash and
        /// not an exception: it reports <see cref="IGpuFence.Signaled"/> as soon as THAT device happens to reach
        /// the value, which is work this device never ran, and a consumer polling it frees resources the GPU here
        /// is still reading.
        /// </para>
        /// </summary>
        internal static MetalGpuFence RequireFence(IGpuFence fence, MetalTimeline timeline)
        {
            if (fence is MetalGpuFence mine && ReferenceEquals(mine.Timeline, timeline)) return mine;

            throw new ArgumentException(
                "That fence was not created by this native Metal device, so it names no value on this device's "
                + "timeline. Create fences through the device you submit against. A fence from a DIFFERENT native "
                + "Metal device is refused here too, by reference rather than by type: arming it would point it at "
                + "a value on the other device's counter, and it would then read signalled for work this device "
                + "never ran.",
                nameof(fence));
        }

        /// <summary>
        /// Block until the GPU is idle. A SAFE NO-OP after the device is dead (M-F6): a torn-down or lost device
        /// has no outstanding work left to finish, so returning is the honest answer and waiting would wait on a
        /// queue nothing can advance.
        /// <para>
        /// M-F5's DRAIN, on the timeline: <c>waitUntilSignaledValue:timeoutMS:</c> for the last submitted value,
        /// counted into <c>DrainCount</c> and <c>DrainMs</c>, waiting in slices so the liveness flip that a
        /// command-buffer failure produces on Metal's own completion thread is observable at all. Metal has no
        /// device-level wait, so there is no <c>vkDeviceWaitIdle</c> equivalent to call, and a value on the
        /// timeline is both the thing to wait for and the thing to time.
        /// </para>
        /// <para>
        /// THE FAILURE READING IS NOT HERE ANY MORE, AND THAT IS THE POINT OF M-G4's HANDLER RATHER THAN A LOSS.
        /// Until the submit path existed there was nothing to attach a handler to, so the drain's own command
        /// buffer was the only place a command-buffer failure could be observed at all. Now every committed
        /// buffer carries the handler, in every configuration, so the reading happens once per buffer at the site
        /// that saw it instead of once per drain on a buffer that carried none of the work.
        /// </para>
        /// <para>
        /// AND IT IS TWO DRAINS RATHER THAN ONE, WHICH IS THE ROW 6 AND ROW 7 SEAM. The setup batch (M-M9) is
        /// flushed first, and a flushed batch is a committed command buffer that signals NO timeline value, so
        /// M-F5's counted drain cannot see it. The second drain is the queue's, taken only when the flush
        /// actually committed something, and it covers the batch by the same enqueue-order argument the teardown
        /// drain rests on. A frame loop that uploads nothing pays for exactly one drain, which is M-F5's.
        /// </para>
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            // THE SETUP BATCH FIRST (M-M9). A drain that ran before the flush would wait for everything except
            // the uploads the caller has just made, which is exactly the case an explicit drain is asked for.
            // The flush COMMITS and does not wait, and it says whether it committed anything, because that is
            // what decides the second drain below.
            bool committed = _setup.Flush();

            // M-F5's DRAIN, counted, over everything a Submit ever took to the queue.
            _timeline.WaitForIdle();

            // AND THE QUEUE DRAIN FOR THE BATCH THE FLUSH JUST COMMITTED, which the timeline cannot cover. A
            // setup batch encodes NO timeline signal (see DrainForRead in MetalGpuDevice.Resources.cs for the
            // whole argument and why it is not given one), so it is invisible to a counted drain, and the flush
            // above just put it BEHIND everything the timeline knows about. An empty command buffer committed
            // after it and waited on is what covers it, by the same enqueue-order argument teardown already
            // rests on. Skipped when the flush committed nothing, so the frame loop's own WaitForIdle is exactly
            // M-F5's drain and nothing more.
            if (committed) DrainCommittedSetupBatch();
        }

        /// <inheritdoc/>
        public void ResizeSwapchain(uint w, uint h) => throw NotBuiltYet("Resizing the swapchain", SwapchainRow);

        /// <inheritdoc/>
        public void Present() => throw NotBuiltYet("Presenting a frame", SwapchainRow);

        /// <summary>
        /// Tear the device down, in the ONE order M-F6 permits: DRAIN first, then the liveness flip, then release
        /// the queue and the device.
        /// <para>
        /// The incumbent already calls its wait first, so this is reproduction rather than repair. It is still
        /// established HERE rather than left to whichever later row adds the first releasable resource, because a
        /// teardown order established once is an order every later row inherits, and the row that adds a resource
        /// is not the row thinking about ordering.
        /// </para>
        /// <para>
        /// A DEAD DEVICE SKIPS THE DRAIN AND THE RELEASES BOTH. On the loss path the driver has already given up
        /// on the work, so waiting would wait for something that will not arrive, and releasing is a call into a
        /// device that reported itself gone. Leaking the two handles to the process end is the cost, and it is
        /// the right one: this is the same trade the liveness token makes for every wrapper.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;

                if (_liveness.IsDead) return;
                if (!KhaozEngineMetal.IsPlatformSupported) return;

                DisposeOnMacOs();
            }
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void DisposeOnMacOs()
        {
            // The setup batch is ABANDONED rather than committed, because the drain below is about to wait and
            // the resources it was filling are being released in the same breath. Its staging buffers go here.
            _setup.Dispose();

            // FIRST. Releasing a device with work in flight is releasing objects the GPU may still be reading.
            // The QUEUE drain rather than the timeline's, deliberately, and it is the stronger of the two here: a
            // buffer that completes is proof that everything committed before it has completed too, which covers
            // M-W6's present buffer, and that one never signals the timeline because it carries no work the
            // timeline allocated a value for.
            MetalCommandBufferFault fault = Drain("waitUntilCompleted (teardown drain)");

            // THE COMPLETION ROUTE STOPS ON EVERY PATH, including the failure one. The table is four slots wide
            // for the whole process, so a queue left in it holds a slot for the life of the process on a device
            // that is going away, and a completion arriving after this point has nothing left to latch on to.
            MetalCompletionHandler.Unregister(Queue.Handle);

            // A drain that saw a failure has already flipped liveness through the latch, so there is nothing left
            // that is safe to release and the handles leak deliberately, the shared event with them.
            if (fault.IsFailure) return;

            // BEFORE THE FLIP, and that ordering is load-bearing rather than tidy. The shared samplers are the
            // device's own (nothing else holds a reference, and a consumer disposing PointSampler would be
            // disposing something it did not create), and every wrapper's Dispose is a no-op once liveness is
            // dead, so releasing them after the flip would leak both for the life of the process.
            _pointSampler.Dispose();
            _linearSampler.Dispose();

            // Flipped BEFORE the releases and after the drain, so no wrapper can observe "alive" after the object
            // it would release has gone, and so a wrapper disposed on another thread mid-teardown becomes a
            // no-op rather than a call against a released object.
            _liveness.MarkDead();

            // AFTER the flip, which is the order MetalTimeline.Dispose documents and the only thing standing
            // between the shared event's unconditional release and a fence polled mid-teardown messaging a
            // released object. The release is unconditional because an MTLSharedEvent is an ordinary
            // reference-counted object with no vkDestroyDevice rule in front of it, so skipping it on a dead
            // device would leak it on the path that matters.
            _timeline.Dispose();

            Queue.Release();
            _device.Release();
        }

        // The queue drain that covers a just-committed setup batch, split out only because Drain is macOS-gated
        // and WaitForIdle's own body is not. See WaitForIdle for why the timeline's drain cannot do this one.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void DrainCommittedSetupBatch() => Drain("waitUntilCompleted (setup batch drain)");

        // The SYNCHRONOUS way a command-buffer reading reaches the latch. The other is the completion handler on
        // every submitted buffer, through MetalCompletionErrorRoute, and the two are kept apart because they read
        // different snapshots on different threads. The site name travels in rather than being inferred
        // downstream, which is what "latched at the fault site" means.
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        MetalCommandBufferFault Drain(string site)
        {
            MetalCommandBufferFault fault = MetalQueueDrain.DrainBlocking(Queue);
            if (_loss.Check(fault, site))
            {
                log.Warn("The native Metal device was already lost, or has just been found lost, at " + site
                    + ". The reason is in the telemetry session header for this run.");
            }
            return fault;
        }

        // The row that owns each unbuilt member, as a full URL, because these messages are read by somebody who
        // has just hit one and needs to know whether to wait for a row or file a bug.
        const string SwapchainRow = "the swapchain row (https://github.com/APKiwiOrg/KhaozEngine/issues/581)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not,
        // which is the shape both sibling backends settled on: a reader who hits this needs to know whether the
        // backend is unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal backend: it lands in {row}. The interop layer, "
                + "the MTLDevice, the MTLCommandQueue, KE_METAL_DEVICE selection, the command-buffer error latch "
                + "and the liveness token ARE live (work-breakdown row 4, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/570), and so are buffers, textures, samplers, "
                + "fences, the device-level uploads and Map (row 6, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/572), and the completion timeline, the command "
                + "list and the submit path (row 7, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/573). This is a statement about the package "
                + "and not about this machine. Select GpuBackendKind.Metal, which goes through Veldrid, for a "
                + "fully working Metal device.");

        // Creation lives in MetalGpuDevice.Create.cs. It is the same split both sibling devices take, because the
        // seam surface and the creation policy are different concerns and neither has room for the other under
        // the file-size cap.
    }
}
