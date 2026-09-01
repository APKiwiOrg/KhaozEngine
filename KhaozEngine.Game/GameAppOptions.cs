using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Construction options for a <see cref="GameApp"/>: window title/size, the fixed design space (0 size =
    /// 1:1 with the window), the design <see cref="ScaleMode"/>, and the per-frame clear colour. Use
    /// <see cref="For"/> for sensible defaults, then tweak the fields you need.
    /// </summary>
    public struct GameAppOptions
    {
        /// <summary>Window title.</summary>
        public string Title;
        /// <summary>Window width in points.</summary>
        public int Width;
        /// <summary>Window height in points.</summary>
        public int Height;
        /// <summary>Design-space width; 0 uses <see cref="Width"/> (1:1 design space).</summary>
        public int DesignWidth;
        /// <summary>Design-space height; 0 uses <see cref="Height"/> (1:1 design space).</summary>
        public int DesignHeight;
        /// <summary>How the design space maps onto the window (default <see cref="ScaleMode.Fit"/>).</summary>
        public ScaleMode ScaleMode;
        /// <summary>Background colour cleared each frame (default dark).</summary>
        public Color ClearColor;

        /// <summary>
        /// How the window presents frames (default <see cref="PresentMode.Vsync"/>). <see cref="PresentMode.Immediate"/>
        /// disables vertical-blank sync for the lowest latency / uncapped fps. Applied at window creation, so it is
        /// honoured on the default window; a custom <see cref="WindowFactory"/> must forward it itself. See
        /// <see cref="FrameCapHz"/> to also pin the rate (vsync alone does not reliably cap on Mac/Metal).
        /// </summary>
        public PresentMode PresentMode;

        /// <summary>
        /// Optional EXPLICIT software frame-rate cap in Hz. A positive value OVERRIDES <see cref="FrameCap"/> with a
        /// fixed cap. The default 0 leaves <see cref="FrameCap"/> in charge (which defaults to the backend-aware
        /// <see cref="Windowing.FrameCap.Auto"/>). When a cap is in force, <see cref="AppWindow.Run(System.Action{Frame})"/> paces the loop
        /// to it with a monotonic-clock limiter (<see cref="FrameLimiter"/>) independent of the swapchain's vsync - so
        /// a game can pin the render rate to an integer multiple of its fixed tick (e.g. 60 or 120 for a 30 Hz tick).
        /// Applied on both the default and a custom <see cref="WindowFactory"/> window (set post-construction), so a
        /// factory need not forward it. To free-run intentionally (the old default), set <see cref="FrameCap"/> to
        /// <see cref="Windowing.FrameCap.Uncapped"/> - a 0 here no longer means uncapped, it means "use FrameCap".
        /// </summary>
        public int FrameCapHz;

        /// <summary>
        /// The frame-cap intent when <see cref="FrameCapHz"/> is 0 (the default): <see cref="Windowing.FrameCap.Auto"/>
        /// (the default - backend-aware: a real cap on Metal + vsync where the CPU otherwise free-runs, uncapped on
        /// D3D11/Vulkan where vsync throttles), <see cref="Windowing.FrameCap.Uncapped"/> (intentional free-run), or a
        /// fixed <see cref="Windowing.FrameCap.Hz"/>. The zero value of this field is <see cref="Windowing.FrameCap.Auto"/>,
        /// so a default-constructed options struct opts into the backend-aware cap rather than free-running. A positive
        /// <see cref="FrameCapHz"/> wins over this.
        /// </summary>
        public FrameCap FrameCap;

        /// <summary>
        /// How the loop throttles the window while it is backgrounded (unfocused / minimized). <c>null</c> (the
        /// default) uses <see cref="BackgroundThrottlePolicy.Default"/> (ON): a minimized window skips render + present
        /// and idles while its update keeps running, and an unfocused-but-visible window drops to a low frame cap. Set
        /// <see cref="BackgroundThrottlePolicy.Disabled"/> to keep rendering full-rate in the background (a live
        /// wallpaper / capture source), or a custom policy. Applied post-construction on both the default and a custom
        /// <see cref="WindowFactory"/> window.
        /// </summary>
        public BackgroundThrottlePolicy? BackgroundThrottle;

        /// <summary>
        /// How the window initially occupies the display (default <see cref="WindowMode.Windowed"/>). Applied
        /// post-construction on both the default and a custom <see cref="WindowFactory"/> window (the window is born
        /// windowed, then switched), so a factory need not forward it. Change it live via
        /// <see cref="GameApp.WindowMode"/> / <see cref="GameApp.Display"/>.
        /// </summary>
        public WindowMode WindowMode;

        /// <summary>
        /// A frame whose wall-clock gap (<see cref="GameClock.RealWallGapSeconds"/>) exceeds this raises
        /// <see cref="GameApp.OnResume"/> - the signal that the OS slept/suspended/hibernated or the app hung for
        /// that long. Default 30s (via <see cref="For"/>), high enough that a normal frame, GC pause, or brief
        /// stall never trips it. 0 or negative disables the hook.
        /// </summary>
        public double ResumeGapThresholdSeconds;

        /// <summary>
        /// Optional: build the window. Default (null) is <c>new AppWindow(Title, Width, Height)</c>. Set it to use
        /// a different policy, e.g. <c>o =&gt; AppWindow.Scaled(o.Title, o.Width, o.Height, 0.87f)</c> for a
        /// display-fitted window. <see cref="GameApp"/> sets <see cref="ClearColor"/> on the result.
        /// </summary>
        public Func<GameAppOptions, AppWindow>? WindowFactory;

        /// <summary>
        /// Optional: build the design viewport. Default (null) is
        /// <c>new DesignViewport(ResolvedDesignWidth, ResolvedDesignHeight, ScaleMode)</c>. Set it for a different
        /// policy, e.g. <c>o =&gt; new AdaptiveViewport(o.DesignWidth, o.DesignHeight)</c> for a responsive,
        /// no-letterbox viewport.
        /// </summary>
        public Func<GameAppOptions, IDesignViewport>? ViewportFactory;

        /// <summary>
        /// Optional path to a PNG decoded (via <see cref="ImageRgba"/>) into the runtime window/taskbar icon.
        /// A convenience for the common single-image case; ignored when <see cref="WindowIcons"/> is set.
        /// macOS ignores window icons (the .app bundle icns owns the Dock icon) - see <see cref="AppWindow.SetIcon"/>.
        /// </summary>
        public string? WindowIconPath;

        /// <summary>
        /// Optional explicit, already-decoded icon images (e.g. 16/32/48 px) for GLFW to pick from per DPI. Takes
        /// priority over <see cref="WindowIconPath"/>. Each is mapped to a <see cref="WindowIcon"/> and applied via
        /// <see cref="AppWindow.SetIcon"/> (Windows/Linux runtime icon; no-op on macOS).
        /// </summary>
        public IReadOnlyList<ImageRgba>? WindowIcons;

        /// <summary>
        /// Optional Windows taskbar identity (AppUserModelID) for the process, e.g. <c>"APKiwi.Nullwake"</c>
        /// (a dotted <c>CompanyName.ProductName</c> by convention). When set, <see cref="GameApp"/> calls
        /// <see cref="AppWindow.TrySetProcessAppUserModelId"/> BEFORE creating the window so Windows 10/11 keys the
        /// taskbar button to the app - fixing the running app's taskbar icon (which otherwise shows the generic
        /// <c>.exe</c> placeholder even though the title bar and Explorer icons are correct) and stabilising
        /// grouping/pinning. Null (the default) keeps the current process-derived identity. No-op off Windows.
        /// </summary>
        public string? AppUserModelId;

        /// <summary>
        /// Opt OUT of the automatic parent-console attach (default <c>false</c>, i.e. the attach is ON). A game head
        /// built as a Windows <c>WinExe</c> (Windows-subsystem, so no stray console window opens behind it) has no
        /// console, so <see cref="GameApp"/> attaches the process to the parent terminal's console when launched
        /// from one (<c>dotnet run</c> / cmd / PowerShell), keeping developer-visible <c>Console.Write*</c> output.
        /// The attach is already a no-op off Windows, for a console-subsystem exe, on a normal Explorer/Start launch
        /// (no parent console), and when output is redirected (CI/pipes are respected) - so leave this
        /// <c>false</c> unless a head must never touch the parent console. The field is inverted (a suppress flag)
        /// so the default-zero struct value keeps the attach on, whether options are built with <see cref="For"/> or
        /// a raw <c>new GameAppOptions { ... }</c>. See <see cref="AppWindow.TryAttachParentConsole"/>.
        /// </summary>
        public bool SuppressParentConsoleAttach;

        /// <summary>
        /// Opt OUT of the automatic last-chance crash file (default <c>false</c>, i.e. the file is ON). By
        /// default <see cref="GameApp"/> arms <see cref="KhaozEngine.Diagnostics.CrashReport"/> for the head, so an unhandled exception
        /// is written with its type, message, stack, the engine version and the graphics backend to a file in
        /// the OS crash location (<c>~/Library/Logs/KhaozEngine</c> on macOS,
        /// <c>%LOCALAPPDATA%\KhaozEngine\crash</c> on Windows), whether or not the game configured any logging.
        /// It costs nothing until a crash happens and writes nowhere else, so leave this <c>false</c> unless the
        /// head installs its own process-level crash handling. The field is inverted (a suppress flag) so the
        /// default-zero struct value keeps the file on.
        /// <para>
        /// A HEAD THAT ARMED ITS OWN DOES NOT NEED THIS FLAG. <see cref="GameApp"/> arms only when
        /// <see cref="KhaozEngine.Diagnostics.CrashReport"/> is not already installed, so an earlier
        /// <c>CrashReport.Install</c> (with its own directory, label and retention) survives: first wins, and
        /// arming is idempotent by REPLACEMENT, so the alternative was silently discarding that configuration.
        /// The flag is for a head that wants no arming at all. See
        /// <see cref="KhaozEngine.Diagnostics.CrashReport"/>.
        /// </para>
        /// </summary>
        public bool SuppressCrashReportFile;

        /// <summary>
        /// Opt OUT of the built-in diagnostics HUD (default <c>false</c>, i.e. the HUD is ON). By default
        /// <see cref="GameApp"/> wires a <see cref="KhaozEngine.Gui.DiagnosticsHud"/> (FPS / frame-ms / heap,
        /// draw-call + triangle counters, and - for a 3D app - per-pass CPU-encode timings), hidden until toggled
        /// with <see cref="DiagnosticsToggleKey"/>. While hidden its only cost is the always-on render counters. The
        /// field is inverted (a disable flag) so the default-zero struct value keeps the HUD on, whether options are
        /// built with <see cref="For"/> or a raw <c>new GameAppOptions { ... }</c>.
        /// </summary>
        public bool DisableDiagnosticsOverlay;

        /// <summary>
        /// The key that shows/hides the built-in diagnostics HUD. <c>null</c> (the default) uses <see cref="Key.F1"/>.
        /// Ignored when <see cref="DisableDiagnosticsOverlay"/> is set. Being nullable, a default-constructed struct
        /// and a <see cref="For"/> result both resolve to F1, and a caller sets a specific key to override.
        /// </summary>
        public Key? DiagnosticsToggleKey;

        /// <summary>
        /// Start the built-in diagnostics HUD SHOWN rather than hidden (default <c>false</c>, the behaviour every
        /// build has had). <see cref="DiagnosticsToggleKey"/> still hides it from there. This exists for a build
        /// whose tester is asked to read a value off the HUD, where "press F1 first" is one instruction the
        /// handoff loses. Ignored when <see cref="DisableDiagnosticsOverlay"/> is set, and being a plain bool the
        /// default-zero struct keeps the HUD hidden.
        /// </summary>
        public bool DiagnosticsVisibleAtBoot;

        /// <summary>
        /// Opt OUT of the turn-key client-side job scheduler (default <c>false</c>, i.e. it is ON). By default
        /// <see cref="GameApp.JobScheduler"/> lazily builds a shared
        /// <see cref="KhaozEngine.Simulation.ThreadPoolJobScheduler"/>, sized to
        /// <see cref="JobSchedulerDegreeOfParallelism"/> (or <c>Environment.ProcessorCount - 1</c>, floor 1, when
        /// that is <c>null</c>), the first time a game reads <see cref="GameApp.JobScheduler"/>. Wire it into an
        /// ECS world with <c>world.DefaultScheduler = App.JobScheduler;</c> so every no-scheduler
        /// <c>World.ParallelForEach</c> call fans across cores with no other change - see
        /// <c>docs/USING-KHAOZENGINE.md</c> "Client-side parallel ECS". Setting this <c>true</c> makes
        /// <see cref="GameApp.JobScheduler"/> resolve to the deterministic single-threaded scheduler instead (the
        /// same scheduler <c>World.DefaultScheduler</c> already defaults to), so the one-line wiring stays safe to
        /// use unconditionally even when a game opts out. The field is inverted (a disable flag) so the
        /// default-zero struct value keeps the scheduler on, whether options are built with <see cref="For"/> or a
        /// raw <c>new GameAppOptions { ... }</c>.
        /// </summary>
        public bool DisableJobScheduler;

        /// <summary>
        /// The player's STORED graphics-backend choice, or <c>null</c> (the default) to let the engine decide.
        /// This is the in-game backend setting: the game reads it from its own settings store and hands it over
        /// as data, because <c>KhaozEngine.Gpu</c> does no file IO and must not take a settings dependency.
        /// <para>Precedence, highest first: the <c>KE_GRAPHICS_BACKEND</c> environment override (the debug lever,
        /// which always wins), then this, then the OS probe. Applied at window creation, so it is honoured on the
        /// default window; a custom <see cref="WindowFactory"/> must forward it itself.</para>
        /// <para>A preference the machine cannot actually create a device on falls back to the platform's default
        /// backend rather than failing to boot. Read <see cref="GameApp.Window"/>'s <c>BackendSelection</c> after startup:
        /// a <c>Source</c> of <see cref="KhaozEngine.Gpu.GpuBackendSource.FallbackAfterFailure"/> means the stored
        /// preference did not work and the game MUST clear it, or the player retries the same broken choice on
        /// every launch. Offer only <c>GpuBackendSelector.SupportedBackends()</c> in the settings UI.</para>
        /// </summary>
        public KhaozEngine.Gpu.GpuBackendKind? GraphicsBackendPreference;

        /// <summary>
        /// Optional explicit worker cap for <see cref="GameApp.JobScheduler"/>. <c>null</c> (the default) sizes it
        /// to <c>Math.Max(1, Environment.ProcessorCount - 1)</c> (leaves one core free for the render/main thread).
        /// Ignored when <see cref="DisableJobScheduler"/> is set. Must be a positive value when set - forwarded
        /// as-is to <see cref="KhaozEngine.Simulation.ThreadPoolJobScheduler"/>'s constructor, which throws on
        /// anything else.
        /// </summary>
        public int? JobSchedulerDegreeOfParallelism;

        /// <summary>
        /// Opt IN to a single-instance guard (default <c>false</c> = multiple instances allowed, the historic
        /// behaviour). When true, <see cref="GameApp"/> claims a named OS mutex at the very top of its
        /// constructor - BEFORE any window or GPU device is created - keyed by <see cref="SingleInstanceId"/>
        /// (falling back to <see cref="AppUserModelId"/> when that is null). If a live instance already holds
        /// the key, this process asks it to come to the foreground, logs one line, and exits cleanly (code 0)
        /// without ever constructing a window. See <c>KhaozEngine.App.SingleInstanceGuard</c> for the
        /// mechanism, including how it composes with a forced <c>AppRelaunch.Restart</c> and with the
        /// auto-updater's post-update relaunch. Setting this true with both <see cref="SingleInstanceId"/> and
        /// <see cref="AppUserModelId"/> null throws at construction (there is nothing safe to key the guard on).
        /// </summary>
        public bool SingleInstance;

        /// <summary>
        /// Optional explicit key for the single-instance guard (see <see cref="SingleInstance"/>). Falls back
        /// to <see cref="AppUserModelId"/> when null. Set this to opt into single-instance without also taking
        /// the Windows taskbar AppUserModelId behaviour, or to use a key distinct from it (e.g. to separate a
        /// dev build from a release build that would otherwise share an AppUserModelId).
        /// </summary>
        public string? SingleInstanceId;

        /// <summary>Resolved design width: <see cref="DesignWidth"/>, or <see cref="Width"/> when it is 0.</summary>
        internal int ResolvedDesignWidth => DesignWidth == 0 ? Width : DesignWidth;
        /// <summary>Resolved design height: <see cref="DesignHeight"/>, or <see cref="Height"/> when it is 0.</summary>
        internal int ResolvedDesignHeight => DesignHeight == 0 ? Height : DesignHeight;

        /// <summary>Sensible defaults: Fit scaling, 1:1 design space, dark clear colour, 30s resume-gap threshold,
        /// vsync present, the backend-aware <see cref="Windowing.FrameCap.Auto"/> cap, the ON background-throttle
        /// policy (default), windowed.</summary>
        public static GameAppOptions For(string title, int width, int height) => new()
        {
            Title = title,
            Width = width,
            Height = height,
            DesignWidth = 0,
            DesignHeight = 0,
            ScaleMode = ScaleMode.Fit,
            ClearColor = new Color(0.10f, 0.12f, 0.16f, 1f),
            ResumeGapThresholdSeconds = 30.0,
            PresentMode = PresentMode.Vsync,
            FrameCapHz = 0,
            FrameCap = FrameCap.Auto,
            BackgroundThrottle = null, // null = BackgroundThrottlePolicy.Default (ON)
            WindowMode = WindowMode.Windowed,
        };
    }
}
