using System;
using System.Collections.Generic;
using Veldrid;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Owns a Veldrid device created via <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool)"/> / <see cref="CreateHeadless()"/> plus the
    /// engine-owned <see cref="GpuDevice"/> wrapping it. Centralizes backend selection (no hard-coded
    /// <c>GraphicsBackend.Metal</c>) and surfaces <see cref="GpuCapabilities"/>. Renderers consume
    /// <see cref="GpuDevice"/>, and the raw Veldrid device stays a private implementation detail of this context.
    /// <para>
    /// It is ALSO the only path a device gets handed back to a consumer on, which is why it additionally adopts an
    /// <see cref="IGpuDevice"/> the engine created itself, with no Veldrid device behind it (the internal
    /// constructor below). Everything a consumer or a session log sees is the same on both paths.
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
        // Serializes GraphicsDevice creation and disposal across every thread and every backend. See the class
        // remarks for why: it closes the concurrent-device-creation race that aborts the Vulkan loader on lavapipe.
        static readonly object _lifecycleGate = new();

        static readonly ILogger log = Log.For<GpuDeviceContext>();

        readonly bool _ownsDevice;
        // The raw Veldrid device, on the Veldrid creation path only. NULL on the adopted-device path: a device the
        // engine built itself has no GraphicsDevice behind it and disposes through IGpuDevice instead (see Dispose).
        readonly GraphicsDevice? _device;

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
        /// nothing. On Direct3D11 this is EXACTLY the DXGI adapter description (Veldrid reads
        /// <c>IDXGIAdapter::GetDesc().Description</c> into <c>GraphicsDevice.DeviceName</c>), which is the string
        /// that identifies the physical card in a bug report, so no Vortice interop is needed to get it.
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
        /// fine. Null members mean the backend does not report that fact, which is what the Veldrid path answers
        /// for both.
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
        /// The engine-owned GPU device: on the Veldrid path, the wrapper around the underlying Veldrid device.
        /// Renderers (Render2D / Render3D) consume this instead of the raw device, so Veldrid stays hidden. That
        /// wrapper is non-owning, and disposal flows through this context's <see cref="Dispose"/> either way.
        /// </summary>
        public IGpuDevice GpuDevice { get; }

        GpuDeviceContext(GraphicsDevice device, GpuBackendSelection selection, bool ownsDevice)
        {
            _device = device;
            Selection = selection;
            _ownsDevice = ownsDevice;
            // Non-owning wrapper: this context owns the raw device's disposal (see Dispose), so the wrapper must
            // not dispose it again.
            GpuDevice = new VeldridGpuDevice(device, selection.Backend, ownsDevice: false);
            // VeldridMap.ReadCapabilities stays the single source, and it is now read ONCE, inside the wrapper.
            Capabilities = GpuDevice.Capabilities;
            ThreadingCaps = Internal.D3D11ThreadingProbe.TryQuery(device, selection.Backend, out string? probeFailure);
            ThreadingProbeFailure = probeFailure;
            // Scanned per created device rather than cached process-wide, so a late-attaching overlay still shows
            // up and there is no static state to reason about. Device creation is rare, and off Windows this is a
            // guard and a return.
            InjectedModules = Internal.InjectedModuleProbe.TryScan(out string? scanFailure);
            LogCreation(selection, Capabilities, ThreadingCaps, ThreadingProbeFailure, InjectedModules, scanFailure);
        }

        /// <summary>
        /// Adopt an <see cref="IGpuDevice"/> the engine created ITSELF, with no Veldrid device behind it. This is
        /// the path a native backend comes back through: its provider creates the device, probes its own driver
        /// capabilities (via the raw-pointer entry on <see cref="Internal.D3D11ThreadingProbe"/>, since there is no
        /// Veldrid device to read a pointer off), and hands both here.
        /// <para>
        /// Everything a consumer or a session log observes is identical to the Veldrid path: the same
        /// capabilities-from-the-device rule, the same four ordered diagnostic lines, the same
        /// <see cref="GpuTelemetry"/> feed, and the same process-wide lifecycle gate around disposal.
        /// </para>
        /// <para>
        /// <paramref name="threadingCaps"/> null means "no answer", exactly as it does on the Veldrid path, and
        /// <paramref name="threadingProbeFailure"/> is the reason when the probe was ATTEMPTED and did not answer
        /// (null when it answered, and null when there was nothing to ask). That pair is precisely what the
        /// raw-pointer entry on <see cref="Internal.D3D11ThreadingProbe"/> hands back, so a provider whose probe
        /// faulted still gets the WARN line rather than a bare "unknown" INFO line that reads like an ordinary
        /// non-Direct3D11 session. <paramref name="ownsDevice"/> false makes disposal a no-op, so a caller can hand
        /// in a device it keeps owning.
        /// </para>
        /// <para>
        /// <paramref name="device"/>'s own <see cref="IGpuDevice.Backend"/> MUST agree with
        /// <paramref name="selection"/>'s, and a mismatch throws. The Veldrid path gets that invariant for free,
        /// because it builds the wrapper from the same selection. Here the two arrive independently, and different
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

            _device = null;
            Selection = selection;
            _ownsDevice = ownsDevice;
            GpuDevice = device;
            Capabilities = device.Capabilities;
            ThreadingCaps = threadingCaps;
            ThreadingProbeFailure = threadingProbeFailure;
            InjectedModules = Internal.InjectedModuleProbe.TryScan(out string? scanFailure);
            LogCreation(selection, Capabilities, ThreadingCaps, ThreadingProbeFailure, InjectedModules, scanFailure);
        }

        // The four diagnostic lines every created device emits, in this order, whichever path created it. One
        // place, so a log from an adopted native device and a log from a Veldrid device answer the same questions
        // in the same order. Two copies of this sequence would be two logs a reader cannot compare, which is the
        // whole reason the lines exist.
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

            log.Info(SelectionLine(selection));
        }

        // Where the backend came from, as a reader sees it in the parentheses. Every member is spelled out
        // rather than leaning on a discard arm: an appended member must show up here as a compile-time gap to
        // fill, not silently render as the default in a tester's log.
        static string OriginOf(GpuBackendSelection selection) => selection.Source switch
        {
            GpuBackendSource.OsProbe => DefaultOrigin,
            GpuBackendSource.EnvironmentOverride => $"{GpuBackendSelector.EnvVarName} override",
            GpuBackendSource.UnrecognizedOverride => $"{DefaultOrigin}, override not recognized",
            GpuBackendSource.UserPreference => "stored user preference",
            GpuBackendSource.FallbackAfterFailure => $"fallback, {selection.RequestedBackend} failed",
            // The 17.40.0 member, and it deliberately reads nothing like the line above it. Nothing failed and
            // nobody chose: the default is a backend this build has no provider for, and the word a reader
            // needs is the missing registration rather than a failure that did not happen.
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
            // BOTH Direct3D11 implementations, via IsDirect3D11. The driver underneath is the same one whichever
            // implementation drove it, so an emulating-command-lists driver is exactly as worth warning about on
            // the native leg. An equality check against Direct3D11 alone would have dropped this line and the two
            // telemetry threading fields it feeds on the one backend the probe was written for.
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
                // Through IGpuDeviceLifecycle, not a cast to the Veldrid wrapper: the cast is what confined this
                // context to one implementation of IGpuDevice. A device with nothing to latch skips it.
                (GpuDevice as IGpuDeviceLifecycle)?.MarkDeviceDisposed();
                // On the Veldrid path this context owns the RAW device and the wrapper is non-owning, so the raw
                // device is what gets destroyed. An adopted device owns whatever it is built on, so it disposes
                // itself.
                if (_device != null) _device.Dispose();
                else GpuDevice.Dispose();
            }
        }
    }
}
