using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Windowing;
using WKey = KhaozEngine.Windowing.Key;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// POC standalone host: wraps a <see cref="AppWindow"/> (which owns the SDL2 window + input + frame loop +
    /// GPU device) and renders a <see cref="Scene3D"/> into each frame. Hides the windowing/GPU plumbing behind a
    /// minimal key/frame callback. The window/input platform lives in KhaozEngine.Windowing; this package stays
    /// backend-free (all GPU work routes through the KhaozEngine.Gpu seam).
    /// </summary>
    public sealed class Render3DHost : IDisposable
    {
        readonly AppWindow _window;

        /// <summary>The scene to drive (camera, model, post settings).</summary>
        public Scene3D Scene { get; }

        public Render3DHost(string title, int width, int height)
        {
            _window = new AppWindow(title, width, height);
            Scene = new Scene3D(_window.GpuDevice, _window.GpuDevice.SwapchainFramebuffer!.Outputs);
        }

        /// <summary>Pump events, call <paramref name="onFrame"/>, render, present — until the window closes.</summary>
        public void Run(Action<FrameInfo> onFrame)
        {
            var down = new HashSet<Key>();
            _window.Run(frame =>
            {
                down.Clear();
                foreach (WKey wk in frame.Input.KeysDown)
                    if (TryMap(wk, out Key k)) down.Add(k);

                var pressed = new HashSet<Key>();
                foreach (WKey wk in frame.Input.KeysPressed)
                    if (TryMap(wk, out Key k)) pressed.Add(k);

                if (pressed.Contains(Key.Escape)) { _window.Close(); return; }

                onFrame(new FrameInfo { Dt = frame.Dt, Down = down, Pressed = pressed });

                Scene.RenderInternal(frame.Commands, frame.Width, frame.Height, _window.GpuDevice.SwapchainFramebuffer!);
            });
        }

        static bool TryMap(WKey k, out Key r)
        {
            switch (k)
            {
                case WKey.Escape: r = Key.Escape; return true;
                case WKey.Space: r = Key.Space; return true;
                case WKey.Q: r = Key.Q; return true;
                case WKey.W: r = Key.W; return true;
                case WKey.E: r = Key.E; return true;
                case WKey.R: r = Key.R; return true;
                case WKey.A: r = Key.A; return true;
                case WKey.S: r = Key.S; return true;
                case WKey.D: r = Key.D; return true;
                case WKey.O: r = Key.O; return true;
                case WKey.C: r = Key.C; return true;
                case WKey.P: r = Key.P; return true;
                case WKey.Up: r = Key.Up; return true;
                case WKey.Down: r = Key.Down; return true;
                case WKey.Left: r = Key.Left; return true;
                case WKey.Right: r = Key.Right; return true;
                case WKey.D1: r = Key.Number1; return true;
                case WKey.D2: r = Key.Number2; return true;
                case WKey.D3: r = Key.Number3; return true;
                case WKey.D4: r = Key.Number4; return true;
                case WKey.D5: r = Key.Number5; return true;
                default: r = default; return false;
            }
        }

        public void Dispose()
        {
            Scene.Dispose();
            _window.Dispose();
        }
    }
}
