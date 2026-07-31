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
    /// <see cref="GpuDevice"/>; the raw Veldrid device stays a private implementation detail of this context.
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
        readonly GraphicsDevice _device;

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

        /// <summary>Clip-space / depth conventions of the live device (see <see cref="GpuCapabilities"/>).</summary>
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
        /// The engine-owned GPU device wrapping the underlying Veldrid device. Renderers (Render2D / Render3D)
        /// consume this instead of the raw device, so Veldrid stays hidden. The wrapper is non-owning: disposal
        /// flows through this context's <see cref="Dispose"/>.
        /// </summary>
        public IGpuDevice GpuDevice { get; }

        GpuDeviceContext(GraphicsDevice device, GpuBackendSelection selection, bool ownsDevice)
        {
            _device = device;
            Selection = selection;
            _ownsDevice = ownsDevice;
            Capabilities = Internal.VeldridMap.ReadCapabilities(device);
            // Non-owning wrapper: this context owns the raw device's disposal (see Dispose), so the wrapper must
            // not dispose it again.
            GpuDevice = new VeldridGpuDevice(device, selection.Backend, ownsDevice: false);
            ThreadingCaps = Internal.D3D11ThreadingProbe.TryQuery(device, selection.Backend, out string? probeFailure);
            // Scanned per created device rather than cached process-wide, so a late-attaching overlay still shows
            // up and there is no static state to reason about. Device creation is rare, and off Windows this is a
            // guard and a return.
            InjectedModules = Internal.InjectedModuleProbe.TryScan(out string? scanFailure);
            LogSelection(selection);
            LogAdapter(Capabilities);
            LogThreadingCaps(selection.Backend, ThreadingCaps, probeFailure);
            LogInjectedModules(InjectedModules, scanFailure);
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
            {
                log.Warn($"{GpuBackendSelector.EnvVarName}='{selection.RequestedOverride}' is not a recognized "
                    + $"backend (metal/vulkan/d3d11/gl). Using {selection.Backend} instead.");
            }

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
        // and it is a WARN precisely so it cannot be lost in a tester's log among the INFO chatter.
        static void LogThreadingCaps(GpuBackendKind backend, GpuThreadingCaps? caps, string? probeFailure)
        {
            if (backend != GpuBackendKind.Direct3D11) return;

            log.Info($"D3D11 driver threading: {GpuThreadingDiagnostics.Describe(caps)}");
            if (GpuThreadingDiagnostics.ShouldWarn(caps))
                log.Warn(GpuThreadingDiagnostics.EmulatedCommandListsWarning);
            else if (probeFailure != null)
                log.Warn($"Could not read the Direct3D11 driver threading capabilities ({probeFailure}). "
                    + "Rendering is unaffected, but a slow-session report from this run cannot rule out a driver "
                    + "that emulates command lists.");
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

            string? failure = GpuBackendSelector.IsBackendSupported(requested)
                ? null
                : "this machine reports no support for it";

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

            log.Warn($"Could not create a {requested} graphics device ({failure}). Falling back to {fallback}. "
                + "If this backend was chosen in the game's graphics settings, that stored choice does not work "
                + "on this machine and should be cleared.");

            return (CreateWindowed(fallback, opts, scDesc), GpuBackendSelector.AfterFallback(selection, fallback));
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
                _ => GraphicsDevice.CreateMetal(opts, scDesc),
            };

        /// <summary>
        /// Veldrid-free headless device for migrated consumers (Render2D) that must not reference Veldrid. Uses
        /// the SAME device options the 2D snapshot path passed verbatim (no depth, no main-swapchain depth, debug
        /// off, Improved binding, sRGB on, sync off) so the golden image stays pixel-identical.
        /// </summary>
        public static GpuDeviceContext CreateHeadless()
            => CreateHeadless(new GraphicsDeviceOptions(false, null, false, ResourceBindingModel.Improved, true, true));

        internal static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options)
        {
            GpuBackendSelection selection = GpuBackendSelector.Resolve();
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
                    _ => GraphicsDevice.CreateMetal(options),
                };
            }
            return new GpuDeviceContext(gd, selection, ownsDevice: true);
        }

        public void Dispose()
        {
            if (!_ownsDevice) return;
            lock (_lifecycleGate)
            {
                // Latch the wrapper first (still inside the gate) so any later straggling drain from a
                // resource wrapper disposed after this context no-ops instead of waiting on a dead device.
                ((VeldridGpuDevice)GpuDevice).MarkDeviceDisposed();
                _device.Dispose();
            }
        }
    }
}
