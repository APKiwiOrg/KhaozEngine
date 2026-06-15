using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

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
        /// <summary>The GPU command list for this frame (advanced; renderers draw into it). Veldrid type.</summary>
        public CommandList Commands { get; internal set; } = null!;
    }

    /// <summary>
    /// Owns the SDL2/Metal window, Veldrid device + swapchain, and the frame loop. Each frame pumps input
    /// into an engine-native <see cref="InputState"/>, clears the swapchain, runs the callback, and presents.
    /// The 5.x renderers build on this. POC: Metal only; needs SDL2 at runtime.
    /// </summary>
    public sealed class AppWindow : IDisposable
    {
        readonly Sdl2Window _window;
        readonly CommandList _cl;
        readonly Frame _frame = new();
        readonly HashSet<Key> _keysDown = new();
        readonly HashSet<MouseButton> _mouseDown = new();
        Vector2 _lastMouse;

        /// <summary>The Veldrid graphics device (advanced GPU boundary; renderers consume it).</summary>
        public GraphicsDevice Device { get; }
        /// <summary>The main swapchain (advanced GPU boundary).</summary>
        public Swapchain MainSwapchain => Device.MainSwapchain;
        /// <summary>Background colour cleared each frame.</summary>
        public Vector4 ClearColor = new(0.10f, 0.12f, 0.16f, 1f);

        public AppWindow(string title, int width, int height)
        {
            var wci = new WindowCreateInfo(100, 100, width, height, WindowState.Normal, title);
            var opts = new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true);
            VeldridStartup.CreateWindowAndGraphicsDevice(wci, opts, GraphicsBackend.Metal, out _window, out var gd);
            Device = gd;
            _window.Resized += () => Device.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            _cl = gd.ResourceFactory.CreateCommandList();
            _lastMouse = Vector2.Zero;
        }

        public bool Exists => _window.Exists;
        public void Close() => _window.Close();

        /// <summary>Run the frame loop until the window closes, calling <paramref name="onFrame"/> each frame.</summary>
        public void Run(Action<Frame> onFrame)
        {
            var sw = Stopwatch.StartNew();
            double last = 0;
            while (_window.Exists)
            {
                InputSnapshot snap = _window.PumpEvents();
                if (!_window.Exists) break;

                double now = sw.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, 0.1);
                last = now;

                InputState input = BuildInput(snap);
                int w = _window.Width, h = _window.Height;

                _cl.Begin();
                _cl.SetFramebuffer(Device.MainSwapchain.Framebuffer);
                _cl.ClearColorTarget(0, new RgbaFloat(ClearColor.X, ClearColor.Y, ClearColor.Z, ClearColor.W));

                _frame.Dt = dt; _frame.Input = input; _frame.Width = w; _frame.Height = h; _frame.Commands = _cl;
                onFrame(_frame);

                _cl.End();
                Device.SubmitCommands(_cl);
                Device.SwapBuffers(Device.MainSwapchain);
            }
        }

        InputState BuildInput(InputSnapshot snap)
        {
            var pressed = new HashSet<Key>();
            var released = new HashSet<Key>();
            foreach (var ke in snap.KeyEvents)
            {
                if (!MapKey(ke.Key, out Key k)) continue;
                if (ke.Down) { if (!ke.Repeat && _keysDown.Add(k)) pressed.Add(k); }
                else if (_keysDown.Remove(k)) released.Add(k);
            }

            var mPressed = new HashSet<MouseButton>();
            foreach (var me in snap.MouseEvents)
            {
                if (!MapMouse(me.MouseButton, out MouseButton b)) continue;
                if (me.Down) { if (_mouseDown.Add(b)) mPressed.Add(b); }
                else _mouseDown.Remove(b);
            }

            var pos = snap.MousePosition;
            var delta = pos - _lastMouse;
            _lastMouse = pos;

            return new InputState(
                new HashSet<Key>(_keysDown), pressed, released,
                new HashSet<MouseButton>(_mouseDown), mPressed,
                pos, delta, snap.WheelDelta, _window.Width, _window.Height);
        }

        static bool MapMouse(Veldrid.MouseButton b, out MouseButton r)
        {
            switch (b)
            {
                case Veldrid.MouseButton.Left: r = MouseButton.Left; return true;
                case Veldrid.MouseButton.Middle: r = MouseButton.Middle; return true;
                case Veldrid.MouseButton.Right: r = MouseButton.Right; return true;
                case Veldrid.MouseButton.Button1: r = MouseButton.X1; return true;
                case Veldrid.MouseButton.Button2: r = MouseButton.X2; return true;
                default: r = default; return false;
            }
        }

        static bool MapKey(Veldrid.Key k, out Key r)
        {
            r = k switch
            {
                >= Veldrid.Key.A and <= Veldrid.Key.Z => Key.A + (k - Veldrid.Key.A),
                >= Veldrid.Key.Number0 and <= Veldrid.Key.Number9 => Key.D0 + (k - Veldrid.Key.Number0),
                >= Veldrid.Key.F1 and <= Veldrid.Key.F12 => Key.F1 + (k - Veldrid.Key.F1),
                Veldrid.Key.Up => Key.Up,
                Veldrid.Key.Down => Key.Down,
                Veldrid.Key.Left => Key.Left,
                Veldrid.Key.Right => Key.Right,
                Veldrid.Key.Space => Key.Space,
                Veldrid.Key.Enter or Veldrid.Key.KeypadEnter => Key.Enter,
                Veldrid.Key.Escape => Key.Escape,
                Veldrid.Key.Tab => Key.Tab,
                Veldrid.Key.BackSpace => Key.Backspace,
                Veldrid.Key.Delete => Key.Delete,
                Veldrid.Key.Insert => Key.Insert,
                Veldrid.Key.Home => Key.Home,
                Veldrid.Key.End => Key.End,
                Veldrid.Key.PageUp => Key.PageUp,
                Veldrid.Key.PageDown => Key.PageDown,
                Veldrid.Key.ShiftLeft => Key.LeftShift,
                Veldrid.Key.ShiftRight => Key.RightShift,
                Veldrid.Key.ControlLeft => Key.LeftControl,
                Veldrid.Key.ControlRight => Key.RightControl,
                Veldrid.Key.AltLeft => Key.LeftAlt,
                Veldrid.Key.AltRight => Key.RightAlt,
                Veldrid.Key.Minus => Key.Minus,
                Veldrid.Key.Plus => Key.Equals,
                Veldrid.Key.Comma => Key.Comma,
                Veldrid.Key.Period => Key.Period,
                Veldrid.Key.Slash => Key.Slash,
                Veldrid.Key.Semicolon => Key.Semicolon,
                Veldrid.Key.BracketLeft => Key.LeftBracket,
                Veldrid.Key.BracketRight => Key.RightBracket,
                Veldrid.Key.BackSlash => Key.Backslash,
                Veldrid.Key.Quote => Key.Apostrophe,
                Veldrid.Key.Grave => Key.Grave,
                _ => Key.None,
            };
            return r != Key.None;
        }

        public void Dispose()
        {
            _cl.Dispose();
            Device.Dispose();
        }
    }
}
