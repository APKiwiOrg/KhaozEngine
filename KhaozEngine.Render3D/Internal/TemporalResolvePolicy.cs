using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>The temporal resolve's two entry points over one set of rules
    /// (<see cref="ShaderSources.TemporalPrepareGlsl"/> and <see cref="ShaderSources.TemporalAccumulateGlsl"/>),
    /// applied to the same values. That they write the same history bit for bit is what
    /// <c>TemporalEntryIdentityGpuTests</c> checks on each backend it runs on. On a software Vulkan device (llvmpipe)
    /// it holds the state identical and the colour within a measured bound.</summary>
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
    /// WHICH ENTRY POINT THE RESOLVE RECORDS, from measurement: the split, on every graphics backend at every internal
    /// size. The split does the per-texel work once per internal texel rather than once per display pixel around it,
    /// and its lean second pass and 16-bit targets cost less than the fused pass's inline 3x3 even at the display's own
    /// size. The fused pass runs only where a device allows too few colour attachments for the split's first pass
    /// (<see cref="Supported"/>) and under the override below.
    /// <para><b>THE TABLE</b>, the cost measurement (TemporalResolveCostPerfGpuTests), resolve and sharpen at most,
    /// fused against split (ms). Direct3D 11 on a hosted Tesla T4, the backend that last kept the fused pass at the
    /// display's size: at Native, 2560x1440, the boxes 2.165 against 2.237 and the field 2.802 against 2.663, and at
    /// 3456x2234 (3342x2160 under the default render cap) 5.423 against 4.762 and 7.056 against 5.818. At Quality,
    /// 2560x1440, the boxes 2.107 against 1.695 and the field 2.640 against 1.959, and at 3456x2234 4.925 against 3.686
    /// and 6.320 against 4.373. Rounding the prepared values in integer steps slowed the fused pass, so the split is
    /// faster or even everywhere. Vulkan on the same GPU, on Windows and on Linux, was already faster or even split at
    /// every size, and Metal at half precision is faster split at every size (TEMPORAL-RESOLVE-UPSCALING-DESIGN
    /// section 3, The resolve). At the display's size the split's targets are display-sized, 24 bytes a pixel, 88.5 MB
    /// at 2560x1440. The measurement prints this pick beside its figures, which is how the table is kept.</para>
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

        /// <summary>The measured pick (the table in the summary).</summary>
        public const TemporalResolveEntry Measured = TemporalResolveEntry.Split;

        /// <summary>The entry point a resolve records: the forced one, else the measured pick.</summary>
        public static TemporalResolveEntry Choose() => Forced ?? Measured;

        /// <summary>The entry point a device can record: the fused one in place of the split where the split's first
        /// pass writes more colour attachments (<see cref="TemporalSplitFormats.FirstPassAttachments"/>) than the
        /// device allows (<see cref="GpuCapabilities.MaxColorAttachments"/>).</summary>
        public static TemporalResolveEntry Supported(TemporalResolveEntry entry, int maxColorAttachments) =>
            entry == TemporalResolveEntry.Split && TemporalSplitFormats.FirstPassAttachments > maxColorAttachments
                ? TemporalResolveEntry.Fused
                : entry;
    }
}
