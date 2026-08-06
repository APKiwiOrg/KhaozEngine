using System;
using KhaozEngine.Diagnostics;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The engine's native Vulkan device as the GPU seam sees it: a real <c>VkDevice</c> and a real graphics
    /// queue, on the shared refcounted instance, that creates and destroys cleanly.
    /// <para>
    /// <b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> This is
    /// work-breakdown row 4 of <c>docs/design/VULKAN-NATIVE-BACKEND-DESIGN-2026-08-05.md</c>: the instance, the
    /// device, the queue, the selective feature enable, the device-loss latch, the liveness token and the
    /// validation pump. The command list is row 7, resources and samplers are row 9, and the swapchain is row 17,
    /// so the members those rows own throw a message saying so rather than returning something that fails later
    /// somewhere less informative. The Direct3D 11 package's <c>D3D11ResourceFactory</c> landed the same way and
    /// its doc paragraph was rewritten at every fill-in, which is the discipline this paragraph is under too: it
    /// is a ledger, and a stale one is worse than none.
    /// </para>
    /// <para>
    /// <b>THE MEMBERS THAT ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the part that can
    /// be read honestly, see below), <see cref="Diagnostics"/> with both of its fields, <see cref="Counters"/> (in
    /// the drain half, see below), <see cref="WaitForIdle"/>, and <see cref="Dispose"/>.
    /// </para>
    /// <para>
    /// <b>THE COMPLETION TIMELINE IS LIVE AND IS NOT REACHABLE THROUGH THE SEAM YET</b> (row 5,
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/515). The device owns one timeline <c>VkSemaphore</c>, a
    /// real <c>IGpuFence</c> over it and the deferred-disposal retire list, and <see cref="WaitForIdle"/> is the
    /// counted <c>vkWaitSemaphores</c> drain on it. What the seam cannot reach is the FENCE FACTORY: the seam
    /// creates a fence through <c>IGpuResourceFactory.CreateFence</c>, and <see cref="Factory"/> is row 9's, so
    /// <see cref="VulkanTimeline.CreateFence"/> is reached through <see cref="Timeline"/> by the rows that build
    /// on it and by nothing else meanwhile. <c>SupportsCompletionFences</c> was already true in this device's
    /// partial capability read, and it is now backed by a real primitive rather than by a promise about one.
    /// </para>
    /// <para>
    /// <b><see cref="Capabilities"/> IS PARTIAL AND SAYS WHICH PART.</b> Row 18
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/528) owns the capability read and the ZERO-permitted-
    /// difference parity test against the incumbent. What this row fills is everything readable off a device with
    /// no renderer on it, and <c>MaxMsaaSampleCount</c> is pinned to 1 rather than guessed, because the incumbent's
    /// own computation is what row 18 reproduces and a number invented here would be a silent lie that
    /// <c>AntiAliasing.ResolveFor</c> would act on. Nothing selects this backend, so a conservative 1 costs
    /// nothing and an invented value would cost the parity test its meaning.
    /// </para>
    /// <para>
    /// <b>TEARDOWN CALLS <c>vkDeviceWaitIdle</c> FIRST</b> (V-F10), unlike the incumbent, which destroys the
    /// memory manager and the pools and only then waits. Everything the device owns is destroyed after that wait
    /// and before the liveness token flips, and only then is the real device destroyed, so no wrapper can observe
    /// "alive" after the object it would destroy has gone. <see cref="Dispose"/> carries the full order.
    /// </para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice : IGpuDevice
    {
        static readonly ILogger log = Log.For<VulkanGpuDevice>();

        readonly VulkanInstanceLease<VulkanInstance> _instance;
        readonly VulkanDeviceLiveness _liveness;
        readonly VulkanDeviceLossLatch _loss;
        readonly VulkanTimeline _timeline;
        readonly VulkanRetireList _retired = new();
        readonly Device _device;
        readonly bool _softwareAdapter;
        readonly object _lifecycle = new();

        bool _disposed;
        bool _syncToVerticalBlank;

        VulkanGpuDevice(VulkanInstanceLease<VulkanInstance> instance, Device device, Queue graphicsQueue,
            uint graphicsQueueFamily, GpuCapabilities capabilities, bool softwareAdapter,
            VulkanDeviceLiveness liveness, VulkanDeviceLossLatch loss, VulkanTimeline timeline)
        {
            _instance = instance;
            _device = device;
            GraphicsQueue = graphicsQueue;
            GraphicsQueueFamily = graphicsQueueFamily;
            _softwareAdapter = softwareAdapter;
            _liveness = liveness;
            _loss = loss;
            _timeline = timeline;
            Capabilities = capabilities;
        }

        /// <inheritdoc/>
        public GpuBackendKind Backend => GpuBackendKind.VulkanNative;

        /// <inheritdoc/>
        public GpuCapabilities Capabilities { get; }

        /// <summary>The ONE queue this device has, graphics and (on a windowed device) presentation both (V-N5).
        /// Held here because it is the device's, and read by the command-list row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517) and the swapchain row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), which are the two that submit and present
        /// through it. The timeline row deliberately does NOT: a semaphore wait needs no queue, which is half the
        /// argument for it being the drain (V-F4).</summary>
        internal Queue GraphicsQueue { get; }

        /// <summary>The family that queue came from. Command pools are created against it, which is why it
        /// travels with the device rather than being re-derived per pool.</summary>
        internal uint GraphicsQueueFamily { get; }

        /// <summary>
        /// The device's ONE completion timeline (V-F1). Every submission takes its next value, every fence holds
        /// one, and the drain waits on the last one handed out. Exposed because it is the primitive rows 7, 8 and
        /// 9 are all built on: the submit path allocates and signals, the uniform ring gates a segment on it, and
        /// the resource factory hands out fences from it.
        /// </summary>
        internal VulkanTimeline Timeline => _timeline;

        /// <summary>
        /// The deferred-disposal retire list (V-F9). A resource's <c>Dispose</c> records
        /// <see cref="VulkanTimeline.LastSubmitted"/> here with its own native destroy, and the destroy runs once
        /// the counter passes that value. Empty today, because no row that creates a destroyable object has landed
        /// yet, and here now because rows 7 and 9 both hand to it.
        /// </summary>
        internal VulkanRetireList Retired => _retired;

        /// <summary>
        /// THE FRAME-BOUNDARY DRAIN of the retire list: run every held destroy the timeline has passed, and leave
        /// the rest. Returns how many ran.
        /// <para>
        /// Nothing calls it yet, because the frame boundary is <see cref="Present"/> and that is row 17's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527). It exists at this row rather than at that one so
        /// that the retire list has exactly ONE release path from the start: this method and the teardown drain
        /// inside <see cref="Dispose"/> both go through <see cref="VulkanRetireList"/>, and no later row has to
        /// invent a second rule about when a deferred destroy is safe.
        /// </para>
        /// </summary>
        internal int DrainRetiredResources()
        {
            if (_liveness.IsDead) return 0;

            return _retired.Drain(_timeline.CompletedValue);
        }

        /// <inheritdoc/>
        /// <remarks>Decision V-G2's <c>softwareAdapter</c> and V-G4's <c>deviceLossReason</c>, both filled from
        /// this row: the first from the chosen device's type and driver id, the second from the latch, which is
        /// null until something is latched and is the header field #427 asks for.</remarks>
        public GpuDeviceDiagnostics Diagnostics => new(_softwareAdapter, _loss.HeaderValue);

        /// <inheritdoc/>
        /// <remarks>
        /// THE DRAIN HALF IS A MEASUREMENT AND THE REST IS ARITHMETIC ABOUT SUBSYSTEMS THAT DO NOT EXIST YET, and
        /// the difference matters enough to say here. <c>DrainCount</c> and <c>DrainMs</c> come off
        /// <see cref="VulkanTimeline.TotalDrain"/> and are the M2 numbers, counted by this row (V-F4). Every other
        /// field is 0 because the thing that could move it is not built: no frame has been OPENED, since
        /// <see cref="Present"/> is row 17's, and no uniform ring exists to stall or to defer a write against,
        /// since that is row 8's (https://github.com/APKiwiOrg/KhaozEngine/issues/518). Each of those zeros is
        /// therefore literally true about this device rather than a placeholder, which is the bar the struct's own
        /// "absent is not zero" rule sets for reporting <c>HasValue</c> at all.
        /// <para>
        /// WHAT A READER STILL MUST NOT DO is divide by <c>FramesBegun</c> while it is 0, and row 18
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/528) is where every field becomes a reading taken from
        /// the subsystem that owns it.
        /// </para>
        /// </remarks>
        public GpuDeviceCounters Counters
        {
            get
            {
                VulkanWaitTotals drain = _timeline.TotalDrain;

                // Named, because the two longs and the two doubles sit next to each other: a transposed pair here
                // compiles, passes every test, and reports a stall count as a drain count in the field.
                return new GpuDeviceCounters(
                    framesBegun: 0,
                    drainCount: drain.Count,
                    drainMs: drain.TotalMs,
                    backpressureStallCount: 0,
                    backpressureStallMs: 0d,
                    offTimelineDeferred: 0,
                    offTimelineOutstanding: 0);
            }
        }

        /// <inheritdoc/>
        /// <remarks>Null, and correct rather than unbuilt: this row creates HEADLESS devices only, and a headless
        /// device has no swapchain by definition. The windowed path is row 17
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/527), and it refuses at creation rather than handing
        /// back a device that cannot present.</remarks>
        public IGpuFramebuffer? SwapchainFramebuffer => null;

        /// <inheritdoc/>
        public IGpuResourceFactory Factory => throw NotBuiltYet("The resource factory", ResourcesRow);

        /// <inheritdoc/>
        public IGpuSampler PointSampler => throw NotBuiltYet("The shared point sampler", ResourcesRow);

        /// <inheritdoc/>
        public IGpuSampler LinearSampler => throw NotBuiltYet("The shared linear sampler", ResourcesRow);

        /// <inheritdoc/>
        /// <remarks>A backing value on a headless device, which is what the seam asks for. It reconfigures
        /// nothing because there is no swapchain to reconfigure, and row 17 is where it starts meaning
        /// something.</remarks>
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
        /// Block until the GPU is idle: <c>vkWaitSemaphores</c> on the last value the timeline handed to a
        /// submission, with an infinite timeout, counted into <c>DrainCount</c> and <c>DrainMs</c> (V-F4). A SAFE
        /// NO-OP after the device is dead (V-F10), because a destroyed device has no outstanding work left to
        /// finish, so returning is the honest answer and waiting would wait on a counter nothing can advance.
        /// <para>
        /// NOT <c>vkDeviceWaitIdle</c> AND NOT <c>vkQueueWaitIdle</c>, which is the change this row made to a
        /// method that already worked. A semaphore wait does not need the queue lock, so a drain from one thread
        /// does not block a submit from another until it finishes, and it names a VALUE, which is what turns a
        /// drain into something with a number attached. Teardown still uses <c>vkDeviceWaitIdle</c>, where there
        /// is no submission left to protect and the question being asked is about the whole device.
        /// </para>
        /// <para>
        /// The result of every native call on that path is checked in every configuration and a
        /// <c>VK_ERROR_DEVICE_LOST</c> is latched at the call's own site (V-G4). See
        /// <see cref="VulkanTimeline.WaitForIdle"/> for which cases return without counting and why.
        /// </para>
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;

            _timeline.WaitForIdle();

            // The strict rung's controlled throw. Placed after the wait rather than before it, so a validation
            // error raised by work this wait was flushing is caught by the same call that flushed it.
            _instance.Value.Messenger?.Pump.ThrowIfLatched("WaitForIdle");
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
        /// Destroy the device, in the ONE order V-F10 permits: <c>vkDeviceWaitIdle</c> FIRST, then the retire
        /// list's teardown drain, then the timeline semaphore, then the liveness flip, then
        /// <c>vkDestroyDevice</c>, then the instance lease.
        /// <para>
        /// The incumbent destroys its memory manager and its pools and only THEN waits, which destroys objects the
        /// GPU may still be reading. Waiting first is the whole fix, and it is why the wait was written here at
        /// row 4 rather than left to whichever later row added the first destroyable resource: a teardown order
        /// established once is an order every later row inherits. This row is the first to add to it, and it slots
        /// its two entries into the WINDOW BETWEEN THE WAIT AND THE FLIP, which is the only window in which
        /// destroying a child object of the device is both safe and legal. Before the wait it would be a destroy
        /// of something the GPU may still be reading, and after the flip every native destroy is skipped by
        /// contract, so anything the device itself owns would leak until <c>vkDestroyDevice</c> collected it.
        /// </para>
        /// <para>
        /// THE RETIRE LIST GOES BEFORE THE SEMAPHORE, and the order of those two is the one edit here that could
        /// look arbitrary. The held destroys are gated on timeline VALUES, and the teardown drain runs them all
        /// regardless because the wait above already made every value passed. Draining them first keeps the rule
        /// intact anyway: nothing that reads the timeline runs after the timeline has gone.
        /// </para>
        /// <para>
        /// A DEAD DEVICE ABANDONS THE RETIRE LIST INSTEAD OF DRAINING IT. On that path the device was destroyed by
        /// its loss rather than by this method, so every object made from it is already gone and running a destroy
        /// would be a call against freed memory, which aborts the process through the Vulkan loader rather than
        /// failing quietly.
        /// </para>
        /// <para>
        /// The lease goes LAST and always, including when the wait or the destroy failed. A device that leaked its
        /// lease would hold the process instance alive for the rest of the run, and every later device would
        /// share an instance whose first holder is gone.
        /// </para>
        /// <para>
        /// The order below is held by nothing more than the sequence of statements: no test asserts it, because
        /// there is no seam that can observe teardown order device-free. An edit that reorders these lines must
        /// re-read this block rather than trust a green suite to catch the regression.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_disposed) return;
                _disposed = true;

                try
                {
                    if (!_liveness.IsDead)
                    {
                        Vk vk = _instance.Value.Api;

                        // FIRST. A device destroyed with work in flight takes the driver down with it on some
                        // implementations and corrupts silently on others.
                        Result waited = vk.DeviceWaitIdle(_device);
                        if (!_loss.Check(waited, "vkDeviceWaitIdle (teardown)") && waited != Result.Success)
                        {
                            log.Warn("vkDeviceWaitIdle returned "
                                + $"{VulkanResultCodes.Token(waited)} during native Vulkan device teardown. The "
                                + "device is destroyed anyway, because there is no recovery available at "
                                + "teardown and leaving it alive would leak everything behind it.");
                        }

                        // The wait above is what makes an unconditional drain correct: the GPU is idle, so every
                        // recorded timeline value has been passed and the values have nothing left to say.
                        _retired.DrainAll();
                        _timeline.Dispose();

                        // Flipped BEFORE the destroy and after the wait, so no wrapper can observe "alive" after
                        // the object it would destroy has gone, and so a wrapper disposed on another thread mid
                        // teardown becomes a no-op rather than a call against freed memory.
                        _liveness.MarkDead();
                        vk.DestroyDevice(_device, null);
                    }
                    else
                    {
                        // A LOST device. Its children went with it, so the held destroys are dropped rather than
                        // run, and the timeline's own Dispose skips the native destroy for the same reason.
                        int dropped = _retired.Abandon();
                        _timeline.Dispose();

                        if (dropped > 0)
                        {
                            log.Warn($"{dropped} deferred native Vulkan destroys were dropped without running, "
                                + "because the device they belonged to was already dead when it was disposed. The "
                                + "objects went with the device, so this is a report rather than a leak.");
                        }
                    }
                }
                finally
                {
                    // ALWAYS, including the device-lost path where the destroy above was skipped. The instance
                    // survives a lost device, and the next device created on it is the recovery such as it is.
                    _instance.Dispose();
                }
            }
        }

        // The row that owns each unbuilt member, as a full URL, because these messages are read by somebody who
        // has just hit one and needs to know whether to wait for a row or file a bug.
        const string CommandListRow = "the command-list row (https://github.com/APKiwiOrg/KhaozEngine/issues/517)";
        const string ResourcesRow = "the resources row (https://github.com/APKiwiOrg/KhaozEngine/issues/519)";
        const string SwapchainRow = "the swapchain row (https://github.com/APKiwiOrg/KhaozEngine/issues/527)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not,
        // which is the shape D3D11ResourceFactory's equivalent settled on: a reader who hits this needs to know
        // whether the backend is unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Vulkan backend: it lands in {row}. The instance, the "
                + "device, the queue, the device-loss latch and the validation pump ARE live (work-breakdown row "
                + "4, https://github.com/APKiwiOrg/KhaozEngine/issues/514). This is a statement about the "
                + "package and not about this machine. Select GpuBackendKind.Vulkan, which goes through Veldrid, "
                + "for a fully working Vulkan device.");
    }
}
