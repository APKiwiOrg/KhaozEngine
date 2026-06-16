using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Optional game-loop facade over <see cref="AppWindow"/>: owns the per-frame composition + ordering
    /// (clock, design viewport, pointer, 3D scene render, 2D batch) so a game subclass only overrides
    /// <see cref="OnLoad"/>/<see cref="OnUpdate"/>/<see cref="OnDraw3D"/>/<see cref="OnDraw2D"/>/
    /// <see cref="OnResize"/> and can't get the frame ordering wrong. A game with special needs can still
    /// drive <see cref="AppWindow.Run"/> directly; that path stays public and unchanged.
    /// </summary>
    public abstract class GameApp : IDisposable
    {
        readonly AppWindow _window;
        readonly GameClock _clock = new();
        readonly DesignViewport _viewport;
        readonly Pointer _pointer = new();
        readonly Render2DSurface _surface2D;
        readonly Render3DSurface? _surface3D;

        InputState _input = InputState.Empty;
        int _frameWidth, _frameHeight;
        int _lastW = -1, _lastH = -1;
        float _dt;

        protected GameApp(in GameAppOptions options)
        {
            _window = new AppWindow(options.Title, options.Width, options.Height)
            {
                ClearColor = options.ClearColor,
            };

            _viewport = new DesignViewport(
                options.ResolvedDesignWidth, options.ResolvedDesignHeight, options.ScaleMode);

            _surface2D = new Render2DSurface(_window);
            if (options.Enable3D)
                _surface3D = new Render3DSurface(_window);
        }

        /// <summary>The underlying window (owns the GPU device, the SDL2 window, and the raw frame loop).</summary>
        protected AppWindow Window => _window;
        /// <summary>The game clock (pause / time-scale over the raw frame delta). Updated each frame before <see cref="OnUpdate"/>.</summary>
        protected GameClock Clock => _clock;
        /// <summary>The design-space viewport. Updated each frame from the window size before <see cref="OnUpdate"/>.</summary>
        protected DesignViewport Viewport => _viewport;
        /// <summary>The design-space pointer. Updated each frame from this frame's input before <see cref="OnUpdate"/>.</summary>
        protected Pointer Pointer => _pointer;
        /// <summary>This frame's raw input snapshot (for custom needs / 3D picking).</summary>
        protected InputState Input => _input;
        /// <summary>The 2D drawing surface bound to the window.</summary>
        protected Render2DSurface Surface2D => _surface2D;
        /// <summary>The 3D surface, or null unless <see cref="GameAppOptions.Enable3D"/>.</summary>
        protected Render3DSurface? Surface3D => _surface3D;
        /// <summary>The 3D scene (<see cref="Surface3D"/>?.Scene), or null unless Enable3D.</summary>
        protected Scene3D? Scene => _surface3D?.Scene;
        /// <summary>The 2D sprite batch (<see cref="Surface2D"/>.Batch).</summary>
        protected SpriteBatch Batch => _surface2D.Batch;
        /// <summary>This frame's window width in points.</summary>
        protected int FrameWidth => _frameWidth;
        /// <summary>This frame's window height in points.</summary>
        protected int FrameHeight => _frameHeight;
        /// <summary>This frame's scaled (simulation) delta in seconds (<see cref="GameClock.ScaledDeltaSeconds"/>).</summary>
        protected float Dt => _dt;

        /// <summary>Background colour cleared each frame; forwards to <see cref="AppWindow.ClearColor"/>.</summary>
        public Vector4 ClearColor
        {
            get => _window.ClearColor;
            set => _window.ClearColor = value;
        }

        /// <summary>Load assets / build initial state. Called once before the loop starts.</summary>
        protected virtual void OnLoad() { }
        /// <summary>Per-frame simulation step. <paramref name="dt"/> is the scaled delta (<see cref="Dt"/>).</summary>
        protected virtual void OnUpdate(float dt) { }
        /// <summary>Submit 3D instances; only called when Enable3D. <paramref name="scene"/>.Begin() is already called.</summary>
        protected virtual void OnDraw3D(Scene3D scene) { }
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

                if (_surface3D is not null)
                {
                    _surface3D.Scene.Begin();
                    OnDraw3D(_surface3D.Scene);
                    _surface3D.Render(frame);
                }

                _surface2D.NewFrame(frame);
                _surface2D.Batch.Begin(_viewport);
                OnDraw2D(_surface2D.Batch);
                _surface2D.Batch.End();
            });
        }

        public void Dispose()
        {
            _surface3D?.Dispose();
            _surface2D.Dispose();
            _window.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
