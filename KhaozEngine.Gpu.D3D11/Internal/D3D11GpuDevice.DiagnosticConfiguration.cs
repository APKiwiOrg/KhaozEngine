using System;
using System.Globalization;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Internal;
using Vortice.DXGI;

namespace KhaozEngine.Gpu.D3D11.Internal;

internal sealed partial class D3D11GpuDevice : IGpuDeviceDiagnosticConfiguration
{
    GpuEffectiveDiagnosticConfiguration? _diagnosticConfiguration;
    public GpuEffectiveDiagnosticConfiguration DiagnosticConfiguration => _diagnosticConfiguration
        ?? throw new InvalidOperationException("This device did not use explicit frozen diagnostic settings.");
    [SupportedOSPlatform("windows")]
    internal static string AdapterKey(IDXGIAdapter adapter)
        => "luid:" + adapter.Description.Luid.ToString("x16", CultureInfo.InvariantCulture);
    [SupportedOSPlatform("windows")]
    static GpuEffectiveDiagnosticConfiguration EffectiveConfiguration(IDXGIAdapter adapter,
        D3D11HeadlessDiagnosticSettings settings, bool debugLayer, string? cache, uint nativeCreationFlags)
    {
        var result = adapter.CheckInterfaceSupport(typeof(IDXGIDevice).GUID, out long driver);
        if (result.Failure) throw new InvalidOperationException("DXGI could not identify the selected device driver.");
        if (settings.AdapterKey is { } pinned && AdapterKey(adapter) != pinned)
            throw new InvalidOperationException("The actual DXGI adapter differs from the frozen LUID.");
        return new(GpuBackendKind.Direct3D11Native, AdapterKey(adapter), "DXGI driver=" + driver.ToString("x16", CultureInfo.InvariantCulture),
            settings.Diagnostics, settings.Diagnostics.CompilerMetadata, settings.Diagnostics.RuntimeInvocations,
            debugLayer, false, true, false, 0,
            [new("d3d.flags1", settings.ShaderFlags.ToString("X8", CultureInfo.InvariantCulture)), new("d3d.flags2", "0"),
             new("d3d.profiles", "vs_5_0,ps_5_0,cs_5_0"), new("entry", "main"), new("frontend", SpirvFrontEndPin.Identity),
             new("hlsl", HlslCrossCompilePin.Identity), new("toolchain", SpirvToolchainVersion.Identity)],
            [new("cache.dxbc", cache ?? "disabled"), new("cache.spirv.memo", settings.CompileCache.ToString()),
             new("device.creation-flags", nativeCreationFlags.ToString("X8", CultureInfo.InvariantCulture)),
             new("drain", settings.RealDrain.ToString()), new("frames-in-flight", settings.FramesInFlight.ToString(CultureInfo.InvariantCulture)),
             new("recording", settings.RecordMode.ToString())]);
    }
}
