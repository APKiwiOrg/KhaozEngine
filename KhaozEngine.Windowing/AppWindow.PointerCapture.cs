using System.Numerics;
using Silk.NET.GLFW;

namespace KhaozEngine.Windowing
{
    // Pointer capture for mouse-look. The decision is the pure PointerCapturePolicy. This partial only records the
    // request and applies the decided GLFW calls once per frame, before BuildInput, so the snapshot's
    // PointerCaptured is exactly what GLFW was told this frame.
    public sealed partial class AppWindow
    {
        bool _captureRequested;
        bool _pointerCaptured;
        bool _captureAwaitingRenewal;
        bool? _rawMotionSupported;

        /// <summary>
        /// Ask for the pointer to be captured (hidden, locked to the window, unbounded relative motion) or released.
        /// The request applies at the start of the next frame and holds only while the window is focused. A focus
        /// loss releases the cursor, and refocusing never re-captures it until the request goes false and then true
        /// again. While captured, <see cref="InputState.MouseDelta"/> is in window points.
        /// </summary>
        public void SetPointerCaptured(bool captured) => _captureRequested = captured;

        /// <summary>True while GLFW holds the cursor captured. Matches this frame's
        /// <see cref="InputState.PointerCaptured"/>.</summary>
        public bool PointerCaptured => _pointerCaptured;

        /// <summary>Apply this frame's capture decision through GLFW. A non-GLFW backend never captures.</summary>
        unsafe void ApplyPointerCapture()
        {
            nint glfwWindow = _window.Native?.Glfw ?? 0;
            if (glfwWindow == 0) return;

            var glfw = GlfwProvider.GLFW.Value;
            var handle = (WindowHandle*)glfwWindow;
            _rawMotionSupported ??= glfw.RawMouseMotionSupported();
            PointerCaptureAction action = PointerCapturePolicy.Decide(
                _captureRequested, _accumulator.IsFocused, _pointerCaptured, _rawMotionSupported.Value,
                _captureAwaitingRenewal);
            _captureAwaitingRenewal = action.AwaitingRenewal;
            _pointerCaptured = action.Captured;
            if (!action.CallsGlfw) return;

            // Set the mode directly. Silk's CursorMode setter also writes raw motion when it is unsupported, which
            // raises a GLFW error on macOS.
            if (action.SetDisabled) glfw.SetInputMode(handle, CursorStateAttribute.Cursor, CursorModeValue.CursorDisabled);
            if (action.SetRawMotion) glfw.SetInputMode(handle, CursorStateAttribute.RawMouseMotion, true);
            if (action.SetNormal) glfw.SetInputMode(handle, CursorStateAttribute.Cursor, CursorModeValue.CursorNormal);
        }

        /// <summary>Framebuffer pixels per window point on each axis (1 on a 1x display or an empty window). The one
        /// ratio both the cursor position and the captured delta use.</summary>
        Vector2 FramebufferScale()
        {
            var size = _window.Size;
            var fb = _window.FramebufferSize;
            float sx = size.X > 0 ? (float)fb.X / size.X : 1f;
            float sy = size.Y > 0 ? (float)fb.Y / size.Y : 1f;
            return new Vector2(sx, sy);
        }
    }
}
