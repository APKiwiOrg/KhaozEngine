using System.Collections.Immutable;

namespace KhaozEngine.Gpu;

/// <summary>A digest with explicit encoding provenance and availability.</summary>
public sealed record GpuDiagnosticDigest(string? Sha256, string Source, GpuDiagnosticStatus Status, string? Reason);
/// <summary>Exact original stage source, optional emitted source and actual compiled bytes.</summary>
public sealed record GpuPipelineStageIdentity(string Stage, string EntryPoint,
    GpuDiagnosticDigest SourceSha256, GpuDiagnosticDigest? EmittedSourceSha256, GpuDiagnosticDigest ShaderArtifactSha256);
/// <summary>Detached, precomputed construction evidence, safe to retain after native disposal.</summary>
public sealed record GpuPipelineConstructionIdentity(GpuDiagnosticDigest PipelineSha256, GpuDiagnosticDigest SourceSha256,
    GpuDiagnosticDigest ShaderArtifactSha256, GpuDiagnosticDigest LayoutSha256,
    GpuDiagnosticDigest DiagnosticConfigurationSha256, GpuDiagnosticDigest CodeGenerationSha256,
    string CompilerFlags, uint LocalSizeX, uint LocalSizeY, uint LocalSizeZ, ImmutableArray<GpuPipelineStageIdentity> Stages);
/// <summary>Optional allocation-free access to the same immutable construction witness.</summary>
public interface IGpuPipelineConstructionDiagnostics
{
    GpuPipelineConstructionIdentity ConstructionIdentity { get; }
}
