using System;
using System.Threading;
using KhaozEngine.Diagnostics;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The optional extra a driver can say about WHY the device went, read at the fault site and appended to the
    /// latched reason. <c>VK_EXT_device_fault</c> is the extension behind it: it reports the faulting address and
    /// the vendor's own fault information, which is the difference between "the device went" and a bug report.
    /// <para>
    /// An interface rather than a call, for the same reason the Direct3D 11 latch takes
    /// <c>ID3D11RemovedReason</c>: the whole latch stays device-free and testable, and a driver without the
    /// extension is a null source rather than a branch inside the latch.
    /// </para>
    /// </summary>
    internal interface IVulkanDeviceFault
    {
        /// <summary>The driver's own fault detail, or null when it has none to give. Must never throw: it is
        /// called during a device loss, and a second failure there replaces the diagnostic with a less
        /// informative one at exactly the moment the first one mattered.</summary>
        string? DescribeFault();
    }

    /// <summary>
    /// DECISION V-G4: the device-loss latch. One per device. It is handed a <c>VkResult</c> at each of the sites
    /// that can see a loss, and on the first one that IS a loss it records the result and the site, appends
    /// whatever <see cref="IVulkanDeviceFault"/> can add, flips <see cref="VulkanDeviceLiveness"/> so every later
    /// destroy is a no-op, and exposes the reason through <see cref="HeaderValue"/>, which
    /// <c>VulkanGpuDevice.Diagnostics</c> reads into the telemetry session header. That closes #427 for the
    /// Vulkan leg on the day the backend lands, which is the correct time: retrofitting the reporting after the
    /// first field crash wastes the crash.
    /// </summary>
    ///
    /// <para><b>EVERY RESULT IS CHECKED IN EVERY CONFIGURATION, and that is the whole reason this is not built on
    /// the incumbent's shape.</b> <c>VulkanUtil.CheckResult</c> is <c>[Conditional("DEBUG")]</c>, so a Release
    /// build checks nothing and a latch hanging off it would never fire in the only configuration anybody ships.
    /// <see cref="VulkanResultCodes"/> carries the unconditional check this hangs off instead.</para>
    ///
    /// <para><b>THE SITES.</b> <c>VK_ERROR_DEVICE_LOST</c> can come back from <c>vkQueueSubmit</c>,
    /// <c>vkQueuePresentKHR</c>, <c>vkAcquireNextImageKHR</c>, <c>vkWaitSemaphores</c>,
    /// <c>vkGetSemaphoreCounterValue</c>, <c>vkMapMemory</c>, <c>vkDeviceWaitIdle</c> and every creation call.
    /// This row wires the two it owns (<c>vkDeviceWaitIdle</c> at <c>WaitForIdle</c> and at teardown), and each
    /// later row wires its own as it lands, which is what "latched at the fault site" means: the site's own name
    /// travels into <see cref="Check"/> rather than being inferred downstream.</para>
    ///
    /// <para><b>THE LATCH IS TAKEN EXACTLY ONCE.</b> Two threads can notice a loss in the same instant, and one
    /// recorded reason with one recorded site is the only useful post-mortem: two would be a race over which one
    /// the header carries. The winner records and flips liveness, the loser answers true and does nothing,
    /// because the device is just as dead from its point of view.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE</b>, over a <c>VkResult</c> value, the plain
    /// <see cref="VulkanDeviceLiveness"/> class and an optional fault source, so the latch, the once-only rule,
    /// the liveness flip, the header string and the fault append all run under <c>dotnet test</c> on a machine
    /// with no Vulkan loader.</para>
    internal sealed class VulkanDeviceLossLatch
    {
        static readonly ILogger log = Log.For<VulkanDeviceLossLatch>();

        const int NotLost = 0;
        const int Lost = 1;

        readonly VulkanDeviceLiveness _liveness;
        readonly IVulkanDeviceFault? _fault;
        readonly ILogger _log;

        int _state = NotLost;
        // The result as the int it is. Volatile has no VkResult overload and Result is a plain 32-bit enum, so
        // storing the raw value is what lets the read stay lock-free without an unsafe cast on a field.
        int _observedResult = (int)Result.Success;
        string? _site;
        string? _faultDetail;
        bool _published;

        /// <param name="liveness">The device's one liveness token, flipped on the first observed loss.</param>
        /// <param name="fault">The driver's fault detail source, or null when this device has none. Null is the
        /// ordinary case today: <c>VK_EXT_device_fault</c> is not in the device extension list this row enables
        /// (V-N6 names <c>VK_KHR_swapchain</c> and nothing else), and wiring it is tracked separately. The seam is
        /// here from the start because a fault source added later must not change the latch.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal VulkanDeviceLossLatch(VulkanDeviceLiveness liveness, IVulkanDeviceFault? fault = null,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;
            _fault = fault;
            _log = logger ?? log;
        }

        /// <summary>True once a device loss has been observed and latched.</summary>
        internal bool IsLost => Volatile.Read(ref _state) == Lost;

        /// <summary>The result that was observed at the site that noticed, or <c>VK_SUCCESS</c> while the device
        /// is fine.</summary>
        internal Result ObservedResult => (Result)Volatile.Read(ref _observedResult);

        /// <summary>Which check site noticed, or null while the device is fine. Carried because a device loss is
        /// reported by every later call too, so saying which one saw it FIRST is the only ordering information a
        /// post-mortem gets.</summary>
        internal string? Site => Volatile.Read(ref _site);

        /// <summary>Whatever <see cref="IVulkanDeviceFault"/> added, or null when there was no source or it had
        /// nothing to say.</summary>
        internal string? FaultDetail => Volatile.Read(ref _faultDetail);

        /// <summary>
        /// THE SESSION-HEADER FIELD, or null while the device is fine. The stable token plus the site, plus the
        /// driver's fault detail when there is one, so a capture groups cleanly across sessions while still
        /// saying where it was seen. The full sentence goes in the session log, not here.
        /// <para>
        /// GATED ON THE PUBLISH FLAG RATHER THAN ON <see cref="IsLost"/>, because those two are not the same
        /// instant. The claiming thread takes the latch and only THEN reads the fault detail, so a header written
        /// from another thread inside that window would see a latch that says lost with no site in it and would
        /// write a line no later read would correct. Until the whole record is stored this reports null, which is
        /// the same answer it gives for a healthy device: an ordinary session's header is written once, so the
        /// worst case is a capture missing a field rather than a capture asserting a wrong one.
        /// </para>
        /// </summary>
        internal string? HeaderValue
        {
            get
            {
                if (!Volatile.Read(ref _published)) return null;
                string detail = FaultDetail is { Length: > 0 } d ? $" ({d})" : string.Empty;
                return $"{VulkanResultCodes.Token(ObservedResult)} at {Site}{detail}";
            }
        }

        /// <summary>
        /// Check one <c>VkResult</c> from <paramref name="site"/>. Returns true when the device is lost, which
        /// includes the case where it was already lost before this call, so a caller can use it as the one
        /// question worth asking: should I stop.
        /// <para>
        /// An ORDINARY failure is not a device loss and is deliberately not latched (see
        /// <see cref="VulkanResultCodes.IsDeviceLoss"/>). It is the caller's to report or throw, because only the
        /// caller knows whether its own failed call is recoverable.
        /// </para>
        /// </summary>
        /// <param name="result">What the call returned.</param>
        /// <param name="site">The Vulkan call's own name, e.g. <c>vkDeviceWaitIdle</c>, optionally with the
        /// engine-level operation that made it. It is what the header and the log line name.</param>
        internal bool Check(Result result, string site)
        {
            if (IsLost) return true;
            if (!VulkanResultCodes.IsDeviceLoss(result)) return false;

            return Latch(result, site);
        }

        // The once-only gate. CompareExchange rather than a lock, because the losing thread has nothing to wait
        // for: the device is dead either way and the winner has already read the only answer worth having.
        bool Latch(Result result, string site)
        {
            if (Interlocked.CompareExchange(ref _state, Lost, NotLost) != NotLost) return true;

            // IMMEDIATELY, and before anything else at all. Every line below this one could raise its own error,
            // and on a driver that reports a fault only once, a second call is how the detail is lost.
            string? detail = ReadFaultDetail();

            Volatile.Write(ref _observedResult, (int)result);
            Volatile.Write(ref _site, string.IsNullOrWhiteSpace(site) ? "an unnamed site" : site);
            Volatile.Write(ref _faultDetail, detail);

            // THE PUBLISH FLAG, and it closes a READER race the once-only gate above never covered. The gate
            // makes the CLAIM atomic, but the claim lands before the record is stored, so a reader on any other
            // thread could see a latch that already said lost with nothing readable in it. HeaderValue is gated
            // on this flag rather than on that claim.
            Volatile.Write(ref _published, true);

            // Flipped after the record is stored AND published, so a disposal racing the flip cannot run between
            // the two and find a latch that says lost with nothing readable in it.
            _liveness.MarkDead();

            _log.Error($"The native Vulkan device was LOST, first noticed at {Site} (the call returned "
                + $"{VulkanResultCodes.Token(result)}). {detail ?? "The driver offers no fault detail, which "
                    + "means VK_EXT_device_fault is not enabled on this device."} Every later destroy on this "
                + "device is now a no-op, fences read as signaled, and drains do nothing. This reason is in the "
                + "telemetry session header for this run.");
            return true;
        }

        // Never allowed to throw: a fault read that faults during a device loss would replace the diagnostic with
        // a second, less informative failure at exactly the moment the first one mattered. The catch is the belt
        // to the interface's own brace, because an implementation is interop against a device that has just died.
        string? ReadFaultDetail()
        {
            if (_fault is null) return null;

            try
            {
                string? detail = _fault.DescribeFault();
                return string.IsNullOrWhiteSpace(detail) ? null : detail;
            }
            catch (Exception ex)
            {
                _log.Warn("Reading the Vulkan device fault detail threw while reporting a device loss, so this "
                    + $"session cannot say why the device went. It threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }
}
