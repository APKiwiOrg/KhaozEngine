using System;
using KhaozEngine.Windowing;

namespace KhaozEngine.Automation
{
    /// <summary>
    /// The window half of <see cref="AutomationHost"/>: the three seams a running host touches on
    /// <see cref="AppWindow"/>, and nothing else.
    /// <para>
    /// It takes an <see cref="AppWindow"/> rather than a <c>GameApp</c> because the window carries all three:
    /// <see cref="AppWindow.InputFilter"/> (the composed snapshot), <see cref="AppWindow.BackgroundThrottle"/> (so an
    /// unfocused window keeps its frame rate) and <see cref="AppWindow.Close"/> (the <c>quit</c> command).
    /// <c>GameApp</c> forwards only the throttle publicly and keeps its window protected, so a <c>GameApp</c>
    /// constructor would be the larger dependency AND the incomplete one. A game on <c>GameApp</c> reaches the
    /// window from inside its own subclass, where <c>Window</c> is in scope:
    /// <code>
    /// protected override void OnLoad()
    /// {
    ///     _automation = new AutomationHost(Window, new AutomationOptions(Enabled: true, StateDirectory));
    ///     _automation.StateProvider = DescribeState;
    ///     _automation.Register("click_tile", ClickTile);
    ///     _automation.Start();
    /// }
    /// </code>
    /// </para>
    /// </summary>
    public sealed partial class AutomationHost
    {
        readonly AppWindow? _window;
        Func<InputState, InputState>? _installedFilter;

        /// <summary>
        /// Configure a host against <paramref name="window"/>. Nothing is wired until <see cref="Start"/> passes the
        /// gates: an inert host leaves the window's input filter, throttle policy and close path exactly as it found
        /// them.
        /// </summary>
        public AutomationHost(AppWindow window, AutomationOptions options)
        {
            ArgumentNullException.ThrowIfNull(window);
            _window = window;
            _options = options;
        }

        /// <summary>
        /// Wire the window, called by <see cref="Start"/> once every gate has passed. The throttle goes to
        /// <see cref="BackgroundThrottlePolicy.Disabled"/> because the default drops an unfocused window to 15 Hz and
        /// suppresses render entirely while minimized, and the agent's terminal takes focus the moment it types.
        /// <see cref="QuitRequested"/> is only defaulted, so a head that wants its own shutdown keeps it.
        /// </summary>
        void AttachWindow()
        {
            if (_window is null) return;
            _installedFilter = Pump;
            _window.InputFilter = _installedFilter;
            _window.BackgroundThrottle = BackgroundThrottlePolicy.Disabled;
            QuitRequested ??= _window.Close;
        }

        /// <summary>
        /// Unwire on dispose: drop the filter so the window goes back to the raw snapshot, and restore the default
        /// throttle. Only touches a filter this host actually installed, so a head that replaced it mid-run keeps its
        /// own.
        /// </summary>
        void DetachWindow()
        {
            if (_window is null || _installedFilter is null) return;
            if (ReferenceEquals(_window.InputFilter, _installedFilter)) _window.InputFilter = null;
            _window.BackgroundThrottle = BackgroundThrottlePolicy.Default;
            _installedFilter = null;
        }
    }
}
