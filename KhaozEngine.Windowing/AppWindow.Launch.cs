using System;
using Silk.NET.GLFW;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The launch-placement half of <see cref="AppWindow"/>: the GLFW focus hints the native window is created with,
    /// which monitor it lands on, and the two dev environment overrides. It is here rather than in
    /// <c>AppWindow.cs</c> because that file is at its size ceiling, and because launch placement is a distinct
    /// concern from window/device construction. The POLICY is pure and lives in <see cref="WindowLaunch"/> /
    /// <see cref="InitialMonitor"/>; this half is only the GLFW and Silk contact.
    /// </summary>
    public sealed partial class AppWindow
    {
        // Resolved once in the constructor, beside KE_MAX_FRAMES: the focus hints, the environment monitor override,
        // and whether there was one. _optionMonitor is the consumer's own request, set post-construction so it also
        // reaches a window built by a custom GameAppOptions.WindowFactory. The environment override outranks it.
        LaunchPlacement _launch;
        InitialMonitor _optionMonitor;
        bool _launchPlacementApplied;

        /// <summary>
        /// True when <c>KE_WINDOW_MONITOR</c> placed this window, so the placement on screen is a developer override
        /// rather than the player's own.
        /// <para><b>The contract a game is expected to honour:</b> while this is true, do NOT write the window
        /// position back to the player's settings. A harness or debug boot that lands the window on another monitor
        /// would otherwise persist that as the player's saved placement, and the next ordinary launch would restore
        /// it. Size, window mode and every other setting are unaffected. See <c>docs/USING-KHAOZENGINE.md</c>, "How a
        /// game remembers its window".</para>
        /// </summary>
        public bool PlacementOverridden => _launch.PlacementOverridden;

        /// <summary>
        /// Which monitor this window launches on. The default <see cref="InitialMonitor.Saved"/> moves nothing, so
        /// whatever the game restored from its own settings stands. Any other value is applied by
        /// <see cref="Run(System.Action{Frame})"/>, once, BEFORE the first frame and therefore AFTER the game's boot
        /// restore has run, so an explicit choice wins over a saved position.
        /// <para>Reading it back gives the EFFECTIVE request: <c>KE_WINDOW_MONITOR</c> outranks whatever was set
        /// here, and setting it while an override is active is recorded but never applied. Setting it after the frame
        /// loop has started is too late for this launch, which is what <see cref="MoveToMonitor"/> is for.</para>
        /// </summary>
        public InitialMonitor InitialMonitor
        {
            get => _launch.PlacementOverridden ? _launch.Monitor : _optionMonitor;
            set => _optionMonitor = value;
        }

        /// <summary>
        /// Apply the launch monitor now, once. <see cref="Run(System.Action{Frame})"/> calls this after
        /// <see cref="Show"/> and before the first frame, which on the <c>GameApp</c> path is after the game's
        /// <c>OnLoad</c> boot restore. Later calls no-op. A request that names no connected monitor (an unplugged
        /// display, a headless run with no monitors at all) moves nothing.
        /// </summary>
        public void ApplyLaunchPlacement()
        {
            if (_launchPlacementApplied) return;
            _launchPlacementApplied = true;

            InitialMonitor request = InitialMonitor;
            if (request.IsSaved) return;

            int index = request.Resolve(Monitors);
            if (index >= 0) MoveToMonitor(index);
        }

        /// <summary>
        /// Resolve the launch placement (consumer option plus the two environment overrides), write the focus hints
        /// to GLFW, and only then create the native window. The hints have to be live at <c>glfwCreateWindow</c>, so
        /// this replaces the bare <c>Initialize</c> call in the constructor rather than sitting anywhere earlier.
        /// GLFW window hints are sticky process state that Silk never resets (it sets its own hints and never calls
        /// <c>glfwDefaultWindowHints</c>), so setting them immediately before the create is both sufficient and the
        /// narrowest window in which they can be disturbed.
        /// </summary>
        void InitializeWithLaunchHints(bool focusOnLaunch)
        {
            _launch = WindowLaunch.FromEnvironment(InitialMonitor.Saved, focusOnLaunch);
            // Console.Error rather than a logger: this package deliberately takes no KhaozEngine.Diagnostics edge
            // (docs/DEPENDENCY-SEAMS.md), the constructor has already attached the parent console on Windows, and a
            // mistyped dev lever has to be visible before a game has configured any logging.
            if (_launch.UnrecognizedMonitorValue is { } badMonitor)
                Console.Error.WriteLine(WindowLaunch.UnrecognizedMonitorWarning(badMonitor));
            if (_launch.UnrecognizedFocusValue is { } badFocus)
                Console.Error.WriteLine(WindowLaunch.UnrecognizedFocusWarning(badFocus));

            ApplyFocusHints(_launch.Hints);
            _window.Initialize(); // creates the native window WITHOUT starting the loop; the handle is valid after this.
        }

        /// <summary>
        /// Write <paramref name="hints"/> to the GLFW window hints. Best-effort: a non-GLFW backend or a GLFW that
        /// refuses the hint leaves the window at its default (focused), which is no worse than the status quo and
        /// never worth failing a boot over.
        /// <para><b>What this buys, per platform.</b> On Windows and X11 a false pair creates and shows the window
        /// without giving it keyboard focus or raising it over the foreground app. On macOS the same is true of
        /// keyboard focus, because GLFW only calls <c>[NSApp activateIgnoringOtherApps]</c> and
        /// <c>makeKeyAndOrderFront</c> from its focus path, which both hints being false skips, leaving a plain
        /// <c>orderFront</c>. What CANNOT be suppressed through GLFW on macOS is the process becoming the active
        /// application in the first place: GLFW's cocoa init finishes launching the app and sets
        /// <c>NSApplicationActivationPolicyRegular</c>, and macOS activates a newly launched regular app. The only
        /// cocoa hints Silk.NET.GLFW 2.23 exposes are <c>InitHint.CocoaChdirResources</c>,
        /// <c>InitHint.CocoaMenubar</c> and <c>WindowHintString.CocoaFrameName</c>, none of which control
        /// activation, and GLFW 3.4 has no hint for it at all. So on a Mac expect the app to come to the front on
        /// the first launch of a run while the window still does not steal the keyboard, and expect a later
        /// <see cref="Show"/> to be quiet.</para>
        /// </summary>
        static void ApplyFocusHints(WindowCreationHints hints)
        {
            try
            {
                Glfw glfw = GlfwProvider.GLFW.Value;
                glfw.WindowHint(WindowHintBool.Focused, hints.Focused);
                glfw.WindowHint(WindowHintBool.FocusOnShow, hints.FocusOnShow);
            }
            catch
            {
                // Best-effort: never let a hint refusal or a non-GLFW backend stop the window from being created.
            }
        }
    }
}
