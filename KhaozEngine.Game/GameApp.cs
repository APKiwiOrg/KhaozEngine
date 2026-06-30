using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Optional 2D game-loop facade over <see cref="AppWindow"/>: owns the per-frame composition + ordering
    /// (clock, design viewport, pointer, 2D batch) so a game subclass only overrides
    /// <see cref="OnLoad"/>/<see cref="OnUpdate"/>/<see cref="OnDraw2D"/>/<see cref="OnResize"/> and can't get
    /// the frame ordering wrong. The <see cref="OnRenderWorld"/> seam runs before the 2D pass for a subclass that
    /// renders a world first (e.g. <c>GameApp3D</c> in <c>KhaozEngine.Game.Render3D</c> drives a 3D scene there) -
    /// this package stays free of any renderer beyond Render2D. A game with special needs can still drive
    /// <see cref="AppWindow.Run"/> directly; that path stays public and unchanged.
    /// </summary>
    public abstract class GameApp : IDisposable
    {
        readonly AppWindow _window;
        readonly GameClock _clock = new();
        readonly IDesignViewport _viewport;
        readonly Pointer _pointer = new();
        readonly Render2DSurface _surface2D;

        InputState _input = InputState.Empty;
        int _frameWidth, _frameHeight;
        int _lastW = -1, _lastH = -1;
        float _dt;

        protected GameApp(in GameAppOptions options)
        {
            // The window + viewport come from the options' factories when set (e.g. AppWindow.Scaled +
            // AdaptiveViewport for a responsive, display-fitted game); otherwise the plain defaults.
            _window = options.WindowFactory?.Invoke(options)
                ?? new AppWindow(options.Title, options.Width, options.Height);
            _window.ClearColor = options.ClearColor;

            // Runtime window/taskbar icon (Windows/Linux; no-op on macOS where the .app icns owns the Dock icon).
            var icons = ResolveWindowIcons(options);
            if (icons.Length > 0) _window.SetIcon(icons);

            _viewport = options.ViewportFactory?.Invoke(options)
                ?? new DesignViewport(options.ResolvedDesignWidth, options.ResolvedDesignHeight, options.ScaleMode);

            _surface2D = new Render2DSurface(_window);
        }

        /// <summary>
        /// Resolve the configured window icon(s) to <see cref="WindowIcon"/>s: explicit
        /// <see cref="GameAppOptions.WindowIcons"/> win, else a single <see cref="GameAppOptions.WindowIconPath"/>
        /// PNG is decoded via <see cref="ImageRgba.Load"/>, else none (empty). Keeps the Render2D decode in this
        /// layer so KhaozEngine.Windowing stays decode-free.
        /// </summary>
        internal static WindowIcon[] ResolveWindowIcons(in GameAppOptions options)
        {
            IReadOnlyList<ImageRgba>? images = options.WindowIcons;
            if ((images == null || images.Count == 0) && !string.IsNullOrEmpty(options.WindowIconPath))
                images = new[] { ImageRgba.Load(options.WindowIconPath) };
            if (images == null || images.Count == 0) return Array.Empty<WindowIcon>();

            var icons = new WindowIcon[images.Count];
            for (int i = 0; i < images.Count; i++)
                icons[i] = new WindowIcon(images[i].Pixels, images[i].Width, images[i].Height);
            return icons;
        }

        /// <summary>The underlying window (owns the GPU device, the Silk.NET/GLFW window, and the raw frame loop).</summary>
        protected AppWindow Window => _window;
        /// <summary>The game clock (pause / time-scale over the raw frame delta). Updated each frame before <see cref="OnUpdate"/>.</summary>
        protected GameClock Clock => _clock;
        /// <summary>The design-space viewport. Updated each frame from the window size before <see cref="OnUpdate"/>.</summary>
        protected IDesignViewport Viewport => _viewport;
        /// <summary>The design-space pointer. Updated each frame from this frame's input before <see cref="OnUpdate"/>.</summary>
        protected Pointer Pointer => _pointer;
        /// <summary>This frame's raw input snapshot (for custom needs / 3D picking).</summary>
        protected InputState Input => _input;
        /// <summary>The 2D drawing surface bound to the window.</summary>
        protected Render2DSurface Surface2D => _surface2D;
        /// <summary>The 2D sprite batch (<see cref="Surface2D"/>.Batch).</summary>
        protected SpriteBatch Batch => _surface2D.Batch;
        /// <summary>This frame's window width in points.</summary>
        protected int FrameWidth => _frameWidth;
        /// <summary>This frame's window height in points.</summary>
        protected int FrameHeight => _frameHeight;
        /// <summary>This frame's scaled (simulation) delta in seconds (<see cref="GameClock.ScaledDeltaSeconds"/>).</summary>
        protected float Dt => _dt;

        /// <summary>Background colour cleared each frame; forwards to <see cref="AppWindow.ClearColor"/>.</summary>
        public Color ClearColor
        {
            get => _window.ClearColor;
            set => _window.ClearColor = value;
        }

        /// <summary>Load assets / build initial state. Called once before the loop starts.</summary>
        protected virtual void OnLoad() { }
        /// <summary>Per-frame simulation step. <paramref name="dt"/> is the scaled delta (<see cref="Dt"/>).</summary>
        protected virtual void OnUpdate(float dt) { }
        /// <summary>
        /// Render a world pass BEFORE the 2D batch each frame (empty by default). A subclass that owns its own
        /// render surface (e.g. a 3D scene) drives it here; <see cref="GameApp"/> itself stays 2D-only.
        /// </summary>
        protected virtual void OnRenderWorld(Frame frame) { }
        /// <summary>Draw the 2D scene / HUD. <paramref name="batch"/>.Begin(Viewport) is already called.</summary>
        protected virtual void OnDraw2D(SpriteBatch batch) { }
        /// <summary>Window resized (also fires once on the first frame). Design space units stay fixed.</summary>
        protected virtual void OnResize(int width, int height) { }

        /// <summary>Request the loop stop (closes the window after the current frame).</summary>
        protected void Quit() => _window.Close();

        /// <summary>Run the fixed, correct per-frame ordering until the window closes.</summary>
        public void Run()
        {
            OnLoad();
            _window.Run(frame =>
            {
                _clock.Update(frame.Dt);
                _input = frame.Input;
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _dt = _clock.ScaledDeltaSeconds;

                _viewport.Update(frame.Width, frame.Height);
                if (frame.Width != _lastW || frame.Height != _lastH)
                {
                    OnResize(frame.Width, frame.Height);
                    _lastW = frame.Width;
                    _lastH = frame.Height;
                }

                _pointer.Update(_input, _viewport);
                OnUpdate(_dt);

                OnRenderWorld(frame);

                _surface2D.NewFrame(frame);
                _surface2D.Batch.Begin(_viewport);
                OnDraw2D(_surface2D.Batch);
                _surface2D.Batch.End();
            });
        }

        /// <summary>Dispose a subclass's own resources (e.g. a 3D surface) before the 2D surface + window tear down.</summary>
        protected virtual void OnDispose() { }

        public void Dispose()
        {
            OnDispose();
            _surface2D.Dispose();
            _window.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
