using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed record MetalHeadlessDiagnosticSettings(GpuPipelineDiagnosticRequest Diagnostics,
    MetalDeviceRequest Device, string? AdapterKey, int FramesInFlight, string? MslCache,
    bool CompileCache, MetalValidationArming Validation) : IGpuHeadlessDiagnosticSettings
{
    public GpuBackendKind Backend => GpuBackendKind.MetalNative;
    public IGpuHeadlessDiagnosticSettings WithDiagnostics(GpuPipelineDiagnosticRequest request)
        => request.RuntimeInvocations ? throw new NotSupportedException("Metal has no runtime invocation route.") : this with { Diagnostics = request };
    public IGpuHeadlessDiagnosticSettings WithAdapter(string adapterKey) => this with { AdapterKey = adapterKey };
    internal static MetalHeadlessDiagnosticSettings Resolve(GpuPipelineDiagnosticRequest request, Func<string, string?> read,
        MetalValidationArming validation)
    {
        string? Read(string key)
        {
            string? value = read(key);
            if (value is not null && GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(value) > 4096)
                throw new InvalidOperationException("A frozen environment option exceeds 4096 UTF-8 bytes.");
            return value;
        }
        if (request.RuntimeInvocations) throw new NotSupportedException("Metal has no runtime invocation route.");
        return new(request, MetalDeviceSelection.Parse(Read(MetalDeviceSelection.EnvVarName)), null,
            MetalFramesInFlight.Resolve(Read(MetalFramesInFlight.EnvVarName), out _), Read(MetalMslCache.EnvVarName),
            SpirvCompileCache.IsEnabled(Read(SpirvCompileCache.DisableVariable)), validation);
    }
}
