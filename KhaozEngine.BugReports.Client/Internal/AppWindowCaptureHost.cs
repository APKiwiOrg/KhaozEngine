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

    public bool SupportsClipCapture
    {
        get
        {
            IGpuDevice device = _window.GpuDevice;
            return device.Capabilities.SupportsCompletionFences
                && device is IGpuSwapchainCapture
                && device is IGpuFencedReadback;
        }
    }

    public bool SupportsSwapchainCopy => _window.GpuDevice is IGpuSwapchainCapture;

    public void AddCaptureTap(IFrameCaptureTap tap) => _window.AddCaptureTap(tap);

    public void RemoveCaptureTap(IFrameCaptureTap tap) => _window.RemoveCaptureTap(tap);
}
