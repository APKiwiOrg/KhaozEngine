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
    /// <b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> Rows 4 and 7
    /// of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> are both here now: the interop layer, the
    /// device, the queue, <c>KE_METAL_DEVICE</c>, the validation report, the command-buffer error latch, the
    /// liveness token, the completion timeline and the SUBMIT path. Resources are row 6 and the swapchain is row
    /// 15, so the members those rows own throw a message saying so rather than returning something that fails
    /// later somewhere less informative, with three deliberate exceptions whose remarks below carry the reasons:
    /// <see cref="Counters"/> returns an absent default (row 16's channels, and absent is not zero),
    /// <see cref="SwapchainFramebuffer"/> returns null (the headless answer is null, not a throw), and
    /// <see cref="SyncToVerticalBlank"/> is a backing value until row 15 gives it a swapchain to reconfigure.
    /// Both sibling backends landed the same way and had this paragraph rewritten at every fill-in, which is the
    /// discipline it is under here too: it is a ledger, and a stale one is worse than none.
    /// </para>
    /// <para>
    /// <b>THE MEMBERS THESE TWO ROWS OWN ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the
    /// part row 4 can read honestly, see below), <see cref="Diagnostics"/> with both of its fields, both
    /// <c>Submit</c> overloads, <see cref="WaitForIdle"/> and <see cref="Dispose"/>. The list they submit comes
    /// from <see cref="CreateCommandList"/>, which the seam reaches through
    /// <c>IGpuResourceFactory.CreateCommandList</c> once row 6 builds the factory.
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
        readonly object _lifecycle = new();

        // THE ONE SERIALISED POINT IN THE FRAME (M-N2). -commit ENQUEUES, and a Metal queue executes in enqueue
        // order, so submit order is the observable order the GPU seam documents only if commits are serialised.
        // The timeline value is allocated and encoded inside this same lock, or two threads could encode in one
        // order and commit in another, which is the half MetalTimeline.EncodeSignalForSubmit cannot enforce
        // itself. Recording is deliberately NOT under it: N lists record concurrently (M-R3).
        readonly object _submitLock = new();

        readonly MetalTimeline _timeline;
        readonly MetalCommandBufferSource _commandBuffers;
        readonly MetalUncommittedBuffers _uncommitted;

        bool _disposed;
        bool _syncToVerticalBlank;

        [SupportedOSPlatform("macos")]
        MetalGpuDevice(MTLDevice device, MTLCommandQueue queue, GpuCapabilities capabilities,
            MetalDeviceLiveness liveness, MetalDeviceLossLatch loss, MetalTimeline timeline,
            MetalUncommittedBuffers uncommitted)
        {
            _device = device;
            Queue = queue;
            _liveness = liveness;
            _loss = loss;
            _timeline = timeline;
            _uncommitted = uncommitted;
            _commandBuffers = new MetalCommandBufferSource(queue);
            Capabilities = capabilities;
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
        /// <c>CreateFence</c> (https://github.com/APKiwiOrg/KhaozEngine/issues/572) and row 8's uniform ring for
        /// its segment gate (https://github.com/APKiwiOrg/KhaozEngine/issues/574).</summary>
        internal MetalTimeline Timeline => _timeline;

        /// <summary>How many uncommitted command buffers this device is holding, against section 6.1's bound.
        /// Read by the device-free assertion and by row 16's counter fill.</summary>
        internal MetalUncommittedBuffers Uncommitted => _uncommitted;

        /// <summary>
        /// A command list against this device's queue (M-R1, M-R2). Internal because the seam hands lists out
        /// through <c>IGpuResourceFactory.CreateCommandList</c>, which is row 6's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/572): this is what that factory will call, and what
        /// the <c>[GpuFact]</c> submit path calls today.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MetalCommandList CreateCommandList()
            => new(_commandBuffers, _uncommitted, new MetalEncoderSink());

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
        public IGpuResourceFactory Factory => throw NotBuiltYet("The resource factory", ResourcesRow);

        /// <inheritdoc/>
        public IGpuSampler PointSampler => throw NotBuiltYet("The shared point sampler", ResourcesRow);

        /// <inheritdoc/>
        public IGpuSampler LinearSampler => throw NotBuiltYet("The shared linear sampler", ResourcesRow);

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

            if (cl is not MetalCommandList list)
            {
                throw new ArgumentException(
                    "That command list was not created by this native Metal device, so it holds no "
                    + "MTLCommandBuffer to commit. Create command lists through the device you submit them to.",
                    nameof(cl));
            }

            MetalGpuFence? armed = fence is null ? null : RequireFence(fence);

            // READ BEFORE THE LOCK so a list with no sealed recording is refused by name rather than inside the
            // one serialised point in the frame.
            IntPtr buffer = list.SealedCommandBuffer;

            if (_liveness.IsDead || !KhaozEngineMetal.IsPlatformSupported)
            {
                // The seal is consumed either way: a list whose recording cannot be submitted is still reusable,
                // and holding the buffer would keep it counted against the queue's own uncommitted maximum.
                list.DiscardRecording();
                return;
            }

            SubmitOnMacOs(list, buffer, armed);
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void SubmitOnMacOs(MetalCommandList list, IntPtr buffer, MetalGpuFence? fence)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            lock (_submitLock)
            {
                ulong value = _timeline.EncodeSignalForSubmit(buffer);

                // BEFORE THE COMMIT. Metal refuses a handler added to a buffer that has already been committed,
                // and a buffer that completed before its handler was attached would report nothing at all.
                MetalCompletionHandler.AttachTo(buffer);

                new MTLCommandBuffer(buffer).Commit();

                _timeline.RegisterSubmitted(value);
                fence?.Arm(value);
            }

            // AFTER the commit and outside the lock: the queue retains a committed buffer until it completes, so
            // this cannot free one the GPU is running, and the release is not something the ordering depends on.
            list.MarkSubmitted();
            _commandBuffers.Release(buffer);
        }

        static MetalGpuFence RequireFence(IGpuFence fence)
            => fence as MetalGpuFence ?? throw new ArgumentException(
                "That fence was not created by this native Metal device, so it names no value on this device's "
                + "timeline. Create fences through the device you submit against.", nameof(fence));

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
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            _timeline.WaitForIdle();
        }

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => throw NotBuiltYet("Uploading to a buffer", ResourcesRow);

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
            => throw NotBuiltYet("Uploading to a buffer", ResourcesRow);

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => throw NotBuiltYet("Uploading to a buffer", ResourcesRow);

        /// <inheritdoc/>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => throw NotBuiltYet("Uploading to a texture", ResourcesRow);

        /// <inheritdoc/>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
            => throw NotBuiltYet("Uploading to a texture", ResourcesRow);

        /// <inheritdoc/>
        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
            => throw NotBuiltYet("Mapping a staging texture", ResourcesRow);

        /// <inheritdoc/>
        public void Unmap(IGpuTexture staging) => throw NotBuiltYet("Mapping a staging texture", ResourcesRow);

        /// <inheritdoc/>
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
            => throw NotBuiltYet("Mapping a staging buffer", ResourcesRow);

        /// <inheritdoc/>
        public void Unmap(IGpuBuffer staging) => throw NotBuiltYet("Mapping a staging buffer", ResourcesRow);

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

            // Flipped BEFORE the releases and after the drain, so no wrapper can observe "alive" after the object
            // it would release has gone, and so a wrapper disposed on another thread mid-teardown becomes a
            // no-op rather than a call against a released object.
            _liveness.MarkDead();

            // AFTER the flip, which is the order row 5 documented and the only thing standing between the shared
            // event's unconditional release and a fence polled mid-teardown messaging a released object.
            _timeline.Dispose();

            Queue.Release();
            _device.Release();
        }

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
        const string ResourcesRow = "the resources row (https://github.com/APKiwiOrg/KhaozEngine/issues/572)";
        const string SwapchainRow = "the swapchain row (https://github.com/APKiwiOrg/KhaozEngine/issues/581)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not,
        // which is the shape both sibling backends settled on: a reader who hits this needs to know whether the
        // backend is unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Metal backend: it lands in {row}. The interop layer, "
                + "the MTLDevice, the MTLCommandQueue, KE_METAL_DEVICE selection, the command-buffer error latch "
                + "and the liveness token ARE live (work-breakdown row 4, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/570), and so are the completion timeline, the "
                + "command list and the submit path (row 7, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/573). This is a statement about the package "
                + "and not about this machine. Select GpuBackendKind.Metal, which goes through Veldrid, for a "
                + "fully working Metal device.");

        // Creation lives in MetalGpuDevice.Create.cs. It is the same split both sibling devices take, because the
        // seam surface and the creation policy are different concerns and neither has room for the other under
        // the file-size cap.
    }
}
