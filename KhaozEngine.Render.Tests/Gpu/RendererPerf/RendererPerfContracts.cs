using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfLayout { TotalOnly, PerPass }
internal enum RendererPerfMetricStatus { Available, NotExecuted, Unsupported, Invalid, BelowResolution, ModelIncomplete }
internal enum RendererPerfPublicationState { Completed, Refused, Failed }

internal sealed record RendererPerfMetric(double? Value, string Unit, string Source, RendererPerfMetricStatus Status, string? Reason);
internal sealed record RendererPerfBlock(string SceneId, RendererPerfLayout Layout, int Iteration, int WarmupFrames, int MeasuredFrames);
internal sealed record RendererPerfControl(int Id, string Version, string WorkSha256, int WarmupFrames, int MeasuredFrames);
internal sealed record RendererPerfRecipeIdentity(string SceneId, string Version, string Profile, string RecipeSha256,
    string SettingsSha256, string ShaderSha256, string GeometrySha256, string AssetSha256);
internal sealed record RendererPerfRuntimePolicy(string Configuration, string RuntimeVersion,
    ImmutableDictionary<string, string> Environment, string GcMode, bool TieredCompilation);
internal sealed record RendererPerfExecutionPolicy(string Backend, string AdapterPolicy, string Runner, string Workflow,
    string Leg, string CompletionPolicy);
internal sealed record RendererPerfLimits(int UnitSeconds, int FrameMilliseconds, int JobMinutes);
internal sealed record RendererPerfBudget(int MaximumBilledMinutes, decimal RatePerMinute, string Currency, decimal MaximumCostUsd);
internal sealed record RendererPerfApproval(string Reference, string Purpose, string CampaignReference, decimal CampaignMaximumUsd);

// Supplied by the workflow's independent commit/dispatch inspection, never by the manifest itself.
internal sealed record RendererPerfAuthorization(string ManifestSha256, string WorkloadSha, string AuthorizationSha,
    string ParentSha, int ParentCount, ImmutableArray<string> ChangedPaths, int RunAttempt, string Leg, string Runner, string Workflow,
    string ManifestChangeKind = "A", string? PreviousRunId = null);

internal sealed record RendererPerfRunIdentity(string UnitId, string ManifestSha256, string WorkloadSha,
    string BaselineMainSha, string AuthorizationSha, string WorkflowSha, string RunId, int Attempt, int ProcessId,
    string HarnessSha256, string Os, string Image, string Cpu, string Runtime, string GcMode, bool TieredCompilation,
    ImmutableDictionary<string, string> Environment, string Adapter, uint VendorId, uint DeviceId, string Driver,
    string Backend, string RecordingMode, bool? SoftwareAdapter, int? TimestampValidBits, double? TimestampPeriodNs,
    bool TimestampSupported, string ValidationFlags, string CaptureFlags, string CompletionPolicy);

internal sealed record RendererPerfRecipeReport(RendererPerfRecipeIdentity Identity, string SourceProvenance,
    string SequenceSha256, ImmutableDictionary<string, long> ExpectedRoutes, ImmutableDictionary<string, long> ActualRoutes,
    ImmutableDictionary<string, RendererPerfMetric> Facts);

// Raw counters and spans are retained. Analysis never substitutes a sum of children for GpuFrame.
[method: JsonConstructor]
internal readonly record struct RendererPerfFrameSample(int FrameId, int SequenceIndex, long CpuStartTicks,
    long CpuEndTicks, long CpuFrequency, ulong? GpuElapsedTicks, double? GpuPeriodNs, RendererPerfMetric GpuFrame,
    ImmutableArray<RendererPerfPassSample> Passes, ImmutableArray<RendererPerfCpuSpan> CpuPhases,
    ImmutableDictionary<string, RendererPerfMetric> Metrics, ImmutableArray<string> Errors);
internal sealed record RendererPerfCpuSpan(string Id, string? ParentId, long StartTicks, long EndTicks);
internal sealed record RendererPerfPassSample(string Id, int Occurrence, string? ParentId, int? ParentOccurrence,
    uint BeginMarker, uint EndMarker, ulong? ElapsedTicks, RendererPerfMetric Duration, bool DeviceOnly);
internal sealed record RendererPerfBlockReport(RendererPerfBlock Block, ImmutableArray<RendererPerfFrameSample> Warmup,
    ImmutableArray<RendererPerfFrameSample> Measured, ImmutableArray<string> Errors);
internal sealed record RendererPerfControlReport(RendererPerfControl Control, string StateSha256,
    ImmutableArray<RendererPerfFrameSample> Warmup, ImmutableArray<RendererPerfFrameSample> Measured,
    ImmutableDictionary<string, RendererPerfMetric> Metrics);
internal sealed record RendererPerfWorkRange(double? Minimum, double? Maximum, string Unit, string Source,
    string Provenance, string? Reason);
internal sealed record RendererPassWorkModel(string PassId, int Occurrence,
    ImmutableDictionary<string, RendererPerfWorkRange> Components, ImmutableArray<string> SerialDependencies,
    ImmutableArray<string> MissingEvidence);
internal sealed record RendererPerfMemoryReport(ImmutableArray<GpuResourceMemorySnapshot> Native,
    ImmutableDictionary<string, RendererPerfMetric> Process)
{
    [JsonRequired]
    public ImmutableArray<RendererPerfMemoryObservation> Observations { get; init; } = [];
    [JsonRequired]
    public ImmutableDictionary<string, RendererPerfMemoryPeak> Peaks { get; init; } = ImmutableDictionary<string, RendererPerfMemoryPeak>.Empty;
}
internal sealed record RendererPerfMemoryObservation(string Operation, string Stage, int? FirstFrame,
    long AtTicks, long Frequency, ImmutableArray<GpuResourceMemorySnapshot> Native,
    ImmutableDictionary<string, RendererPerfMetric> Process, string? Reason);
internal sealed record RendererPerfMemoryPeak(RendererPerfMetric Metric, int Observation);
internal sealed record RendererPerfAssessment(bool ValidForPromotion, ImmutableArray<string> Reasons,
    ImmutableDictionary<string, RendererPerfMetric> Metrics, ImmutableArray<RendererPassWorkModel> WorkModels);
internal sealed record RendererPerfPublication(RendererPerfPublicationState State, bool ReturnedByRun,
    bool FinalUnitPublished, string? OriginalFailure, ImmutableArray<string> Errors, RendererPerfMetric ElapsedMilliseconds,
    RendererPerfMetric ActualCostUsd, RendererPerfMetric EstimatedCostUsd)
{
    [JsonRequired]
    public RendererPerfFailureTextEvidence FailureText { get; init; } = new(false, 0, 0);
}
internal sealed record RendererPerfReport(string Schema, RendererPerfManifest Manifest, RendererPerfRunIdentity Identity,
    ImmutableArray<RendererPerfRecipeReport> Recipes, ImmutableArray<RendererPerfBlockReport> Blocks,
    RendererPerfMemoryReport Memory, ImmutableArray<GpuPipelineCompilerReport> Pipelines,
    ImmutableArray<RendererPerfControlReport> Controls, RendererPerfAssessment Assessment,
    RendererPerfPublication Publication, ImmutableArray<RendererPerfExpectedSidecar> Sidecars)
{
    [JsonRequired]
    public RendererPerfCompilerEvidence Compiler { get; init; } = RendererPerfCompilerEvidence.Pending;
    [JsonRequired]
    public ImmutableArray<RendererPerfFrameSample> Conditioning { get; init; } = [];
    [JsonRequired]
    public ImmutableArray<RendererPerfBatchReport> Batches { get; init; } = [];
    [JsonRequired]
    public ImmutableArray<RendererPerfStartupReport> Startup { get; init; } = [];
}

internal sealed class RendererPerfProtocolException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    internal string Code { get; } = code;
}
