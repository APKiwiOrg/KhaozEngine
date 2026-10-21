using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// Exact visibility gathers selected while building a fragment program, before SPIR-V compilation. Metal's raw
/// R8 gathers match Load bits, but the current 3x3 substitution changes visibility arithmetic and release decisions.
/// The current-side substitution is therefore withdrawn on Metal. Both history blocks still gather there.
/// Vulkan's gathered full programs fail exact fused/split colour identity on lavapipe, even though their isolated
/// decision and A/B oracles pass. Vulkan therefore retains every load, restoring the pre-gather SPIR-V byte for byte.
/// Direct3D 11 retains all twelve gathers.
/// </summary>
internal static class TemporalVisibilityGatherPolicy
{
    sealed class Sources(string gathered, string loaded)
    {
        readonly string _gatheredCrlf = gathered.Replace("\n", "\r\n", StringComparison.Ordinal);
        readonly string _loadedCrlf = loaded.Replace("\n", "\r\n", StringComparison.Ordinal);

        internal string UseLoads(string fragment) => fragment.Replace(gathered, loaded, StringComparison.Ordinal)
            .Replace(_gatheredCrlf, _loadedCrlf, StringComparison.Ordinal);
    }

    static readonly Sources Current = new(TemporalVisibilityFastPath.CurrentSources(true),
        TemporalVisibilityFastPath.CurrentSources(false));
    static readonly Sources Previous = new(TemporalVisibilityFastPath.BlockSources("previous", "PreviousKeyShadowVisibility", true),
        TemporalVisibilityFastPath.BlockSources("previous", "PreviousKeyShadowVisibility", false));
    static readonly Sources Depth = new(TemporalVisibilityFastPath.BlockSources("stored", "PrevDepth", true),
        TemporalVisibilityFastPath.BlockSources("stored", "PrevDepth", false));

    internal static bool GatherCurrent(GpuBackendKind backend) => backend is not (GpuBackendKind.MetalNative
        or GpuBackendKind.VulkanNative);

    /// <summary>Only generated source blocks change. This also works after the path-export test hook,
    /// which leaves that block intact. A frozen D program or a program without visibility comes back unchanged.</summary>
    internal static string ForBackend(string fragment, GpuBackendKind backend)
    {
        if (!GatherCurrent(backend)) fragment = Current.UseLoads(fragment);
        if (backend == GpuBackendKind.VulkanNative) fragment = Depth.UseLoads(Previous.UseLoads(fragment));
        return fragment;
    }
}
