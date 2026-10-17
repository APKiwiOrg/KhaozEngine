using KhaozEngine.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.BugReports.Client.Internal;

/// <summary>What the recorder needs of the window: its capture capabilities and its tap registry.</summary>
internal interface IFrameCaptureHost
{
    /// <summary>
    /// True when the device has completion fences and implements both <see cref="IGpuSwapchainCapture"/> and
    /// <see cref="IGpuFencedReadback"/>, everything the rolling clip's non-blocking readback ring needs.
    /// </summary>
    bool SupportsClipCapture { get; }

    /// <summary>True when the device implements <see cref="IGpuSwapchainCapture"/>, all a screenshot needs.</summary>
    bool SupportsSwapchainCopy { get; }

    void AddCaptureTap(IFrameCaptureTap tap);

    /// <summary>Unregisters <paramref name="tap"/> and calls its <see cref="IFrameCaptureTap.Detach"/>.</summary>
    void RemoveCaptureTap(IFrameCaptureTap tap);
}
