using Silk.NET.GLFW;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// GLFW-backed text clipboard. <see cref="KhaozEngine.Platform"/> is BCL-only and must not reference Silk,
    /// so the Silk/GLFW clipboard lives here and is registered with <c>Platform.Clipboard</c> through the
    /// provider seam (<c>Clipboard.RegisterTextProvider</c>) by <see cref="AppWindow"/>. GLFW's clipboard works
    /// on Windows, Linux, and macOS; it is what gives Windows and Linux a working text clipboard (the inherited
    /// SDL2 path was a no-op because the engine never calls <c>SDL_Init</c>).
    /// </summary>
    /// <remarks>
    /// GLFW clipboard calls must run on the main thread (the GLFW thread), which is where <see cref="AppWindow"/>
    /// runs its frame callback, so consumers reading/writing the clipboard from a frame are on the right thread.
    /// </remarks>
    internal static unsafe class GlfwClipboard
    {
        private static readonly Glfw Glfw = GlfwProvider.GLFW.Value;

        /// <summary>
        /// Returns the clipboard's text, or <c>null</c> when it cannot be read (no window handle, GLFW not
        /// initialised, or non-text content). Returning <c>null</c> tells the provider seam to fall through to
        /// the OS backends rather than reporting an empty clipboard.
        /// </summary>
        public static string? ReadText(nint glfwWindow)
        {
            if (glfwWindow == 0)
            {
                return null;
            }

            try
            {
                return Glfw.GetClipboardString((WindowHandle*)glfwWindow);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Sets the clipboard's text. Returns <c>false</c> when there is no window handle or the native call
        /// fails, so the provider seam can fall through to the OS backends.
        /// </summary>
        public static bool WriteText(nint glfwWindow, string text)
        {
            if (glfwWindow == 0)
            {
                return false;
            }

            try
            {
                Glfw.SetClipboardString((WindowHandle*)glfwWindow, text);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
