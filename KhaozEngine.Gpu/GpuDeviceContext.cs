using System;
using System.Collections.Generic;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Owns the <see cref="IGpuDevice"/> a registered <see cref="IGpuBackendProvider"/> built, created through
    /// <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool)"/> / <see cref="CreateHeadless()"/>.
    /// Centralizes backend selection and surfaces <see cref="GpuCapabilities"/>.
    /// <para>
    /// It is the only path a device gets handed back to a consumer on, so everything a consumer or a session log
    /// sees about a device is decided here, once, whichever backend built it.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Device creation and disposal are serialized process-wide behind a single static gate, on every backend.
    /// Concurrent device creation races the Vulkan loader's dispatch setup: on Mesa 25.2.8 lavapipe under
    /// full test-suite parallelism, two threads simultaneously inside <c>vkCreateDevice</c> /
    /// <c>vkGetDeviceQueue</c> made the loader see a just-created device as invalid and abort. Creation and
    /// disposal are rare relative to render work, so serializing them process-wide costs nothing measurable.
    /// </remarks>
    public sealed partial class GpuDeviceContext : IDisposable
    {
        // Serializes device creation and disposal across every thread and every backend. See the class remarks
        // for why: it closes the concurrent-device-creation race that aborts the Vulkan loader on lavapipe.
        static readonly object _lifecycleGate = new();

        static readonly ILogger log = Log.For<GpuDeviceContext>();

        readonly bool _ownsDevice;

        /// <summary>The selected graphics backend (from <see cref="GpuBackendSelector"/>).</summary>
        public GpuBackendKind Backend => Selection.Backend;

        /// <summary>
        /// The backend choice WITH its provenance: OS probe, an honoured <c>KE_GRAPHICS_BACKEND</c> override, an
        /// unrecognized one (the raw value is kept), the game's stored user preference, or a fallback after the
        /// requested backend failed to create. Logged once per created device, so a session log answers "which
        /// backend actually ran" without the tester having to reproduce their shell.
        /// <para>
        /// A <see cref="GpuBackendSource.FallbackAfterFailure"/> source is the one a consuming game must ACT on:
        /// <see cref="GpuBackendSelection.RequestedBackend"/> did not work on this machine, so a stored preference
        /// naming it has to be cleared or the player retries the same broken choice on every launch. The engine
        /// cannot clear it: writing settings would mean file IO, which this package does not do.
        /// </para>
        /// </summary>
        public GpuBackendSelection Selection { get; }

        /// <summary>
        /// Clip-space / depth conventions of the live device (see <see cref="GpuCapabilities"/>).
        /// <para>
        /// Read straight off <see cref="GpuDevice"/> rather than derived a second time here, so the two copies
        /// cannot say different things. They did once: the device name and the sampler feature flags were
        /// populated on one and dropped on the other. One reader is what fixed it, and reading the device's own
        /// answer is what keeps it fixed for a device the engine built itself, which has no shared reader to
        /// point a second derivation at.
        /// </para>
        /// </summary>
        public GpuCapabilities Capabilities { get; }

        /// <summary>
        /// The graphics driver's multi-threading capabilities, on Direct3D11 only. Null on every other backend, off
        /// Windows, and when the query failed: those all mean "no answer", and
        /// <see cref="GpuThreadingDiagnostics.Describe"/> renders them the same way. Read once at device creation
        /// and logged next to the backend line, so a tester's log answers whether their driver is emulating
        /// command lists without them reproducing anything. A game debug overlay can surface it too.
        /// </summary>
        public GpuThreadingCaps? ThreadingCaps { get; }

        /// <summary>
        /// Why the Direct3D11 driver-threading probe produced no answer, or null when it answered or there was
        /// nothing to ask (any other backend, off Windows). <see cref="ThreadingCaps"/> being null cannot tell
        /// those two apart on its own, and the difference is exactly whether a WARN belongs under the threading
        /// line, so the reason is carried rather than discarded. Internal because the public surface exposes the
        /// ANSWER and not the plumbing behind it: the reason is already in the session log.
        /// </summary>
        internal string? ThreadingProbeFailure { get; }

        /// <summary>
        /// The adapter the device is running on, as the backend reports it, or an empty string when it reports
        /// nothing. On Direct3D11 this is EXACTLY the DXGI adapter description
        /// (<c>IDXGIAdapter::GetDesc().Description</c>), which is the string that identifies the physical card in
        /// a bug report.
        /// <para>
        /// The same value as <see cref="GpuCapabilities.DeviceName"/> on <see cref="Capabilities"/>, which stays
        /// the single source. It is named again here because "adapter description" is what a reader chasing a
        /// Direct3D11 problem goes looking for, and they will not guess to look under capabilities.
        /// </para>
        /// </summary>
        public string AdapterDescription => Capabilities.DeviceName;

        /// <summary>
        /// The known third-party overlay / capture injectors loaded into this process at device creation, or null
        /// when nothing was scanned (off Windows, or the scan failed). An EMPTY list is the opposite fact from
        /// null: the scan ran and the process is clean. Render it with
        /// <see cref="GpuInjectedModules.Describe"/>, which keeps those two apart.
        /// <para>
        /// Worth surfacing because software of this kind hooks Direct3D and causes stutter, corrupted frames, and
        /// driver-level crashes that read as engine bugs. The engine logs a warning for a non-empty list at device
        /// creation, so a debug overlay row is for the player who never opens a log.
        /// </para>
        /// </summary>
        public IReadOnlyList<string>? InjectedModules { get; }

        /// <summary>
        /// The live device diagnostics: whether this session is on a software rasterizer, and why the device was
        /// lost if it has been. Read THROUGH to the device on every access rather than captured, because a device
        /// loss happens at an arbitrary moment after creation and a cached value would always say the device was
        /// fine. Null members mean the backend does not report that fact.
        /// </summary>
        public GpuDeviceDiagnostics Diagnostics => GpuDevice.Diagnostics;

        /// <summary>
        /// The live soak counters: drains, ring backpressure stalls, and off-timeline deferrals, cumulative since
        /// the device was created. Read THROUGH to the device for the same reason the diagnostics are, since these
        /// move on every frame and a captured copy would report the moment the context was built.
        /// <see cref="GpuDeviceCounters.HasValue"/> is false on every backend that keeps none of them.
        /// </summary>
        public GpuDeviceCounters Counters => GpuDevice.Counters;

        /// <summary>
        /// The engine-owned GPU device, as its backend package built it. Renderers (Render2D / Render3D) consume
        /// this and never a backend type. Disposal flows through this context's <see cref="Dispose"/>.
        /// </summary>
        public IGpuDevice GpuDevice { get; }

        /// <summary>
        /// Adopt the <see cref="IGpuDevice"/> a backend package built. This is the ONE path a device arrives on:
        /// the registered provider creates it, probes its own driver capabilities (via the raw-pointer entry on
        /// <see cref="Internal.D3D11ThreadingProbe"/>), and hands both here.
        /// <para>
        /// Everything a consumer or a session log observes is decided here rather than by the backend: the same
        /// capabilities-from-the-device rule, the same four ordered diagnostic lines, the same
        /// <see cref="GpuTelemetry"/> feed, and the same process-wide lifecycle gate around disposal.
        /// </para>
        /// <para>
        /// <paramref name="threadingCaps"/> null means "no answer", and
        /// <paramref name="threadingProbeFailure"/> is the reason when the probe was ATTEMPTED and did not answer
        /// (null when it answered, and null when there was nothing to ask). That pair is precisely what the
        /// raw-pointer entry on <see cref="Internal.D3D11ThreadingProbe"/> hands back, so a provider whose probe
        /// faulted still gets the WARN line rather than a bare "unknown" INFO line that reads like an ordinary
        /// non-Direct3D11 session. <paramref name="ownsDevice"/> false makes disposal a no-op, so a caller can hand
        /// in a device it keeps owning.
        /// </para>
        /// <para>
        /// <paramref name="device"/>'s own <see cref="IGpuDevice.Backend"/> MUST agree with
        /// <paramref name="selection"/>'s, and a mismatch throws. The two arrive independently, and different
        /// consumers downstream read different halves of the pair (the golden image filename, the telemetry session
        /// header, the Direct3D11 threading gate), so a mismatched pair would not fail the run, it would
        /// misattribute it. Silent misattribution is the worst outcome for a rollout whose whole purpose is
        /// attributing field measurements to a backend.
        /// </para>
        /// </summary>
        internal GpuDeviceContext(IGpuDevice device, GpuThreadingCaps? threadingCaps, string? threadingProbeFailure,
            GpuBackendSelection selection, bool ownsDevice)
        {
            if (device.Backend != selection.Backend)
            {
                throw new ArgumentException(
                    $"The adopted device reports backend {device.Backend}, but the selection it is being adopted "
                    + $"with says {selection.Backend}. Everything that attributes this session to a backend reads "
                    + "one or the other of those two (the golden image filename, the telemetry session header, the "
                    + "Direct3D11 threading line), so a mismatched pair misattributes the run instead of failing "
                    + "it. Hand in the selection the device was actually created on.",
                    nameof(selection));
            }

            Selection = selection;
            _ownsDevice = ownsDevice;
            GpuDevice = device;
            Capabilities = device.Capabilities;
            ThreadingCaps = threadingCaps;
            ThreadingProbeFailure = threadingProbeFailure;
            InjectedModules = Internal.InjectedModuleProbe.TryScan(out string? scanFailure);
            LogCreation(selection, Capabilities, ThreadingCaps, ThreadingProbeFailure, InjectedModules, scanFailure);
        }

        // The four diagnostic lines every created device emits, in this order, whichever backend created it. One
        // place, so two backends' logs answer the same questions in the same order. Two copies of this sequence
        // would be two logs a reader cannot compare, which is the whole reason the lines exist.
        static void LogCreation(GpuBackendSelection selection, GpuCapabilities capabilities,
            GpuThreadingCaps? threadingCaps, string? probeFailure, IReadOnlyList<string>? modules,
            string? scanFailure)
        {
            LogSelection(selection);
            LogAdapter(capabilities);
            LogThreadingCaps(selection.Backend, threadingCaps, probeFailure);
            LogInjectedModules(modules, scanFailure);
        }

        // One line per created device saying which backend is live and who chose it. The warning arm is the
        // valuable one: a mistyped KE_GRAPHICS_BACKEND is otherwise indistinguishable from the OS default, so a
        // remote perf comparison can be spent proving nothing.
        static void LogSelection(GpuBackendSelection selection)
        {
            // An unrecognized override is reported whenever one was present and did NOT decide the backend, which
            // since 17.23.0 includes the case where a stored preference supplied the backend instead. Keying off
            // the raw value rather than the source is what keeps that warning alive on the new path.
            if (selection.RequestedOverride != null && selection.Source != GpuBackendSource.EnvironmentOverride)
                log.Warn(UnrecognizedOverrideWarning(selection.RequestedOverride, selection.Backend));

            // The 18.0.0 retirement, said out loud once per device. A redirect that only showed up as a changed
            // backend name in the boot line would be exactly the silent implementation swap the removal design
            // refuses: a tester who set KE_GRAPHICS_BACKEND=metal, or a player whose settings file still says
            // Direct3D11, has to be told the backend they named is gone and which one ran.
            if (Retired(selection.RequestedBackend))
            {
                log.Warn(GpuBackendSelector.RetirementWarning(
                    selection.RequestedBackend!.Value, selection.Backend));
            }

            log.Info(SelectionLine(selection));
        }

        // Where the backend came from, as a reader sees it in the parentheses. Every member is spelled out
        // rather than leaning on a discard arm: an appended member must show up here as a compile-time gap to
        // fill, not silently render as the default in a tester's log.
        static string OriginOf(GpuBackendSelection selection) => selection.Source switch
        {
            GpuBackendSource.OsProbe => DefaultOrigin,
            // The RETIRED arms come first, and the ordering is the whole reason they read correctly: a switch
            // expression takes the first matching arm, so a guarded arm placed after its own unguarded source is
            // a compile error rather than a subtle one. Since 18.0.0 two sources can carry a retired
            // RequestedBackend, and both have to say so.
            GpuBackendSource.EnvironmentOverride when Retired(selection.RequestedBackend) =>
                $"{GpuBackendSelector.EnvVarName} override, {selection.RequestedBackend} retired",
            GpuBackendSource.EnvironmentOverride => $"{GpuBackendSelector.EnvVarName} override",
            GpuBackendSource.UnrecognizedOverride => $"{DefaultOrigin}, override not recognized",
            GpuBackendSource.UserPreference => "stored user preference",
            // A retirement is not a failure, and a reader who is told a device failed to create goes looking for
            // a driver. Both arms still report FallbackAfterFailure to the GAME, on purpose: the action is
            // identical (clear the stored choice) and a second source would have made every consumer handle two.
            GpuBackendSource.FallbackAfterFailure when Retired(selection.RequestedBackend) =>
                $"fallback, {selection.RequestedBackend} retired",
            GpuBackendSource.FallbackAfterFailure => $"fallback, {selection.RequestedBackend} failed",
            // Retired in 18.0.0 and never produced any more (see the member). Kept as a spelled-out arm rather
            // than folded into the discard so a 17.40.0 capture replayed through here still reads as itself.
            GpuBackendSource.DefaultProviderMissing =>
                $"default, {selection.RequestedBackend} has no registered provider",
            _ => $"unknown source {(int)selection.Source}",
        };

        /// <summary>
        /// The word the boot line uses for a backend nothing asked for: <c>default</c>. It read <c>OS probe</c>
        /// until 17.40.0, and the flip is what made that wrong rather than merely wordy. A native backend was
        /// unreachable without naming it, so every native session printed
        /// <c>(KE_GRAPHICS_BACKEND override)</c>, and a reader triaging a capture learned which backend ran AND
        /// that somebody had chosen it. After the flip the native backend is what a session gets by DEFAULT,
        /// and the line has to say the difference: an override still reads as an override, and the default
        /// reads as the default.
        /// </summary>
        internal const string DefaultOrigin = "default";

        // Null-tolerant, because RequestedBackend is only set when the engine took a different backend than the
        // one asked for, which is exactly the case the two retirement arms above are switching on.
        static bool Retired(GpuBackendKind? requested)
            => requested is GpuBackendKind kind && GpuBackendSelector.IsRetired(kind);

        /// <summary>
        /// The boot line exactly as it reaches the log, built here rather than inline so a test reads the same
        /// string a tester does. The same reason <see cref="UnrecognizedOverrideWarning"/> is factored out, and
        /// the same failure it prevents: a test asserting on a reconstruction of this line passes while the
        /// line itself says something else.
        /// </summary>
        internal static string SelectionLine(GpuBackendSelection selection)
            => $"GPU backend: {selection.Backend} ({OriginOf(selection)})";

        /// <summary>
        /// The unrecognized-override WARN as a tester reads it, built here rather than inline so a test can read
        /// the same string the log gets. The token list comes from <see cref="GpuBackendSelector.RecognizedTokens"/>
        /// and is never spelled out here: as a literal it went stale on both native appends, which is a diagnostic
        /// that omits the very token the reader meant to type.
        /// </summary>
        internal static string UnrecognizedOverrideWarning(string requestedOverride, GpuBackendKind chosen)
            => $"{GpuBackendSelector.EnvVarName}='{requestedOverride}' is not a recognized backend "
                + $"({GpuBackendSelector.RecognizedTokens}). Using {chosen} instead.";

        // Which physical adapter the session actually ran on, right under the backend line. Logged on EVERY
        // backend, unlike the D3D11 lines below, because an adapter name means something everywhere and a bug
        // report that does not say which GPU rendered is a bug report nobody can reproduce. On Direct3D11 the
        // string IS the DXGI adapter description (see AdapterDescription).
        static void LogAdapter(GpuCapabilities capabilities)
        {
            string name = string.IsNullOrWhiteSpace(capabilities.DeviceName)
                ? "unknown (the backend reported no adapter name)"
                : capabilities.DeviceName;
            log.Info($"GPU adapter: {name}");
        }

        // The Direct3D11 companion to the backend line. Silent on every other backend: a Metal or Vulkan log
        // gains nothing from a line saying a D3D11 capability is unknown. The WARN arm is the one that matters,
        // and it is a WARN precisely so it cannot be lost in a tester's log among the INFO chatter. Which warning
        // (if any) belongs there is GpuThreadingDiagnostics.WarningFor, pure so both creation paths can be pinned
        // headlessly rather than only the one a Windows Direct3D11 machine can reach.
        static void LogThreadingCaps(GpuBackendKind backend, GpuThreadingCaps? caps, string? probeFailure)
        {
            // Via IsDirect3D11, which still covers both members: the retired GpuBackendKind.Direct3D11 never
            // reaches a created device, and the predicate is about the DRIVER rather than an implementation.
            if (!backend.IsDirect3D11()) return;

            log.Info($"D3D11 driver threading: {GpuThreadingDiagnostics.Describe(caps)}");
            string? warning = GpuThreadingDiagnostics.WarningFor(caps, probeFailure);
            if (warning != null) log.Warn(warning);
        }

        // Which third-party overlays were hooked into the process when the device was made. Gated on the SCAN
        // rather than the backend: overlays inject on Windows whatever API is in use, so a Windows Vulkan session
        // wants this line too, and off Windows there is no scan and therefore no line at all.
        static void LogInjectedModules(IReadOnlyList<string>? modules, string? scanFailure)
        {
            if (modules is null)
            {
                if (scanFailure != null)
                    log.Warn($"Could not check this process for graphics overlay software ({scanFailure}). "
                        + "Rendering is unaffected, but a crash report from this run cannot rule out an injected "
                        + "overlay.");
                return;
            }

            log.Info($"Graphics overlay software: {GpuInjectedModules.Describe(modules)}");
            if (GpuInjectedModules.ShouldWarn(modules))
                log.Warn(GpuInjectedModules.Warning(modules));
        }


        public void Dispose()
        {
            if (!_ownsDevice) return;
            lock (_lifecycleGate)
            {
                // Latch the device first (still inside the gate) so any later straggling drain from a
                // resource wrapper disposed after this context no-ops instead of waiting on a dead device.
                // Through IGpuDeviceLifecycle rather than a cast to any one implementation, which is what keeps
                // this context free of every backend. A device with nothing to latch skips it.
                (GpuDevice as IGpuDeviceLifecycle)?.MarkDeviceDisposed();
                // An adopted device owns whatever it is built on, so it disposes itself.
                GpuDevice.Dispose();
            }
        }
    }
}
