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
    /// <b>THE MEMBERS THIS ROW OWNS ARE LIVE:</b> <see cref="Backend"/>, <see cref="Capabilities"/> (in the part
    /// this row can read honestly, see below), <see cref="Diagnostics"/> with both of its fields,
    /// <see cref="WaitForIdle"/>, and <see cref="Dispose"/>.
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
    /// memory manager and the pools and only then waits. Then the liveness token flips, and only then is the real
    /// device destroyed, so no wrapper can observe "alive" after the object it would destroy has gone.
    /// </para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice : IGpuDevice
    {
        static readonly ILogger log = Log.For<VulkanGpuDevice>();

        readonly VulkanInstanceLease<VulkanInstance> _instance;
        readonly VulkanDeviceLiveness _liveness;
        readonly VulkanDeviceLossLatch _loss;
        readonly Device _device;
        readonly bool _softwareAdapter;
        readonly object _lifecycle = new();

        bool _disposed;
        bool _syncToVerticalBlank;

        VulkanGpuDevice(VulkanInstanceLease<VulkanInstance> instance, Device device, Queue graphicsQueue,
            uint graphicsQueueFamily, GpuCapabilities capabilities, bool softwareAdapter,
            VulkanDeviceLiveness liveness, VulkanDeviceLossLatch loss)
        {
            _instance = instance;
            _device = device;
            GraphicsQueue = graphicsQueue;
            GraphicsQueueFamily = graphicsQueueFamily;
            _softwareAdapter = softwareAdapter;
            _liveness = liveness;
            _loss = loss;
            Capabilities = capabilities;
        }

        /// <inheritdoc/>
        public GpuBackendKind Backend => GpuBackendKind.VulkanNative;

        /// <inheritdoc/>
        public GpuCapabilities Capabilities { get; }

        /// <summary>The ONE queue this device has, graphics and (on a windowed device) presentation both (V-N5).
        /// Held here because it is the device's, and read by the timeline row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/515) and the command-list row
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517), which are the two that submit through it.</summary>
        internal Queue GraphicsQueue { get; }

        /// <summary>The family that queue came from. Command pools are created against it, which is why it
        /// travels with the device rather than being re-derived per pool.</summary>
        internal uint GraphicsQueueFamily { get; }

        /// <inheritdoc/>
        /// <remarks>Decision V-G2's <c>softwareAdapter</c> and V-G4's <c>deviceLossReason</c>, both filled from
        /// this row: the first from the chosen device's type and driver id, the second from the latch, which is
        /// null until something is latched and is the header field #427 asks for.</remarks>
        public GpuDeviceDiagnostics Diagnostics => new(_softwareAdapter, _loss.HeaderValue);

        /// <inheritdoc/>
        /// <remarks>Row 18 (https://github.com/APKiwiOrg/KhaozEngine/issues/528) fills these from the subsystems
        /// that count them, none of which exist yet. The default is the honest answer meanwhile, and the struct's
        /// own doc already says absent is not zero, so a capture from this row reports no channels rather than
        /// reporting zeros a reader would take for measurements.</remarks>
        public GpuDeviceCounters Counters => default;

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
        /// Block until the GPU is idle, as <c>vkDeviceWaitIdle</c>. A SAFE NO-OP after the device is dead
        /// (V-F10): a destroyed device has no outstanding work left to finish, so returning is the honest answer
        /// and spinning would spin on a counter nothing can advance.
        /// <para>
        /// The result is checked, in every configuration, and a <c>VK_ERROR_DEVICE_LOST</c> is latched here with
        /// this call's own name (V-G4). <c>vkDeviceWaitIdle</c> is one of the calls the spec names as able to
        /// return it, and it is one of the two sites this row owns.
        /// </para>
        /// </summary>
        public void WaitForIdle()
        {
            if (_liveness.IsDead) return;

            Vk vk = _instance.Value.Api;
            Result waited = vk.DeviceWaitIdle(_device);
            if (_loss.Check(waited, "vkDeviceWaitIdle (WaitForIdle)")) return;

            VulkanResultCodes.Require(waited, "vkDeviceWaitIdle");
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
        /// Destroy the device, in the ONE order V-F10 permits: <c>vkDeviceWaitIdle</c> FIRST, then the liveness
        /// flip, then <c>vkDestroyDevice</c>, then the instance lease.
        /// <para>
        /// The incumbent destroys its memory manager and its pools and only THEN waits, which destroys objects the
        /// GPU may still be reading. Waiting first is the whole fix, and it is why the wait is here rather than
        /// left to whichever later row adds the first destroyable resource: a teardown order established once is
        /// an order every later row inherits.
        /// </para>
        /// <para>
        /// The lease goes LAST and always, including when the wait or the destroy failed. A device that leaked its
        /// lease would hold the process instance alive for the rest of the run, and every later device would
        /// share an instance whose first holder is gone.
        /// </para>
        /// <para>
        /// The wait-flip-destroy-release order below is held by nothing more than the sequence of statements: no
        /// test asserts it, because there is no seam that can observe teardown order device-free. An edit that
        /// reorders these lines must re-read this block rather than trust a green suite to catch the regression.
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

                        // Flipped BEFORE the destroy and after the wait, so no wrapper can observe "alive" after
                        // the object it would destroy has gone, and so a wrapper disposed on another thread mid
                        // teardown becomes a no-op rather than a call against freed memory.
                        _liveness.MarkDead();
                        vk.DestroyDevice(_device, null);
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
