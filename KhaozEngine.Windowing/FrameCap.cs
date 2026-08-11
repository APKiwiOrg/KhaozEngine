using KhaozEngine.Gpu;

namespace KhaozEngine.Windowing
{
    /// <summary>
    /// A software frame-rate cap intent: <see cref="Auto"/> (the engine picks a backend-aware default),
    /// <see cref="Uncapped"/> (an intentional free-run), or a fixed <see cref="Hz"/> value. This is the consumer's
    /// REQUEST. <see cref="Resolve"/> turns it into the concrete Hz the loop actually paces to for a given backend and
    /// present mode. The default value (<c>default(FrameCap)</c>) is <see cref="Auto"/>, so a zero-initialised options
    /// struct opts into the backend-aware default rather than free-running.
    /// <para><b>Auto semantics.</b> Vsync throttles the CPU frame rate on the D3D11 and Vulkan paths, so
    /// <see cref="Auto"/> there stays uncapped and lets vsync do the work. The Veldrid Metal present does NOT throttle
    /// the CPU from vsync alone (a Mac client free-runs a whole core plus the GPU well above the refresh), so
    /// <see cref="Auto"/> on that backend with vsync resolves to a real cap - the display refresh rate when it is
    /// known, else <see cref="DefaultMetalAutoCapHz"/>. With <see cref="PresentMode.Immediate"/> (any backend) the
    /// consumer asked for an uncapped, lowest-latency present, so <see cref="Auto"/> respects that and stays
    /// uncapped.</para>
    /// <para><b>Which Metal.</b> The sentence above is measured on the VELDRID Metal present and applies to it
    /// alone. The engine's own <see cref="GpuBackendKind.MetalNative"/> backend was measured at rollout gate 5 and
    /// its present DOES throttle from vsync, so it takes the uncapped arm with the other native backends (see
    /// <see cref="Resolve"/>).</para>
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

        /// <summary>The fallback cap for <see cref="Auto"/> on the incumbent Metal backend + vsync when the live
        /// display refresh rate is unavailable. 120 Hz: high enough to feel smooth, low enough to stop the free-run
        /// burning a whole core.</summary>
        public const int DefaultMetalAutoCapHz = 120;

        /// <summary>The engine picks a backend-aware default (uncapped where vsync throttles, a real cap on the
        /// incumbent Metal backend + vsync). The default <see cref="FrameCap"/> value.</summary>
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
        /// regardless of backend. <see cref="Auto"/> resolves per the type remarks: a real cap only on
        /// (the incumbent <see cref="GpuBackendKind.Metal"/> + <see cref="PresentMode.Vsync"/>) - the
        /// <paramref name="displayRefreshHz"/> when positive, else <see cref="DefaultMetalAutoCapHz"/> - and 0
        /// (uncapped) everywhere else, since vsync throttles there or the consumer chose an uncapped present. Pure.
        /// <para>
        /// <b>The capped arm is the INCUMBENT Metal backend alone, and that is a measurement rather than an
        /// assumption.</b> The real question this arm asks is whether the backend's present throttles the CPU from
        /// vsync alone, not which API it is. It briefly covered both Metal implementations
        /// (<c>GpuBackendKinds.IsMetal</c>) as a conservative default, because the native Metal backend sets
        /// <c>displaySyncEnabled</c> unconditionally and bounds <c>maximumDrawableCount</c>, either of which could
        /// make the software cap redundant there. Rollout gate 5 read it on 2026-08-11 and the answer is that the
        /// native present throttles, on three legs: an uncapped 8000-frame field capture with vsync on blocked in
        /// the drawable acquire exactly once per frame for 15.175 ms against a 16.669 ms median frame (91 percent
        /// of every frame), a human windowed pass on a display pinned to 120 Hz sat at 120 fps, and toggling vsync
        /// OFF mid-session jumped to 700 fps and beyond with visible tearing, which is what rules out any other
        /// bottleneck as the source of the pacing. A software cap at the display refresh cannot bind on top of
        /// that, so <see cref="GpuBackendKind.MetalNative"/> takes the uncapped arm (decision M-W3 of
        /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). This is also the arm #380's
        /// present-pacing work will revisit.
        /// </para>
        /// <para>
        /// So no native backend is in this arm: a <see cref="GpuBackendKind.Direct3D11Native"/> or
        /// <see cref="GpuBackendKind.VulkanNative"/> present throttles the CPU from vsync exactly as its
        /// incumbent's does, and <see cref="GpuBackendKind.MetalNative"/> now measures the same way, so each
        /// behaves identically to the implementation it is being A/B'd against.
        /// </para>
        /// </summary>
        public int Resolve(GpuBackendKind backend, PresentMode present, int displayRefreshHz)
            => Kind switch
            {
                CapKind.Fixed => Value,
                CapKind.Uncapped => 0,
                _ => backend == GpuBackendKind.Metal && present == PresentMode.Vsync
                        ? (displayRefreshHz > 0 ? displayRefreshHz : DefaultMetalAutoCapHz)
                        : 0,
            };
    }
}
