using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal enum CoverageGpuFilterPath { Exact, FilteredPreserve, FilteredFlush }

/// <summary>Creation-fixed capability choice. Sampling arithmetic never enables a path.</summary>
internal static class CoverageGpuFilterPolicy
{
    // Vulkan guarantees at least 128 MiB for a storage-buffer range. Larger prototype work reservations
    // keep the original exact pipeline instead of increasing the supported-device memory requirement.
    internal const long MaximumWorkWords = (128L * 1024 * 1024) / sizeof(uint);

    internal static CoverageGpuFilterPath Resolve(GpuBackendKind backend, GpuCapabilities capabilities)
    {
        if (backend != GpuBackendKind.VulkanNative || !capabilities.SupportsShaderFloatControls || !capabilities.SupportsFloat32Rte)
            return CoverageGpuFilterPath.Exact;
        if (capabilities.SupportsFloat32DenormPreserve) return CoverageGpuFilterPath.FilteredPreserve;
        return capabilities.SupportsFloat32DenormFlush ? CoverageGpuFilterPath.FilteredFlush : CoverageGpuFilterPath.Exact;
    }
}
