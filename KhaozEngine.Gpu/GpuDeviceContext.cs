using System;
using Veldrid;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// TRANSITIONAL Veldrid bridge created via <see cref="CreateForWindow"/> / <see cref="CreateHeadless()"/>.
    /// Phase 3a centralizes backend selection (no more hard-coded <c>GraphicsBackend.Metal</c>) and surfaces
    /// <see cref="GpuCapabilities"/>, but still exposes the raw Veldrid <see cref="GraphicsDevice"/> so the
    /// existing renderers keep working unchanged. Phase 3b/3c replace <see cref="Device"/> with the wrapped
    /// engine GPU types and this transitional accessor goes away.
    /// </summary>
    public sealed class GpuDeviceContext : IDisposable
    {
        readonly bool _ownsDevice;

        /// <summary>The selected graphics backend (from <see cref="GpuBackendSelector"/>).</summary>
        public GpuBackendKind Backend { get; }

        /// <summary>Clip-space / depth conventions of the live device (see <see cref="GpuCapabilities"/>).</summary>
        public GpuCapabilities Capabilities { get; }

        /// <summary>
        /// The underlying Veldrid device — the implementation detail behind <see cref="GpuDevice"/>. Internal as
        /// of phase 3d: no renderer consumes it; consumers use the engine-owned <see cref="GpuDevice"/>.
        /// </summary>
        internal GraphicsDevice Device { get; }

        /// <summary>
        /// The engine-owned GPU device wrapping the same underlying Veldrid <see cref="Device"/>. Phase-3b
        /// renderers (Render2D) consume this instead of the raw device, so Veldrid stays hidden. The wrapper is
        /// non-owning — disposal flows through this context's <see cref="Dispose"/>.
        /// </summary>
        public IGpuDevice GpuDevice { get; }

        GpuDeviceContext(GraphicsDevice device, GpuBackendKind backend, bool ownsDevice)
        {
            Device = device;
            Backend = backend;
            _ownsDevice = ownsDevice;
            Capabilities = new GpuCapabilities(device.IsClipSpaceYInverted, device.IsDepthRangeZeroToOne);
            // Non-owning wrapper: this context owns the raw device's disposal (see Dispose), so the wrapper must
            // not dispose it again.
            GpuDevice = new VeldridGpuDevice(device, backend, ownsDevice: false);
        }

        /// <summary>
        /// Create a windowed graphics device on the selected backend (via <see cref="GpuBackendSelector"/>) from a
        /// platform-native window handle. The window/input platform (KhaozEngine.Windowing, on Silk.NET) creates the
        /// native window and passes its handle here as a <see cref="GpuWindowHandle"/>, so this package needs no
        /// windowing dependency of its own. Builds the Veldrid <c>SwapchainSource</c> for the handle's
        /// <see cref="GpuWindowKind"/>, then creates the backend device with a main swapchain (so
        /// <see cref="IGpuDevice.SwapchainFramebuffer"/> is non-null). The returned context owns the device's
        /// disposal: dispose this context, not the underlying device.
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height)
        {
            SwapchainSource source = window.Kind switch
            {
                GpuWindowKind.Cocoa => SwapchainSource.CreateNSWindow(window.Handle),
                GpuWindowKind.Win32 => SwapchainSource.CreateWin32(window.Handle, IntPtr.Zero),
                GpuWindowKind.X11 => SwapchainSource.CreateXlib(window.Display, window.Handle),
                GpuWindowKind.Wayland => SwapchainSource.CreateWayland(window.Display, window.Handle),
                _ => throw new NotSupportedException($"Unknown GpuWindowKind '{window.Kind}'."),
            };

            // Engine-owned default windowed device options (depth swapchain, Improved binding, sRGB, vsync) -
            // the same options the previous windowed CreateWindow path passed. Veldrid's GraphicsDeviceOptions stays
            // internal to this package so consumers never reference a Veldrid type.
            var opts = new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true);
            var scDesc = new SwapchainDescription(source, width, height, null, true, false);

            GpuBackendKind kind = GpuBackendSelector.Select();
            GraphicsDevice gd = kind switch
            {
                GpuBackendKind.Metal => GraphicsDevice.CreateMetal(opts, scDesc),
                GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(opts, scDesc),
                GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(opts, scDesc),
                GpuBackendKind.OpenGL => throw new NotSupportedException(
                    "Windowed OpenGL device-from-handle is not supported (Silk would need to own the GL context)."),
                _ => GraphicsDevice.CreateMetal(opts, scDesc),
            };
            return new GpuDeviceContext(gd, kind, ownsDevice: true);
        }

        /// <summary>
        /// Create an offscreen (no-swapchain) graphics device on the selected backend for headless capture.
        /// On the current dev box this resolves to Metal (<c>GraphicsDevice.CreateMetal</c>); other backends are
        /// mapped but not exercised in Phase 3a. <paramref name="options"/> matches what the snapshot helpers
        /// previously passed verbatim so the golden image stays pixel-identical.
        /// </summary>
        /// <summary>
        /// Veldrid-free headless device for migrated consumers (Render2D) that must not reference Veldrid. Uses
        /// the SAME device options the 2D snapshot path passed verbatim (no depth, no main-swapchain depth, debug
        /// off, Improved binding, sRGB on, sync off) so the golden image stays pixel-identical.
        /// </summary>
        public static GpuDeviceContext CreateHeadless()
            => CreateHeadless(new GraphicsDeviceOptions(false, null, false, ResourceBindingModel.Improved, true, true));

        internal static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options)
        {
            GpuBackendKind kind = GpuBackendSelector.Select();
            GraphicsDevice gd = kind switch
            {
                GpuBackendKind.Metal => GraphicsDevice.CreateMetal(options),
                GpuBackendKind.Vulkan => GraphicsDevice.CreateVulkan(options),
                GpuBackendKind.Direct3D11 => GraphicsDevice.CreateD3D11(options),
                GpuBackendKind.OpenGL => throw new NotSupportedException(
                    "Headless OpenGL device creation is not supported in Phase 3a (needs a context surface)."),
                _ => GraphicsDevice.CreateMetal(options),
            };
            return new GpuDeviceContext(gd, kind, ownsDevice: true);
        }

        public void Dispose()
        {
            if (_ownsDevice) Device.Dispose();
        }
    }
}
