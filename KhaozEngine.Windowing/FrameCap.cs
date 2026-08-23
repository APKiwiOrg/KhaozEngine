using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A software frame-rate cap intent: <see cref="Auto"/> (the engine picks a backend-aware default),
    /// <see cref="Uncapped"/> (an intentional free-run), or a fixed <see cref="Hz"/> value. This is the consumer's
    /// REQUEST. <see cref="Resolve"/> turns it into the concrete Hz the loop actually paces to for a given backend and
    /// present mode. The default value (<c>default(FrameCap)</c>) is <see cref="Auto"/>, so a zero-initialised options
    /// struct opts into the backend-aware default rather than free-running.
    /// <para><b>Auto semantics.</b> Vsync throttles the CPU frame rate on all three live backends, so
    /// <see cref="Auto"/> stays uncapped everywhere and lets vsync do the work. With
    /// <see cref="PresentMode.Immediate"/> the consumer asked for an uncapped, lowest-latency present, so
    /// <see cref="Auto"/> respects that too. The one backend that needed a software cap was the Veldrid Metal
    /// incumbent, deleted in 18.0.0, and <see cref="Resolve"/> carries the measurement that says none of its
    /// successors does.</para>
    /// Pure and headless-testable. Only <see cref="AppWindow"/> supplies the (impure) live display refresh rate.
    /// </summary>
    public readonly record struct FrameCap
    {
        /// <summary>Which kind of cap this value carries.</summary>
        public enum CapKind : byte
        {
            /// <summary>Let the engine pick a backend-aware default (see the type remarks). The zero value, so the
            /// default <see cref="FrameCap"/> is <see cref="Auto"/>.</summary>
            Auto = 0,
            /// <summary>Free-run intentionally: never cap the loop, whatever the backend.</summary>
            Uncapped = 1,
            /// <summary>Pace to a fixed <see cref="Value"/> Hz.</summary>
            Fixed = 2,
        }

        /// <summary>The kind of cap (auto / uncapped / fixed).</summary>
        public CapKind Kind { get; private init; }

        /// <summary>The target Hz for a <see cref="CapKind.Fixed"/> cap (0 for <see cref="Auto"/> / <see cref="Uncapped"/>).</summary>
        public int Value { get; private init; }

        /// <summary>A sensible software cap for a Mac client that wants one without reading the monitor. 120 Hz:
        /// high enough to feel smooth, low enough to stop a free-run burning a whole core.
        /// <para>
        /// UNUSED BY <see cref="Resolve"/> SINCE 18.0.0, and kept as public API. It was the <see cref="Auto"/>
        /// cap for the Veldrid Metal incumbent when no display refresh was known, and that backend is deleted.
        /// A consumer that still wants the number can pass <c>FrameCap.Hz(DefaultMetalAutoCapHz)</c>, so
        /// removing it would break that consumer for a word.
        /// </para></summary>
        public const int DefaultMetalAutoCapHz = 120;

        /// <summary>The engine picks a backend-aware default, which is uncapped on every live backend since
        /// 18.0.0 because vsync throttles the present on all three. The default <see cref="FrameCap"/>
        /// value.</summary>
        public static FrameCap Auto => default;

        /// <summary>Free-run intentionally (never cap), whatever the backend. This is the explicit "0 = uncapped" intent.</summary>
        public static FrameCap Uncapped => new() { Kind = CapKind.Uncapped };

        /// <summary>Pace to a fixed <paramref name="hz"/> Hz. A non-positive value is treated as <see cref="Uncapped"/>.</summary>
        public static FrameCap Hz(int hz) => hz > 0 ? new() { Kind = CapKind.Fixed, Value = hz } : Uncapped;

        /// <summary>True when this is the backend-aware <see cref="Auto"/> default (not an explicit consumer choice).</summary>
        public bool IsAuto => Kind == CapKind.Auto;

        /// <summary>True when this is an intentional free-run (<see cref="Uncapped"/>).</summary>
        public bool IsUncapped => Kind == CapKind.Uncapped;

        /// <summary>
        /// Resolve this cap to the concrete Hz the loop should pace to on <paramref name="backend"/> with
        /// <paramref name="present"/> (0 = uncapped). A fixed cap is its own value and an uncapped cap is 0, both
        /// regardless of backend. Pure.
        /// <para>
        /// <b><see cref="Auto"/> RESOLVES TO 0 ON EVERY LIVE BACKEND SINCE 18.0.0, and that is a measurement
        /// rather than a simplification.</b> The question the arm asks is whether the backend's present throttles
        /// the CPU from vsync alone, not which API it is, and the only backend that ever answered no was the
        /// Veldrid Metal incumbent, which was deleted. Rollout gate 5 read
        /// <see cref="GpuBackendKind.MetalNative"/> on 2026-08-11 and its present throttles, on three legs: an
        /// uncapped 8000-frame field capture with vsync on blocked in the drawable acquire exactly once per frame
        /// for 15.175 ms against a 16.669 ms median frame (91 percent of every frame), a human windowed pass on a
        /// display pinned to 120 Hz sat at 120 fps, and toggling vsync OFF mid-session jumped to 700 fps and
        /// beyond with visible tearing, which rules out any other bottleneck as the source of the pacing. A
        /// <see cref="GpuBackendKind.Direct3D11Native"/> or <see cref="GpuBackendKind.VulkanNative"/> present
        /// throttles from vsync as its incumbent's did (decision M-W3 of
        /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). This is the arm #380's present-pacing
        /// work will revisit.
        /// </para>
        /// <para>
        /// The <paramref name="backend"/> and <paramref name="displayRefreshHz"/> parameters are KEPT rather than
        /// removed, and this is row 9 of the GpuBackendKind append audit: a backend whose present free-runs under
        /// vsync puts an arm back here, and in
        /// <see cref="DisplaySettings.RequiresFrameCapWarning"/> in the same commit, or a consumer is silently
        /// left free-running. Removing the parameters would make that a signature change on public API instead of
        /// a one-line arm.
        /// </para>
        /// </summary>
        public int Resolve(GpuBackendKind backend, PresentMode present, int displayRefreshHz)
            => Kind switch
            {
                CapKind.Fixed => Value,
                CapKind.Uncapped => 0,
                _ => 0,
            };
    }
}
