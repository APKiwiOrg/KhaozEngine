using Silk.NET.GLFW;

namespace KhaozEngine.Windowing
{
    // OS content scale queries. Silk.NET 2.23 binds glfwGetMonitorContentScale but not the GLFW 3.4 per-window
    // query or glfwGetPlatform, so both exports are resolved from the GLFW library Silk already loaded. No second
    // native library is loaded. Any miss falls back to the framebuffer-to-window ratio the engine used before.
    public sealed partial class AppWindow
    {
        static unsafe delegate* unmanaged[Cdecl]<WindowHandle*, float*, float*, void> s_getWindowContentScale;
        static unsafe delegate* unmanaged[Cdecl]<int> s_getPlatform;
        static bool s_scaleExportsResolved;

        /// <summary>Logical size and exact OS scale for the window's current framebuffer. The query is cheap, so the
        /// frame loop calls it every frame and a monitor move or OS scale change lands on the next frame.</summary>
        LogicalMetrics CurrentLogicalMetrics()
        {
            var framebuffer = _window.FramebufferSize;
            var size = _window.Size;
            return DisplayScale.Logical(framebuffer.X, framebuffer.Y, size.X, size.Y, WindowContentScale());
        }

        /// <summary>The OS content scale of this window, or NaN when the backend or export is unavailable.</summary>
        unsafe float WindowContentScale()
        {
            nint glfwWindow = _window.Native?.Glfw ?? 0;
            if (glfwWindow == 0) return float.NaN;
            ResolveScaleExports(GlfwProvider.GLFW.Value);
            if (s_getWindowContentScale == null) return float.NaN;
            float x = float.NaN, y = float.NaN;
            s_getWindowContentScale((WindowHandle*)glfwWindow, &x, &y);
            return x;
        }

        /// <summary>The primary monitor's size in window coordinates plus how many window coordinates span one
        /// logical point there. Window placement and the monitor size stay in window coordinates. Only the
        /// <c>Scaled</c> cap converts through the ratio, which is 1 wherever the platform is unknown.</summary>
        static unsafe ScreenMetrics PrimaryScreenMetrics()
        {
            var (width, height) = PrimaryScreenSize();
            if (width <= 0 || height <= 0) return new ScreenMetrics(width, height, 1f);
            try
            {
                var glfw = GlfwProvider.GLFW.Value;
                ResolveScaleExports(glfw);
                Silk.NET.GLFW.Monitor* monitor = glfw.GetPrimaryMonitor();
                if (monitor == null || s_getPlatform == null) return new ScreenMetrics(width, height, 1f);
                glfw.GetMonitorContentScale(monitor, out float scale, out _);
                return new ScreenMetrics(width, height, DisplayScale.CoordinatesPerPoint(s_getPlatform(), scale));
            }
            catch
            {
                return new ScreenMetrics(width, height, 1f); // no display: keep the legacy cap.
            }
        }

        static unsafe void ResolveScaleExports(Glfw glfw)
        {
            if (s_scaleExportsResolved) return;
            s_scaleExportsResolved = true;
            if (glfw.Context.TryGetProcAddress("glfwGetWindowContentScale", out nint scale))
                s_getWindowContentScale = (delegate* unmanaged[Cdecl]<WindowHandle*, float*, float*, void>)scale;
            if (glfw.Context.TryGetProcAddress("glfwGetPlatform", out nint platform))
                s_getPlatform = (delegate* unmanaged[Cdecl]<int>)platform;
        }
    }
}
