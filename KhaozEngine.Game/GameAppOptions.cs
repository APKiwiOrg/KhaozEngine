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
        /// Optional software frame-rate cap in Hz (0 = uncapped, the default). When set, <see cref="AppWindow.Run"/>
        /// paces the loop to this rate with a monotonic-clock limiter (<see cref="FrameLimiter"/>), independent of the
        /// swapchain's vsync - so a game can pin the render rate to an integer multiple of its fixed tick (e.g. 60 or
        /// 120 for a 30 Hz tick) to keep presentation phase-aligned with the tick. This is the deterministic cap; use
        /// it when vsync does not throttle (notably the Veldrid Metal path). Applied on both the default and a custom
        /// <see cref="WindowFactory"/> window (set post-construction), so a factory need not forward it.
        /// </summary>
        public int FrameCapHz;

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

        /// <summary>Resolved design width: <see cref="DesignWidth"/>, or <see cref="Width"/> when it is 0.</summary>
        internal int ResolvedDesignWidth => DesignWidth == 0 ? Width : DesignWidth;
        /// <summary>Resolved design height: <see cref="DesignHeight"/>, or <see cref="Height"/> when it is 0.</summary>
        internal int ResolvedDesignHeight => DesignHeight == 0 ? Height : DesignHeight;

        /// <summary>Sensible defaults: Fit scaling, 1:1 design space, dark clear colour, 30s resume-gap threshold,
        /// vsync present, no software frame cap.</summary>
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
        };
    }
}
