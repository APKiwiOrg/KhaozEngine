using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gui;
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
        readonly UiViewport _ui = new();
        readonly Pointer _uiPointer = new();
        readonly Render2DSurface _surface2D;

        // Built-in frame-cost HUD (FPS/frame-ms/heap + draw counters + 3D pass timings), toggled with F1. Null when
        // opted out via GameAppOptions.DisableDiagnosticsOverlay. The font + white pixel back its rendering.
        readonly DiagnosticsHud? _hud;
        readonly DpiFont? _hudFont;
        readonly Texture2D? _hudWhite;

        InputState _input = InputState.Empty;
        int _frameWidth, _frameHeight;
        int _lastW = -1, _lastH = -1;
        float _dt;
        readonly double _resumeGapThresholdSeconds;

        protected GameApp(in GameAppOptions options)
        {
            // WinExe support: a Windows-subsystem game head (OutputType=WinExe, which stops a stray console window
            // opening behind the game) has no console, so Console.Write* output vanishes when the game is launched
            // from a terminal (dotnet run / cmd / PowerShell). Attach the parent process's console (if any) FIRST,
            // before anything - even the AppUserModelId call below - can write, so no startup logging is lost.
            // No-op off Windows, for a console-subsystem exe, when a console already exists, on a normal
            // Explorer/Start launch (no parent console), or when output is redirected (CI/pipes are left
            // untouched); never throws. Opt out with GameAppOptions.SuppressParentConsoleAttach.
            AppWindow.TryAttachParentConsole(enable: !options.SuppressParentConsoleAttach);

            // Last-chance crash net for the invisible case: a Windows GUI launch (no parent console, so the attach
            // above was a no-op) has no window yet and no console, so an uncaught STARTUP crash would vanish
            // silently. When the process still owns no console, install a floor that writes the fatal exception to
            // a file under the per-user local app-data dir. A terminal launch (console now attached) shows the
            // exception on stderr and skips this; a game that wires KhaozEngine.Diagnostics.CrashHandler still gets
            // its richer game.log too. Never throws.
            if (!AppWindow.ProcessHasConsole)
                StartupCrashLog.InstallForNoConsole(options.Title);

            _resumeGapThresholdSeconds = options.ResumeGapThresholdSeconds;

            // Windows taskbar identity: set the process's explicit AppUserModelID BEFORE the native window is
            // created, so Windows 10/11 keys the taskbar button to the app (grouping/pinning + resolving the
            // running-app icon). No-op off Windows or when AppUserModelId is null. Must precede window creation.
            AppWindow.TrySetProcessAppUserModelId(options.AppUserModelId);

            // Frame-cap intent: an explicit positive FrameCapHz wins over FrameCap. Otherwise FrameCap governs
            // (defaulting to the backend-aware Auto). This is the value applied to the window below.
            FrameCap requestedCap = options.FrameCapHz > 0 ? FrameCap.Hz(options.FrameCapHz) : options.FrameCap;

            // The window + viewport come from the options' factories when set (e.g. AppWindow.Scaled +
            // AdaptiveViewport for a responsive, display-fitted game); otherwise the plain defaults. The window is
            // born hidden (AppWindow's ctor); it is revealed by Show() below, after the icon is applied.
            _window = options.WindowFactory?.Invoke(options)
                ?? new AppWindow(options.Title, options.Width, options.Height, options.PresentMode, requestedCap);
            _window.ClearColor = options.ClearColor;
            // FrameCap and BackgroundThrottle are post-construction properties, so they apply on BOTH the default
            // window (above) and a custom WindowFactory window (which cannot know these options otherwise). PresentMode
            // selects the swapchain vsync at creation, so it is honoured only on the default window. A factory must
            // forward it.
            _window.FrameCap = requestedCap;
            _window.BackgroundThrottle = options.BackgroundThrottle ?? BackgroundThrottlePolicy.Default;
            // WindowMode is a post-construction switch (the window is born windowed), so it applies on BOTH the
            // default and a custom WindowFactory window. Only drive it when a fullscreen mode is requested so a
            // plain windowed app never touches the state.
            if (options.WindowMode != WindowMode.Windowed) _window.WindowMode = options.WindowMode;

            // Runtime window/taskbar icon (Windows/Linux; no-op on macOS where GLFW ignores window icons). Applied
            // while the window is still hidden so the Windows taskbar button - created when we Show() below - is
            // born with this icon rather than GLFW's generic default.
            var icons = ResolveWindowIcons(options);
            if (icons.Length > 0) _window.SetIcon(icons);

            // macOS Dock / Cmd-Tab icon: GLFW cannot set it and an unbundled dotnet-run app has no .app icns, so
            // drive NSApplication.setApplicationIconImage from the same icon PNG. Only the single-PNG
            // WindowIconPath case is covered (NSImage decodes PNG); a WindowIcons-only (already-decoded RGBA)
            // config leaves the Dock icon untouched. Runs after the window ctor, so the shared NSApplication exists.
            if (OperatingSystem.IsMacOS() && !string.IsNullOrEmpty(options.WindowIconPath)
                && System.IO.File.Exists(options.WindowIconPath))
                _window.SetMacDockIcon(System.IO.File.ReadAllBytes(options.WindowIconPath));

            // Reveal the window now that the runtime icon is set (born hidden - see AppWindow.Show).
            _window.Show();

            _viewport = options.ViewportFactory?.Invoke(options)
                ?? new DesignViewport(options.ResolvedDesignWidth, options.ResolvedDesignHeight, options.ScaleMode);

            _surface2D = new Render2DSurface(_window);

            // Turn-key diagnostics HUD (default ON, hidden until F1). Built here so every GameApp / GameApp3D game
            // gets the frame-cost overlay for free. SupportsPassTimings is a constant-returning virtual, safe to
            // call from this base ctor (GameApp3D returns true so it also shows the per-pass timing section). The
            // font is a DpiFont so the panel stays crisp on HiDPI, drawn through the point-space UI pass.
            DiagnosticsOverlayTheme? hudTheme = BuildDiagnosticsTheme(options);
            if (hudTheme != null)
            {
                _hud = new DiagnosticsHud(hudTheme, withPassTimings: SupportsPassTimings);
                _hudFont = _surface2D.LoadDefaultDpiFont(32f);
                _hudWhite = _surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            }
        }

        /// <summary>
        /// Build the diagnostics HUD theme from <paramref name="options"/>, or null when the built-in overlay is
        /// opted out (<see cref="GameAppOptions.DisableDiagnosticsOverlay"/>). The toggle key is
        /// <see cref="GameAppOptions.DiagnosticsToggleKey"/>, defaulting to <see cref="Key.F1"/>. Pure, so the
        /// enable/toggle-key precedence is headless-testable.
        /// </summary>
        internal static DiagnosticsOverlayTheme? BuildDiagnosticsTheme(in GameAppOptions options)
        {
            if (options.DisableDiagnosticsOverlay) return null;
            var theme = DiagnosticsOverlayTheme.Default;
            theme.ToggleKey = options.DiagnosticsToggleKey ?? Key.F1;
            return theme;
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
        /// <summary>The point-space UI viewport (1 logical point = the DPI scale in device pixels). Updated each frame
        /// from the frame's logical + framebuffer size before <see cref="OnUpdate"/>; draw DPI-aware UI through it in
        /// <see cref="OnDrawUi"/>.</summary>
        protected UiViewport Ui => _ui;
        /// <summary>The design-space pointer. Updated each frame from this frame's input before <see cref="OnUpdate"/>.</summary>
        protected Pointer Pointer => _pointer;
        /// <summary>The point-space UI pointer (mapped through <see cref="Ui"/>). Hit-test <see cref="OnDrawUi"/> widgets with this, not <see cref="Pointer"/>.</summary>
        protected Pointer UiPointer => _uiPointer;
        /// <summary>This frame's raw input snapshot (for custom needs / 3D picking).</summary>
        protected InputState Input => _input;
        /// <summary>Gamepad rumble OUTPUT seam (see <see cref="KhaozEngine.Windowing.Rumble.IRumble"/>): sustained
        /// <see cref="KhaozEngine.Windowing.Rumble.IRumble.SetRumble"/> or fire-and-forget
        /// <see cref="KhaozEngine.Windowing.Rumble.IRumble.Pulse"/>; the loop ticks decay/auto-stop. A backend/pad with
        /// no motors (the current GLFW backend) is a graceful no-op, so call it unconditionally.</summary>
        protected KhaozEngine.Windowing.Rumble.IRumble Rumble => _window.Rumble;
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

        /// <summary>
        /// The built-in diagnostics HUD (FPS / frame-ms / heap, draw counters, and - for a 3D app - per-pass
        /// timings), or null when opted out via <see cref="GameAppOptions.DisableDiagnosticsOverlay"/>. Hidden until
        /// toggled with the configured key (F1 by default). A game may drive it directly, e.g.
        /// <c>Diagnostics?.SetNetStatsSource(() =&gt; ...)</c> to add a Network section, or read <see cref="DiagnosticsHud.Visible"/>.
        /// </summary>
        protected DiagnosticsHud? Diagnostics => _hud;

        /// <summary>Whether this game type feeds the HUD a per-pass timing section (a 3D app does). The base 2D
        /// <see cref="GameApp"/> returns false and <c>GameApp3D</c> overrides it. A constant, so it is safe for the
        /// base constructor to read when building the HUD.</summary>
        protected virtual bool SupportsPassTimings => false;

        /// <summary>
        /// The whole-frame render stats shown in the HUD's Draw-stats section. The base returns the 2D batch's
        /// <see cref="SpriteBatch.FrameStats"/>, and <c>GameApp3D</c> overrides it to also add the 3D scene's
        /// <c>LastFrameStats</c>. Read once per frame, after the world + 2D passes, just before the overlay draws.
        /// </summary>
        protected virtual RenderFrameStats CollectFrameStats() => _surface2D.Batch.FrameStats;

        /// <summary>Background colour cleared each frame; forwards to <see cref="AppWindow.ClearColor"/>.</summary>
        public Color ClearColor
        {
            get => _window.ClearColor;
            set => _window.ClearColor = value;
        }

        /// <summary>
        /// The cohesive runtime display-control surface (present mode, frame cap, window mode, resolution). Read
        /// <see cref="IDisplaySettings.CurrentDisplay"/>, tweak, and <see cref="IDisplaySettings.ApplyDisplay"/> it
        /// back from a settings screen - all safe to call mid-session with no crash and no leaked swapchain. The
        /// individual <see cref="PresentMode"/> / <see cref="FrameCapHz"/> / <see cref="WindowMode"/> pass-throughs
        /// below are conveniences over the same surface.
        /// </summary>
        public IDisplaySettings Display => _window;

        /// <summary>How the window presents frames; forwards to <see cref="AppWindow.PresentMode"/> (reconfigures the
        /// live swapchain's vsync in place). On Metal, pair vsync with <see cref="FrameCapHz"/> for a real cap.</summary>
        public PresentMode PresentMode
        {
            get => _window.PresentMode;
            set => _window.PresentMode = value;
        }

        /// <summary>Software frame-rate cap in Hz, forwarding to <see cref="AppWindow.FrameCapHz"/>. Setting a positive
        /// value is an explicit fixed cap and 0 an explicit free-run. The getter returns the RESOLVED effective cap
        /// (so with the default <see cref="Windowing.FrameCap.Auto"/> it reflects the backend-aware cap). Use
        /// <see cref="FrameCap"/> for the richer intent.</summary>
        public int FrameCapHz
        {
            get => _window.FrameCapHz;
            set => _window.FrameCapHz = value;
        }

        /// <summary>The frame-cap intent (auto / uncapped / fixed), forwarding to <see cref="AppWindow.FrameCap"/>. The
        /// default <see cref="Windowing.FrameCap.Auto"/> is backend-aware (a real cap on Metal + vsync, uncapped where
        /// vsync throttles). A consumer-set value always wins.</summary>
        public FrameCap FrameCap
        {
            get => _window.FrameCap;
            set => _window.FrameCap = value;
        }

        /// <summary>How the loop throttles the window while backgrounded (unfocused / minimized), forwarding to
        /// <see cref="AppWindow.BackgroundThrottle"/>. Default <see cref="BackgroundThrottlePolicy.Default"/> (ON).</summary>
        public BackgroundThrottlePolicy BackgroundThrottle
        {
            get => _window.BackgroundThrottle;
            set => _window.BackgroundThrottle = value;
        }

        /// <summary>How the window occupies the display; forwards to <see cref="AppWindow.WindowMode"/>.</summary>
        public WindowMode WindowMode
        {
            get => _window.WindowMode;
            set => _window.WindowMode = value;
        }

        /// <summary>The active graphics backend (Metal / D3D11 / Vulkan); forwards to <see cref="AppWindow.Backend"/>.
        /// Useful to branch display defaults per platform (e.g. force a <see cref="FrameCapHz"/> on Metal).</summary>
        public GpuBackendKind Backend => _window.Backend;

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
        /// <summary>
        /// Draw the point-space UI layer (empty by default), in a separate pass AFTER <see cref="OnDraw2D"/> each
        /// frame with <paramref name="batch"/>.Begin(<see cref="Ui"/>) already called. Author DPI-aware, reflowing UI
        /// here (text via <c>DpiFont.For(Ui.DpiScale)</c>, chrome auto-snapped to device pixels) so it stays crisp on
        /// HiDPI; hit-test with <see cref="UiPointer"/>. The design-space game field stays in <see cref="OnDraw2D"/>.
        /// </summary>
        protected virtual void OnDrawUi(SpriteBatch batch) { }
        /// <summary>Window resized (also fires once on the first frame). Design space units stay fixed.</summary>
        protected virtual void OnResize(int width, int height) { }
        /// <summary>
        /// Called on the first frame after a wall-clock gap larger than
        /// <see cref="GameAppOptions.ResumeGapThresholdSeconds"/> (OS sleep/suspend/hibernate or a long hang). The
        /// game can run offline catch-up, re-sync timers, or pause. Fires once per gap, before <see cref="OnUpdate"/>
        /// on that frame, and never on the first frame. Disabled when the threshold is 0 or negative.
        /// </summary>
        protected virtual void OnResume(TimeSpan wallGap) { }

        /// <summary>Pure fire rule for <see cref="OnResume"/>: a supra-threshold wall-clock gap, with a
        /// non-positive threshold disabling it. The gap's own frame-1 (0) and backward-step (clamped to 0)
        /// handling lives in <see cref="GameClock"/>, so this only decides the threshold crossing.</summary>
        internal static bool ShouldRaiseResume(double wallGapSeconds, double thresholdSeconds)
            => thresholdSeconds > 0.0 && wallGapSeconds > thresholdSeconds;

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
                _dt = _clock.ScaledDeltaSeconds;

                // Minimized (render-suppressed) frames still tick simulation so netcode / physics / timers keep
                // advancing, but skip everything render-facing (frame size, viewport, pointer, draw) - the window has
                // no drawable while iconified, and the last-known frame size stays put for any FrameWidth read.
                if (frame.RenderSuppressed)
                {
                    if (ShouldRaiseResume(_clock.RealWallGapSeconds, _resumeGapThresholdSeconds))
                        OnResume(TimeSpan.FromSeconds(_clock.RealWallGapSeconds));
                    OnUpdate(_dt);
                    return;
                }

                _frameWidth = frame.Width;
                _frameHeight = frame.Height;

                _viewport.Update(frame.Width, frame.Height);
                if (frame.Width != _lastW || frame.Height != _lastH)
                {
                    OnResize(frame.Width, frame.Height);
                    _lastW = frame.Width;
                    _lastH = frame.Height;
                }

                _pointer.Update(_input, _viewport);

                // Point-space UI viewport + pointer: 1 logical point = the DPI scale in device pixels, reflowing to
                // the logical window size (stable per display, so DpiFont atlases re-bake only on a DPI change).
                _ui.Update(frame);
                _uiPointer.Update(_input, _ui);

                // A supra-threshold wall-clock gap means the OS slept/suspended (or the app hung) between frames.
                // Raise OnResume before OnUpdate so a game can catch up offline / re-sync timers for this frame.
                if (ShouldRaiseResume(_clock.RealWallGapSeconds, _resumeGapThresholdSeconds))
                    OnResume(TimeSpan.FromSeconds(_clock.RealWallGapSeconds));

                OnUpdate(_dt);

                // Advance the diagnostics HUD (sample FPS from the RAW frame delta, process the F1 toggle + fade)
                // BEFORE the world render, so a 3D subclass can gate this frame's pass timing on its visibility.
                _hud?.Update(_input, frame.Dt);

                OnRenderWorld(frame);

                _surface2D.NewFrame(frame);
                _surface2D.Batch.Begin(_viewport);
                OnDraw2D(_surface2D.Batch);
                _surface2D.Batch.End();

                // Point-space UI pass: a second begin in the DPI-aware UiViewport, so DPI UI draws crisp on top of
                // the (letterboxed) design-space field. Empty by default, so a game that only uses OnDraw2D is unaffected.
                _surface2D.Batch.Begin(_ui);
                OnDrawUi(_surface2D.Batch);
                _surface2D.Batch.End();

                // Diagnostics HUD on top, in its own point-space UI pass (crisp on HiDPI). The aggregated draw stats
                // (2D batch + any 3D scene, via CollectFrameStats) are handed in just before the panel draws. No-op
                // while hidden (the overlay draws nothing and the throttled provider builds no sections).
                if (_hud is { } hud)
                {
                    hud.SetDrawStats(CollectFrameStats());
                    _surface2D.Batch.Begin(_ui);
                    hud.Draw(_surface2D.Batch, _hudFont!.For(_ui.DpiScale), _hudWhite!, _ui.DesignBounds);
                    _surface2D.Batch.End();
                }
            });
        }

        /// <summary>Dispose a subclass's own resources (e.g. a 3D surface) before the 2D surface + window tear down.</summary>
        protected virtual void OnDispose() { }

        public void Dispose()
        {
            OnDispose();
            _hudFont?.Dispose();
            _hudWhite?.Dispose();
            _surface2D.Dispose();
            _window.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
