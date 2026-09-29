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
    /// WHICH ENTRY POINT THE RESOLVE RECORDS, per graphics backend, from measurement, on whether the internal size is
    /// below the display's (<see cref="Upscales"/>): the preset's ratio or an explicit one, times the render cap, as
    /// the scene sizes its targets, so a ratio override or a cap gets the entry point its real size measured. The split
    /// does the per-texel work once per internal texel rather than once per display pixel around it, so it gains most
    /// where display pixels outnumber internal texels. At the display's own size it has nothing to share, and gains
    /// only where its lean second pass and its 16-bit targets cost less than the fused pass's inline 3x3.
    /// <para><b>THE TABLE</b>, the cost measurement (TemporalResolveCostPerfGpuTests), resolve and sharpen at most,
    /// fused against split (ms), with the split's colour and reactive at 16 bits. On a Tesla T4, hosted runs
    /// 36517355602 (Direct3D 11, Windows Vulkan) and 36524126806 (Linux Vulkan), below the display the split is faster
    /// on every backend and size: the boxes at 2560x1440 Quality 1.99 against 1.69 on Direct3D 11, 2.68 against 1.66
    /// on Windows Vulkan and 2.41 against 1.95 on Linux Vulkan, the moving field at 3456x2234 Quality 5.70 against
    /// 4.49, 6.65 against 4.73 and 6.69 against 5.21. The Native rows at 3456x2234 render at 3342x2160 under the
    /// default render cap, so they lie below the display too, and there the split is faster on every backend. At the
    /// display's own size, 2560x1440 at Native, Windows Vulkan is faster split by 0.70 and 0.36, Linux Vulkan by 0.03,
    /// and Direct3D 11 slower, the boxes 2.00 against 2.26 and the field 2.65 against 2.75. On an Apple M2 Max under
    /// Metal, locally, below the display the split is faster by 0.22 to 0.65, the boxes at 2560x1440 Quality 1.98
    /// against 1.67, and at the display's size even or faster, the moving field 2.51 against 2.50 and the boxes 2.02
    /// against 1.87. So the split wherever the internal size is below the display, and at the display's size on Metal
    /// and Vulkan, with the fused pass on Direct3D 11 there. At the display's size the split's targets are
    /// display-sized, 24 bytes a pixel, 88.5 MB at 2560x1440. The measurement prints this pick beside its figures,
    /// which is how the table is kept.</para>
    /// <para><b>THE OVERRIDE, FOR DIAGNOSIS.</b> <see cref="EnvironmentVariable"/> set to <c>fused</c> or <c>split</c>
    /// forces one entry point for every scene and renderer the process creates: for tests, for measurement, and to
    /// tell a fault in one entry point from the other on a player's machine. It is not a setting, a game does not ship
    /// it set, and it is read once. A scene can also be forced on its own
    /// (<c>Scene3D.TemporalResolveEntryForTests</c>).</para>
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

        /// <summary>Whether the scene renders below the display's size on either axis, which is what the entry point is
        /// chosen on: the preset's ratio or an explicit one, times the render cap, as the internal targets are
        /// sized.</summary>
        public static bool Upscales(int internalWidth, int internalHeight, int displayWidth, int displayHeight) =>
            internalWidth < displayWidth || internalHeight < displayHeight;

        /// <summary>The measured pick for a backend rendering below the display's size or at it (the table in the
        /// summary).</summary>
        public static TemporalResolveEntry Measured(GpuBackendKind backend, bool upscales) =>
            upscales || backend.IsVulkan() || backend.IsMetal()
                ? TemporalResolveEntry.Split
                : TemporalResolveEntry.Fused;

        /// <summary>The entry point a resolve records: the forced one, else the measured pick.</summary>
        public static TemporalResolveEntry Choose(GpuBackendKind backend, bool upscales) =>
            Forced ?? Measured(backend, upscales);
    }
}
