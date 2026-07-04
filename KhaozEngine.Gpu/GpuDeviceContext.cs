using System;
using Veldrid;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Owns a Veldrid device created via <see cref="CreateForWindow"/> / <see cref="CreateHeadless()"/> plus the
    /// engine-owned <see cref="GpuDevice"/> wrapping it. Centralizes backend selection (no hard-coded
    /// <c>GraphicsBackend.Metal</c>) and surfaces <see cref="GpuCapabilities"/>. Renderers consume
    /// <see cref="GpuDevice"/>; the raw Veldrid device stays a private implementation detail of this context.
    /// </summary>
    public sealed class GpuDeviceContext : IDisposable
    {
        readonly bool _ownsDevice;
        readonly GraphicsDevice _device;

        /// <summary>The selected graphics backend (from <see cref="GpuBackendSelector"/>).</summary>
        public GpuBackendKind Backend { get; }

        /// <summary>Clip-space / depth conventions of the live device (see <see cref="GpuCapabilities"/>).</summary>
        public GpuCapabilities Capabilities { get; }

        /// <summary>
        /// The engine-owned GPU device wrapping the underlying Veldrid device. Renderers (Render2D / Render3D)
        /// consume this instead of the raw device, so Veldrid stays hidden. The wrapper is non-owning: disposal
        /// flows through this context's <see cref="Dispose"/>.
        /// </summary>
        public IGpuDevice GpuDevice { get; }

        GpuDeviceContext(GraphicsDevice device, GpuBackendKind backend, bool ownsDevice)
        {
            _device = device;
            Backend = backend;
            _ownsDevice = ownsDevice;
            Capabilities = new GpuCapabilities(device.IsClipSpaceYInverted, device.IsDepthRangeZeroToOne,
                device.DeviceName ?? "", device.Features.SamplerAnisotropy, device.Features.SamplerLodBias,
                Internal.VeldridMap.MaxMsaaSampleCount(device));
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
        /// <see cref="IGpuDevice.SwapchainFramebuffer"/> is non-null). <paramref name="syncToVerticalBlank"/> selects
        /// vsync (default true, unchanged) vs immediate presentation; it feeds both the device options and the
        /// swapchain description. The returned context owns the device's disposal: dispose this context, not the
        /// underlying device.
        /// </summary>
        public static GpuDeviceContext CreateForWindow(in GpuWindowHandle window, uint width, uint height,
            bool syncToVerticalBlank = true)
        {
            SwapchainSource source = window.Kind switch
            {
                GpuWindowKind.Cocoa => SwapchainSource.CreateNSWindow(window.Handle),
                GpuWindowKind.Win32 => SwapchainSource.CreateWin32(window.Handle, IntPtr.Zero),
                GpuWindowKind.X11 => SwapchainSource.CreateXlib(window.Display, window.Handle),
                GpuWindowKind.Wayland => SwapchainSource.CreateWayland(window.Display, window.Handle),
                _ => throw new NotSupportedException($"Unknown GpuWindowKind '{window.Kind}'."),
            };

            // Engine-owned default windowed device options (depth swapchain, Improved binding, sRGB) - the same
            // options the previous windowed CreateWindow path passed, with the vsync flag now caller-selected.
            // Veldrid's GraphicsDeviceOptions stays internal to this package so consumers never reference a Veldrid type.
            var opts = new GraphicsDeviceOptions(false, null, syncToVerticalBlank, ResourceBindingModel.Improved, true, true);
            var scDesc = new SwapchainDescription(source, width, height, null, syncToVerticalBlank, false);

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
            if (_ownsDevice) _device.Dispose();
        }
    }
}
