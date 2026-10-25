using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal sealed record VulkanHeadlessDiagnosticSettings(GpuPipelineDiagnosticRequest Diagnostics,
    VulkanDeviceRequest Device, string? AdapterKey, VulkanValidationMode Validation, int FramesInFlight,
    string? PipelineCache, string? SpirvCache, bool CompileCache) : IGpuHeadlessDiagnosticSettings
{
    public GpuBackendKind Backend => GpuBackendKind.VulkanNative;
    public IGpuHeadlessDiagnosticSettings WithDiagnostics(GpuPipelineDiagnosticRequest request)
        => request.RuntimeInvocations ? throw new NotSupportedException("Vulkan has no runtime invocation diagnostics route.")
            : this with { Diagnostics = request };
    public IGpuHeadlessDiagnosticSettings WithAdapter(string adapterKey) => this with { AdapterKey = adapterKey };
    internal static VulkanHeadlessDiagnosticSettings Resolve(GpuPipelineDiagnosticRequest request, Func<string, string?> read)
    {
        string? Read(string key)
        {
            string? value = read(key);
            if (value is not null && GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(value) > 4096)
                throw new InvalidOperationException("A frozen environment option exceeds 4096 UTF-8 bytes.");
            return value;
        }
        string? diagnostics = Read("KE_VULKAN_PIPELINE_DIAGNOSTICS");
        if (!request.CompilerMetadata && diagnostics == "1")
            throw new InvalidOperationException("Approved environment requests Vulkan compiler capture on the timed device.");
        if (request.RuntimeInvocations) throw new NotSupportedException("Vulkan has no runtime invocation diagnostics route.");
        return new(request, VulkanPhysicalDeviceSelection.Parse(Read(VulkanPhysicalDeviceSelection.EnvVarName)), null,
            VulkanValidation.Parse(Read(VulkanValidation.EnvVarName), out _),
            VulkanFramesInFlight.Resolve(Read(VulkanFramesInFlight.EnvVarName), out _),
            Read(VulkanPipelineCacheFile.EnvVarName), Read(SpirvBytesCache.EnvVarName),
            SpirvCompileCache.IsEnabled(Read(SpirvCompileCache.DisableVariable)));
    }
}
