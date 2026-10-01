namespace KhaozEngine.Windowing
{
    /// <summary>
    /// The GLFW calls one frame needs to bring pointer capture in line with the request, plus the renewal latch the
    /// next frame passes back in. All three call flags false means the frame touches GLFW not at all.
    /// </summary>
    /// <param name="SetDisabled">Put the cursor in GLFW's disabled mode (hidden, locked, unbounded motion).</param>
    /// <param name="SetNormal">Put the cursor back in GLFW's normal mode.</param>
    /// <param name="SetRawMotion">Turn on raw mouse motion. Set only with <paramref name="SetDisabled"/> and only
    /// when the platform supports it.</param>
    /// <param name="AwaitingRenewal">The latch for the next frame. True after a focus loss until the request is
    /// seen false while focused, so a request held across the loss cannot capture again on refocus.</param>
    internal readonly record struct PointerCaptureAction(
        bool SetDisabled, bool SetNormal, bool SetRawMotion, bool AwaitingRenewal)
    {
        /// <summary>True when the frame has at least one GLFW call to make.</summary>
        public bool CallsGlfw => SetDisabled || SetNormal || SetRawMotion;
    }

    /// <summary>
    /// The pure per-frame pointer capture decision <see cref="AppWindow"/> applies before it builds the input
    /// snapshot. Capture holds only while it is requested and the window is focused. A focus loss drops it, and a
    /// refocus never restores it until the request is renewed by a false then true edge seen while focused.
    /// </summary>
    internal static class PointerCapturePolicy
    {
        /// <summary>Decide this frame's GLFW calls.</summary>
        /// <param name="requested">The latest <see cref="AppWindow.SetPointerCaptured"/> value.</param>
        /// <param name="focused">The window's focus, from the same source that stamps
        /// <see cref="InputState.WindowFocused"/>.</param>
        /// <param name="currentlyCaptured">Whether GLFW was last told to disable the cursor.</param>
        /// <param name="rawMotionSupported">Whether GLFW reports raw mouse motion as supported.</param>
        /// <param name="awaitingRenewal">The previous frame's <see cref="PointerCaptureAction.AwaitingRenewal"/>.
        /// Starts false.</param>
        public static PointerCaptureAction Decide(
            bool requested, bool focused, bool currentlyCaptured, bool rawMotionSupported, bool awaitingRenewal)
        {
            bool awaiting = !focused || (requested && awaitingRenewal);
            bool target = requested && !awaiting;
            bool capture = target && !currentlyCaptured;
            bool release = !target && currentlyCaptured;
            return new PointerCaptureAction(capture, release, capture && rawMotionSupported, awaiting);
        }
    }
}
