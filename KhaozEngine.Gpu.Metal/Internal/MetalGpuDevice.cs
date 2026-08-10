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
    /// <b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> Rows 4 and 6
    /// of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> have landed: the interop layer, the
    /// device, the queue, <c>KE_METAL_DEVICE</c>, the validation report, the command-buffer error latch, the
    /// liveness token and a drain before teardown (row 4), and the resource factory, the shared sampler pair,
    /// the device-level uploads, the setup command buffer and <c>Map</c> with its read drain (row 6). The command
    /// list is row 7 and the swapchain is row 15, so the members those rows own throw a message saying so rather
    /// than returning something that fails later somewhere less informative, with three deliberate exceptions
    /// whose remarks below carry the reasons: <see cref="Counters"/> returns an absent default (row 16's
    /// channels, and absent is not zero), <see cref="SwapchainFramebuffer"/> returns null (the headless answer is
    /// null, not a throw), and <see cref="SyncToVerticalBlank"/> is a backing value until row 15 gives it a
    /// swapchain to reconfigure. Both sibling backends landed the same way and had this paragraph rewritten at
    /// every fill-in, which is the discipline it is under here too: it is a ledger, and a stale one is worse than
    /// none.
    /// </para>
    /// <para>
    /// <b>THE MEMBERS THESE ROWS OWN ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the part
    /// row 4 can read honestly, see below), <see cref="Diagnostics"/> with both of its fields,
    /// <see cref="WaitForIdle"/>, <see cref="Dispose"/>, and row 6's whole resource half in
    /// <c>MetalGpuDevice.Resources.cs</c>.
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
    /// <b>TEARDOWN IS M-F6's ORDER AND NOTHING ELSE:</b> drain first, then flip the liveness token inside the
    /// lifecycle lock, then release the queue and the device. The incumbent already waits first, so this is
    /// reproduction rather than repair, which is the one place this backend inherits a correct teardown instead
    /// of fixing one.
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

        MetalSampler _pointSampler = null!;
        MetalSampler _linearSampler = null!;

        bool _disposed;
        bool _syncToVerticalBlank;

        MetalGpuDevice(MTLDevice device, MTLCommandQueue queue, GpuCapabilities capabilities,
            MetalDeviceLiveness liveness, MetalDeviceLossLatch loss, MetalTimeline timeline)
        {
            _device = device;
            Queue = queue;
            _liveness = liveness;
            _loss = loss;
            _timeline = timeline;
            Capabilities = capabilities;
            _setup = new MetalSetupCommands(queue, liveness);
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
        /// timeline row's completion handler is the one that will call it on every frame.</summary>
        internal MetalDeviceLossLatch Loss => _loss;

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
        public void Submit(IGpuCommandList cl) => throw NotBuiltYet("Submitting a command list", CommandListRow);

        /// <inheritdoc/>
        public void Submit(IGpuCommandList cl, IGpuFence fence)
            => throw NotBuiltYet("Submitting a command list with a completion fence", CommandListRow);

        /// <summary>
        /// Block until the GPU is idle. A SAFE NO-OP after the device is dead (M-F6): a torn-down or lost device
        /// has no outstanding work left to finish, so returning is the honest answer and waiting would wait on a
        /// queue nothing can advance.
        /// <para>
        /// Implemented as an empty command buffer committed and waited on, which is a real drain because a Metal
        /// queue executes in enqueue order (see <see cref="MetalQueueDrain"/>). Metal has no device-level wait,
        /// so there is no <c>vkDeviceWaitIdle</c> equivalent to call. The timeline row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/571) replaces this with
        /// <c>waitUntilSignaledValue:timeoutMS:</c> plus the <c>DrainCount</c> and <c>DrainMs</c> counters, which
        /// is the stronger of the two because it is bounded and counted.
        /// </para>
        /// <para>
        /// The reading is checked, in every configuration, and a command-buffer failure is latched here with this
        /// call's own name (M-G4). It is one of the two sites this row owns.
        /// </para>
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            // THE SETUP BATCH FIRST (M-M9). A drain that ran before the flush would wait for everything except
            // the uploads the caller has just made, which is exactly the case an explicit drain is asked for.
            _setup.Flush();

            Drain("waitUntilCompleted (WaitForIdle)");
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
            MetalCommandBufferFault fault = Drain("waitUntilCompleted (teardown drain)");

            // A drain that saw a failure has already flipped liveness through the latch, so there is nothing left
            // that is safe to release and the two handles leak deliberately.
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

            // AFTER the flip, which is the order MetalTimeline.Dispose documents: its release is unconditional,
            // because an MTLSharedEvent is an ordinary reference-counted object with no vkDestroyDevice rule in
            // front of it, and skipping it on a dead device would leak it on the path that matters.
            _timeline.Dispose();

            Queue.Release();
            _device.Release();
        }

        // The one place a command-buffer reading reaches the latch in this row. The site name travels in rather
        // than being inferred downstream, which is what "latched at the fault site" means.
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
        const string CommandListRow = "the command-list row (https://github.com/APKiwiOrg/KhaozEngine/issues/573)";
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
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/572). This is a statement about the package "
                + "and not about this machine. Select GpuBackendKind.Metal, which goes through Veldrid, for a "
                + "fully working Metal device.");

        // Creation lives in MetalGpuDevice.Create.cs. It is the same split both sibling devices take, because the
        // seam surface and the creation policy are different concerns and neither has room for the other under
        // the file-size cap.
    }
}
