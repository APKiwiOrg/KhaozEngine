using SilkKey = Silk.NET.Input.Key;
using GlfwInputAction = Silk.NET.GLFW.InputAction;

namespace KhaozEngine.Windowing
{
    // GLFW-specific keyboard callbacks. AppWindow is the only type that touches raw platform input.
    public sealed partial class AppWindow
    {
        // Keep both delegates rooted while the native window can call them.
        Silk.NET.GLFW.GlfwCallbacks.KeyCallback? _keyCallback;
        Silk.NET.GLFW.GlfwCallbacks.KeyCallback? _prevKeyCallback;
        Silk.NET.GLFW.GlfwCallbacks.CharCallback? _charCallback;
        Silk.NET.GLFW.GlfwCallbacks.CharCallback? _prevCharCallback;

        /// <summary>
        /// Capture GLFW key repeat and chain Silk's earlier callback so its press and release events remain live.
        /// Both callbacks run on the main thread during the frame poll.
        /// </summary>
        unsafe void WireKeyRepeat()
        {
            nint glfwWindow = _window.Native?.Glfw ?? 0;
            if (glfwWindow == 0) return;

            var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
            var handle = (Silk.NET.GLFW.WindowHandle*)glfwWindow;
            _keyCallback = (window, key, code, action, mods) =>
            {
                if (action == GlfwInputAction.Repeat && MapKey((SilkKey)(int)key, out Key k))
                    _accumulator.OnKeyRepeat(k);
                _prevKeyCallback?.Invoke(window, key, code, action, mods);
            };
            _prevKeyCallback = glfw.SetKeyCallback(handle, _keyCallback);
        }

        /// <summary>
        /// Capture layout-aware committed Unicode text. The source flag stays true on empty frames so a dead key
        /// cannot fall back to a US-layout physical-key symbol. Silk's prior callback remains in the chain.
        /// </summary>
        unsafe void WireTextInput()
        {
            nint glfwWindow = _window.Native?.Glfw ?? 0;
            if (glfwWindow == 0) return;

            var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
            var handle = (Silk.NET.GLFW.WindowHandle*)glfwWindow;
            _charCallback = (window, codepoint) =>
            {
                _accumulator.OnTextInput(codepoint);
                _prevCharCallback?.Invoke(window, codepoint);
            };
            _prevCharCallback = glfw.SetCharCallback(handle, _charCallback);
            _accumulator.EnableTextInput();
        }
    }
}
