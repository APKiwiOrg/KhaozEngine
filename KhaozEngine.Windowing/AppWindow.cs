using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Platform;
using Silk.NET.Core;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Glfw;
using KhaozEngine.Gpu;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;
// GLFW interop for key auto-repeat. Targeted aliases instead of `using Silk.NET.GLFW;` to avoid clashing
// with Silk.NET.Windowing's Monitor / the engine's own MouseButton.
using GlfwInputAction = Silk.NET.GLFW.InputAction;

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
    public sealed class AppWindow : IDisposable, IDisplaySettings
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
        // Keys that fired a GLFW REPEAT this frame (held past the OS repeat delay). Silk's high-level keyboard drops
        // REPEAT, so we capture it off the raw GLFW key callback; snapshotted + cleared per frame like _pressed.
        readonly HashSet<Key> _repeated = new();
        // The chained GLFW key callback (held so the native delegate isn't GC'd) and Silk's previous callback we
        // re-invoke from it so the high-level KeyDown/KeyUp keep firing. See WireKeyRepeat.
        Silk.NET.GLFW.GlfwCallbacks.KeyCallback? _keyCallback;
        Silk.NET.GLFW.GlfwCallbacks.KeyCallback? _prevKeyCallback;
        readonly HashSet<MouseButton> _mouseDown = new();
        readonly HashSet<MouseButton> _mousePressed = new();
        readonly SilkGamepadReader _gamepads = new();
        Vector2 _lastMouse;
        float _wheelAccum;
        bool _focused = true;   // windows open focused; Silk's FocusChanged keeps this in sync.

        readonly int _maxFrames;
        int _frameCount;
        bool _shown;   // the window is born hidden (see the ctor); Show() reveals it exactly once.

        /// <summary>The engine-owned GPU device (renderers consume this; backend GPU types stay hidden).</summary>
        public IGpuDevice GpuDevice => _device;
        /// <summary>The selected graphics backend (centralized via <see cref="GpuBackendSelector"/>).</summary>
        public GpuBackendKind Backend => _gpu.Backend;
        /// <summary>Clip-space / depth conventions of the live device (see <see cref="GpuCapabilities"/>).</summary>
        public GpuCapabilities Capabilities => _gpu.Capabilities;
        /// <summary>Physical framebuffer (drawable) size in pixels - the actual resolution the swapchain renders at
        /// (e.g. 2x the logical size on a HiDPI/Retina display). This is the 3D scene's real render resolution;
        /// if it is below the monitor's native pixels, the OS is upscaling the window.</summary>
        public int FramebufferWidth => _window.FramebufferSize.X;
        public int FramebufferHeight => _window.FramebufferSize.Y;
        /// <summary>Logical window size in points (physical size / DPI scale). FramebufferWidth / LogicalWidth is the
        /// DPI scale factor (1.0 = no HiDPI scaling, 2.0 = Retina).</summary>
        public int LogicalWidth => _window.Size.X;
        public int LogicalHeight => _window.Size.Y;
        /// <summary>Background colour cleared each frame.</summary>
        public Color ClearColor = new(0.10f, 0.12f, 0.16f, 1f);

        // Optional software frame-rate cap for Run(): 0 = uncapped. Rebuilt when FrameCapHz is set.
        FrameLimiter _frameLimiter;
        int _frameCapHz;
        // Runtime display state. PresentMode/WindowMode start from the ctor; _windowedSize is the size to restore
        // when leaving a fullscreen mode; _windowedPos is the windowed position to restore (null = leave it where
        // the OS put it, until the first MoveTo); _warnedMetalVsync dedups the one-time Metal-vsync-needs-a-cap warning.
        PresentMode _presentMode;
        WindowMode _windowMode = WindowMode.Windowed;
        Vector2D<int> _windowedSize;
        Vector2D<int>? _windowedPos;
        bool _warnedMetalVsync;

        /// <summary>
        /// Software frame-rate cap in Hz for <see cref="Run"/> (0 = uncapped, the default). Paces the loop with a
        /// monotonic-clock <see cref="FrameLimiter"/> independent of the swapchain's vsync, so a game can pin the
        /// render rate to an integer multiple of its fixed tick (e.g. 60/120 for a 30 Hz tick) - the deterministic cap
        /// where vsync does not throttle (notably the Veldrid Metal path). Settable any time; takes effect next frame.
        /// </summary>
        public int FrameCapHz
        {
            get => _frameCapHz;
            set { _frameCapHz = value; _frameLimiter = new FrameLimiter(value); WarnIfMetalVsyncUncapped(); }
        }

        /// <summary>
        /// How the window presents finished frames (<see cref="Windowing.PresentMode.Vsync"/> /
        /// <see cref="Windowing.PresentMode.Immediate"/>). Settable at runtime: the setter reconfigures the live
        /// swapchain's <c>SyncToVerticalBlank</c> in place (no recreate, no leaked swapchain, size + depth preserved),
        /// so a game can flip vsync mid-session. On Metal it engages the layer's vsync but does not by itself cap the
        /// CPU frame rate - pair vsync with a <see cref="FrameCapHz"/> (the setter warns once if you do not).
        /// </summary>
        public PresentMode PresentMode
        {
            get => _presentMode;
            set
            {
                _presentMode = value;
                _device.SyncToVerticalBlank = value == PresentMode.Vsync;
                WarnIfMetalVsyncUncapped();
            }
        }

        /// <summary>
        /// How the window occupies the display (<see cref="Windowing.WindowMode.Windowed"/> /
        /// <see cref="Windowing.WindowMode.BorderlessFullscreen"/> /
        /// <see cref="Windowing.WindowMode.ExclusiveFullscreen"/>). Settable at runtime: the setter drives the Silk
        /// window's state / border / geometry (policy computed by <see cref="WindowModePlanner"/>) and the swapchain
        /// follows the new framebuffer size via the existing <c>FramebufferResize</c> hook. The HiDPI framebuffer
        /// semantics are unchanged (the swapchain is always sized to the physical drawable, not the logical size).
        /// </summary>
        public WindowMode WindowMode
        {
            get => _windowMode;
            set => ApplyWindowMode(value);
        }

        /// <summary>Current logical window width in points (see <see cref="LogicalWidth"/>).</summary>
        public int WindowWidth => _window.Size.X;
        /// <summary>Current logical window height in points (see <see cref="LogicalHeight"/>).</summary>
        public int WindowHeight => _window.Size.Y;

        /// <summary>
        /// Set the windowed size in logical points. In <see cref="Windowing.WindowMode.Windowed"/> it applies
        /// immediately (Silk resizes the window and the <c>FramebufferResize</c> hook resizes the swapchain to the new
        /// drawable); in a fullscreen mode it is stored as the size to restore when returning to windowed. Non-positive
        /// sizes are ignored. HiDPI is preserved: the backbuffer tracks the physical framebuffer, not this logical size.
        /// </summary>
        public void Resize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            _windowedSize = new Vector2D<int>(width, height);
            if (_windowMode == WindowMode.Windowed)
                _window.Size = _windowedSize; // FramebufferResize -> ResizeSwapchain keeps the swapchain in step.
        }

        /// <summary>Current window top-left X in virtual-desktop (screen) coordinates.</summary>
        public int WindowX => _window.Position.X;
        /// <summary>Current window top-left Y in virtual-desktop (screen) coordinates.</summary>
        public int WindowY => _window.Position.Y;

        /// <summary>Move the window top-left to (<paramref name="x"/>, <paramref name="y"/>) in virtual-desktop
        /// coordinates. Applied immediately when windowed; in a fullscreen mode it is remembered as the windowed
        /// position to restore when returning to windowed (symmetric with <see cref="Resize"/>).</summary>
        public void MoveTo(int x, int y)
        {
            _windowedPos = new Vector2D<int>(x, y);
            if (_windowMode == WindowMode.Windowed) _window.Position = _windowedPos.Value;
        }

        /// <summary>The connected monitors (index, name, bounds in window coordinates); empty on headless / no display.</summary>
        public IReadOnlyList<MonitorInfo> Monitors => EnumerateMonitors();

        /// <summary>Index into <see cref="Monitors"/> of the monitor currently holding the window, or -1 when unknown.</summary>
        public int CurrentMonitorIndex
            => WindowPlacement.MonitorIndexFor(WindowX, WindowY, WindowWidth, WindowHeight, Monitors);

        /// <summary>Place the window on the monitor at <paramref name="index"/> into <see cref="Monitors"/>: centred
        /// when windowed, re-covering the monitor when borderless fullscreen. Out-of-range indices are ignored.</summary>
        public void MoveToMonitor(int index)
        {
            IReadOnlyList<MonitorInfo> monitors = Monitors;
            if (index < 0 || index >= monitors.Count) return;
            MonitorInfo m = monitors[index];
            var (x, y) = WindowPlacement.CenterOn(m, WindowWidth, WindowHeight);
            if (_windowMode == WindowMode.BorderlessFullscreen)
            {
                // Cover the chosen monitor directly from its bounds (no _window.Monitor pinning needed).
                RealizePlan(WindowModePlanner.Compute(WindowMode.BorderlessFullscreen,
                    m.X, m.Y, m.Width, m.Height, _windowedSize.X, _windowedSize.Y));
                _windowedPos = new Vector2D<int>(x, y); // remembered for a later return to windowed
            }
            else MoveTo(x, y);
        }

        /// <summary>Clamp the window back on-screen (e.g. after restoring a saved position whose monitor is gone).
        /// A no-op when the window is already adequately visible.</summary>
        public void EnsureVisible()
        {
            var (x, y) = WindowPlacement.ClampVisible(WindowX, WindowY, WindowWidth, WindowHeight, Monitors);
            MoveTo(x, y);
        }

        /// <summary>Build the <see cref="MonitorInfo"/> list from Silk's monitor enumeration (index, name, bounds in
        /// window coordinates). Empty on headless / no display (same try-guard style as <see cref="CurrentMonitorBounds"/>).
        /// AppWindow is the only class that touches the Silk monitor statics; the placement math is pure in
        /// <see cref="WindowPlacement"/>.</summary>
        IReadOnlyList<MonitorInfo> EnumerateMonitors()
        {
            var list = new List<MonitorInfo>();
            try
            {
                int i = 0;
                foreach (IMonitor m in Monitor.GetMonitors(_window))
                {
                    var b = m.Bounds;
                    list.Add(new MonitorInfo(i, m.Name ?? $"Monitor {i}", b.Origin.X, b.Origin.Y, b.Size.X, b.Size.Y));
                    i++;
                }
            }
            catch
            {
                list.Clear();
            }
            return list;
        }

        /// <summary>A snapshot of the current runtime display state, including window position.</summary>
        public DisplaySettings CurrentDisplay =>
            new(_presentMode, _frameCapHz, _windowMode, WindowWidth, WindowHeight, WindowX, WindowY);

        /// <summary>
        /// Apply a whole <see cref="DisplaySettings"/> snapshot mid-session (a settings-screen "Apply"): window mode
        /// first, then resolution, then placement (clamp on-screen + move, when the snapshot carries a position), then
        /// frame cap, then present mode (so the Metal vsync/cap warning reflects the final cap). Every step is
        /// individually safe at any time; no swapchain is recreated for the present-mode change and none is leaked for
        /// the resolution change.
        /// </summary>
        public void ApplyDisplay(in DisplaySettings settings)
        {
            if (settings.WindowMode != _windowMode) ApplyWindowMode(settings.WindowMode);
            if (settings.Width > 0 && settings.Height > 0) Resize(settings.Width, settings.Height);
            if (settings.HasPosition)
            {
                // Clamp against the final size so a stale saved position on a now-gone monitor self-corrects.
                var (x, y) = WindowPlacement.ClampVisible(settings.X, settings.Y, WindowWidth, WindowHeight, Monitors);
                MoveTo(x, y);
            }
            FrameCapHz = settings.FrameCapHz;
            PresentMode = settings.PresentMode;
        }

        /// <summary>Drive the Silk window into <paramref name="mode"/> using the pure <see cref="WindowModePlanner"/>
        /// policy: set the border, then the state (enter/leave fullscreen), then the geometry the plan asks for. For
        /// exclusive fullscreen the monitor is pinned first so the OS gives this window a display.</summary>
        void ApplyWindowMode(WindowMode mode)
        {
            var (mx, my, mw, mh) = CurrentMonitorBounds();
            WindowModePlan plan = WindowModePlanner.Compute(mode, mx, my, mw, mh,
                _windowedSize.X, _windowedSize.Y,
                _windowedPos.HasValue, _windowedPos?.X ?? 0, _windowedPos?.Y ?? 0);

            if (mode == WindowMode.ExclusiveFullscreen)
                _window.Monitor ??= Monitor.GetMainMonitor(_window);

            RealizePlan(plan);
            _windowMode = mode;
        }

        /// <summary>Write a <see cref="WindowModePlan"/> onto the Silk window: border, then state (enter/leave
        /// fullscreen), then the size / position the plan gates. Shared by <see cref="ApplyWindowMode"/> and
        /// <see cref="MoveToMonitor"/> (which computes a borderless plan for a specific monitor's bounds).</summary>
        void RealizePlan(WindowModePlan plan)
        {
            _window.WindowBorder = plan.Border == WindowBorderTarget.Hidden ? WindowBorder.Hidden : WindowBorder.Resizable;
            _window.WindowState = plan.State == WindowStateTarget.Fullscreen ? WindowState.Fullscreen : WindowState.Normal;
            if (plan.SetSize) _window.Size = new Vector2D<int>(plan.Width, plan.Height);
            if (plan.SetPosition) _window.Position = new Vector2D<int>(plan.X, plan.Y);
        }

        /// <summary>The bounds of the window's current monitor (or the primary monitor when the window has none, i.e.
        /// windowed), in window coordinates; (0,0,0,0) when it cannot be determined (headless / no display).</summary>
        (int X, int Y, int W, int H) CurrentMonitorBounds()
        {
            try
            {
                IMonitor? m = _window.Monitor ?? Monitor.GetMainMonitor(_window);
                if (m == null) return (0, 0, 0, 0);
                var b = m.Bounds;
                return (b.Origin.X, b.Origin.Y, b.Size.X, b.Size.Y);
            }
            catch
            {
                return (0, 0, 0, 0);
            }
        }

        /// <summary>Emit a one-time warning when vsync is selected with no frame cap on Metal, where the Veldrid Metal
        /// present does not throttle the CPU from vsync alone. Pure decision via
        /// <see cref="DisplaySettings.RequiresFrameCapWarning"/>; written to <c>Console.Error</c> so a bare AppWindow
        /// host (no logger) still surfaces it. Deduped so it never spams a settings screen.</summary>
        void WarnIfMetalVsyncUncapped()
        {
            if (_warnedMetalVsync) return;
            if (!DisplaySettings.RequiresFrameCapWarning(Backend, _presentMode, _frameCapHz)) return;
            _warnedMetalVsync = true;
            Console.Error.WriteLine(
                "[KhaozEngine] PresentMode.Vsync does not reliably cap the frame rate on Metal (the Veldrid Metal " +
                "present does not throttle the CPU). Set FrameCapHz (e.g. your tick rate x2, like 60 or 120) for a " +
                "deterministic cap on macOS.");
        }

        /// <summary>Create a window with vsync present and no frame cap (the historical defaults).</summary>
        public AppWindow(string title, int width, int height)
            : this(title, width, height, PresentMode.Vsync, frameCapHz: 0) { }

        /// <summary>
        /// Create a window selecting how it presents (<paramref name="presentMode"/>) and an optional software frame
        /// cap (<paramref name="frameCapHz"/>, 0 = uncapped). <paramref name="presentMode"/> feeds the swapchain's
        /// vsync at creation time; <paramref name="frameCapHz"/> paces <see cref="Run"/> and can also be changed later
        /// via <see cref="FrameCapHz"/>.
        /// </summary>
        public AppWindow(string title, int width, int height, PresentMode presentMode, int frameCapHz = 0)
        {
            _frameCapHz = frameCapHz;
            _frameLimiter = new FrameLimiter(frameCapHz);
            _presentMode = presentMode;
            _windowedSize = new Vector2D<int>(width, height);
            // KE_MAX_FRAMES: render N frames then close (lets a windowed smoke test run a few frames + exit cleanly).
            _maxFrames = int.TryParse(Environment.GetEnvironmentVariable("KE_MAX_FRAMES"), out int mf) && mf > 0 ? mf : 0;

            GlfwWindowing.RegisterPlatform();
            var opts = WindowOptions.Default with
            {
                Size = new Vector2D<int>(width, height),
                Title = title,
                // We drive the GPU ourselves via Veldrid; Silk must not create a GL/Vulkan context.
                API = GraphicsAPI.None,
                // Born HIDDEN. On Windows the taskbar button is created when the window is first shown and is
                // keyed to whatever icon the window has at that instant; if we show it before SetIcon runs, the
                // button is stuck with GLFW's generic default (WM_SETICON later refreshes the title bar but not
                // an already-created taskbar button). So the host applies the runtime icon while hidden, then
                // calls Show() - GameApp does this automatically. Run() also shows, so a bare AppWindow host that
                // never calls Show() still gets a visible window.
                IsVisible = false,
            };
            _window = Window.Create(opts);
            _window.Initialize(); // creates the native window WITHOUT starting the loop; the handle is valid after this.

            GpuWindowHandle handle = BuildHandle(_window);
            _gpu = GpuDeviceContext.CreateForWindow(handle, (uint)width, (uint)height,
                syncToVerticalBlank: presentMode == PresentMode.Vsync);
            _device = _gpu.GpuDevice;
            _cl = _device.Factory.CreateCommandList();

            // Resize the swapchain to the drawable (framebuffer) size, not the logical window size.
            _window.FramebufferResize += s => _device.ResizeSwapchain((uint)s.X, (uint)s.Y);

            _input = _window.CreateInput();
            WireInput();

            // Wire the GLFW text clipboard into Platform.Clipboard. Platform is BCL-only and can't reference
            // Silk, so AppWindow (which owns the GLFW window) registers the provider; this is what makes text
            // get/set work on Windows and Linux, and is the primary text path on macOS too. Capture the native
            // GLFW handle by value (no `this` capture); Dispose clears the provider before GLFW is torn down.
            nint glfwWindow = _window.Native?.Glfw ?? 0;
            if (glfwWindow != 0)
            {
                Clipboard.RegisterTextProvider(
                    () => GlfwClipboard.ReadText(glfwWindow),
                    text => GlfwClipboard.WriteText(glfwWindow, text));
            }

            _lastMouse = Vector2.Zero;
        }

        /// <summary>
        /// Open a window for a fixed design resolution, sized up to fill the display. The window opens at the
        /// largest multiple of (<paramref name="designWidth"/> x <paramref name="designHeight"/>) that preserves the
        /// design aspect and fits within <paramref name="screenFraction"/> of the primary monitor's work area,
        /// clamped to [1, <paramref name="maxScale"/>]. A small-tall (portrait) design on a desktop monitor thus
        /// opens large enough to read instead of at life-size; pair with a <c>DesignViewport</c> (Fit) so the whole
        /// UI scales uniformly. Never opens smaller than the design size, and falls back to it if the monitor size
        /// is unavailable.
        /// </summary>
        public static AppWindow Scaled(string title, int designWidth, int designHeight,
            float screenFraction = 0.9f, float maxScale = 2f,
            PresentMode presentMode = PresentMode.Vsync, int frameCapHz = 0)
        {
            GlfwWindowing.RegisterPlatform();
            var (sw, sh) = PrimaryScreenSize();
            var (w, h) = FitToScreen(designWidth, designHeight, sw, sh, screenFraction, maxScale);
            return new AppWindow(title, w, h, presentMode, frameCapHz);
        }

        /// <summary>
        /// Pure window-sizing policy (no monitor / GPU access, so it is unit-testable): the largest size with the
        /// design's aspect ratio that fits within <paramref name="screenFraction"/> of a
        /// <paramref name="screenWidth"/> x <paramref name="screenHeight"/> display, expressed as a uniform scale of
        /// the design clamped to [1, <paramref name="maxScale"/>]. Returns the design size unchanged when the screen
        /// size is unknown (&lt;= 0) or too small to grow into.
        /// </summary>
        public static (int Width, int Height) FitToScreen(int designWidth, int designHeight,
            int screenWidth, int screenHeight, float screenFraction = 0.9f, float maxScale = 2f)
        {
            if (designWidth <= 0 || designHeight <= 0) return (designWidth, designHeight);
            if (screenWidth <= 0 || screenHeight <= 0) return (designWidth, designHeight);

            float availW = screenWidth * screenFraction;
            float availH = screenHeight * screenFraction;
            // Uniform scale that keeps the design fully inside the available area on both axes.
            float scale = ViewportMath.Fit(designWidth, designHeight, availW, availH);
            scale = Math.Clamp(scale, 1f, MathF.Max(1f, maxScale));
            return ((int)MathF.Round(designWidth * scale), (int)MathF.Round(designHeight * scale));
        }

        /// <summary>
        /// The primary monitor's size in window coordinates, or (0, 0) if it cannot be determined. Requires the
        /// Silk GLFW platform to be registered (the constructors / <see cref="Scaled"/> do this).
        /// </summary>
        public static (int Width, int Height) PrimaryScreenSize()
        {
            try
            {
                IMonitor? monitor = Monitor.GetMainMonitor(null);
                if (monitor == null) return (0, 0);
                Vector2D<int> size = monitor.Bounds.Size;
                return (size.X, size.Y);
            }
            catch
            {
                return (0, 0); // headless / no display: caller falls back to the design size.
            }
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

        /// <summary>
        /// Reveal the window (it is born hidden - see the ctor). Call once the runtime icon has been applied so
        /// that on Windows the taskbar button is created with the correct icon rather than GLFW's generic default.
        /// Idempotent: the first call shows the window, later calls no-op. <see cref="Run"/> also calls this, so a
        /// host that drives <see cref="Run"/> without ever calling <see cref="Show"/> still gets a visible window
        /// (just without the pre-show icon guarantee unless it set the icon first). The GameApp facade calls
        /// <see cref="SetIcon"/> then <see cref="Show"/> in its constructor.
        /// </summary>
        public void Show()
        {
            if (_shown) return;
            _shown = true;
            _window.IsVisible = true;
        }

        /// <summary>
        /// Set the process's Windows taskbar identity (AppUserModelID) so Windows 10/11 keys the running app's
        /// taskbar button to it - fixing the taskbar icon (otherwise the generic <c>.exe</c> placeholder) and
        /// stabilising grouping/pinning. Forwards to <see cref="KhaozEngine.Platform.WindowsAppId.TrySetProcessAppUserModelId"/>:
        /// a no-op returning <c>false</c> off Windows or on a null/empty id, and it never throws. MUST be called
        /// BEFORE constructing any <see cref="AppWindow"/> (the ctor creates the native window, and the identity
        /// must be set first). GameApp calls this automatically from <c>GameAppOptions.AppUserModelId</c>.
        /// </summary>
        public static bool TrySetProcessAppUserModelId(string? appUserModelId)
            => KhaozEngine.Platform.WindowsAppId.TrySetProcessAppUserModelId(appUserModelId);

        /// <summary>
        /// Set the runtime window/taskbar icon from one or more already-decoded RGBA8 images (supply 16/32/48...
        /// and GLFW picks per DPI; a single image is fine). Safe to call any time after construction - the native
        /// handle exists from the ctor on. Passing nothing (or an empty list) is a no-op.
        /// <para><b>Platform:</b> Windows and Linux/X11 apply it to the title bar + taskbar at runtime (this is the
        /// regression fix vs MonoGame's embedded Icon.bmp). On macOS GLFW ignores window icons, so this is a
        /// deliberate no-op (never throws); use <see cref="SetMacDockIcon"/> for the Cocoa Dock icon. The Windows
        /// .exe icon shown when the app is not running is a per-consumer <c>&lt;ApplicationIcon&gt;</c>, independent
        /// of this API.</para>
        /// </summary>
        public void SetIcon(params WindowIcon[] icons)
        {
            // macOS: glfwSetWindowIcon is unsupported on Cocoa. Skip entirely so it can never raise a GLFW error.
            if (OperatingSystem.IsMacOS()) return;
            var raw = ToRawImages(icons);
            if (raw.Length == 0) return;
            _window.SetWindowIcon(raw); // WM_SETICON: title bar + Alt-Tab. Does NOT touch the taskbar button.

            // Windows taskbar button: it reads the window CLASS icon (GCLP_HICON), which glfwSetWindowIcon leaves as
            // GLFW's generic default (a .NET <ApplicationIcon> is not named "GLFW_ICON", so GLFW never picks it up).
            // Copy the icon we just set onto the class icon so the taskbar shows it too. GameApp calls SetIcon while
            // the window is still hidden (born hidden, revealed by Show()), so the taskbar button - created on first
            // show - is born with the right icon. No-op off Windows / with no native HWND; never throws.
            if (OperatingSystem.IsWindows())
                KhaozEngine.Platform.WindowsWindowIcon.TrySyncTaskbarIconFromWindow(_window.Native?.Win32?.Hwnd ?? 0);
        }

        /// <summary>
        /// macOS only: set the running app's Dock / Cmd-Tab icon from PNG-encoded bytes, via
        /// <c>NSApplication.setApplicationIconImage:</c> (<see cref="KhaozEngine.Platform.ApplicationIcon"/>). This
        /// is the Cocoa counterpart to <see cref="SetIcon"/>: GLFW cannot set the Dock icon and an unbundled
        /// <c>dotnet run</c> app has no <c>.app</c> icns, so without this such a run shows the generic document
        /// icon. A no-op returning <c>false</c> off macOS or on empty input; never throws. Call once at startup
        /// (the window ctor has already created the shared NSApplication this drives).
        /// </summary>
        public bool SetMacDockIcon(byte[] pngBytes) => KhaozEngine.Platform.ApplicationIcon.TrySetMacDockIcon(pngBytes);

        /// <summary>Pure WindowIcon -> Silk <see cref="RawImage"/> mapping (RGBA8, top-left origin) feeding GLFW's
        /// SetWindowIcon. Empty in -> empty out, so <see cref="SetIcon"/> no-ops. Kept seamable for headless tests.</summary>
        internal static RawImage[] ToRawImages(IReadOnlyList<WindowIcon> icons)
        {
            if (icons == null || icons.Count == 0) return Array.Empty<RawImage>();
            var raw = new RawImage[icons.Count];
            for (int i = 0; i < icons.Count; i++)
                raw[i] = new RawImage(icons[i].Width, icons[i].Height, icons[i].Pixels);
            return raw;
        }

        /// <summary>Run the frame loop until the window closes, calling <paramref name="onFrame"/> each frame. When
        /// <see cref="FrameCapHz"/> is set, the loop is paced to that rate with a monotonic-clock limiter after
        /// present (independent of the swapchain's vsync).</summary>
        public void Run(Action<Frame> onFrame)
        {
            Show(); // ensure visible even if the host never called Show() (GameApp calls it after SetIcon). Idempotent.
            var clock = System.Diagnostics.Stopwatch.StartNew();
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

                // Software frame cap: pace the loop to FrameCapHz. Silk's own loop runs the callback as fast as the
                // GPU allows (the Veldrid Metal present does not throttle the CPU), so idle here to hold the target
                // cadence. Hybrid sleep + short spin keeps it accurate without a Windows-timer-granularity overshoot.
                if (_frameLimiter.Enabled)
                {
                    double wait = _frameLimiter.WaitBeforeNext(clock.Elapsed.TotalSeconds);
                    if (wait > 0) PreciseIdle(clock, wait);
                }

                if (_maxFrames > 0 && ++_frameCount >= _maxFrames) _window.Close();
            };
            _window.Run();
        }

        /// <summary>Idle for <paramref name="seconds"/> using the monotonic <paramref name="clock"/>: sleep the bulk
        /// (leaving a ~1 ms margin so the OS timer granularity can't overshoot the cap), then spin the remainder.</summary>
        static void PreciseIdle(System.Diagnostics.Stopwatch clock, double seconds)
        {
            double deadline = clock.Elapsed.TotalSeconds + seconds;
            int bulkMs = (int)(seconds * 1000.0) - 1;
            if (bulkMs > 0) System.Threading.Thread.Sleep(bulkMs);
            while (clock.Elapsed.TotalSeconds < deadline) System.Threading.Thread.SpinWait(64);
        }

        void WireInput()
        {
            // Track OS focus so BuildInput can stamp it onto the snapshot. Silk keeps the render loop running and
            // reports a live cursor while unfocused, so without this consumers would see hover/clicks as if focused.
            _window.FocusChanged += focused => _focused = focused;
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
            WireKeyRepeat();
        }

        /// <summary>
        /// Capture OS key auto-repeat. GLFW fires a <c>REPEAT</c> key action while a key is held (after the user's
        /// OS repeat delay, then at the OS repeat rate), but Silk's high-level keyboard maps only PRESS/RELEASE and
        /// drops REPEAT, so <see cref="WireInput"/>'s KeyDown/KeyUp never see it. We install our own GLFW key callback
        /// to record repeats into <see cref="_repeated"/>, then CHAIN to Silk's previous callback so its KeyDown/KeyUp
        /// (and thus <see cref="_pressed"/>/<see cref="_released"/>) keep working unchanged. GLFW key codes share the
        /// <see cref="SilkKey"/> integer values, so we reuse <see cref="MapKey"/>. Per the input hard rule, this is the
        /// only place the GLFW statics are touched; callbacks run on the GLFW/main thread during the frame poll (same
        /// as the KeyDown handler), so the shared sets need no locking.
        /// </summary>
        unsafe void WireKeyRepeat()
        {
            nint glfwWindow = _window.Native?.Glfw ?? 0;
            if (glfwWindow == 0) return; // non-GLFW backend: repeat stays empty; press/release are unaffected.

            var glfw = Silk.NET.GLFW.GlfwProvider.GLFW.Value;
            var handle = (Silk.NET.GLFW.WindowHandle*)glfwWindow;
            _keyCallback = (window, key, code, action, mods) =>
            {
                if (action == GlfwInputAction.Repeat && MapKey((SilkKey)(int)key, out Key k))
                    _repeated.Add(k);
                _prevKeyCallback?.Invoke(window, key, code, action, mods); // keep Silk's KeyDown/KeyUp alive
            };
            // SetKeyCallback returns the previously-installed callback (Silk's); capture it to re-invoke above.
            _prevKeyCallback = glfw.SetKeyCallback(handle, _keyCallback);
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
                _gamepads.Read(_input.Gamepads), windowFocused: _focused, repeated: new HashSet<Key>(_repeated));

            _pressed.Clear();
            _released.Clear();
            _repeated.Clear();
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
            // Unregister the GLFW clipboard provider before GLFW is torn down so a later Clipboard call can
            // never dereference this window's freed GLFW handle.
            try { Clipboard.ClearTextProvider(); } catch { }
            try { _input?.Dispose(); } catch { }
            try { _cl?.Dispose(); } catch { }
            try { _gpu?.Dispose(); } catch { }
            try { _window?.Dispose(); } catch { }
        }
    }
}
