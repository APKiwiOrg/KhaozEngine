using System;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// The native windowing platform a <see cref="GpuWindowHandle"/> belongs to. Picks which Veldrid
    /// <c>SwapchainSource</c> factory <see cref="GpuDeviceContext.CreateForWindow"/> uses. Cocoa/Win32 carry
    /// just the window handle; X11/Wayland also need the display pointer.
    /// </summary>
    public enum GpuWindowKind { Cocoa, Win32, X11, Wayland }

    /// <summary>
    /// A platform-native window handle handed to the GPU layer so it can build a Veldrid swapchain WITHOUT the
    /// GPU package taking a windowing dependency. The window/input platform (KhaozEngine.Windowing, on Silk.NET)
    /// reads the native handle and passes it here as an opaque <see cref="IntPtr"/> plus a <see cref="Kind"/>.
    /// <see cref="Display"/> is only used for X11 (the X display) and Wayland (the wl_display); for Cocoa/Win32
    /// it is <see cref="IntPtr.Zero"/>.
    /// </summary>
    public readonly struct GpuWindowHandle
    {
        /// <summary>The native windowing platform this handle belongs to.</summary>
        public GpuWindowKind Kind { get; }
        /// <summary>The native window handle (NSWindow / HWND / X11 Window / Wayland surface).</summary>
        public IntPtr Handle { get; }
        /// <summary>The native display pointer (X11 Display / Wayland wl_display); <see cref="IntPtr.Zero"/> for Cocoa/Win32.</summary>
        public IntPtr Display { get; }

        public GpuWindowHandle(GpuWindowKind kind, IntPtr handle, IntPtr display)
        {
            Kind = kind;
            Handle = handle;
            Display = display;
        }

        /// <summary>Convenience for the common Cocoa/Win32 case where no display pointer is needed.</summary>
        public GpuWindowHandle(GpuWindowKind kind, IntPtr handle)
            : this(kind, handle, IntPtr.Zero)
        {
        }
    }
}
