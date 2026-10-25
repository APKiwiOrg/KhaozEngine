using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal sealed record D3D11HeadlessDiagnosticSettings(GpuPipelineDiagnosticRequest Diagnostics,
    D3D11AdapterRequest Adapter, string? AdapterKey, uint CreationFlags, uint ShaderFlags,
    D3D11RecordMode RecordMode, bool RealDrain, int FramesInFlight, string? ShaderCache,
    bool CompileCache) : IGpuHeadlessDiagnosticSettings
{
    public GpuBackendKind Backend => GpuBackendKind.Direct3D11Native;
    public IGpuHeadlessDiagnosticSettings WithDiagnostics(GpuPipelineDiagnosticRequest request)
        => this with { Diagnostics = request };
    public IGpuHeadlessDiagnosticSettings WithAdapter(string adapterKey) => this with { AdapterKey = adapterKey };
    internal D3D11DiagnosticOptions CompilerOptions(bool actualDebugLayer)
        => new(Diagnostics.CompilerMetadata, RecordMode, actualDebugLayer, Diagnostics.RuntimeInvocations);
    internal static D3D11HeadlessDiagnosticSettings Resolve(GpuPipelineDiagnosticRequest request, Func<string, string?> read)
    {
        string? Read(string key)
        {
            string? value = read(key);
            if (value is not null && GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(value) > 4096)
                throw new InvalidOperationException("A frozen environment option exceeds 4096 UTF-8 bytes.");
            return value;
        }
        string? reflection = Read("KE_D3D11_PIPELINE_DIAGNOSTICS");
        string? invocations = Read("KE_D3D11_INVOCATION_DIAGNOSTICS");
        if (!request.CompilerMetadata && reflection == "1" || !request.RuntimeInvocations && invocations == "1")
            throw new InvalidOperationException("Approved environment requests diagnostics on the timed device.");
        string? debug = Read(D3D11ShaderDebug.EnvVarName);
        return new(request, D3D11AdapterSelection.Parse(Read(D3D11AdapterSelection.EnvVarName)), null,
            GpuD3D11DeviceFlags.Resolve(Read(GpuD3D11DeviceFlags.EnvVarName), out _) | D3D11DebugLayer.Resolve(debug, out _),
            D3D11ShaderDebug.Resolve(debug, out _), D3D11RecordModes.Resolve(Read(D3D11RecordModes.EnvVarName), out _),
            D3D11RealDrain.Resolve(Read(D3D11RealDrain.EnvVarName), out _),
            D3D11FramesInFlight.Resolve(Read(D3D11FramesInFlight.EnvVarName), out _),
            Read(D3D11DxbcCache.EnvVarName), SpirvCompileCache.IsEnabled(Read(SpirvCompileCache.DisableVariable)));
    }
}
