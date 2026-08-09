using System;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-G4: the command-buffer error latch. One per device. It is handed a
    /// <see cref="MetalCommandBufferFault"/> at each site that can see one, and on the first that IS a failure it
    /// records the code, the description and the site, flips <see cref="MetalDeviceLiveness"/> so every later
    /// release is a no-op, and exposes the reason through <see cref="HeaderValue"/>, which the device reads into
    /// the telemetry session header's existing <c>deviceLossReason</c> field. That closes #427 for the Metal leg
    /// on the day the backend lands, which is the correct time.
    ///
    /// <para><b>THE INCUMBENT CANNOT REPORT ANY OF THIS.</b> It reads <c>MTLCommandBuffer.status</c> in exactly
    /// one place (<c>WaitForIdleCore</c>, to decide whether to wait) and never reads <c>.error</c> at all, so a
    /// Metal device loss today is invisible to the engine and to telemetry. There is no fix to port here: the
    /// reporting is new.</para>
    ///
    /// <para><b>EVERY FAILURE LATCHES, NOT ONLY THE DEVICE-LEVEL CODES, AND THAT IS THE DESIGN'S RULING RATHER
    /// THAN AN ACCIDENT OF THIS CODE.</b> M-G4 says "an error LATCHES the MTLCommandBufferError code and the
    /// localized description AT THE FAULT SITE, flips the liveness token". It is worth writing down what that
    /// costs and why it is still right, because the reading it rules out is the tempting one. Vulkan's latch
    /// fires only on <c>VK_ERROR_DEVICE_LOST</c> and lets an ordinary failure be the caller's to report, and the
    /// same triage looks available here: <c>DeviceRemoved</c> and <c>Timeout</c> are about the device while
    /// <c>OutOfMemory</c> is about the workload. It is not available, because the seam has no recovery path for
    /// EITHER. A Metal command buffer that fails has already discarded its work, the GPU seam exposes no way to
    /// resubmit it, and a frame whose commands did not run is a frame the renderer will happily follow with
    /// another that reads its results. So the conservative direction is to stop, and the cost of stopping is that
    /// the native objects behind a dead device are leaked to the process end rather than released into a driver
    /// state nobody can reason about.</para>
    ///
    /// <para><b>THE LATCH IS TAKEN EXACTLY ONCE.</b> Metal completion handlers arrive on an arbitrary internal
    /// thread in no guaranteed order (M-F2), so two failures can be seen in the same instant, and one recorded
    /// reason with one recorded site is the only useful post-mortem: two would be a race over which one the
    /// header carries. The winner records and flips liveness, the loser answers true and does nothing, because
    /// the device is just as dead from its point of view.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE</b>, over a plain snapshot and the plain
    /// <see cref="MetalDeviceLiveness"/> class, so the latch, the once-only rule, the liveness flip, the header
    /// string and the publish race all run under <c>dotnet test</c> on Linux and Windows.</para>
    /// </summary>
    internal sealed class MetalDeviceLossLatch
    {
        static readonly ILogger log = Log.For<MetalDeviceLossLatch>();

        const int Healthy = 0;
        const int Lost = 1;

        readonly MetalDeviceLiveness _liveness;
        readonly ILogger _log;

        int _state = Healthy;
        // Stored as longs because Volatile has no overload for an enum, and both are NSInteger-shaped already.
        long _status;
        long _code;
        string? _description;
        string? _site;
        bool _published;

        /// <param name="liveness">The device's one liveness token, flipped on the first observed failure.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal MetalDeviceLossLatch(MetalDeviceLiveness liveness, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(liveness);

            _liveness = liveness;
            _log = logger ?? log;
        }

        /// <summary>True once a command-buffer failure has been observed and latched.</summary>
        internal bool IsLost => Volatile.Read(ref _state) == Lost;

        /// <summary>What was observed at the site that noticed, or the healthy reading while nothing has
        /// failed.</summary>
        internal MetalCommandBufferFault Observed => IsLost
            ? new MetalCommandBufferFault((ObjC.MTLCommandBufferStatus)Volatile.Read(ref _status),
                (ObjC.MTLCommandBufferError)Volatile.Read(ref _code), Volatile.Read(ref _description) ?? "")
            : MetalCommandBufferFault.Completed;

        /// <summary>Which site noticed, or null while the device is fine. Carried because a device that has gone
        /// reports failures from every later call too, so saying which one saw it FIRST is the only ordering
        /// information a post-mortem gets out of an unordered completion stream.</summary>
        internal string? Site => Volatile.Read(ref _site);

        /// <summary>
        /// THE SESSION-HEADER FIELD, or null while the device is fine. The stable token plus the site, plus the
        /// driver's own sentence when there is one, so a capture groups cleanly across sessions while still
        /// saying where it was seen.
        /// <para>
        /// GATED ON THE PUBLISH FLAG RATHER THAN ON <see cref="IsLost"/>, because those two are not the same
        /// instant. The claiming thread takes the latch and only THEN stores the record, so a header written from
        /// another thread inside that window would see a latch that says lost with nothing readable in it and
        /// would write a line no later read would correct. Until the whole record is stored this reports null,
        /// which is the same answer it gives for a healthy device: an ordinary session's header is written once,
        /// so the worst case is a capture missing a field rather than a capture asserting a wrong one.
        /// </para>
        /// </summary>
        internal string? HeaderValue
        {
            get
            {
                if (!Volatile.Read(ref _published)) return null;

                MetalCommandBufferFault fault = Observed;
                string detail = fault.Description is { Length: > 0 } d ? $" ({d})" : string.Empty;
                return $"{fault.Token()} at {Site}{detail}";
            }
        }

        /// <summary>
        /// Check one command-buffer reading from <paramref name="site"/>. Returns true when the device is lost,
        /// which includes the case where it was already lost before this call, so a caller can use it as the one
        /// question worth asking: should I stop.
        /// </summary>
        /// <param name="fault">What the finished command buffer reported.</param>
        /// <param name="site">The engine-level operation that read it, e.g. <c>waitUntilCompleted (teardown
        /// drain)</c>. It is what the header and the log line name.</param>
        internal bool Check(in MetalCommandBufferFault fault, string site)
        {
            if (IsLost) return true;
            if (!fault.IsFailure) return false;

            return Latch(fault, site);
        }

        // The once-only gate. CompareExchange rather than a lock, because the losing thread has nothing to wait
        // for: the device is dead either way and the winner has already recorded the only answer worth having.
        // A lock would also be the wrong primitive on this path, since row 5's caller is a driver callback.
        bool Latch(in MetalCommandBufferFault fault, string site)
        {
            if (Interlocked.CompareExchange(ref _state, Lost, Healthy) != Healthy) return true;

            Volatile.Write(ref _status, (long)fault.Status);
            Volatile.Write(ref _code, (long)fault.Code);
            Volatile.Write(ref _description, fault.Description);
            Volatile.Write(ref _site, string.IsNullOrWhiteSpace(site) ? "an unnamed site" : site);

            // THE PUBLISH FLAG, and it closes a READER race the once-only gate above never covered. The gate
            // makes the CLAIM atomic, but the claim lands before the record is stored, so a reader on any other
            // thread could see a latch that already said lost with nothing readable in it.
            Volatile.Write(ref _published, true);

            // Flipped after the record is stored AND published, so a disposal racing the flip cannot run between
            // the two and find a latch that says lost with nothing readable in it.
            _liveness.MarkDead();

            _log.Error($"A native Metal command buffer FAILED, first noticed at {Site} ({fault.Token()}). "
                + (fault.Description is { Length: > 0 } d
                    ? "The driver says: " + d + ". "
                    : "The driver offered no description. ")
                + "Every later release on this device is now a no-op, fences read as signaled, and drains do "
                + "nothing. The seam has no way to resubmit discarded work, so the device is treated as gone "
                + "rather than as recoverable. This reason is in the telemetry session header for this run.");
            return true;
        }
    }
}
