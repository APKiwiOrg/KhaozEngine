using System;
using System.Collections.Immutable;
using System.Threading;
using KhaozEngine.Gpu.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

internal interface IVulkanPipelineExecutableApi
{
    Result Properties(ulong pipeline, ref uint count, Span<PipelineExecutablePropertiesKHR> values);
    Result Statistics(ulong pipeline, uint executable, ref uint count, Span<PipelineExecutableStatisticKHR> values);
}

internal interface IVulkanPipelineDiagnosticsSource
{
    VulkanPipelineDiagnostics? Diagnostics { get; }
}

/// <summary>Cold collection. Native materialization is admitted by a frozen per-read request.</summary>
internal sealed class VulkanPipelineDiagnostics
{
    internal static VulkanPipelineDiagnostics Production { get; } = new(false, false, false, null, "validation=unknown,statistics=off");
    internal static readonly GpuPipelineCompilerReadRequest MaximumRequest = new(64, 256, 16384, 256, 16384, 4);
    readonly bool _requested, _extension, _feature;
    IVulkanPipelineExecutableApi? _api;
    readonly string _configuration;
    readonly string? _failure;
    readonly IVulkanCompilerMaterializer _materializer;
    readonly GpuEffectiveDiagnosticConfiguration? _effective;
    internal bool StatisticsSupported => _extension && _feature;
    internal VulkanPipelineDiagnostics WithConfiguration(GpuEffectiveDiagnosticConfiguration? effective)
        => new(_requested, _extension, _feature, _api, _configuration, _failure, _materializer, effective);

    internal VulkanPipelineDiagnostics(bool requested, bool extensionSupported, bool featureEnabled,
        IVulkanPipelineExecutableApi? api, string configuration, string? failure = null,
        IVulkanCompilerMaterializer? materializer = null, GpuEffectiveDiagnosticConfiguration? effective = null)
    {
        _requested = requested; _extension = extensionSupported; _feature = featureEnabled;
        _api = api; _configuration = configuration; _failure = failure;
        _materializer = materializer ?? VulkanCompilerMaterializer.Instance; _effective = effective;
    }

    internal PipelineCreateFlags CaptureFlags => _requested && _extension && _feature
        ? PipelineCreateFlags.CreateCaptureStatisticsBitKhr : 0;
    internal void Bind(IVulkanPipelineExecutableApi api) => _api = api;

    internal GpuPipelineConstructionIdentity Construction(VulkanPipelineIdentity identity, PipelineCreateFlags flags)
        => new(GpuDiagnosticIdentityEncoding.Known(identity.PipelineSha256, "vulkan.pipeline/1"),
            GpuDiagnosticIdentityEncoding.Stages("source", identity.Stages),
            GpuDiagnosticIdentityEncoding.Stages("artifact", identity.Stages),
            GpuDiagnosticIdentityEncoding.Known(identity.LayoutSha256, "vulkan.layout/1"),
            _effective is not null ? GpuDiagnosticIdentityEncoding.Configuration(_effective)
                : GpuDiagnosticIdentityEncoding.Text($"VK_KHR_pipeline_executable_properties,requested={_requested},extension={_extension},"
                    + $"pipelineExecutableInfo={_feature},flags={(uint)flags},{_configuration},{identity.CompilerFlags}", "vulkan.diagnostic-configuration/1"),
            identity.CodeGeneration, identity.CompilerFlags, identity.LocalSizeX, identity.LocalSizeY, identity.LocalSizeZ, identity.Stages);

    internal GpuPipelineCompilerReport Read(ulong pipeline, PipelineCreateFlags flags, VulkanPipelineIdentity identity,
        GpuPipelineConstructionIdentity? construction = null)
    {
        var witness = construction ?? Construction(identity, flags);
        if (pipeline != 0 && _failure is null && !_requested)
            return new(witness, GpuDiagnosticStatus.Unsupported, "Pipeline diagnostics were not requested on this device.", []);
        return Read(pipeline, flags, identity, MaximumRequest, _ => { }, default, witness).Report;
    }

    internal GpuPipelineCompilerReadResult Read(ulong pipeline, PipelineCreateFlags flags, VulkanPipelineIdentity identity,
        GpuPipelineCompilerReadRequest request, Action<string> checkOperation, CancellationToken cancellation,
        GpuPipelineConstructionIdentity? construction = null)
    {
        var witness = construction ?? Construction(identity, flags);
        string? absence = !_extension ? "VK_KHR_pipeline_executable_properties is unavailable."
            : !_feature ? "pipelineExecutableInfo is unavailable or was not enabled." : null;
        string? invalid = pipeline == 0 ? "The pipeline was disposed or has no native handle."
            : _failure ?? (!_requested ? "Compiler capture was not requested. This is unqueried evidence, not capability absence." : null);
        if (invalid is null && absence is null && (flags & PipelineCreateFlags.CreateCaptureStatisticsBitKhr) == 0)
            invalid = "The pipeline lacks VK_PIPELINE_CREATE_CAPTURE_STATISTICS_BIT_KHR.";
        if (invalid is null && absence is null && _api is null) invalid = "Pipeline executable entry points are unavailable.";
        return new VulkanCompilerRead(_api, _materializer, pipeline, witness, request, checkOperation, cancellation)
            .Run(invalid, absence);
    }

    internal static GpuPipelineConstructionIdentity DisabledIdentity { get; } = Production.Construction(new("", "", "", "", "unavailable"), 0);
    internal static GpuPipelineCompilerReport Disabled(bool disposed)
        => new(DisabledIdentity, disposed ? GpuDiagnosticStatus.Invalid : GpuDiagnosticStatus.Unsupported,
            disposed ? "The pipeline was disposed." : "Pipeline diagnostics were not requested on this device.", []);

    internal static GpuDiagnosticMetric Missing(string unit, string reason)
        => new(null, unit, "VK_KHR_pipeline_executable_properties", GpuDiagnosticStatus.Unsupported,
            reason, GpuDiagnosticProvenance.Unknown);
}

internal sealed class VulkanPipelineDiagnosticBinding
{
    readonly VulkanPipelineDiagnostics _diagnostics;
    readonly VulkanPipelineIdentity _identity;
    readonly PipelineCreateFlags _flags;
    internal GpuPipelineConstructionIdentity ConstructionIdentity { get; }
    internal VulkanPipelineDiagnosticBinding(VulkanPipelineDiagnostics diagnostics, VulkanPipelineIdentity identity, PipelineCreateFlags flags)
    {
        _diagnostics = diagnostics; _identity = identity; _flags = flags;
        ConstructionIdentity = diagnostics.Construction(identity, flags);
    }
    internal GpuPipelineCompilerReport Read(ulong handle)
        => _diagnostics.Read(handle, _flags, _identity, ConstructionIdentity);
    internal GpuPipelineCompilerReadResult Read(ulong handle, GpuPipelineCompilerReadRequest request, Action<string> check, CancellationToken cancellation)
        => _diagnostics.Read(handle, _flags, _identity, request, check, cancellation, ConstructionIdentity);
}
