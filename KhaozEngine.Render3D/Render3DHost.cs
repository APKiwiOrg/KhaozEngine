using System;
using System.Collections.Generic;
using System.Diagnostics;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Owns the SDL2 window + Metal graphics device + swapchain, and runs the frame loop. Hides Veldrid
    /// entirely. POC simplification: windowing lives here until a dedicated platform package exists.
    /// </summary>
    public sealed class Render3DHost : IDisposable
    {
        readonly Sdl2Window _window;
        readonly GraphicsDevice _gd;
        readonly CommandList _cl;
        readonly HashSet<Key> _down = new();

        /// <summary>The scene to drive (camera, model, post settings).</summary>
        public Scene3D Scene { get; }

        public Render3DHost(string title, int width, int height)
        {
            var wci = new WindowCreateInfo(100, 100, width, height, WindowState.Normal, title);
            var opts = new GraphicsDeviceOptions(
                debug: false,
                swapchainDepthFormat: null,
                syncToVerticalBlank: true,
                resourceBindingModel: ResourceBindingModel.Improved,
                preferDepthRangeZeroToOne: true,
                preferStandardClipSpaceYDirection: true);

            VeldridStartup.CreateWindowAndGraphicsDevice(wci, opts, GraphicsBackend.Metal, out _window, out _gd);
            _window.Resized += () => _gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            Scene = new Scene3D(_gd, _gd.MainSwapchain.Framebuffer.OutputDescription);
            _cl = _gd.ResourceFactory.CreateCommandList();
        }

        /// <summary>Pump events, call <paramref name="onFrame"/>, render, present — until the window closes.</summary>
        public void Run(Action<FrameInfo> onFrame)
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

                var pressed = new HashSet<Key>();
                foreach (var ke in snap.KeyEvents)
                {
                    if (!TryMap(ke.Key, out Key k)) continue;
                    if (ke.Down) { if (!ke.Repeat) pressed.Add(k); _down.Add(k); }
                    else _down.Remove(k);
                }
                if (pressed.Contains(Key.Escape)) { _window.Close(); break; }

                onFrame(new FrameInfo { Dt = dt, Down = _down, Pressed = pressed });

                _cl.Begin();
                Scene.RenderInternal(_cl, _window.Width, _window.Height, _gd.MainSwapchain.Framebuffer);
                _cl.End();
                _gd.SubmitCommands(_cl);
                _gd.SwapBuffers(_gd.MainSwapchain);
            }
        }

        static bool TryMap(Veldrid.Key k, out Key r)
        {
            switch (k)
            {
                case Veldrid.Key.Escape: r = Key.Escape; return true;
                case Veldrid.Key.Space: r = Key.Space; return true;
                case Veldrid.Key.Q: r = Key.Q; return true;
                case Veldrid.Key.W: r = Key.W; return true;
                case Veldrid.Key.E: r = Key.E; return true;
                case Veldrid.Key.R: r = Key.R; return true;
                case Veldrid.Key.A: r = Key.A; return true;
                case Veldrid.Key.S: r = Key.S; return true;
                case Veldrid.Key.D: r = Key.D; return true;
                case Veldrid.Key.O: r = Key.O; return true;
                case Veldrid.Key.C: r = Key.C; return true;
                case Veldrid.Key.P: r = Key.P; return true;
                case Veldrid.Key.Up: r = Key.Up; return true;
                case Veldrid.Key.Down: r = Key.Down; return true;
                case Veldrid.Key.Left: r = Key.Left; return true;
                case Veldrid.Key.Right: r = Key.Right; return true;
                case Veldrid.Key.Number1: r = Key.Number1; return true;
                case Veldrid.Key.Number2: r = Key.Number2; return true;
                case Veldrid.Key.Number3: r = Key.Number3; return true;
                case Veldrid.Key.Number4: r = Key.Number4; return true;
                case Veldrid.Key.Number5: r = Key.Number5; return true;
                default: r = default; return false;
            }
        }

        public void Dispose()
        {
            Scene.Dispose();
            _cl.Dispose();
            _gd.Dispose();
        }
    }
}
