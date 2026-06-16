using System;
using System.Collections.Generic;
using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using KhaozEngine.Gpu;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace KhaozEngine.Windowing
{
    /// <summary>One render frame: timing, the input snapshot, and the GPU command list to draw into
    /// (the swapchain is already bound and cleared by <see cref="AppWindow"/>).</summary>
    public sealed class Frame
    {
        public float Dt { get; internal set; }
        public InputState Input { get; internal set; } = InputState.Empty;
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        /// <summary>The engine GPU command list for this frame (the swapchain is already bound and cleared;
        /// renderers draw into it). Backend GPU types stay hidden behind <see cref="IGpuCommandList"/>.</summary>
        public IGpuCommandList Commands { get; internal set; } = null!;
    }

    /// <summary>
    /// Owns the Silk.NET window + input + frame loop (GLFW natives bundled per-RID, so no <c>brew install sdl2</c>),
    /// the engine GPU device (<see cref="IGpuDevice"/>, backend GPU types hidden behind the KhaozEngine.Gpu seam),
    /// and presentation. The Veldrid swapchain is created from the native window handle via
    /// <see cref="GpuDeviceContext.CreateForWindow"/>. Each frame pumps Silk input into an engine-native
    /// <see cref="InputState"/>, clears the swapchain, runs the callback, and presents. The 5.x renderers build on
    /// this. The GPU backend is selected by <see cref="GpuBackendSelector"/> (Metal on this dev box).
    /// </summary>
    public sealed class AppWindow : IDisposable
    {
        readonly IWindow _window;
        readonly IInputContext _input;
        readonly GpuDeviceContext _gpu;
        readonly IGpuDevice _device;
        readonly IGpuCommandList _cl;
        readonly Frame _frame = new();

        // Edge-tracking input state (mirrors the previous model). Silk fires KeyDown/KeyUp/MouseDown/MouseUp/Scroll
        // on its event pump; we accumulate into per-frame sets and snapshot them once per render callback.
        readonly HashSet<Key> _keysDown = new();
        readonly HashSet<Key> _pressed = new();
        readonly HashSet<Key> _released = new();
        readonly HashSet<MouseButton> _mouseDown = new();
        readonly HashSet<MouseButton> _mousePressed = new();
        readonly SilkGamepadReader _gamepads = new();
        Vector2 _lastMouse;
        float _wheelAccum;

        readonly int _maxFrames;
        int _frameCount;

        /// <summary>The engine-owned GPU device (renderers consume this; backend GPU types stay hidden).</summary>
        public IGpuDevice GpuDevice => _device;
        /// <summary>The selected graphics backend (centralized via <see cref="GpuBackendSelector"/>).</summary>
        public GpuBackendKind Backend => _gpu.Backend;
        /// <summary>Clip-space / depth conventions of the live device (see <see cref="GpuCapabilities"/>).</summary>
        public GpuCapabilities Capabilities => _gpu.Capabilities;
        /// <summary>Background colour cleared each frame.</summary>
        public Vector4 ClearColor = new(0.10f, 0.12f, 0.16f, 1f);

        public AppWindow(string title, int width, int height)
        {
            // KE_MAX_FRAMES: render N frames then close (lets a windowed smoke test run a few frames + exit cleanly).
            _maxFrames = int.TryParse(Environment.GetEnvironmentVariable("KE_MAX_FRAMES"), out int mf) && mf > 0 ? mf : 0;

            GlfwWindowing.RegisterPlatform();
            var opts = WindowOptions.Default with
            {
                Size = new Vector2D<int>(width, height),
                Title = title,
                // We drive the GPU ourselves via Veldrid; Silk must not create a GL/Vulkan context.
                API = GraphicsAPI.None,
            };
            _window = Window.Create(opts);
            _window.Initialize(); // creates the native window WITHOUT starting the loop; the handle is valid after this.

            GpuWindowHandle handle = BuildHandle(_window);
            _gpu = GpuDeviceContext.CreateForWindow(handle, (uint)width, (uint)height);
            _device = _gpu.GpuDevice;
            _cl = _device.Factory.CreateCommandList();

            // Resize the swapchain to the drawable (framebuffer) size, not the logical window size.
            _window.FramebufferResize += s => _device.ResizeSwapchain((uint)s.X, (uint)s.Y);

            _input = _window.CreateInput();
            WireInput();

            _lastMouse = Vector2.Zero;
        }

        static GpuWindowHandle BuildHandle(IWindow window)
        {
            var native = window.Native ?? throw new NotSupportedException("Silk window exposed no native handle.");
            if (OperatingSystem.IsMacOS())
            {
                IntPtr nsWindow = native.Cocoa ?? throw new NotSupportedException("No Cocoa native handle.");
                return new GpuWindowHandle(GpuWindowKind.Cocoa, nsWindow);
            }
            if (OperatingSystem.IsWindows())
            {
                var win32 = native.Win32 ?? throw new NotSupportedException("No Win32 native handle.");
                return new GpuWindowHandle(GpuWindowKind.Win32, win32.Hwnd);
            }
            if (OperatingSystem.IsLinux())
            {
                if (native.X11 is { } x11)
                    return new GpuWindowHandle(GpuWindowKind.X11, (IntPtr)x11.Window, x11.Display);
                if (native.Wayland is { } wl)
                    return new GpuWindowHandle(GpuWindowKind.Wayland, wl.Surface, wl.Display);
                throw new NotSupportedException("No X11 or Wayland native handle on this Linux session.");
            }
            throw new NotSupportedException("Unsupported windowing platform for the Silk -> Veldrid bridge.");
        }

        public bool Exists => !_window.IsClosing;
        public void Close() => _window.Close();

        /// <summary>Run the frame loop until the window closes, calling <paramref name="onFrame"/> each frame.</summary>
        public void Run(Action<Frame> onFrame)
        {
            _window.Render += dt =>
            {
                float fdt = (float)Math.Min(dt, 0.1);
                InputState input = BuildInput();
                int w = _window.FramebufferSize.X, h = _window.FramebufferSize.Y;

                _cl.Begin();
                _cl.SetFramebuffer(_device.SwapchainFramebuffer!);
                _cl.ClearColorTarget(0, ClearColor);

                _frame.Dt = fdt; _frame.Input = input; _frame.Width = w; _frame.Height = h; _frame.Commands = _cl;
                onFrame(_frame);

                _cl.End();
                _device.Submit(_cl);
                _device.Present();

                if (_maxFrames > 0 && ++_frameCount >= _maxFrames) _window.Close();
            };
            _window.Run();
        }

        void WireInput()
        {
            foreach (IKeyboard kb in _input.Keyboards)
            {
                kb.KeyDown += (_, key, _) => { if (MapKey(key, out Key k) && _keysDown.Add(k)) _pressed.Add(k); };
                kb.KeyUp += (_, key, _) => { if (MapKey(key, out Key k) && _keysDown.Remove(k)) _released.Add(k); };
            }
            foreach (IMouse m in _input.Mice)
            {
                m.MouseDown += (_, btn) => { if (MapMouse(btn, out MouseButton b) && _mouseDown.Add(b)) _mousePressed.Add(b); };
                m.MouseUp += (_, btn) => { if (MapMouse(btn, out MouseButton b)) _mouseDown.Remove(b); };
                m.Scroll += (_, wheel) => _wheelAccum += wheel.Y;
            }
        }

        InputState BuildInput()
        {
            Vector2 pos = _lastMouse;
            var mice = _input.Mice;
            // Silk/GLFW report the cursor in LOGICAL points; the render viewport (Frame.Width/Height ->
            // DesignViewport / SpriteBatch.Begin) is in FRAMEBUFFER pixels. On a HiDPI display (Retina Mac at 2x,
            // scaled Windows) those differ, so scale the cursor into framebuffer space to keep input and rendering
            // in one coordinate system (otherwise Pointer hit-testing is off by the DPI factor). 1x = no-op.
            if (mice.Count > 0) pos = ToFramebuffer(mice[0].Position);
            Vector2 delta = pos - _lastMouse;

            var input = new InputState(
                new HashSet<Key>(_keysDown), new HashSet<Key>(_pressed), new HashSet<Key>(_released),
                new HashSet<MouseButton>(_mouseDown), new HashSet<MouseButton>(_mousePressed),
                pos, delta, _wheelAccum,
                _window.FramebufferSize.X, _window.FramebufferSize.Y,
                _gamepads.Read(_input.Gamepads));

            _pressed.Clear();
            _released.Clear();
            _mousePressed.Clear();
            _lastMouse = pos;
            _wheelAccum = 0f;
            return input;
        }

        /// <summary>Scale a logical-point cursor position into framebuffer pixels (DPI factor per axis; 1x = identity).</summary>
        Vector2 ToFramebuffer(Vector2 logical)
        {
            var size = _window.Size;
            var fb = _window.FramebufferSize;
            float sx = size.X > 0 ? (float)fb.X / size.X : 1f;
            float sy = size.Y > 0 ? (float)fb.Y / size.Y : 1f;
            return new Vector2(logical.X * sx, logical.Y * sy);
        }

        static bool MapMouse(SilkMouseButton b, out MouseButton r)
        {
            switch (b)
            {
                case SilkMouseButton.Left: r = MouseButton.Left; return true;
                case SilkMouseButton.Middle: r = MouseButton.Middle; return true;
                case SilkMouseButton.Right: r = MouseButton.Right; return true;
                case SilkMouseButton.Button4: r = MouseButton.X1; return true;
                case SilkMouseButton.Button5: r = MouseButton.X2; return true;
                default: r = default; return false;
            }
        }

        static bool MapKey(SilkKey k, out Key r)
        {
            r = k switch
            {
                >= SilkKey.A and <= SilkKey.Z => Key.A + (k - SilkKey.A),
                >= SilkKey.Number0 and <= SilkKey.Number9 => Key.D0 + (k - SilkKey.Number0),
                >= SilkKey.F1 and <= SilkKey.F12 => Key.F1 + (k - SilkKey.F1),
                SilkKey.Up => Key.Up,
                SilkKey.Down => Key.Down,
                SilkKey.Left => Key.Left,
                SilkKey.Right => Key.Right,
                SilkKey.Space => Key.Space,
                SilkKey.Enter or SilkKey.KeypadEnter => Key.Enter,
                SilkKey.Escape => Key.Escape,
                SilkKey.Tab => Key.Tab,
                SilkKey.Backspace => Key.Backspace,
                SilkKey.Delete => Key.Delete,
                SilkKey.Insert => Key.Insert,
                SilkKey.Home => Key.Home,
                SilkKey.End => Key.End,
                SilkKey.PageUp => Key.PageUp,
                SilkKey.PageDown => Key.PageDown,
                SilkKey.ShiftLeft => Key.LeftShift,
                SilkKey.ShiftRight => Key.RightShift,
                SilkKey.ControlLeft => Key.LeftControl,
                SilkKey.ControlRight => Key.RightControl,
                SilkKey.AltLeft => Key.LeftAlt,
                SilkKey.AltRight => Key.RightAlt,
                SilkKey.SuperLeft => Key.LeftSuper,
                SilkKey.SuperRight => Key.RightSuper,
                SilkKey.Minus => Key.Minus,
                SilkKey.Equal => Key.Equals,
                SilkKey.LeftBracket => Key.LeftBracket,
                SilkKey.RightBracket => Key.RightBracket,
                SilkKey.BackSlash => Key.Backslash,
                SilkKey.Semicolon => Key.Semicolon,
                SilkKey.Apostrophe => Key.Apostrophe,
                SilkKey.Comma => Key.Comma,
                SilkKey.Period => Key.Period,
                SilkKey.Slash => Key.Slash,
                SilkKey.GraveAccent => Key.Grave,
                _ => Key.None,
            };
            return r != Key.None;
        }

        public void Dispose()
        {
            try { _input?.Dispose(); } catch { }
            try { _cl?.Dispose(); } catch { }
            try { _gpu?.Dispose(); } catch { }
            try { _window?.Dispose(); } catch { }
        }
    }
}
