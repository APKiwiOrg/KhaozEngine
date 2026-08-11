using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The engine's native Metal device as the GPU seam sees it: a real <c>MTLDevice</c> and a real
    /// <c>MTLCommandQueue</c> that create and tear down cleanly.
    /// <para>
    /// <b>NO MEMBER OF <see cref="IGpuDevice"/> IS UNBUILT ANY MORE, and this paragraph is the ledger that says
    /// so.</b> Rows 4, 6, 7, 9 to 14, 15 and 16 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> have landed: the interop layer, the device,
    /// the queue, <c>KE_METAL_DEVICE</c>, the validation report, the command-buffer error latch, the liveness
    /// token and a drain before teardown (row 4), the resource factory, the shared sampler pair, the device-level
    /// uploads, the setup command buffer and <c>Map</c> with its read drain (row 6), the completion timeline, the
    /// command list and the SUBMIT path (row 7), the shader path, the layouts and index tables, the pipelines, the
    /// render passes and the bind flush (rows 9 to 13), the draws, the dispatches and the transfer family, which
    /// leave <c>IGpuCommandList</c> with no unbuilt member at all (row 14), the <c>CAMetalLayer</c>, the drawable,
    /// the present, the queued resize and the vsync toggle (row 15), and the whole capability read, the counter
    /// fill and the native frame-capture path (row 16). What is left is row 18's shared-home extraction and row
    /// 19's CI leg, its doc sweep and its rollout gates, and neither is a member of this type. Both sibling
    /// backends had this paragraph rewritten at every fill-in, which is the discipline it is under here too: it is
    /// a ledger, and a stale one is worse than none. There is no SECOND ledger any more either, which is what
    /// closed https://github.com/APKiwiOrg/KhaozEngine/issues/601.
    /// </para>
    /// <para>
    /// <b>SO EVERY MEMBER IS LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> in full,
    /// <see cref="Counters"/> in every channel, <see cref="Diagnostics"/> with both of its fields, both
    /// <c>Submit</c> overloads, <see cref="WaitForIdle"/>, <see cref="Dispose"/>, row 6's whole resource half in
    /// <c>MetalGpuDevice.Resources.cs</c>, and row 15's swapchain half in <c>MetalGpuDevice.Present.cs</c>. A
    /// HEADLESS device answers null for <c>SwapchainFramebuffer</c> and does nothing at a present, which is
    /// correct rather than unbuilt: a headless device has no swapchain by definition. The list the submit path
    /// takes comes from <see cref="CreateCommandList"/>, which the seam reaches through
    /// <c>IGpuResourceFactory.CreateCommandList</c>.
    /// </para>
    /// <para>
    /// <b><see cref="Capabilities"/> IS COMPLETE AS OF ROW 16</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/582), including M-C3's sample-count walk, and it is
    /// pinned member for member against the incumbent at ZERO permitted differences by
    /// <c>NativeVsVeldridMetalCapabilityParityTests</c>. Every decision behind it is in
    /// <see cref="MetalCapabilityRead"/>, which has no device in it.
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

        readonly DeviceLiveness _liveness;
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

        // THE THIRD UNCOUNTED EMISSION SEAM, one member wide, boxed once here for the same reason: a list takes
        // it as an interface, so constructing one per list would box one per list to describe a value that cannot
        // differ. See IMetalComputeApi for why the compute encoder gets its own line rather than a member on the
        // render one.
        readonly IMetalComputeApi _computeApi;

        // THE ONE INDEX-TABLE CACHE (row 10), inline rather than in the constructor because the factory built
        // below reads it lazily and a field initialiser cannot be out of order with that. Deduplicating the
        // per-program binding tables is what makes M-R9's pipeline-switch comparison a handle compare instead of
        // a comparison that is never equal.
        readonly MetalIndexTableCache _indexTables = new();

        // MM4's depth, and the two subsystems that are the only things in this backend sized by it: the uniform
        // ring's segments (M-M3) and each list's staging arena slots (M-M8). No command buffer anywhere is
        // allocated per frame in flight, which is the whole of M-R2.
        readonly int _framesInFlight;
        readonly MetalBackpressure _backpressure;
        readonly MetalRingAllocator _rings;
        readonly IMetalStagingSource _staging;

        // M-N4's FOURTH READ, KEPT rather than only checked. MetalDeviceRequirements refuses a device whose
        // reported buffer-offset alignment is 0 or is something M-M3's 256-byte stride is not a multiple of, so
        // what survives here is a power of two that divides the stride, and it is what every bind's composed
        // offset is checked against. 256 would refuse offsets the device accepts (macOS reports 16 or 32).
        readonly uint _bufferOffsetAlignment;

        // THE DEVICE'S BORDER-COLOUR ANSWER, read off the probe's snapshot at creation because a sampler cannot
        // ask it safely: the way to ask a device directly is to build a border sampler, which is the abort.
        readonly bool _supportsBorderColor;

        MetalSampler _pointSampler = null!;
        MetalSampler _linearSampler = null!;

        bool _disposed;
        bool _syncToVerticalBlank;

        [SupportedOSPlatform("macos")]
        MetalGpuDevice(MTLDevice device, MTLCommandQueue queue, GpuCapabilities capabilities,
            DeviceLiveness liveness, MetalDeviceLossLatch loss, MetalTimeline timeline,
            MetalUncommittedBuffers uncommitted, IMetalSetupNative setupNative, int framesInFlight,
            IMetalStagingSource staging, uint bufferOffsetAlignment, bool supportsBorderColor)
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
            _computeApi = new MetalComputeApi();
            _bufferOffsetAlignment = bufferOffsetAlignment;
            _supportsBorderColor = supportsBorderColor;

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
        internal DeviceLiveness Liveness => _liveness;

        /// <summary>Whether this device supports sampler border colours, which is its <c>MTLGPUFamilyMac2</c>
        /// answer read once at creation. False on a virtualized GPU, where a Border-mode sampler is refused by
        /// name rather than aborting the process under the debug layer. See <see cref="MetalSamplerPolicy"/>.
        /// </summary>
        internal bool SupportsBorderColor => _supportsBorderColor;

        /// <summary>The device's ONE index-table cache (M-R9, row 10). Read by the shader compiler, which is the
        /// only site that puts a table in it, because a table is a property of the emission and shader-set
        /// creation is where an emission exists.</summary>
        internal MetalIndexTableCache IndexTables => _indexTables;

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
                _liveness, _renderApi, _computeApi,
                // THE DEVICE'S OWN ALIGNMENT, read once at creation by M-N4's probe. Every composed bind offset
                // is checked against it, which is section 18's third named risk for this row.
                _bufferOffsetAlignment,
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
        /// <remarks>
        /// M-G6's FILL, WITH NO SEAM ADDITION. Every field comes off the subsystem that already counts it, and
        /// which subsystem that is matters enough to say here. <c>DrainCount</c> and <c>DrainMs</c> come off
        /// <see cref="MetalTimeline.TotalDrain"/> (M-F5, row 5) and are the MM2 numbers.
        /// <c>BackpressureStallCount</c> and <c>BackpressureStallMs</c> come off <see cref="MetalBackpressure"/>
        /// (row 8) and are MM3's. <c>OffTimelineDeferred</c> and <c>OffTimelineOutstanding</c> come off
        /// <see cref="MetalRingAllocator.OffTimelinePatches"/> and are deliberately NOT folded into that pair.
        /// <para>
        /// <b>THE BACKPRESSURE PAIR HAS EXACTLY ONE SOURCE HERE, WHICH IS THE READING DIFFERENCE FROM VULKAN.</b>
        /// There is no command-buffer pool to wait on (M-R2), so this counts the uniform ring's segment acquire
        /// ALONE, where the same seam field on the native Vulkan backend folds in a command list's own oldest
        /// buffer slot. A non-zero reading here is therefore unambiguous: the pipeline is deeper than
        /// <c>KE_METAL_FRAMES_IN_FLIGHT</c> allows on this machine, and MM4's exit criterion (zero across a whole
        /// capture window at the default depth) is a statement about one thing. The seam's own doc comment says
        /// the second meaning is Vulkan's rather than universal, which is row 19's edit.
        /// </para>
        /// <para>
        /// <b>A DEFERRED PATCH IS NOT A STALL AND IS NOT FOLDED IN.</b> Nobody blocks on the off-timeline path,
        /// and the writes it counts are usually load-time before any frame exists, so folding it would turn a
        /// load-time <c>UpdateBuffer</c> into evidence against the frames-in-flight count and make MM4's
        /// zero-stall criterion unreachable for a reason unrelated to depth. See
        /// <see cref="MetalRingPatchStats"/>.
        /// </para>
        /// <para>
        /// <b><c>FramesBegun</c> AND THE ACQUIRE PAIR ARE THE PRESENT BOUNDARY's, AND ARE ZERO ON A HEADLESS
        /// DEVICE BECAUSE THAT IS LITERALLY TRUE.</b> <c>FramesBegun</c> comes off
        /// <see cref="MetalPresentBoundary.FramesBegun"/> (row 15), which counts EVERY boundary including one
        /// whose drawable came back nil, because a skipped present is not a skipped frame (M-W5). The acquire pair
        /// comes off <see cref="MetalAcquireWaits"/>, which records EVERY <c>nextDrawable</c> rather than only one
        /// that blocked: Metal offers no zero-timeout probe, so a boundary cannot tell an instant acquire from a
        /// blocked one except by timing it, and one entry per boundary is exactly what the seam's own doc says a
        /// CPU-blocking acquire reports. A headless device has no swapchain, opens no frame at this seam and never
        /// waits on a drawable, so all three read zero and that is the bar the struct's own "absent is not zero"
        /// rule sets for reporting <see cref="GpuDeviceCounters.HasValue"/> at all. What a reader must not do is
        /// divide by <c>FramesBegun</c> while it is 0.
        /// </para>
        /// <para>
        /// <b>THE ACQUIRE COUNT RUNS EXACTLY ONE AHEAD OF <c>FramesBegun</c> ON A WINDOWED DEVICE</b>, and that
        /// offset is a fact rather than an off-by-one: the first drawable is acquired at CREATION, before any
        /// frame exists, because M-W4 keeps the incumbent's timing of taking the next frame's drawable at the
        /// boundary.
        /// </para>
        /// </remarks>
        public GpuDeviceCounters Counters
        {
            get
            {
                MetalWaitTotals drain = _timeline.TotalDrain;
                MetalWaitTotals stalls = _backpressure.Totals;
                MetalRingPatchStats patches = _rings.OffTimelinePatches;
                MetalWaitTotals acquires = _acquireWaits.Totals;

                // Named arguments, because the two longs and the two doubles sit next to each other: a transposed
                // pair here compiles, passes every test, and reports a stall count as a drain count in the field.
                return new GpuDeviceCounters(
                    framesBegun: _present?.FramesBegun ?? 0,
                    drainCount: drain.Count,
                    drainMs: drain.TotalMs,
                    backpressureStallCount: stalls.Count,
                    backpressureStallMs: stalls.TotalMs,
                    offTimelineDeferred: patches.Deferred,
                    offTimelineOutstanding: patches.Outstanding,
                    acquireWaitCount: acquires.Count,
                    acquireWaitMs: acquires.TotalMs);
            }
        }

        /// <summary>M-W4's pair, cumulative since the device was created. Read by the counter fill above and
        /// asserted against it by identity, the way the drain and backpressure pairs are. It lives on the DEVICE
        /// rather than on the present boundary so a headless device has something to read without the fill having
        /// to ask whether a swapchain exists.</summary>
        internal MetalWaitTotals AcquireTotals => _acquireWaits.Totals;

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

            // THE COMPLETION ROUTE STOPS ON EVERY PATH, including the failure one. The table is 64 slots wide
            // for the whole process, so a queue left in it holds a slot for the life of the process on a device
            // that is going away, and a completion arriving after this point has nothing left to latch on to.
            MetalCompletionHandler.Unregister(Queue.Handle);

            // THE SWAPCHAIN GOES ON BOTH PATHS, ABOVE THE FAULT RETURN, and it is the one exception to the leak
            // posture below. It releases the held drawable, the orphan target and the layer. Two of those three
            // are CoreAnimation objects: releasing a CAMetalDrawable or a CAMetalLayer is an objc_release with
            // no dependence on the MTLDevice at all, which is exactly the argument MetalTimeline.Dispose makes
            // for the shared event (an ordinary reference-counted Objective-C object has no vkDestroyDevice rule
            // in front of it, so skipping the release leaks it on the path that matters). It bites harder here,
            // because on the ADOPT path the layer is the HOST VIEW's own and outlives this device: leaking it
            // permanently over-retains a layer a consumer's window still owns, once per device it creates over
            // that window. Releasing is also safe with work possibly still running, faulted drain or not,
            // because a committed present buffer retains the drawable until it completes (M-H3), so this is
            // never the last reference to something the GPU is reading. AFTER the drain above either way, and
            // that drain is the QUEUE's rather than the timeline's precisely because M-W6's present command
            // buffer signals no timeline value.
            //
            // THE ORPHAN TARGET IS THE ONE OF THE THREE THAT STILL LEAKS ON THE FAULT PATH, and it is named
            // rather than pretended away: it is an ordinary engine MTLTexture, and MetalTexture.Dispose is a
            // no-op once liveness is dead, which a faulted drain has already made it through the latch. So its
            // handle leaks with the queue, the device and the shared event, for the same reason they do.
            _present?.Dispose();

            // A drain that saw a failure has already flipped liveness through the latch, so nothing MADE FROM
            // THE DEVICE is safe to release any more and those handles leak deliberately: the queue, the device,
            // the shared event, and the orphan texture the line above could not release.
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

        // THE SECOND LEDGER IS GONE, AND THAT CLOSES
        // https://github.com/APKiwiOrg/KhaozEngine/issues/601. NotBuiltYet built the refusal ResizeSwapchain and
        // Present threw, naming what was live as well as what was not, and it stopped being updated after row 7:
        // by row 16 it was telling a reader that a backend which compiles shaders, builds pipelines, binds render
        // targets and reports its own capabilities could do none of those. It had no discipline behind it because
        // it was a SECOND ledger in a file whose class doc is the first. Row 15 gives both of its callers real
        // bodies, so the right fix is deletion rather than another rewording: there is now exactly one ledger in
        // this file and it is the paragraph at the top.
        //
        // Creation lives in MetalGpuDevice.Create.cs and the swapchain surface in MetalGpuDevice.Present.cs. It
        // is the same split both sibling devices take, because the seam surface, the creation policy and the
        // frame boundary are different concerns and none has room for the others under the file-size cap.
    }
}
