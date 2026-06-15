using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using KhaozEngine.Render2D.Internal;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Owns the SDL2 window + Metal graphics device + swapchain and runs the frame loop. Hides Veldrid.
    /// POC simplification: windowing lives here until a dedicated platform package exists.
    /// </summary>
    public sealed class Render2DHost : IDisposable
    {
        readonly Sdl2Window _window;
        readonly Render2DCore _core;
        readonly CommandList _cl;
        readonly HashSet<Key> _down = new();

        /// <summary>The batch consumers draw with inside <see cref="Run"/>.</summary>
        public SpriteBatch Batch => _core.Batch;
        /// <summary>Background colour cleared each frame.</summary>
        public Vector4 ClearColor = new(0.10f, 0.12f, 0.16f, 1f);

        public Render2DHost(string title, int width, int height)
        {
            var wci = new WindowCreateInfo(100, 100, width, height, WindowState.Normal, title);
            var opts = new GraphicsDeviceOptions(false, null, true, ResourceBindingModel.Improved, true, true);
            VeldridStartup.CreateWindowAndGraphicsDevice(wci, opts, GraphicsBackend.Metal, out _window, out var gd);
            _window.Resized += () => gd.MainSwapchain.Resize((uint)_window.Width, (uint)_window.Height);
            _core = new Render2DCore(gd, gd.MainSwapchain.Framebuffer.OutputDescription);
            _cl = gd.ResourceFactory.CreateCommandList();
        }

        public Texture2D LoadTexture(string pngPath) => _core.LoadTexture(pngPath);
        public Texture2D CreateTexture(byte[] rgba, int width, int height) => _core.CreateTexture(rgba, width, height);
        public SpriteFont LoadFont(string ttfPath, float pixelHeight) => _core.LoadFont(ttfPath, pixelHeight);

        public void Run(Action<FrameInfo> onFrame)
        {
            var gd = _core.Gd;
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
                    if (!Keys.TryMap(ke.Key, out Key k)) continue;
                    if (ke.Down) { if (!ke.Repeat) pressed.Add(k); _down.Add(k); }
                    else _down.Remove(k);
                }
                if (pressed.Contains(Key.Escape)) { _window.Close(); break; }

                int w = _window.Width, h = _window.Height;
                _cl.Begin();
                _cl.SetFramebuffer(gd.MainSwapchain.Framebuffer);
                _cl.ClearColorTarget(0, new RgbaFloat(ClearColor.X, ClearColor.Y, ClearColor.Z, ClearColor.W));
                _core.Batch.NewFrame(_cl, w, h);
                onFrame(new FrameInfo { Dt = dt, Width = w, Height = h, Down = _down, Pressed = pressed });
                _cl.End();
                gd.SubmitCommands(_cl);
                gd.SwapBuffers(gd.MainSwapchain);
            }
        }

        public void Dispose() { _cl.Dispose(); _core.Dispose(); }
    }

    internal static class Keys
    {
        public static bool TryMap(Veldrid.Key k, out Key r)
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
                default: r = default; return false;
            }
        }
    }
}
