using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Exact visibility gathers selected while building a fragment program, before SPIR-V compilation. Metal's raw
/// R8 gathers match Load bits, but the current 3x3 substitution changes visibility arithmetic and release decisions.
/// The current-side substitution is therefore withdrawn on Metal. Both history blocks still gather there.
/// Direct3D 11 and Vulkan retain all twelve gathers, subject to their unchanged exact device oracles.
/// </summary>
internal static class TemporalVisibilityGatherPolicy
{
    static readonly string CurrentGathers = TemporalVisibilityFastPath.CurrentSources(true);
    static readonly string CurrentLoads = TemporalVisibilityFastPath.CurrentSources(false);
    static readonly string CurrentGathersCrlf = CurrentGathers.Replace("\n", "\r\n", StringComparison.Ordinal);
    static readonly string CurrentLoadsCrlf = CurrentLoads.Replace("\n", "\r\n", StringComparison.Ordinal);

    internal static bool GatherCurrent(GpuBackendKind backend) => backend != GpuBackendKind.MetalNative;

    /// <summary>Only the generated current-source block changes. This also works after the path-export test hook,
    /// which leaves that block intact. A frozen D program or a program without visibility comes back unchanged.</summary>
    internal static string ForBackend(string fragment, GpuBackendKind backend) => GatherCurrent(backend) ? fragment
        : fragment.Replace(CurrentGathers, CurrentLoads, StringComparison.Ordinal)
            .Replace(CurrentGathersCrlf, CurrentLoadsCrlf, StringComparison.Ordinal);
}
