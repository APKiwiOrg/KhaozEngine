using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The temporal resolve's two entry points over one set of rules
    /// (<see cref="ShaderSources.TemporalPrepareGlsl"/> and <see cref="ShaderSources.TemporalAccumulateGlsl"/>). Their
    /// outputs are the same.</summary>
    internal enum TemporalResolveEntry
    {
        /// <summary>One display-resolution pass that prepares its 3x3 inline
        /// (<see cref="ShaderSources.TemporalResolveFrag"/>), then the depth store.</summary>
        Fused,

        /// <summary>A pass per internal texel that prepares each texel once into the split's targets
        /// (<see cref="ShaderSources.TemporalPrepareFrag"/>), then a lean pass per display pixel over them
        /// (<see cref="ShaderSources.TemporalAccumulateFrag"/>).</summary>
        Split,
    }

    /// <summary>
    /// WHICH ENTRY POINT THE RESOLVE RECORDS, per graphics backend and preset, from measurement. The split does the
    /// per-texel work once per internal texel rather than once per display pixel around it, so it can pay only where
    /// display pixels outnumber internal texels (the upscaling presets). At Native there is one texel a pixel,
    /// nothing to share, and the split's second pass and targets are pure cost.
    /// <para><b>THE TABLE</b>, the cost measurement (TemporalResolveCostPerfGpuTests) on the hosted NVIDIA runners
    /// (run 36495925808), resolve and sharpen at most, fused against split (ms). Direct3D 11 on a Tesla T4: the boxes at Quality 1.82 against 2.06 and at Native
    /// 1.83 against 2.63 at 2560x1440, the moving field at Quality 5.18 against 5.21 at 3456x2234, so the split is
    /// slower at every size and preset. Vulkan on the same T4: the boxes at Quality 2.87 against 2.29 (Windows) and
    /// 2.47 against 2.58 (Linux) at 2560x1440, the moving field at Quality 6.90 against 6.07 and 6.64 against 6.24 at
    /// 3456x2234, at Native 3.41 against 3.57 and 3.30 against 3.80. Metal on an Apple M2 Max: the boxes at Quality
    /// 1.94 against 1.68, at Native even within the spread. So the split on Metal and Vulkan at the upscaling
    /// presets, and the fused pass on Direct3D 11 and at Native everywhere, where a backend and preset holds none of
    /// the split's targets. The measurement prints this pick beside its figures, which is how the table is
    /// kept.</para>
    /// <para><b>THE OVERRIDE.</b> <see cref="EnvironmentVariable"/> set to <c>fused</c> or <c>split</c> forces one
    /// entry point for every scene and renderer the process creates, for tests and measurement. It is read once. A
    /// scene can also be forced on its own (<c>Scene3D.TemporalResolveEntryForTests</c>).</para>
    /// </summary>
    internal static class TemporalResolvePolicy
    {
        /// <summary>The environment variable that forces an entry point for the process.</summary>
        public const string EnvironmentVariable = "KE_TEMPORAL_RESOLVE";

        /// <summary>The entry point <see cref="EnvironmentVariable"/> forced when the process first asked, or
        /// null.</summary>
        public static TemporalResolveEntry? Forced { get; } =
            Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

        /// <summary><c>fused</c> or <c>split</c> in any case, anything else null.</summary>
        internal static TemporalResolveEntry? Parse(string? value) =>
            string.Equals(value, "fused", StringComparison.OrdinalIgnoreCase) ? TemporalResolveEntry.Fused
            : string.Equals(value, "split", StringComparison.OrdinalIgnoreCase) ? TemporalResolveEntry.Split
            : null;

        /// <summary>The measured pick for a backend and preset (the table in the summary).</summary>
        public static TemporalResolveEntry Measured(GpuBackendKind backend, TemporalUpscale preset) =>
            preset == TemporalUpscale.Native || backend.IsDirect3D11()
                ? TemporalResolveEntry.Fused
                : TemporalResolveEntry.Split;

        /// <summary>The entry point a resolve records: the forced one, else the measured pick.</summary>
        public static TemporalResolveEntry Choose(GpuBackendKind backend, TemporalUpscale preset) =>
            Forced ?? Measured(backend, preset);
    }
}
