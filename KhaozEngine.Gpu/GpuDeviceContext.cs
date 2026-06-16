using System;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// TRANSITIONAL Veldrid bridge created via <see cref="CreateWindow"/> / <see cref="CreateHeadless"/>.
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
        /// The underlying Veldrid device. TRANSITIONAL bridge for Phase 3a: the renderers still consume Veldrid
        /// directly. Replaced by wrapped engine GPU types in Phase 3b/3c.
        /// </summary>
        public GraphicsDevice Device { get; }

        GpuDeviceContext(GraphicsDevice device, GpuBackendKind backend, bool ownsDevice)
        {
            Device = device;
            Backend = backend;
            _ownsDevice = ownsDevice;
            Capabilities = new GpuCapabilities(device.IsClipSpaceYInverted, device.IsDepthRangeZeroToOne);
        }

        /// <summary>
        /// Create an SDL2 window + graphics device on the selected backend (via <see cref="GpuBackendSelector"/>).
        /// Replaces the per-package <c>VeldridStartup.CreateWindowAndGraphicsDevice(..., GraphicsBackend.Metal, ...)</c>
        /// calls. The returned context does NOT own the device's disposal contract differently from before:
        /// callers that previously disposed <c>GraphicsDevice</c> should now dispose this context.
        /// </summary>
        public static (Sdl2Window window, GpuDeviceContext ctx) CreateWindow(
            string title, int width, int height, GraphicsDeviceOptions? options = null, int x = 100, int y = 100)
        {
            var wci = new WindowCreateInfo(x, y, width, height, WindowState.Normal, title);
            GraphicsDeviceOptions opts = options ??
                new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true);
            GpuBackendKind kind = GpuBackendSelector.Select();
            GraphicsBackend backend = GpuBackendSelector.ToVeldrid(kind);
            VeldridStartup.CreateWindowAndGraphicsDevice(wci, opts, backend, out Sdl2Window window, out GraphicsDevice gd);
            return (window, new GpuDeviceContext(gd, kind, ownsDevice: true));
        }

        /// <summary>
        /// Create an offscreen (no-swapchain) graphics device on the selected backend for headless capture.
        /// On the current dev box this resolves to Metal (<c>GraphicsDevice.CreateMetal</c>); other backends are
        /// mapped but not exercised in Phase 3a. <paramref name="options"/> matches what the snapshot helpers
        /// previously passed verbatim so the golden image stays pixel-identical.
        /// </summary>
        public static GpuDeviceContext CreateHeadless(GraphicsDeviceOptions options)
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
