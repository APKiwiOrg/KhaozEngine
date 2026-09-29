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
    /// per-texel work once per internal texel rather than once per display pixel around it, so it pays where display
    /// pixels outnumber internal texels (the upscaling presets) and on a backend whose compiler makes the fused pass
    /// dear. At Native there is one texel a pixel and nothing to share.
    /// <para><b>THE TABLE</b>, resolve and sharpen, fused against split, the twelve keyed boxes at 2560x1440 (ms):
    /// Direct3D 11 on a Tesla T4, Quality 1.80 against 1.65, Native 1.77 against 2.21. Vulkan on the same T4, Quality
    /// 2.85 against 1.77 (Windows) and 2.64 against 1.86 (Linux), Native 2.88 against 2.26 and 2.76 against 2.26. Metal
    /// on an Apple M2 Max, Quality 1.92 against 1.70, Native even within the spread. So the split everywhere but
    /// Native on Direct3D 11, where it is slower, and on Metal, where it gains nothing and its targets would cost
    /// memory for no return. The cost measurement (TemporalResolveCostPerfGpuTests) times both on any real GPU and
    /// prints this pick beside them, which is how the table is kept.</para>
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
            preset == TemporalUpscale.Native && (backend.IsDirect3D11() || backend.IsMetal())
                ? TemporalResolveEntry.Fused
                : TemporalResolveEntry.Split;

        /// <summary>The entry point a resolve records: the forced one, else the measured pick.</summary>
        public static TemporalResolveEntry Choose(GpuBackendKind backend, TemporalUpscale preset) =>
            Forced ?? Measured(backend, preset);
    }
}
