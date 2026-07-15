using System;

namespace KhaozEngine.Windowing
{
    /// <summary>The window's live OS activity for a frame: whether it holds input focus and whether it is minimized
    /// (iconified). Fed to <see cref="BackgroundThrottlePolicy.Plan"/> to decide that frame's pacing. Pure value.</summary>
    public readonly record struct WindowActivity(bool Focused, bool Minimized);

    /// <summary>The pacing decision for one frame: whether to render + present it at all, and the frame cap (Hz, 0 =
    /// uncapped) the loop should idle to afterwards. Produced by <see cref="BackgroundThrottlePolicy.Plan"/>.</summary>
    public readonly record struct FramePlan(bool RenderAndPresent, int CapHz);

    /// <summary>
    /// How the frame loop throttles a backgrounded window so it stops burning a core and the GPU while the player is
    /// not looking at it. Two independent behaviours, both ON by default:
    /// <list type="bullet">
    /// <item><b>Minimized</b> (<see cref="PauseRenderWhenMinimized"/>): skip render + present entirely and idle the
    /// loop at <see cref="MinimizedHz"/>. Update still runs each idle tick (a minimized game keeps its simulation,
    /// netcode, and timers advancing), and events keep pumping so the window can be restored.</item>
    /// <item><b>Unfocused but visible</b> (<see cref="ThrottleWhenUnfocused"/>): keep rendering, but cap the loop to
    /// <see cref="UnfocusedHz"/> (or lower, if the base cap is already lower).</item>
    /// </list>
    /// <see cref="Default"/> is the ON policy. <see cref="Disabled"/> opts out of both for an app that intentionally
    /// renders full-rate in the background (a live wallpaper, a capture/stream source). Compose a custom policy with
    /// the <c>init</c> setters. The decision is the pure, headless-testable <see cref="Plan"/>.
    /// </summary>
    public readonly record struct BackgroundThrottlePolicy
    {
        /// <summary>Cap the loop to <see cref="UnfocusedHz"/> while the window is visible but unfocused. Default true.</summary>
        public bool ThrottleWhenUnfocused { get; init; }

        /// <summary>The frame cap (Hz) applied while unfocused but visible (the lower of this and the base cap wins).
        /// Default <see cref="DefaultUnfocusedHz"/>.</summary>
        public int UnfocusedHz { get; init; }

        /// <summary>Skip render + present and idle the loop while the window is minimized (update still runs). Default true.</summary>
        public bool PauseRenderWhenMinimized { get; init; }

        /// <summary>The idle rate (Hz) the loop ticks at while minimized - low enough to release the CPU, high enough
        /// that update-driven simulation stays responsive on restore. Default <see cref="DefaultMinimizedHz"/>.</summary>
        public int MinimizedHz { get; init; }

        /// <summary>Default cap applied while the window is visible but unfocused (15 Hz).</summary>
        public const int DefaultUnfocusedHz = 15;

        /// <summary>Default idle rate while the window is minimized (10 Hz).</summary>
        public const int DefaultMinimizedHz = 10;

        /// <summary>The default policy: throttle when unfocused (to <see cref="DefaultUnfocusedHz"/>) AND pause render
        /// when minimized (idling at <see cref="DefaultMinimizedHz"/>). This is the engine default.</summary>
        public static BackgroundThrottlePolicy Default => new()
        {
            ThrottleWhenUnfocused = true,
            UnfocusedHz = DefaultUnfocusedHz,
            PauseRenderWhenMinimized = true,
            MinimizedHz = DefaultMinimizedHz,
        };

        /// <summary>Opt out of both behaviours: always render, never throttle, for an app that intentionally renders
        /// in the background. The Hz fields keep their defaults but are unused while both flags are off.</summary>
        public static BackgroundThrottlePolicy Disabled => new()
        {
            ThrottleWhenUnfocused = false,
            UnfocusedHz = DefaultUnfocusedHz,
            PauseRenderWhenMinimized = false,
            MinimizedHz = DefaultMinimizedHz,
        };

        /// <summary>
        /// Decide the pacing for one frame given the window's <paramref name="activity"/> and the already-resolved
        /// base frame cap <paramref name="baseCapHz"/> (0 = uncapped). Rules, in order: a minimized window with
        /// <see cref="PauseRenderWhenMinimized"/> does not render and idles at <see cref="MinimizedHz"/>. Otherwise an
        /// unfocused window with <see cref="ThrottleWhenUnfocused"/> renders but caps to the lower of the base cap and
        /// <see cref="UnfocusedHz"/>. Otherwise the frame renders at the base cap. Pure.
        /// </summary>
        public FramePlan Plan(WindowActivity activity, int baseCapHz)
        {
            if (activity.Minimized && PauseRenderWhenMinimized)
            {
                // Idle at MinimizedHz, falling back to the base cap (or DefaultMinimizedHz) if it was left non-positive
                // so a paused frame never spins uncapped.
                int idle = MinimizedHz > 0 ? MinimizedHz : (baseCapHz > 0 ? baseCapHz : DefaultMinimizedHz);
                return new FramePlan(RenderAndPresent: false, CapHz: idle);
            }

            if (!activity.Focused && ThrottleWhenUnfocused)
                return new FramePlan(RenderAndPresent: true, CapHz: CombineCap(baseCapHz, UnfocusedHz));

            return new FramePlan(RenderAndPresent: true, CapHz: baseCapHz);
        }

        /// <summary>The lower of two caps, treating a non-positive value as "uncapped" (so the other side wins, and
        /// two uncapped sides stay uncapped).</summary>
        static int CombineCap(int a, int b)
        {
            if (a <= 0) return b <= 0 ? 0 : b;
            if (b <= 0) return a;
            return Math.Min(a, b);
        }
    }
}
