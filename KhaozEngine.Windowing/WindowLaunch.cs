using System;
using System.Globalization;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The two GLFW focus hints a window is CREATED with. <see cref="Focused"/> is <c>GLFW_FOCUSED</c> (the window
    /// takes input focus when it is created visible) and <see cref="FocusOnShow"/> is <c>GLFW_FOCUS_ON_SHOW</c> (it
    /// takes input focus when it is later shown). Both are needed: the engine's window is born hidden and revealed by
    /// <see cref="AppWindow.Show"/>, so the second hint is the one that decides the common path, and the first covers
    /// a host that creates a visible window.
    /// <para>Pure data, computed by <see cref="WindowLaunch.Resolve"/> and written to GLFW by
    /// <see cref="AppWindow"/>, which stays the only class that touches the GLFW statics.</para>
    /// </summary>
    public readonly record struct WindowCreationHints(bool Focused, bool FocusOnShow);

    /// <summary>
    /// The resolved launch placement for one window: the GLFW focus hints to create it with, which monitor to put it
    /// on, whether a dev environment override supplied that placement, and any environment value that was set and
    /// understood as nothing.
    /// </summary>
    /// <param name="Monitor">The effective monitor request (the environment override when there is one, else the
    /// consumer's option).</param>
    /// <param name="Hints">The GLFW focus hints to create the window with.</param>
    /// <param name="PlacementOverridden">True when <c>KE_WINDOW_MONITOR</c> supplied the placement, which is what a
    /// game reads through <see cref="AppWindow.PlacementOverridden"/> to skip writing the window position back to its
    /// settings for this run.</param>
    /// <param name="UnrecognizedMonitorValue">The <c>KE_WINDOW_MONITOR</c> value verbatim when it was set and matched
    /// nothing, else null.</param>
    /// <param name="UnrecognizedFocusValue">The <c>KE_WINDOW_FOCUS</c> value verbatim when it was set and matched
    /// nothing, else null.</param>
    public readonly record struct LaunchPlacement(
        InitialMonitor Monitor,
        WindowCreationHints Hints,
        bool PlacementOverridden,
        string? UnrecognizedMonitorValue = null,
        string? UnrecognizedFocusValue = null);

    /// <summary>
    /// The pure launch-placement policy: how a consumer's launch options plus the two dev environment overrides
    /// (<see cref="MonitorVar"/>, <see cref="FocusVar"/>) become a <see cref="LaunchPlacement"/>. No Silk and no GLFW
    /// access, so every rule below is headless-testable. <see cref="AppWindow"/> reads the environment once at
    /// construction, beside <c>KE_MAX_FRAMES</c>, and applies the result.
    /// <para><b>Precedence, highest first:</b> the environment override, then the consumer's option, then the engine
    /// default (focus on launch, and no monitor of the engine's choosing). An environment override beats the game's
    /// SAVED position too, because the engine applies the placement after the game's boot restore has run.</para>
    /// <para>A value that is set and matches nothing is IGNORED rather than fatal, and comes back through the
    /// <see cref="LaunchPlacement"/> so the caller can log one line naming what was typed. A mistyped dev lever that
    /// silently does nothing is indistinguishable from an unset one, which is how a whole session gets spent proving
    /// nothing (same reasoning as the GPU env levers).</para>
    /// </summary>
    public static class WindowLaunch
    {
        /// <summary>The dev override for which monitor the window launches on: <c>rightmost</c>, <c>leftmost</c>,
        /// <c>primary</c>, or a monitor index.</summary>
        public const string MonitorVar = "KE_WINDOW_MONITOR";

        /// <summary>The dev override for launch focus: <c>0</c> (also <c>false</c> / <c>no</c> / <c>off</c>) creates
        /// the window without keyboard focus.</summary>
        public const string FocusVar = "KE_WINDOW_FOCUS";

        /// <summary>The GLFW focus hints for a given launch-focus decision. Both hints carry the same value: one
        /// covers a window created visible, the other a window revealed later by <see cref="AppWindow.Show"/>.</summary>
        public static WindowCreationHints HintsFor(bool focusOnLaunch) => new(focusOnLaunch, focusOnLaunch);

        /// <summary>
        /// Resolve the launch placement from the consumer's options and the two environment values (pass the raw
        /// strings, null when unset). Pure: <see cref="FromEnvironment"/> is the impure wrapper.
        /// </summary>
        public static LaunchPlacement Resolve(InitialMonitor option, bool focusOnLaunch,
            string? monitorEnv, string? focusEnv)
        {
            bool overridden = TryParseMonitor(monitorEnv, out InitialMonitor envMonitor, out string? badMonitor);
            bool focus = TryParseFocus(focusEnv, out bool envFocus, out string? badFocus) ? envFocus : focusOnLaunch;
            return new LaunchPlacement(overridden ? envMonitor : option, HintsFor(focus), overridden,
                badMonitor, badFocus);
        }

        /// <summary>The same decision read from the live environment. The one impure member here.</summary>
        public static LaunchPlacement FromEnvironment(InitialMonitor option, bool focusOnLaunch)
            => Resolve(option, focusOnLaunch,
                Environment.GetEnvironmentVariable(MonitorVar),
                Environment.GetEnvironmentVariable(FocusVar));

        /// <summary>
        /// Parse a <see cref="MonitorVar"/> value: <c>rightmost</c>, <c>leftmost</c>, <c>primary</c> (case and
        /// surrounding whitespace insensitive), or a non-negative integer index. Returns false for an unset, blank or
        /// unrecognized value, and a non-blank unrecognized one comes back through
        /// <paramref name="unrecognizedValue"/> verbatim (original case) so the caller can warn.
        /// </summary>
        public static bool TryParseMonitor(string? value, out InitialMonitor monitor, out string? unrecognizedValue)
        {
            monitor = InitialMonitor.Saved;
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string token = value.Trim();
            switch (token.ToLowerInvariant())
            {
                case "rightmost": monitor = InitialMonitor.Rightmost; return true;
                case "leftmost": monitor = InitialMonitor.Leftmost; return true;
                case "primary": monitor = InitialMonitor.Primary; return true;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index >= 0)
            {
                monitor = InitialMonitor.At(index);
                return true;
            }

            unrecognizedValue = value;
            return false;
        }

        /// <summary>
        /// Parse a <see cref="FocusVar"/> value: <c>1</c> / <c>true</c> / <c>yes</c> / <c>on</c> for focus, <c>0</c> /
        /// <c>false</c> / <c>no</c> / <c>off</c> for no focus. Returns false for an unset, blank or unrecognized
        /// value (the caller keeps its own default), and a non-blank unrecognized one comes back through
        /// <paramref name="unrecognizedValue"/> verbatim.
        /// </summary>
        public static bool TryParseFocus(string? value, out bool focus, out string? unrecognizedValue)
        {
            focus = true;
            unrecognizedValue = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "1": case "true": case "yes": case "on":
                    focus = true;
                    return true;
                case "0": case "false": case "no": case "off":
                    focus = false;
                    return true;
                default:
                    unrecognizedValue = value;
                    return false;
            }
        }

        /// <summary>The WARN body for a <see cref="MonitorVar"/> value that was set and understood as nothing. Names
        /// what was typed and what would have worked.</summary>
        public static string UnrecognizedMonitorWarning(string value)
            => $"{MonitorVar}='{value}' is not a recognized monitor (rightmost, leftmost, primary, or a monitor "
                + "index from 0). Leaving the launch placement to the game.";

        /// <summary>The WARN body for a <see cref="FocusVar"/> value that was set and understood as nothing.</summary>
        public static string UnrecognizedFocusWarning(string value)
            => $"{FocusVar}='{value}' is not a recognized on/off value (1/true/yes/on, 0/false/no/off). Leaving "
                + "launch focus at the game's own setting.";
    }
}
