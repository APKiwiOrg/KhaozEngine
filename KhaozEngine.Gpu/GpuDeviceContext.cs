using System;
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
        /// The backend choice WITH its provenance: OS probe, an honoured <c>KE_GRAPHICS_BACKEND</c> override, or an
        /// unrecognized one that fell back to the probe (the raw value is kept). Logged once per created device, so
        /// a session log answers "which backend actually ran" without the tester having to reproduce their shell.
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
            LogSelection(selection);
            LogThreadingCaps(selection.Backend, ThreadingCaps, probeFailure);
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

        static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank, GpuBackendSelection selection)
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
            lock (_lifecycleGate)
            {
                gd = CreateWindowed(selection.Backend, opts, scDesc);
            }
            return new GpuDeviceContext(gd, selection, ownsDevice: true);
        }

        // Creates a windowed device on `kind`, with no probing, no fallback, and no resolution. The single place
        // that maps a backend onto a Veldrid factory for the windowed path.
        static GraphicsDevice CreateWindowed(GpuBackendKind kind, GraphicsDeviceOptions opts, SwapchainDescription scDesc)
            => kind switch
            {
                GpuBackendKind.Metal => GraphicsDevice.CreateMetal(opts, scDesc),
                GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(opts, scDesc),
                GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(opts, scDesc),
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
                    GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(options),
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
