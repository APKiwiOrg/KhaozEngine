using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gpu;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Simulation;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Optional 2D game-loop facade over <see cref="AppWindow"/>: owns the per-frame composition + ordering
    /// (clock, design viewport, pointer, 2D batch) so a game subclass only overrides
    /// <see cref="OnLoad"/>/<see cref="OnUpdate"/>/<see cref="OnDraw2D"/>/<see cref="OnResize"/> and can't get
    /// the frame ordering wrong. The <see cref="OnPrepareWorld"/> + <see cref="OnRenderWorld"/> seam pair runs before
    /// the 2D pass for a subclass that renders a world first (e.g. <c>GameApp3D</c> in
    /// <c>KhaozEngine.Game.Render3D</c> drives a 3D scene there) - this package stays free of any renderer beyond
    /// Render2D. The pair straddles the window's two frame phases: queues are filled in
    /// <see cref="OnPrepareWorld"/> before the frame's command list opens, and recorded in
    /// <see cref="OnRenderWorld"/> inside it. A game with special needs can still drive
    /// <see cref="AppWindow.Run(Action{Frame}, Action{Frame})"/> directly, and that path stays public and unchanged.
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

        // Opt-in auto-pause while the window is backgrounded (GameAppOptions.PauseOnFocusLoss). Holds the
        // focus edge and whether the pause on the clock is ours to lift, so a game's own pause survives a
        // refocus. Constructed either way and inert when the option is off.
        readonly FocusAutoPause _focusAutoPause;

        InputState _input = InputState.Empty;
        int _frameWidth, _frameHeight;
        int _lastW = -1, _lastH = -1;
        float _dt;
        readonly double _resumeGapThresholdSeconds;

        // Turn-key client-side worker pool (JobScheduler, below): built lazily on first read so a game that never
        // touches it never pays for a ThreadPoolJobScheduler it doesn't use. The two option fields are captured
        // here (rather than re-read from `options` later) because GameAppOptions is a struct arg, gone once the
        // ctor returns.
        readonly bool _jobSchedulerDisabled;
        readonly int? _jobSchedulerDegreeOfParallelism;
        IJobScheduler? _jobScheduler;

        // Single-instance guard (opt-in, GameAppOptions.SingleInstance): the acquired lock is kept alive for
        // the process lifetime and released in Dispose. The listener thread polls it for a foreground request
        // from a losing second launch and only sets a flag - the actual AppWindow.RequestForeground() call
        // happens on the main thread inside Run's frame callback below, since GLFW itself is not thread-safe
        // for that call.
        ISingleInstanceLock? _singleInstanceLock;
        Thread? _singleInstanceListener;
        volatile bool _foregroundRequested;
        volatile bool _disposed;

        protected GameApp(in GameAppOptions options)
        {
            // Single-instance guard: claimed BEFORE anything else in the ctor - even the console attach below -
            // so a losing second launch never creates a window, console, or crash-log hook. Opt-in
            // (GameAppOptions.SingleInstance); see KhaozEngine.App.SingleInstanceGuard for the mechanism
            // (composes with a forced AppRelaunch.Restart and with the auto-updater's post-update relaunch).
            if (options.SingleInstance)
            {
                string? key = ResolveSingleInstanceKey(options);
                if (string.IsNullOrEmpty(key))
                {
                    throw new InvalidOperationException(
                        "GameAppOptions.SingleInstance requires SingleInstanceId or AppUserModelId to be set.");
                }

                SingleInstanceAcquireResult acquire = SingleInstanceGuard.TryAcquire(key);
                if (acquire.Outcome == SingleInstanceOutcome.AlreadyRunning)
                {
                    // The existing owner has already been asked (best-effort) to come to the foreground. This
                    // process must go no further - no window, no GPU device - so it exits right here. Log.Info
                    // is a safe no-op if the game has not configured logging yet, and writes through whatever
                    // sink it configured (console/file) when it has.
                    Log.Info($"Another instance is already running (single-instance key '{key}'); asked it to come to the foreground and exiting.");
                    Environment.Exit(0);
                    return; // unreachable after Exit; documents intent for anyone reading the ctor top-to-bottom.
                }

                _singleInstanceLock = acquire.Lock;
                _singleInstanceListener = new Thread(ListenForForegroundRequests)
                {
                    IsBackground = true,
                    Name = "KE-SingleInstance",
                };
                _singleInstanceListener.Start();
            }

            // WinExe support: a Windows-subsystem game head (OutputType=WinExe, which stops a stray console window
            // opening behind the game) has no console, so Console.Write* output vanishes when the game is launched
            // from a terminal (dotnet run / cmd / PowerShell). Attach the parent process's console (if any) FIRST,
            // before anything - even the AppUserModelId call below - can write, so no startup logging is lost.
            // No-op off Windows, for a console-subsystem exe, when a console already exists, on a normal
            // Explorer/Start launch (no parent console), or when output is redirected (CI/pipes are left
            // untouched); never throws. Opt out with GameAppOptions.SuppressParentConsoleAttach.
            AppWindow.TryAttachParentConsole(enable: !options.SuppressParentConsoleAttach);

            // Last-chance crash file, armed for EVERY head rather than only for the no-console case (see
            // TryArmCrashReport for what it is and when it declines).
            TryArmCrashReport(options);

            _resumeGapThresholdSeconds = options.ResumeGapThresholdSeconds;
            _focusAutoPause = new FocusAutoPause(options.PauseOnFocusLoss);
            _jobSchedulerDisabled = options.DisableJobScheduler;
            _jobSchedulerDegreeOfParallelism = options.JobSchedulerDegreeOfParallelism;

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
                ?? new AppWindow(options.Title, options.Width, options.Height, options.PresentMode, requestedCap,
                    options.GraphicsBackendPreference);
            _window.ClearColor = options.ClearColor;
            // FrameCap and BackgroundThrottle are post-construction properties, so they apply on BOTH the default
            // window (above) and a custom WindowFactory window (which cannot know these options otherwise). PresentMode
            // and GraphicsBackendPreference both feed device/swapchain creation, so they are honoured only on the
            // default window. A factory must forward them.
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

            // The graphics backend is the first fact worth having in a crash file and the first one that exists:
            // the device is up as soon as the window is. A boot-time crash that reads "backend: MetalNative" is a
            // different investigation from the same crash on another backend, and the tester should not have to
            // remember which one they launched. Free when the crash file is opted out (the note is just held).
            CrashReport.Note("backend", _window.Backend.ToString());

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
                _hud = new DiagnosticsHud(hudTheme, withPassTimings: SupportsPassTimings,
                    visibleAtBoot: options.DiagnosticsVisibleAtBoot);
                _hudFont = _surface2D.LoadDefaultDpiFont(32f);
                _hudWhite = _surface2D.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            }
        }

        /// <summary>
        /// Arm the last-chance crash file for this head, and say whether it did.
        ///
        /// <para><b>ARMED FOR EVERY HEAD, not only for the no-console case.</b> A terminal launch does print the
        /// exception on stderr, which is exactly what is gone an hour later when someone asks what the crash
        /// said: the engine's own showcase lost a one-off managed exception that way, with the operating
        /// system's crash report naming only coreclr's dispatch frames
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/607). So the floor writes type, message, stack,
        /// timestamp, engine version and the graphics backend to a file beside the OS crash report.</para>
        ///
        /// <para><b>AND IT DECLINES TWICE: on the opt-out, and on an arming that already exists.</b> FIRST WINS.
        /// A head that called <see cref="CrashReport.Install(CrashReportOptions)"/> itself before constructing
        /// its <see cref="GameApp"/> chose its own directory, label and retention, and that arming call is
        /// idempotent by REPLACEMENT, so arming unconditionally here silently threw that configuration away and
        /// pointed the reports somewhere the head was not looking. The turn-key floor exists for the head that
        /// did nothing, and it should behave that way.</para>
        ///
        /// <para>Pure enough to test headlessly, which is the other half of why it is a member: the ctor it
        /// came out of needs a window, so neither the opt-out nor the first-wins rule was reachable from a
        /// test. Never throws.</para>
        /// </summary>
        /// <param name="options">The head's options, read for <see cref="GameAppOptions.SuppressCrashReportFile"/>
        /// and for the process label.</param>
        /// <returns>True when this call armed it, false when it was opted out or already armed.</returns>
        internal static bool TryArmCrashReport(in GameAppOptions options)
        {
            if (options.SuppressCrashReportFile) return false;
            if (CrashReport.IsInstalled) return false;

            CrashReport.Install(new CrashReportOptions { ProcessLabel = options.Title });
            return true;
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
        /// The single-instance guard key <see cref="GameApp"/>'s constructor resolves from
        /// <paramref name="options"/>: <see cref="GameAppOptions.SingleInstanceId"/> when non-empty, else
        /// <see cref="GameAppOptions.AppUserModelId"/>, else null (which the ctor turns into a thrown
        /// <see cref="InvalidOperationException"/> when <see cref="GameAppOptions.SingleInstance"/> is set).
        /// Pure, so the fallback precedence is headless-testable without standing up a window - mirrors
        /// <see cref="BuildDiagnosticsTheme"/> / <see cref="CreateJobScheduler"/>.
        /// </summary>
        internal static string? ResolveSingleInstanceKey(in GameAppOptions options)
            => !string.IsNullOrEmpty(options.SingleInstanceId) ? options.SingleInstanceId : options.AppUserModelId;

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
        /// toggled with the configured key (F1 by default), unless
        /// <see cref="GameAppOptions.DiagnosticsVisibleAtBoot"/> starts it shown. A game may drive it directly, e.g.
        /// <c>Diagnostics?.SetNetStatsSource(() =&gt; ...)</c> to add a Network section,
        /// <c>Diagnostics?.AddSection(() =&gt; ...)</c> to compose a section of its own alongside the built-in ones,
        /// or read <see cref="DiagnosticsHud.Visible"/>.
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

        /// <summary>
        /// Turn-key client-side worker pool for multi-core ECS scaling. Built lazily on first read: a shared
        /// <see cref="ThreadPoolJobScheduler"/> sized to <see cref="GameAppOptions.JobSchedulerDegreeOfParallelism"/>
        /// (or <c>Math.Max(1, Environment.ProcessorCount - 1)</c>, leaving one core free for the render/main
        /// thread, when that option is <c>null</c>) - or, when <see cref="GameAppOptions.DisableJobScheduler"/> is
        /// set, the deterministic single-threaded <see cref="SingleThreadedJobScheduler"/> instead. Either way the
        /// property is always non-null and always safe to wire unconditionally:
        /// <c>world.DefaultScheduler = App.JobScheduler;</c> once at startup routes every subsequent no-scheduler
        /// <c>World.ParallelForEach</c> call for that world across cores (or leaves it single-threaded when opted
        /// out), no per-call scheduler plumbing needed. Built once and reused for the app's lifetime.
        /// <see cref="ThreadPoolJobScheduler"/> holds no unmanaged resources, so <see cref="Dispose"/> does not
        /// need to release it. See <c>docs/USING-KHAOZENGINE.md</c> "Client-side parallel ECS" for the
        /// determinism note and more wiring examples.
        /// </summary>
        public IJobScheduler JobScheduler => _jobScheduler ??= CreateJobScheduler(_jobSchedulerDisabled, _jobSchedulerDegreeOfParallelism);

        /// <summary>
        /// Build the scheduler <see cref="JobScheduler"/> lazily caches, from the two
        /// <see cref="GameAppOptions"/> knobs. <paramref name="disabled"/> (<see cref="GameAppOptions.DisableJobScheduler"/>)
        /// wins: it returns a fresh <see cref="SingleThreadedJobScheduler"/> and ignores
        /// <paramref name="degreeOfParallelism"/> entirely. Otherwise a <see cref="ThreadPoolJobScheduler"/> sized
        /// to <paramref name="degreeOfParallelism"/> (<see cref="GameAppOptions.JobSchedulerDegreeOfParallelism"/>)
        /// when positive, else <c>Math.Max(1, Environment.ProcessorCount - 1)</c>. Pure (reads only its
        /// parameters, not process-wide mutable state beyond the processor count), so the sizing/disable
        /// precedence is headless-testable without standing up a window - mirrors
        /// <see cref="BuildDiagnosticsTheme"/>.
        /// </summary>
        internal static IJobScheduler CreateJobScheduler(bool disabled, int? degreeOfParallelism)
        {
            if (disabled) return new SingleThreadedJobScheduler();
            int dop = degreeOfParallelism is > 0
                ? degreeOfParallelism.Value
                : Math.Max(1, Environment.ProcessorCount - 1);
            return new ThreadPoolJobScheduler(dop);
        }

        /// <summary>Load assets / build initial state. Called once before the loop starts.</summary>
        protected virtual void OnLoad() { }
        /// <summary>Per-frame simulation step. <paramref name="dt"/> is the scaled delta (<see cref="Dt"/>).</summary>
        protected virtual void OnUpdate(float dt) { }
        /// <summary>
        /// Fill a world pass's queues for this frame, BEFORE the frame's command list is opened (empty by default).
        /// Runs after <see cref="OnUpdate"/> on the same frame, so a queue filled here carries this frame's state.
        /// <para>
        /// This is the seam for per-frame GPU work that needs a command list of its OWN, which cannot be opened while
        /// the frame's list is recording (<c>GameApp3D</c> queues its 3D draws and runs <c>Scene3D.PrepareFrame</c>
        /// here for exactly that reason - see <see cref="KhaozEngine.Windowing.AppWindow.Run(Action{Frame}, Action{Frame})"/>
        /// and <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>). Do NOT record into
        /// <see cref="Frame.Commands"/> here: the frame's list is not open yet. Draw in <see cref="OnRenderWorld"/>.
        /// </para>
        /// <para>Skipped on a render-suppressed (minimized) frame, exactly like <see cref="OnRenderWorld"/>.</para>
        /// </summary>
        protected virtual void OnPrepareWorld(Frame frame) { }
        /// <summary>
        /// Render a world pass BEFORE the 2D batch each frame (empty by default). A subclass that owns its own
        /// render surface (e.g. a 3D scene) drives it here, and <see cref="GameApp"/> itself stays 2D-only. The frame's
        /// command list is recording by now, so anything that has to run before it opened goes in
        /// <see cref="OnPrepareWorld"/>.
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

        /// <summary>
        /// Background-thread body for the single-instance guard's foreground-request listener (started from
        /// the ctor only when <see cref="GameAppOptions.SingleInstance"/> is set). Polls
        /// <see cref="ISingleInstanceLock.WaitForForegroundRequest"/> in short chunks so it notices
        /// <see cref="_disposed"/> promptly rather than blocking on one long wait; only flips the flag that
        /// <see cref="Run"/> checks on the main thread each frame - it never touches <see cref="_window"/>
        /// itself (GLFW is not thread-safe for that).
        /// </summary>
        void ListenForForegroundRequests()
        {
            ISingleInstanceLock? instanceLock = _singleInstanceLock;
            if (instanceLock is null) return;

            while (!_disposed)
            {
                if (instanceLock.WaitForForegroundRequest(TimeSpan.FromSeconds(1)))
                {
                    _foregroundRequested = true;
                }
            }
        }

        /// <summary>
        /// Run the fixed, correct per-frame ordering until the window closes.
        /// <para>
        /// The body is split across the window's two frame phases (see
        /// <see cref="KhaozEngine.Windowing.AppWindow.Run(Action{Frame}, Action{Frame})"/>): everything up to and
        /// including <see cref="OnPrepareWorld"/> runs BEFORE the frame's command list is opened, and the draw passes
        /// run inside it. The order a game sees is unchanged by that split - <see cref="OnUpdate"/> still precedes
        /// <see cref="OnPrepareWorld"/>, which still precedes <see cref="OnRenderWorld"/> and the 2D passes - but the
        /// world's queues are now filled at a point where a subsystem may still open a command list of its own
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>).
        /// </para>
        /// </summary>
        public void Run()
        {
            OnLoad();
            // Named, because the two parameters are the same delegate type and swapping them would compile and then
            // put every draw pass outside the frame's command list.
            _window.Run(onFrame: RecordPhase, onPrepare: PreparePhase);
        }

        /// <summary>
        /// The frame's PRE-RECORD phase: clock, input, viewports, pointers, <see cref="OnUpdate"/>, the diagnostics
        /// HUD tick and <see cref="OnPrepareWorld"/>. Nothing here records into <see cref="Frame.Commands"/>, and the
        /// frame's command list is not open yet, so a world pass may submit GPU work on a list of its own.
        /// </summary>
        void PreparePhase(Frame frame)
        {
            // A losing second launch (single-instance guard conflict) asked us to come to the foreground.
            // Consume the flag and drive the actual OS focus call here, on the main/window thread - see
            // AppWindow.RequestForeground's thread-safety note.
            if (_foregroundRequested)
            {
                _foregroundRequested = false;
                _window.RequestForeground();
            }

            // Before the clock update, so a frame that loses focus already reports a zero scaled delta.
            // Inert unless GameAppOptions.PauseOnFocusLoss is set, and it only lifts a pause it took itself.
            _focusAutoPause.Update(frame.Input.WindowFocused, _clock);

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
            // BEFORE the world pass, so a 3D subclass can gate this frame's pass timing on its visibility.
            _hud?.Update(_input, frame.Dt);

            // The world's queues, filled with this frame's state and with no command list open.
            OnPrepareWorld(frame);
        }

        /// <summary>
        /// The frame's RECORD phase: the world pass and the 2D / UI / overlay passes, all into the frame's command
        /// list, which is open and cleared by now. A render-suppressed frame skips it entirely.
        /// </summary>
        void RecordPhase(Frame frame)
        {
            if (frame.RenderSuppressed) return;

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

            // Stop the foreground-request listener (it notices within its 1s poll chunk, IsBackground so it
            // never blocks process exit either way) and release the single-instance key promptly so a fresh
            // launch right after this one (e.g. AppRelaunch.Restart) does not need to wait out the
            // predecessor-wait timeout to acquire it.
            _disposed = true;
            _singleInstanceLock?.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
