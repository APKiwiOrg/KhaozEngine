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
    /// <see cref="Auto"/> on Metal with vsync resolves to a real cap - the display refresh rate when it is known,
    /// else <see cref="DefaultMetalAutoCapHz"/>. With <see cref="PresentMode.Immediate"/> (any backend) the consumer
    /// asked for an uncapped, lowest-latency present, so <see cref="Auto"/> respects that and stays uncapped.</para>
    /// <para><b>Which Metal.</b> The sentence above measures the VELDRID Metal present, which is the only Metal
    /// implementation that has ever run a windowed session. Both Metal implementations take the capped arm today
    /// (see <see cref="Resolve"/>), and whether the engine's own native Metal backend needs the cap at all is an
    /// open MEASUREMENT rather than a settled fact.</para>
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

        /// <summary>The fallback cap for <see cref="Auto"/> on Metal + vsync when the live display refresh rate is
        /// unavailable. 120 Hz: high enough to feel smooth, low enough to stop the free-run burning a whole core.</summary>
        public const int DefaultMetalAutoCapHz = 120;

        /// <summary>The engine picks a backend-aware default (uncapped where vsync throttles, a real cap on Metal +
        /// vsync). The default <see cref="FrameCap"/> value.</summary>
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
        /// (Metal + <see cref="PresentMode.Vsync"/>) - the <paramref name="displayRefreshHz"/> when positive, else
        /// <see cref="DefaultMetalAutoCapHz"/> - and 0 (uncapped) everywhere else, since vsync throttles there or the
        /// consumer chose an uncapped present. Pure.
        /// <para>
        /// <b>The Metal arm covers BOTH Metal implementations, and that is a conservative default awaiting a
        /// measurement, not a finding.</b> The real question this arm asks is whether the backend's present
        /// throttles the CPU from vsync alone, which is why it was an equality against the Veldrid Metal kind for
        /// as long as that was the only Metal there was. It is a family predicate now
        /// (<c>GpuBackendKinds.IsMetal</c>) because the native Metal backend sets <c>displaySyncEnabled</c>
        /// unconditionally and bounds <c>maximumDrawableCount</c>, either of which may make the software cap
        /// redundant there. Defaulting it into the capped arm is safe in both directions: if that present does
        /// throttle, a cap at the display refresh does not bind and costs nothing, and if it does not, the cap is
        /// required. Gate 5's windowed pass reads which it is, with a mid-session vsync toggle as the instrument,
        /// and the answer is recorded here either way (decision M-W3 of
        /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>). This is also the arm #380's
        /// present-pacing work will revisit.
        /// </para>
        /// <para>
        /// The other native backends are NOT in this arm and must not be added to it: a
        /// <see cref="GpuBackendKind.Direct3D11Native"/> or <see cref="GpuBackendKind.VulkanNative"/> present
        /// throttles the CPU from vsync exactly as its incumbent's does, so each has to behave identically to the
        /// implementation it is being A/B'd against.
        /// </para>
        /// </summary>
        public int Resolve(GpuBackendKind backend, PresentMode present, int displayRefreshHz)
            => Kind switch
            {
                CapKind.Fixed => Value,
                CapKind.Uncapped => 0,
                _ => backend.IsMetal() && present == PresentMode.Vsync
                        ? (displayRefreshHz > 0 ? displayRefreshHz : DefaultMetalAutoCapHz)
                        : 0,
            };
    }
}
