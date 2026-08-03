using System;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// DECISION G3: the device-loss latch. One per device. It is handed an HRESULT at each of the sites that can
    /// see a removal, and on the first one that IS a removal it calls <c>GetDeviceRemovedReason</c> immediately,
    /// records the reason and the site, flips <see cref="D3D11DeviceLiveness"/> so every later disposal is a
    /// no-op, and hands the reason to the telemetry session header.
    /// <para>
    /// IMMEDIACY IS THE WHOLE DESIGN. <c>DXGI_ERROR_DEVICE_REMOVED</c> is sticky: it is returned by every call
    /// after the device dies, so the reason is only meaningful at the FIRST site that notices, and subsequent
    /// calls muddy it. That is not a theoretical concern here. All 25 <c>DEVICE_REMOVED</c> stacks on #423
    /// pointed at a texture view constructor deep inside resource-set activation, which is the site that happened
    /// to make the next call rather than the site anything went wrong at, and the investigation those stacks cost
    /// is the reason #427 exists.
    /// </para>
    ///
    /// <para><b>THE THREE HRESULT SITES, plus a fourth that arrives as a throw.</b> Decision G3 names the first
    /// three, and the device row wires all four:
    /// <list type="number">
    ///   <item><description>After <see cref="D3D11Swapchain.Present"/>, which returns its raw HRESULT precisely
    ///   so this can read it at the site rather than downstream of a throw.</description></item>
    ///   <item><description>After every staging <c>Map</c>. That path arrives with the copies and readback row,
    ///   so the seam is defined here and the call site is that row's.</description></item>
    ///   <item><description>After replay, at the end of <c>Submit</c>.</description></item>
    ///   <item><description><see cref="CheckAfterFault"/>, for a site whose native call THROWS instead of
    ///   returning. The swapchain's resize apply is the known one (#489): <c>ResizeBuffers</c>, <c>GetBuffer</c>
    ///   and <c>CreateRenderTargetView</c> all end in <c>CheckError</c>, so a device that dies during a resize
    ///   arrives as a <c>SharpGenException</c> and never as an HRESULT anything reads.</description></item>
    /// </list></para>
    ///
    /// <para><b>THE LATCH IS TAKEN EXACTLY ONCE, and that is what the interlocked state is for.</b> Two threads
    /// can notice a removal in the same instant (a present on the submit thread and a map on a loader thread),
    /// and one recorded reason with one recorded site is the only useful post-mortem: two would be a race over
    /// which one the header carries. The winner records and flips liveness, and the loser answers true and does
    /// nothing, because the device is just as dead from its point of view. Reading the reason itself is
    /// idempotent and side-effect-free, so <see cref="CheckAfterFault"/> may read it before it knows whether it
    /// won. What must not happen is another CALL between the fault and the read, and that is what the ordering
    /// inside <see cref="Latch"/> is about.</para>
    ///
    /// <para><b>IT FLIPS LIVENESS, and that widens what <see cref="D3D11DeviceLiveness"/> was originally for.</b>
    /// That token was written for teardown, flipped by the context before the real device is destroyed. A device
    /// loss is the other way a device stops existing: the objects are already gone, the application has not asked
    /// for anything, and every release from here on would be a release against freed memory. Flipping the same
    /// token is what makes the rest of the shutdown quiet, and the token's one-way contract holds for the same
    /// reason it always did.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE</b>, over <see cref="ID3D11RemovedReason"/> and the plain
    /// <see cref="D3D11DeviceLiveness"/> class, so the latch, the once-only rule, the liveness flip, the header
    /// string and the fault path all run under <c>dotnet test</c> on macOS.</para>
    /// </summary>
    internal sealed class D3D11DeviceLossLatch
    {
        static readonly ILogger log = Log.For<D3D11DeviceLossLatch>();

        const int NotLost = 0;
        const int Lost = 1;

        readonly D3D11DeviceLiveness _liveness;
        readonly ID3D11RemovedReason _reason;
        readonly ILogger _log;

        int _state = NotLost;
        int _observedHresult = D3D11DeviceLossCodes.Ok;
        int _removedReason = D3D11DeviceLossCodes.Ok;
        string? _site;
        bool _published;

        /// <param name="liveness">The device's one liveness token, flipped on the first observed loss.</param>
        /// <param name="reason">The device, as the one call this makes at a fault site.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal D3D11DeviceLossLatch(D3D11DeviceLiveness liveness, ID3D11RemovedReason reason, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(reason);

            _liveness = liveness;
            _reason = reason;
            _log = logger ?? log;
        }

        /// <summary>True once a device loss has been observed and latched.</summary>
        internal bool IsLost => Volatile.Read(ref _state) == Lost;

        /// <summary>The HRESULT that was observed at the site that noticed, or <c>S_OK</c> while the device is
        /// fine. For <see cref="CheckAfterFault"/> this is the removal reason itself, since that path has no
        /// HRESULT of its own to report.</summary>
        internal int ObservedHresult => Volatile.Read(ref _observedHresult);

        /// <summary><c>GetDeviceRemovedReason</c>'s answer, read at the fault site, or <c>S_OK</c> while the
        /// device is fine. THE field #427 is about.</summary>
        internal int RemovedReason => Volatile.Read(ref _removedReason);

        /// <summary>Which check site noticed, or null while the device is fine. Carried because the sticky HRESULT
        /// makes the site itself a weak clue, and saying which one saw it first is the only ordering information
        /// a post-mortem gets.</summary>
        internal string? Site => Volatile.Read(ref _site);

        /// <summary>
        /// THE SESSION-HEADER FIELD, or null while the device is fine. The stable token from
        /// <see cref="D3D11DeviceLossCodes.Token"/> plus the site, so a capture groups cleanly across sessions
        /// while still saying where it was seen. The full sentence goes in the session log, not here.
        /// <para>
        /// GATED ON THE PUBLISH FLAG RATHER THAN ON <see cref="IsLost"/>, because those two are not the same
        /// instant. The claiming thread takes the latch and only THEN calls <c>GetDeviceRemovedReason</c>, so a
        /// header written from another thread inside that window would see a latch that says lost with no reason
        /// and no site in it, and would write a line like "an unknown reason at " into the capture that no later
        /// read would correct. Until the reason and the site are both stored this reports null, which is the same
        /// answer it gives for a healthy device: an ordinary session's header is written once, so the worst case
        /// is a capture missing a field rather than a capture asserting a wrong one.
        /// </para>
        /// </summary>
        internal string? HeaderValue
            => Volatile.Read(ref _published) ? $"{D3D11DeviceLossCodes.Token(RemovedReason)} at {Site}" : null;

        /// <summary>
        /// Check one HRESULT from <paramref name="site"/>. Returns true when the device is lost, which includes
        /// the case where it was already lost before this call, so a caller can use it as the one question worth
        /// asking: should I stop.
        /// <para>
        /// An ORDINARY failure is not a device loss and is deliberately not latched here (see
        /// <see cref="D3D11DeviceLossCodes.IsDeviceLoss"/>). It is the caller's to report or throw, because only
        /// the caller knows whether its own failed call is recoverable.
        /// </para>
        /// </summary>
        internal bool Check(int hresult, string site)
        {
            if (IsLost) return true;
            if (!D3D11DeviceLossCodes.IsDeviceLoss(hresult)) return false;

            return Latch(hresult, site);
        }

        /// <summary>
        /// THE FOURTH SITE (#489): a native call at <paramref name="site"/> threw, so there is no HRESULT to
        /// check, and the question is whether the device is why. Asks the device for its removal reason directly
        /// and latches when the answer is not <c>S_OK</c>. Returns true when the device is lost.
        /// <para>
        /// The swapchain's resize apply is the known caller. It releases the backbuffer views, resizes, and
        /// recreates them, because <c>ResizeBuffers</c> fails while any reference to a backbuffer survives, and a
        /// throw in the middle leaves the framebuffer pointing at released views. There is no rollback available
        /// (holding the old views across the resize is precisely what <c>ResizeBuffers</c> forbids), so the latch
        /// IS the repair: once the device is known dead nothing binds again, and the reason is captured at the
        /// site rather than muddied by whatever ran next.
        /// </para>
        /// <para>
        /// A FALSE ANSWER MEANS THE THROW WAS SOMETHING ELSE, and the caller must go on treating it as its own
        /// fault rather than swallowing it. This answers one question and never handles the exception.
        /// </para>
        /// </summary>
        internal bool CheckAfterFault(string site)
        {
            if (IsLost) return true;

            int reason = ReadReason();
            if (reason == D3D11DeviceLossCodes.Ok) return false;

            return Latch(reason, site, reason);
        }

        // The once-only gate. CompareExchange rather than a lock, because the losing thread has nothing to wait
        // for: the device is dead either way and the winner has already read the only answer worth having.
        bool Latch(int observedHresult, string site, int? knownReason = null)
        {
            if (Interlocked.CompareExchange(ref _state, Lost, NotLost) != NotLost) return true;

            // IMMEDIATELY, and before anything else at all. Every line below this one is a call that could raise
            // its own error and overwrite what the runtime is holding, which is the exact failure G3 is about.
            int reason = knownReason ?? ReadReason();

            Volatile.Write(ref _observedHresult, observedHresult);
            Volatile.Write(ref _removedReason, reason);
            Volatile.Write(ref _site, string.IsNullOrWhiteSpace(site) ? "an unnamed site" : site);

            // THE PUBLISH FLAG, and it closes a READER race the once-only gate above never covered. The gate
            // makes the CLAIM atomic, but the claim lands before the record is stored, and on the Check path
            // before GetDeviceRemovedReason has even been called, so a reader on any other thread could see a
            // latch that already said lost with no reason and no site in it. HeaderValue is gated on this flag
            // rather than on that claim, so it reports the unlatched form until the whole record is here.
            Volatile.Write(ref _published, true);

            // Flipped after the reason is recorded AND published, so a disposal racing the flip cannot run
            // between the two and find a latch that says lost with nothing readable in it.
            _liveness.MarkDead();

            _log.Error($"The Direct3D 11 device was LOST, first noticed at {Site} "
                + $"(the call returned {D3D11DeviceLossCodes.Token(observedHresult)}). GetDeviceRemovedReason "
                + $"says: {D3D11DeviceLossCodes.Describe(reason)}. Every later release on this device is now a "
                + "no-op, fences read as signaled, and drains do nothing. This reason is in the telemetry session "
                + "header for this run.");
            return true;
        }

        // Never allowed to throw: a reason read that faults during a device loss would replace the diagnostic
        // with a second, less informative failure at exactly the moment the first one mattered. An unreadable
        // reason is reported as S_OK by the interface contract, and the catch here is the belt to that brace,
        // because the implementation is interop against a device that has just died.
        int ReadReason()
        {
            try
            {
                return _reason.GetDeviceRemovedReason();
            }
            catch (Exception ex)
            {
                _log.Warn("GetDeviceRemovedReason threw while reporting a Direct3D 11 device loss, so this "
                    + $"session cannot say why the device went. It threw {ex.GetType().Name}: {ex.Message}");
                return D3D11DeviceLossCodes.Ok;
            }
        }
    }
}
