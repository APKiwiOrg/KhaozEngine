using System.Collections.Immutable;

namespace KhaozEngine.Gpu;

/// <summary>Requested optional observation features, distinct from their effective native state.</summary>
public sealed record GpuPipelineDiagnosticRequest(bool CompilerMetadata, bool RuntimeInvocations);
/// <summary>One backend-produced option. Tables are canonical ordinal ordered and unique.</summary>
public sealed record GpuDiagnosticOption(string Key, string Value);
/// <summary>Effective native configuration, supplied by the backend rather than inferred by a collector.</summary>
public sealed record GpuEffectiveDiagnosticConfiguration(GpuBackendKind Backend, string AdapterKey, string Driver,
    GpuPipelineDiagnosticRequest Requested, bool CompilerMetadataEnabled, bool RuntimeInvocationsEnabled,
    bool DebugLayerEnabled, bool ValidationEnabled, bool PipelineStatisticsSupported, bool PipelineExecutableFeatureEnabled,
    uint PipelineCaptureFlags, ImmutableArray<GpuDiagnosticOption> CodeGenerationOptions,
    ImmutableArray<GpuDiagnosticOption> ObservationOptions);
/// <summary>Optional effective configuration on a native device.</summary>
public interface IGpuDeviceDiagnosticConfiguration
{
    GpuEffectiveDiagnosticConfiguration DiagnosticConfiguration { get; }
}
/// <summary>Backend-owned immutable resolved settings. Copies preserve all unrelated native/compiler choices.</summary>
public interface IGpuHeadlessDiagnosticSettings
{
    GpuBackendKind Backend { get; }
    GpuPipelineDiagnosticRequest Diagnostics { get; }
    IGpuHeadlessDiagnosticSettings WithDiagnostics(GpuPipelineDiagnosticRequest request);
    IGpuHeadlessDiagnosticSettings WithAdapter(string adapterKey);
}
/// <summary>Optional settings route beside the ordinary provider. It is adopted through the normal context gate.</summary>
public interface IGpuHeadlessDiagnosticProvider
{
    IGpuHeadlessDiagnosticSettings ResolveHeadlessSettings(GpuPipelineDiagnosticRequest request);
    GpuProviderDevice CreateHeadless(IGpuHeadlessDiagnosticSettings settings);
}
