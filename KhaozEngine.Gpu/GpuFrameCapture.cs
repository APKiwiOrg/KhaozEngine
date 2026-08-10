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
    /// WHICH Metal serves an armed capture is <see cref="VeldridPathCaptures"/> for the Veldrid path, and the
    /// native Metal device itself for its own. The two are separate code paths rather than one widened gate, and
    /// decision M-G5 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> is why: the native backend
    /// owns its command queue, so it captures with the pointer in hand while the Veldrid path has to find the
    /// queue by reflection. Both consume an arm at a PRESENT boundary, so a headless device of either kind never
    /// consumes one.
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

        /// <summary>
        /// Whether the VELDRID device wrapper is the thing that services an armed capture on
        /// <paramref name="backend"/>. True for <see cref="GpuBackendKind.Metal"/> and nothing else.
        /// <para>
        /// Extracted from the inline check it used to be so the append audit can assert it device-free, the way
        /// <c>D3D11ThreadingProbe.IsApplicable</c> is the pure half of an impure site. What it pins is that this
        /// is NOT the family question: widening it to <c>GpuBackendKinds.IsMetal</c> would read as the fix for
        /// <see cref="GpuBackendKind.MetalNative"/> arming no capture, and it would fix nothing, because a
        /// provider-built device never becomes the Veldrid wrapper this runs inside. The native backend owns its
        /// own queue and services its own captures with the pointer in hand, which is also what removes the
        /// reflection into Veldrid's private <c>_commandQueue</c> field on that path (decision M-G5 of
        /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>).
        /// </para>
        /// <para>
        /// THAT NATIVE PATH NOW EXISTS: <c>MetalGpuDevice.ServiceFrameCaptureAtPresentBoundary</c> is the native
        /// device's own consumption site and takes the same <see cref="TryConsume"/> and
        /// <see cref="NextAction"/> this one does. So this member stays FALSE for
        /// <see cref="GpuBackendKind.MetalNative"/> permanently rather than pending a fix, which is what the
        /// append audit asserts. The third of the three sites the Metal append degraded SILENTLY is closed by a
        /// second implementation rather than by a wider predicate, and the arm it consumes is consumed at a
        /// PRESENT, so a headless device of either kind still consumes nothing.
        /// </para>
        /// </summary>
        internal static bool VeldridPathCaptures(GpuBackendKind backend) => backend == GpuBackendKind.Metal;

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
