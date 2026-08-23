namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Debug hook to capture a single Metal GPU frame to an Xcode <c>.gputrace</c>, for diagnosing stale/garbage
    /// buffer reads on Metal (RenderDoc cannot capture Metal). Call <see cref="ArmNext"/> with an output path (e.g.
    /// from a debug key in a spawn-heavy scene); the engine wraps the NEXT frame's <c>Submit</c> with
    /// <c>MTLCaptureManager</c> start/stop and writes the trace, then disarms. Open the <c>.gputrace</c> in Xcode
    /// and inspect each skinned draw's bound bone/instance buffer contents.
    /// </summary>
    /// <remarks>
    /// The environment variable <c>MTL_CAPTURE_ENABLED=1</c> MUST be set before the app launches (before the Metal
    /// device is created), or Metal disables programmatic capture. No-op on non-Metal backends and if the capture
    /// API is unavailable. Debug-only, and do not arm in shipping builds.
    /// <para>
    /// The native Metal device services an armed capture itself, with its own command-queue pointer in hand
    /// (decision M-G5 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). The arm is consumed at a
    /// PRESENT boundary, so a headless device never consumes one.
    /// </para>
    /// </remarks>
    public static class GpuFrameCapture
    {
        static string? _armedPath;
        static readonly object _gate = new();

        /// <summary>Arm a one-shot capture: the next frame <c>Submit</c> is written to <paramref name="outputPath"/>
        /// (a fresh, non-existent path; Metal creates the .gputrace bundle).</summary>
        public static void ArmNext(string outputPath)
        {
            lock (_gate) _armedPath = outputPath;
        }

        /// <summary>True if a capture is currently armed (not yet consumed).</summary>
        public static bool IsArmed { get { lock (_gate) return _armedPath != null; } }

        internal static bool TryConsume(out string path)
        {
            lock (_gate)
            {
                if (_armedPath == null) { path = ""; return false; }
                path = _armedPath;
                _armedPath = null;
                return true;
            }
        }

        /// <summary>What to do at a swapchain present boundary for the one-shot full-frame capture.</summary>
        internal enum CaptureAction { None, StartAfterPresent, StopAfterPresent }

        /// <summary>Pure present-boundary state machine. While a capture is in progress, the NEXT present ends it
        /// (<see cref="CaptureAction.StopAfterPresent"/>). Otherwise, if an arm was just consumed at this present,
        /// begin capturing the next frame (<see cref="CaptureAction.StartAfterPresent"/>). This makes the capture
        /// span exactly one full frame (all its Submits, between two presents).</summary>
        internal static CaptureAction NextAction(bool capturing, bool armConsumed)
        {
            if (capturing) return CaptureAction.StopAfterPresent;
            if (armConsumed) return CaptureAction.StartAfterPresent;
            return CaptureAction.None;
        }
    }
}
