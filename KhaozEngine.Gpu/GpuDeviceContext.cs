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
    public sealed class GpuDeviceContext : IDisposable
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

            // Every member is spelled out rather than leaning on a discard arm: an appended member must show up
            // here as a compile-time gap to fill, not silently render as "OS probe" in a tester's log.
            string origin = selection.Source switch
            {
                GpuBackendSource.OsProbe => "OS probe",
                GpuBackendSource.EnvironmentOverride => $"{GpuBackendSelector.EnvVarName} override",
                GpuBackendSource.UnrecognizedOverride => "OS probe, override not recognized",
                GpuBackendSource.UserPreference => "stored user preference",
                GpuBackendSource.FallbackAfterFailure => $"fallback, {selection.RequestedBackend} failed",
                _ => $"unknown source {(int)selection.Source}",
            };
            log.Info($"GPU backend: {selection.Backend} ({origin})");
        }

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

        // The Direct3D11 device options for both creation paths, plus the one-time log that proves whether the
        // opt-in diagnostic flag is on. Shared so the windowed and headless sites cannot drift: a lever that works
        // on only one of them is worse than no lever, because a tester setting it sees it do nothing in half their
        // runs and concludes the flag is irrelevant.
        static D3D11DeviceOptions BuildD3D11Options()
        {
            uint flags = GpuD3D11DeviceFlags.FromEnvironment(out string? unrecognized);
            if (unrecognized != null) log.Warn(GpuD3D11DeviceFlags.UnrecognizedWarning(unrecognized));
            else if (flags != 0) log.Info(GpuD3D11DeviceFlags.ActiveDescription);

            return new D3D11DeviceOptions
            {
                UseImmediateContext = true,
                DeviceCreationFlags = flags,
            };
        }

        /// <summary>
        /// Create a windowed graphics device on the selected backend (via <see cref="GpuBackendSelector"/>) from a
        /// platform-native window handle. The window/input platform (KhaozEngine.Windowing, on Silk.NET) creates the
        /// native window and passes its handle here as a <see cref="GpuWindowHandle"/>, so this package needs no
        /// windowing dependency of its own. Builds the Veldrid <c>SwapchainSource</c> for the handle's
        /// <see cref="GpuWindowKind"/>, then creates the backend device with a main swapchain (so
        /// <see cref="IGpuDevice.SwapchainFramebuffer"/> is non-null). <paramref name="syncToVerticalBlank"/> selects
        /// vsync (default true, unchanged) vs immediate presentation; it feeds both the device options and the
        /// swapchain description. The returned context owns the device's disposal: dispose this context, not the
        /// underlying device.
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank = true)
            => CreateForWindow(window, width, height, syncToVerticalBlank, preferredBackend: null);

        /// <summary>
        /// The same windowed device creation as <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool)"/>,
        /// with a stored USER PREFERENCE (the consuming game's in-game graphics setting) sitting between the
        /// <c>KE_GRAPHICS_BACKEND</c> override and the OS probe. Null (the default path) resolves exactly as
        /// before. The preference arrives as data: this package does no file IO and gains no settings dependency.
        /// <para>Note the pairing with the <see cref="GpuBackendKind"/> overload below: a NULLABLE argument is a
        /// preference that may be absent and is resolved against the environment, while a NON-NULLABLE argument
        /// names the backend outright and skips resolution entirely.</para>
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendKind? preferredBackend)
            => CreateForWindow(window, width, height, syncToVerticalBlank,
                GpuBackendSelector.Resolve(preferredBackend));

        /// <summary>
        /// Create a windowed device on EXACTLY <paramref name="backend"/>: no environment override, no stored
        /// preference, no OS probe, and no fallback. This is the "retry as X" lever, for a consumer driving its
        /// own recovery (the engine's built-in fallback does not need it). A failure propagates, because a caller
        /// that named one backend outright is not asking to be quietly given a different one.
        /// <para>Contrast the <see cref="GpuBackendKind"/>? overload above: nullable means "a preference, maybe
        /// absent" and is resolved against the environment WITH fallback, non-nullable means "this one" and is
        /// not. The resulting <see cref="Selection"/> reports
        /// <see cref="GpuBackendSource.UserPreference"/>, since naming a backend from outside the engine is the
        /// same provenance class as a stored preference: neither the environment nor the probe chose it.</para>
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendKind backend)
            => CreateForWindow(window, width, height, syncToVerticalBlank,
                new GpuBackendSelection(backend, GpuBackendSource.UserPreference, null), allowFallback: false);

        static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendSelection selection, bool allowFallback = true)
        {
            // A backend this package cannot reference is created by its registered provider instead, and the
            // branch is taken up here rather than inside CreateOrFallBack because the two paths share none of
            // their inputs: a native device wants no SwapchainSource and no GraphicsDeviceOptions, and building
            // them anyway would put Veldrid work on the creation path of a backend whose premise is having none.
            if (GpuBackendProviders.RequiresProvider(selection.Backend))
                return CreateFromProvider(window, width, height, syncToVerticalBlank, selection, allowFallback);

            SwapchainSource source = window.Kind switch
            {
                GpuWindowKind.Cocoa => SwapchainSource.CreateNSWindow(window.Handle),
                GpuWindowKind.Win32 => SwapchainSource.CreateWin32(window.Handle, IntPtr.Zero),
                GpuWindowKind.X11 => SwapchainSource.CreateXlib(window.Display, window.Handle),
                GpuWindowKind.Wayland => SwapchainSource.CreateWayland(window.Display, window.Handle),
                _ => throw new NotSupportedException($"Unknown GpuWindowKind '{window.Kind}'."),
            };

            // Engine-owned default windowed device options (no swapchain depth attachment, Improved binding,
            // linear non-sRGB swapchain) - the same options the previous windowed CreateWindow path passed, with
            // the vsync flag now caller-selected. Veldrid's GraphicsDeviceOptions stays internal to this package
            // so consumers never reference a Veldrid type.
            var opts = new GraphicsDeviceOptions(false, null, syncToVerticalBlank, ResourceBindingModel.Improved, true, true);
            var scDesc = new SwapchainDescription(source, width, height, null, syncToVerticalBlank, false);

            GraphicsDevice gd;
            GpuBackendSelection actual;
            lock (_lifecycleGate)
            {
                (gd, actual) = CreateOrFallBack(opts, scDesc, selection, allowFallback);
            }
            // Constructed OUTSIDE the gate, as before: the gate exists to serialize Veldrid device creation and
            // disposal, and capability reads / the D3D11 threading probe were never inside it.
            return new GpuDeviceContext(gd, actual, ownsDevice: true);
        }

        /// <summary>
        /// Creates the requested device, falling back to the OS-probe backend rather than propagating when the
        /// requested one cannot be had. This is what stops a player from choosing a backend their machine cannot
        /// run and ending up with a client that will not start and cannot be fixed from inside the game.
        /// </summary>
        /// <remarks>
        /// Two guards, because neither alone is enough. The functional probe rules out the backend up front (no
        /// Vulkan ICD, no required surface extension), and the try/catch covers the case the probe cannot see: a
        /// broken or partial driver that answers "supported" and then fails at device creation anyway.
        /// <para>
        /// Retrying needs NO new window. The native window is already created and initialized by the time
        /// <see cref="GpuWindowHandle"/> is built, and that handle is a plain readonly struct of native pointers
        /// holding no device state, so the second attempt reuses it as-is.
        /// </para>
        /// </remarks>
        static (GraphicsDevice Device, GpuBackendSelection Selection) CreateOrFallBack(
            GraphicsDeviceOptions opts, SwapchainDescription scDesc, GpuBackendSelection selection, bool allowFallback)
        {
            GpuBackendKind requested = selection.Backend;
            GpuBackendKind fallback = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());

            // Nothing to fall back TO when the request already IS the OS-probe default. That covers every call
            // with no override and no preference, i.e. every pre-17.23.0 call site and the whole macOS/Linux
            // default path, which therefore behaves exactly as it always did: create, and let a failure throw.
            if (!allowFallback || requested == fallback)
                return (CreateWindowed(requested, opts, scDesc), selection);

            string? failure = GpuBackendSelector.IsBackendSupported(requested) ? null : NoMachineSupport;

            if (failure is null)
            {
                try
                {
                    return (CreateWindowed(requested, opts, scDesc), selection);
                }
                catch (Exception ex)
                {
                    // Deliberately broad. The Vulkan leg throws VeldridException, the Direct3D11 leg surfaces
                    // SharpGen.Runtime.SharpGenException out of Vortice's Result.CheckError (whose only common
                    // ancestor with VeldridException is System.Exception), and a machine missing a loader library
                    // outright throws DllNotFoundException or TypeInitializationException from the P/Invoke layer
                    // before either type is reached. Naming the two known types would miss exactly the
                    // no-driver-installed case this fallback exists for.
                    failure = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            WarnFallback(requested, failure, fallback);

            return (CreateWindowed(fallback, opts, scDesc), GpuBackendSelector.AfterFallback(selection, fallback));
        }

        // The reason a requested backend could not be had, when the machine itself says so. Shared by the Veldrid
        // and provider paths: the two probe different things (Veldrid's own loader check, and a registered
        // provider's functional probe) but they are the SAME answer to a reader, and two wordings would read as
        // two different problems in a session log.
        const string NoMachineSupport = "this machine reports no support for it";

        // The one fallback warning, in one place, for the same reason. It is the line that tells a player their
        // stored graphics choice does not work here, and a provider-backed backend that fell back has to say it
        // identically or a support reply is written against wording that depends on which backend was asked for.
        static void WarnFallback(GpuBackendKind requested, string failure, GpuBackendKind fallback)
            => log.Warn($"Could not create a {requested} graphics device ({failure}). Falling back to {fallback}. "
                + "If this backend was chosen in the game's graphics settings, that stored choice does not work "
                + "on this machine and should be cleared.");

        /// <summary>
        /// The decision a provider-backed request gets BEFORE anything is created, and the single place decision
        /// I2's two failure modes are told apart. Pure enough to pin headlessly, which matters because the
        /// alternative is only reachable on a machine that has the backend.
        /// <para>
        /// A backend with NO registered provider throws <see cref="GpuBackendProviderMissingException"/> here, and
        /// it throws FIRST, before the support probe below can answer false for the same request and turn a
        /// forgotten one-line registration into a run on a quietly different backend. That ordering is the whole
        /// invariant: a missing registration is a wiring fault in the app, an unsupported machine is a fact about
        /// the hardware, and only the second one is allowed to fall back.
        /// </para>
        /// <para>
        /// Returns null when creation should be attempted, or the reason to warn with and fall back on. With
        /// <paramref name="allowFallback"/> false there is nothing to fall back to, so the probe is skipped
        /// entirely and a real failure throws, exactly as the Veldrid path treats a caller that named one backend
        /// outright.
        /// </para>
        /// </summary>
        internal static string? PreflightProvider(GpuBackendKind backend, bool allowFallback,
            out IGpuBackendProvider provider)
        {
            provider = GpuBackendProviders.Require(backend);
            if (!allowFallback) return null;
            return GpuBackendSelector.IsBackendSupported(backend) ? null : NoMachineSupport;
        }

        // The provider-backed half of windowed creation. Same two guards as the Veldrid path and in the same
        // order: rule the backend out up front with the functional probe, then catch what the probe cannot see (a
        // driver that answers "supported" and fails at device creation anyway).
        static GpuDeviceContext CreateFromProvider(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendSelection selection, bool allowFallback)
        {
            GpuBackendKind fallback = GpuBackendSelector.ProbeOS(GpuBackendSelector.DetectOS());
            // The same "nothing to fall back TO" guard the Veldrid path carries, and it matters here from the day
            // the OS probe starts answering with a provider-backed kind: falling back onto the backend that just
            // refused would warn about a change that is not one, then fail again for the same reason.
            bool canFallBack = allowFallback && selection.Backend != fallback;

            string? failure = PreflightProvider(selection.Backend, canFallBack, out IGpuBackendProvider provider);
            var request = new GpuWindowedDeviceRequest(window, width, height, syncToVerticalBlank);

            if (failure is null)
            {
                // Seeded so the catch below needs no assignment of its own. Nothing ever adopts this value: the
                // only path past the guard below is the one where creation returned, and creation either assigns
                // or throws.
                GpuProviderDevice created = default;
                try
                {
                    // Inside the same process-wide gate the Veldrid path uses. Device creation is serialized on
                    // every backend, so a provider needs no lifecycle lock of its own and cannot race one.
                    lock (_lifecycleGate)
                    {
                        created = provider.CreateForWindow(request);
                    }
                }
                catch (Exception ex) when (canFallBack)
                {
                    // Deliberately broad, for the reason the Veldrid path spells out: the failure can be anything
                    // from a driver HRESULT wrapper to a DllNotFoundException out of the P/Invoke layer, and the
                    // no-driver case is exactly the one this fallback exists for.
                    failure = $"{ex.GetType().Name}: {ex.Message}";
                }

                // Adoption sits OUTSIDE that try, and the CREATION call is the only thing inside it. The catch
                // answers one question, "can this machine run the backend", and the fallback shape it produces (a
                // WARN telling a player their stored graphics choice does not work here, then a boot on another
                // backend) is the answer to that question and to nothing else. Adopt validates what the provider
                // HANDED BACK, so both of its throws report a bug in the provider instead. Inside the try they
                // would come out as the machine-incapability answer, which is the exact misattribution both of
                // those guards exist to prevent, and it would ship as a green run on a different backend.
                if (failure is null) return Adopt(created, selection);
            }

            WarnFallback(selection.Backend, failure, fallback);
            // Back through the ordinary entry with the fallback's own selection and no further fallback, so the
            // fallback device is created by whichever path owns it and the post-fallback report is the same
            // AfterFallback record a Veldrid-path fallback produces.
            return CreateForWindow(window, width, height, syncToVerticalBlank,
                GpuBackendSelector.AfterFallback(selection, fallback), allowFallback: false);
        }

        // The provider path's construction step, shared by the windowed and headless entries so the guard and the
        // ownership decision cannot drift apart between them.
        //
        // Every throw out of here is a BUG IN THE PROVIDER, never a machine that cannot run the backend, which is
        // why the windowed entry calls this outside its fallback catch. See the comment at that call site.
        static GpuDeviceContext Adopt(in GpuProviderDevice created, GpuBackendSelection selection)
        {
            if (created.Device is null)
            {
                throw new InvalidOperationException(
                    $"The {selection.Backend} backend provider returned no device. A provider that cannot create "
                    + "one must throw, so the failure carries a reason the fallback can log, instead of handing "
                    + "back an empty result the caller has to guess at.");
            }

            try
            {
                // The provider built it, so this context owns its disposal, exactly as it owns the raw Veldrid
                // device on the other path.
                return new GpuDeviceContext(created.Device, created.ThreadingCaps, created.ThreadingProbeFailure,
                    selection, ownsDevice: true);
            }
            catch
            {
                // Ownership transfers on a SUCCESSFUL construction only. A rejected device has no context to
                // dispose it and no other reference anywhere, so without this its adapter, swapchain and driver
                // allocations live until the process exits. Rejecting the device is exactly the case where the
                // provider is already misbehaving, so it is also the case least likely to have cleaned up after
                // itself.
                DisposeRejected(created.Device);
                throw;
            }
        }

        // Releases a device that adoption refused, without letting the release replace the reason for the refusal.
        // A provider handing back a device the engine will not adopt is misbehaving by definition, so its Dispose
        // may be equally broken, and an exception thrown here would unwind in place of the provider-bug exception
        // the caller has to see. Under the same gate the ordinary teardown uses, because it is the same
        // destruction.
        static void DisposeRejected(IGpuDevice device)
        {
            try
            {
                lock (_lifecycleGate)
                {
                    device.Dispose();
                }
            }
            catch (Exception ex)
            {
                log.Warn($"Disposing the device adoption refused threw {ex.GetType().Name}: {ex.Message}. This is "
                    + "the cleanup, not the fault: the refusal it was disposed for is the exception coming out of "
                    + "device creation, and that is the one to act on.");
            }
        }

        // Creates a windowed device on `kind`, with no probing, no fallback, and no resolution. The single place
        // that maps a backend onto a Veldrid factory for the windowed path.
        static GraphicsDevice CreateWindowed(GpuBackendKind kind, GraphicsDeviceOptions opts, SwapchainDescription scDesc)
            => kind switch
            {
                GpuBackendKind.Metal => GraphicsDevice.CreateMetal(opts, scDesc),
                GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(opts, scDesc),
                GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(opts, BuildD3D11Options(), scDesc),
                GpuBackendKind.OpenGL => throw new NotSupportedException(
                    "Windowed OpenGL device-from-handle is not supported (Silk would need to own the GL context)."),
                GpuBackendKind.Direct3D11Native => throw NotCreatedByVeldrid(kind),
                _ => throw NotCreatedByVeldrid(kind),
            };

        // The arm an appended member used to fall into, and the reason this audit exists. A switch expression over
        // an enum does NOT throw SwitchExpressionException for an unlisted member when it carries a discard, and
        // both of these carried one that read `GraphicsDevice.CreateMetal(...)`. So a new backend did not fail
        // here: it silently asked Veldrid for a METAL device, which on Windows fails naming an API the caller
        // never selected, from a stack that says nothing about the backend actually requested.
        static NotSupportedException NotCreatedByVeldrid(GpuBackendKind kind)
            => new($"{kind} is not created here. It is a provider-backed backend, built by the "
                + "IGpuBackendProvider registered for it (GpuBackendProviders) and adopted through the "
                + "IGpuDevice constructor, and every entry into this path branches on "
                + "GpuBackendProviders.RequiresProvider before reaching the Veldrid switch. Reaching this means "
                + "that branch was bypassed.");

        // The engine-owned headless device options, in ONE place so the resolved and the backend-named entries
        // cannot drift apart. Verbatim what the 2D snapshot path passed (no depth, no main-swapchain depth, debug
        // off, Improved binding, sRGB on, sync off), which is what keeps the golden images pixel-identical.
        static GraphicsDeviceOptions DefaultHeadlessOptions
            => new(false, null, false, ResourceBindingModel.Improved, true, true);

        /// <summary>
        /// Veldrid-free headless device for migrated consumers (Render2D) that must not reference Veldrid, on the
        /// backend <see cref="GpuBackendSelector"/> resolves from the environment. Uses the SAME device options the
        /// 2D snapshot path passed verbatim (no depth, no main-swapchain depth, debug off, Improved binding, sRGB
        /// on, sync off) so the golden image stays pixel-identical.
        /// </summary>
        public static GpuDeviceContext CreateHeadless() => CreateHeadless(DefaultHeadlessOptions);

        /// <summary>
        /// Create a headless device on EXACTLY <paramref name="backend"/>: no environment override, no stored
        /// preference, no OS probe, and no fallback, the headless twin of
        /// <see cref="CreateForWindow(in GpuWindowHandle, uint, uint, bool, GpuBackendKind)"/>. A provider-backed
        /// backend with no registered provider throws <see cref="GpuBackendProviderMissingException"/> (decision
        /// I2), and every other failure propagates, because a caller that named one backend outright is not asking
        /// to be quietly given a different one.
        /// <para>
        /// PUBLIC because comparing two backends in ONE process is a first-class need rather than a test trick.
        /// Backend-parity work drives the incumbent and the native Direct3D 11 implementations A against B, and
        /// phase 3 of the native backend program replaces one with the other under the same measurements. The
        /// alternative is what those callers reach for when this does not exist: pulling the provider out of
        /// <see cref="GpuBackendProviders"/> and calling <see cref="IGpuBackendProvider.CreateHeadless"/>
        /// directly. That skips the process-wide creation gate this class owns, and the gate is not optional
        /// bookkeeping. Concurrent device creation races the Vulkan loader's dispatch setup, and every provider is
        /// written on the promise that the engine serializes creation for it, so a device made around the outside
        /// of it races every device made through it.
        /// </para>
        /// <para>
        /// The resulting <see cref="Selection"/> reports <see cref="GpuBackendSource.UserPreference"/>, the same
        /// provenance the windowed named-backend overload reports and for the same reason: naming a backend from
        /// outside the engine is one provenance class, and neither the environment nor the probe chose it.
        /// </para>
        /// </summary>
        public static GpuDeviceContext CreateHeadless(GpuBackendKind backend)
            => CreateHeadless(DefaultHeadlessOptions,
                new GpuBackendSelection(backend, GpuBackendSource.UserPreference, null));

        internal static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options)
            => CreateHeadless(options, GpuBackendSelector.Resolve());

        // The one headless creation path, so the resolved entry and the backend-named entry share the provider
        // branch, the lifecycle gate and the adoption step rather than each routing its own way to a device.
        static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options, GpuBackendSelection selection)
        {
            // No probe and no fallback here, which is exactly what the Veldrid headless path has always done:
            // headless creation propagates its failure. A headless run that quietly changed backend would file its
            // golden images under a backend that never rendered them, and a missing registration throws with a
            // message naming the one line that fixes it.
            if (GpuBackendProviders.RequiresProvider(selection.Backend))
            {
                IGpuBackendProvider provider = GpuBackendProviders.Require(selection.Backend);
                GpuProviderDevice created;
                lock (_lifecycleGate)
                {
                    created = provider.CreateHeadless();
                }
                return Adopt(created, selection);
            }

            GraphicsDevice gd;
            lock (_lifecycleGate)
            {
                gd = selection.Backend switch
                {
                    GpuBackendKind.Metal => GraphicsDevice.CreateMetal(options),
                    GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(options),
                    GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(options, BuildD3D11Options()),
                    GpuBackendKind.OpenGL => throw new NotSupportedException(
                        "Headless OpenGL device creation is not supported in Phase 3a (needs a context surface)."),
                    GpuBackendKind.Direct3D11Native => throw NotCreatedByVeldrid(selection.Backend),
                    _ => throw NotCreatedByVeldrid(selection.Backend),
                };
            }
            return new GpuDeviceContext(gd, selection, ownsDevice: true);
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
