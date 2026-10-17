using System;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>The real <see cref="IFrameCaptureHost"/>: a window and its GPU device.</summary>
internal sealed class AppWindowCaptureHost : IFrameCaptureHost
{
    readonly AppWindow _window;

    internal AppWindowCaptureHost(AppWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public bool SupportsClipCapture => ClipCaptureOn(_window.GpuDevice);

    public bool SupportsSwapchainCopy => SwapchainCopyOn(_window.GpuDevice);

    /// <summary>
    /// The swapchain predicate every host shares: the device implements <see cref="IGpuSwapchainCapture"/> and
    /// reports <see cref="IGpuSwapchainCapture.SwapchainCopySupported"/>, so a copyable frame can ever come.
    /// </summary>
    internal static bool SwapchainCopyOn(IGpuDevice device)
        => device is IGpuSwapchainCapture { SwapchainCopySupported: true };

    /// <summary>
    /// The clip predicate every host shares: <see cref="SwapchainCopyOn"/>, completion fences and
    /// <see cref="IGpuFencedReadback"/>.
    /// </summary>
    internal static bool ClipCaptureOn(IGpuDevice device)
        => SwapchainCopyOn(device) && device.Capabilities.SupportsCompletionFences && device is IGpuFencedReadback;

    public void AddCaptureTap(IFrameCaptureTap tap) => _window.AddCaptureTap(tap);

    public void RemoveCaptureTap(IFrameCaptureTap tap) => _window.RemoveCaptureTap(tap);
}
