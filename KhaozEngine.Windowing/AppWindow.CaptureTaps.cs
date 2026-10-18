using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The frame capture half of <see cref="AppWindow"/>: the tap registry the frame loop drives through
    /// <see cref="FramePhases"/>. It is built on the first <see cref="AddCaptureTap"/>, so a window that never
    /// registers a tap runs exactly the frame it always did.
    /// </summary>
    public sealed partial class AppWindow
    {
        FrameCaptureTaps? _captureTaps;

        /// <summary>
        /// Register <paramref name="tap"/> to run on every rendered frame, after the game's draws. A tap already
        /// registered is left as it is. The first tap asks the device for copyable swapchain images, which can
        /// take effect a frame later. Call on the frame thread.
        /// </summary>
        public void AddCaptureTap(IFrameCaptureTap tap)
        {
            ArgumentNullException.ThrowIfNull(tap);
            _captureTaps ??= new FrameCaptureTaps(_device, _device as IGpuSwapchainCapture);
            _captureTaps.Add(tap);
        }

        /// <summary>
        /// Unregister <paramref name="tap"/> and call its <see cref="IFrameCaptureTap.Detach"/>. A tap not
        /// registered is ignored. Safe from inside a tap callback. Removing the last tap releases the copyable
        /// swapchain request. Call on the frame thread.
        /// </summary>
        public void RemoveCaptureTap(IFrameCaptureTap tap)
        {
            ArgumentNullException.ThrowIfNull(tap);
            _captureTaps?.Remove(tap);
        }
    }
}
