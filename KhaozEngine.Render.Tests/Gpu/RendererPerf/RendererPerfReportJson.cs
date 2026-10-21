using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Gpu;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfManifest;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfReportJson
{
    const int MaximumBytes = 256 * 1024 * 1024;

    internal static byte[] Serialize(RendererPerfReport report)
    {
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
            _ = Parse(bytes);
            return bytes;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new RendererPerfProtocolException("Json", error.Message, error);
        }
    }

    internal static RendererPerfReport Parse(ReadOnlySpan<byte> utf8)
    {
        RendererPerfReport report = ReadJson<RendererPerfReport>(utf8, MaximumBytes);
        Validate(report);
        return report;
    }

    static void Validate(RendererPerfReport report)
    {
        Require(report.Schema == "khaozengine.renderer-perf-report/1", "Schema", "Expected renderer-perf-report/1.");
        RendererPerfManifest.Validate(report.Manifest);
        RendererPerfRunIdentity identity = report.Identity;
        foreach (string sha in new[] { identity.WorkloadSha, identity.BaselineMainSha, identity.AuthorizationSha, identity.WorkflowSha }) Hash(sha, 40);
        Hash(identity.ManifestSha256, 64);
        Hash(identity.HarnessSha256, 64);
        Require(identity.UnitId == report.Manifest.UnitId && identity.WorkloadSha == report.Manifest.WorkloadSha
            && identity.BaselineMainSha == report.Manifest.BaselineMainSha && identity.AuthorizationSha != identity.WorkloadSha
            && Text(identity.RunId) && identity.Attempt == 1 && identity.ProcessId > 0, "Identity", "Report execution identity does not identify one authorized first attempt.");
        Require(new[] { identity.Os, identity.Image, identity.Cpu, identity.Runtime, identity.GcMode, identity.Adapter,
            identity.Driver, identity.Backend, identity.RecordingMode, identity.ValidationFlags, identity.CaptureFlags,
            identity.CompletionPolicy }.All(Text), "Identity", "Report identity is missing runtime, hardware or configuration facts.");
        Require(identity.Environment.All(p => Text(p.Key) && Text(p.Value)), "Identity", "Environment entries cannot be null or empty.");
        Require(identity.TimestampPeriodNs == null || double.IsFinite(identity.TimestampPeriodNs.Value)
            && identity.TimestampPeriodNs > 0, "Identity", "Timestamp period must be positive and finite or unavailable.");
        Require(identity.TimestampValidBits == null || identity.TimestampValidBits is >= 1 and <= 64,
            "Identity", "Timestamp valid-bit count must be 1 to 64 or unavailable.");
        RendererPerfPublication publication = report.Publication;
        bool completed = publication.State == RendererPerfPublicationState.Completed;
        Require(completed == publication.FinalUnitPublished && (!completed || publication.ReturnedByRun)
            && (completed ? publication.OriginalFailure == null && publication.Errors.IsEmpty : Text(publication.OriginalFailure)),
            "Publication", "Completed requires final publication and no failures. Refused or Failed preserves the original cause.");
        Require(!report.Assessment.ValidForPromotion || completed && report.Assessment.Reasons.IsEmpty,
            "Publication", "Partial or refused evidence cannot be promoted.");
        Metric(publication.ElapsedMilliseconds);
        Metric(publication.ActualCostUsd);
        Metric(publication.EstimatedCostUsd);
        Metrics(report.Assessment.Metrics);
        Metrics(report.Memory.Process);
        if (report.Assessment.ValidForPromotion)
        {
            RendererPerfRuntimePolicy runtime = report.Manifest.Runtime;
            Require(identity.SoftwareAdapter == false && identity.VendorId == 0x10de && identity.DeviceId == 0x1eb8
                && identity.TimestampSupported && identity.TimestampValidBits != null && identity.TimestampPeriodNs != null
                && identity.Backend == report.Manifest.Execution.Backend && identity.CompletionPolicy == report.Manifest.Execution.CompletionPolicy
                && identity.Runtime == runtime.RuntimeVersion && identity.GcMode == runtime.GcMode
                && identity.TieredCompilation == runtime.TieredCompilation && identity.Environment.Count == runtime.Environment.Count
                && identity.Environment.All(p => runtime.Environment.TryGetValue(p.Key, out string? value) && value == p.Value),
                "Identity", "Promotion requires the declared T4 hardware, timestamps, runtime and completion configuration.");
        }
        Require(report.Recipes.Length <= report.Manifest.Recipes.Length && (!completed || report.Recipes.Length == report.Manifest.Recipes.Length),
            "Publication", "Recipe evidence is incomplete or excessive.");
        for (int i = 0; i < report.Recipes.Length; i++)
        {
            RendererPerfRecipeReport recipe = report.Recipes[i];
            Require(recipe.Identity == report.Manifest.Recipes[i] && Text(recipe.SourceProvenance), "Identity", "Recipe identity/order or source provenance differs.");
            Hash(recipe.SequenceSha256, 64);
            Metrics(recipe.Facts);
        }
        Require(report.Blocks.Length <= report.Manifest.Blocks.Length && (!completed || report.Blocks.Length == report.Manifest.Blocks.Length),
            "Publication", "Final report is missing blocks or contains extra blocks.");
        for (int i = 0; i < report.Blocks.Length; i++)
        {
            RendererPerfBlockReport block = report.Blocks[i];
            Require(block.Block == report.Manifest.Blocks[i], "Publication", "Report block order differs from the manifest.");
            ValidateSamples(block.Warmup, block.Measured, block.Block.WarmupFrames, block.Block.MeasuredFrames, completed);
            if (block.Block.Layout == RendererPerfLayout.TotalOnly)
                Require(block.Warmup.Concat(block.Measured).All(f => f.Passes.IsEmpty), "Publication", "Total-only layout cannot contain per-pass markers.");
        }
        Require(report.Controls.Length <= report.Manifest.Controls.Length && (!completed || report.Controls.Length == report.Manifest.Controls.Length),
            "Publication", "Final report is missing controls or contains extra controls.");
        for (int i = 0; i < report.Controls.Length; i++)
        {
            RendererPerfControlReport control = report.Controls[i];
            Require(control.Control == report.Manifest.Controls[i], "Publication", "Report control order differs from the manifest.");
            Hash(control.StateSha256, 64);
            Metrics(control.Metrics);
            ValidateSamples(control.Warmup, control.Measured, control.Control.WarmupFrames, control.Control.MeasuredFrames, completed);
        }
        if (report.Assessment.ValidForPromotion)
            Require(report.Blocks.All(b => b.Errors.IsEmpty && b.Warmup.Concat(b.Measured).All(Promotable))
                && report.Controls.All(c => c.Warmup.Concat(c.Measured).All(Promotable)),
                "Publication", "Invalid GPU queries, frame failures or incomplete pass timing cannot support promotion.");
        ValidateNative(report);
        foreach (RendererPerfSidecar sidecar in report.Sidecars)
        {
            Hash(sidecar.Sha256, 64);
            Require(Text(sidecar.Path) && !sidecar.Path.StartsWith('/') && !sidecar.Path.Contains('\\')
                && !sidecar.Path.Contains(':') && !sidecar.Path.Split('/').Any(p => p is ".." or "." or ""), "Identity", "Sidecar path must be a relative artifact path.");
        }
    }

    static bool Promotable(RendererPerfFrameSample frame) => frame.Errors.IsEmpty
        && frame.GpuFrame.Status == RendererPerfMetricStatus.Available && frame.GpuElapsedTicks > 0
        && frame.Passes.All(p => p.Duration.Status is RendererPerfMetricStatus.Available
            or RendererPerfMetricStatus.NotExecuted or RendererPerfMetricStatus.BelowResolution);

    static void ValidateSamples(ImmutableArray<RendererPerfFrameSample> warmup, ImmutableArray<RendererPerfFrameSample> measured,
        int expectedWarmup, int expectedMeasured, bool completed)
    {
        Require(warmup.Length <= expectedWarmup && measured.Length <= expectedMeasured
            && (measured.IsEmpty || warmup.Length == expectedWarmup)
            && (!completed || warmup.Length == expectedWarmup && measured.Length == expectedMeasured),
            "Publication", "Missing or extra frames cannot be published as a complete block.");
        var frameIds = new HashSet<int>();
        for (int i = 0; i < warmup.Length; i++) ValidateFrame(warmup[i], (256 - expectedWarmup + i) % 256, frameIds);
        for (int i = 0; i < measured.Length; i++) ValidateFrame(measured[i], i % 256, frameIds);
    }

    static void ValidateFrame(RendererPerfFrameSample frame, int sequenceIndex, HashSet<int> frameIds)
    {
        Metric(frame.GpuFrame);
        Metrics(frame.Metrics);
        Require(frame.FrameId >= 0 && frameIds.Add(frame.FrameId) && frame.SequenceIndex == sequenceIndex
            && frame.CpuFrequency > 0 && frame.CpuEndTicks >= frame.CpuStartTicks, "Sample", "Frame identity, fixed sequence or monotonic CPU interval is invalid.");
        Require(frame.GpuPeriodNs == null || double.IsFinite(frame.GpuPeriodNs.Value) && frame.GpuPeriodNs > 0,
            "Sample", "GPU period must be finite and positive or unavailable.");
        Require(frame.GpuFrame.Status != RendererPerfMetricStatus.Available || frame.GpuElapsedTicks != null && frame.GpuPeriodNs != null,
            "Sample", "Available GPU time needs raw elapsed ticks and a period.");
        Require(frame.Passes.Length <= 255 && frame.Passes.Select(p => (p.Id, p.Occurrence)).Distinct().Count() == frame.Passes.Length,
            "Sample", "Scope occurrences are duplicated or exceed the 512-query layout capacity.");
        foreach (RendererPerfPassSample pass in frame.Passes)
        {
            Metric(pass.Duration);
            Require(Text(pass.Id) && pass.Occurrence >= 0 && pass.BeginMarker >= 1 && pass.BeginMarker < pass.EndMarker
                && pass.EndMarker <= 510 && (pass.ParentId == null) == (pass.ParentOccurrence == null), "Sample", "Pass key, parent or reserved marker pair is invalid.");
        }
        foreach (RendererPerfCpuSpan span in frame.CpuPhases)
            Require(Text(span.Id) && span.StartTicks >= frame.CpuStartTicks && span.EndTicks >= span.StartTicks
                && span.EndTicks <= frame.CpuEndTicks, "Sample", "CPU child is outside the frame wall interval.");
    }

    static void ValidateNative(RendererPerfReport report)
    {
        foreach (GpuPipelineCompilerReport pipeline in report.Pipelines)
        {
            foreach (string hash in new[] { pipeline.PipelineSha256, pipeline.SourceSha256, pipeline.ShaderArtifactSha256,
                pipeline.LayoutSha256, pipeline.DiagnosticConfigurationSha256 }) Hash(hash, 64);
            Require(Text(pipeline.CompilerFlags) && (pipeline.Status == GpuDiagnosticStatus.Available
                ? !pipeline.Executables.IsEmpty : Text(pipeline.Reason)), "Metric", "Compiler collection needs executable evidence or an unavailable reason.");
            foreach (GpuPipelineExecutableReport executable in pipeline.Executables)
            {
                Require(Text(executable.Name) && Text(executable.Stage) && Text(executable.EntryPoint), "Metric", "Executable identity is missing.");
                Provenance(executable.PhysicalRegisters, GpuDiagnosticProvenance.VendorCompiler, "registers");
                Provenance(executable.SpillBytes, GpuDiagnosticProvenance.VendorCompiler, "bytes");
                Provenance(executable.AchievedOccupancy, GpuDiagnosticProvenance.RuntimeCounter, "ratio", ratio: true);
                Provenance(executable.ExecutionWidth, GpuDiagnosticProvenance.HardwareLimit, "threads");
                Provenance(executable.MaximumThreadsPerGroup, GpuDiagnosticProvenance.HardwareLimit, "threads");
                foreach (GpuPipelineStatistic statistic in executable.Statistics)
                {
                    Require(Text(statistic.Name) && Text(statistic.Description), "Metric", "Raw compiler statistics must retain their names and descriptions.");
                    Metric(statistic.Metric);
                }
            }
        }
        foreach (GpuResourceMemorySnapshot memory in report.Memory.Native)
        {
            foreach (GpuDiagnosticMetric logical in new[] { memory.LogicalBufferBytes, memory.LogicalTextureBytes,
                memory.StagingCapacityBytes, memory.HistoryPayloadBytes, memory.ScratchPayloadBytes,
                memory.PeakLogicalPayloadBytes }) Provenance(logical, GpuDiagnosticProvenance.LogicalPayload, "bytes");
            Provenance(memory.LiveResourceCount, GpuDiagnosticProvenance.LogicalPayload, "count");
            Provenance(memory.CommittedBytes, GpuDiagnosticProvenance.BackendAllocation, "bytes");
            Provenance(memory.ResidentBytes, GpuDiagnosticProvenance.Residency, "bytes");
            Provenance(memory.BudgetBytes, GpuDiagnosticProvenance.Budget, "bytes");
        }
    }

    static void Provenance(GpuDiagnosticMetric metric, GpuDiagnosticProvenance provenance, string unit, bool ratio = false)
    {
        Metric(metric);
        if (metric.Status != GpuDiagnosticStatus.Available) return;
        Require(metric.Provenance == provenance && metric.Unit == unit,
            "Metric", $"Normalized {unit} requires {provenance} evidence and matching units.");
        GpuDiagnosticValue value = metric.Value!;
        Require(ratio ? value.Type == GpuDiagnosticValueType.Float64 && value.Float64 is >= 0 and <= 1
            : value.Type == GpuDiagnosticValueType.UInt64 || value.Type == GpuDiagnosticValueType.Int64 && value.Int64 >= 0,
            "Metric", "Normalized counts/bytes must be nonnegative integers. Achieved occupancy must be a ratio from 0 to 1.");
    }

    static void Metrics(ImmutableDictionary<string, RendererPerfMetric> metrics)
    {
        foreach (var pair in metrics)
        {
            Require(Text(pair.Key), "Metric", "Metric key is missing.");
            Metric(pair.Value);
        }
    }

    static void Metric(RendererPerfMetric? metric)
    {
        if (metric == null) throw new RendererPerfProtocolException("Metric", "Metric object is missing. Use an unavailable value with a reason.");
        Require(Text(metric.Unit) && Text(metric.Source) && (metric.Status == RendererPerfMetricStatus.Available
            ? metric.Value != null && double.IsFinite(metric.Value.Value) : metric.Value == null && Text(metric.Reason)),
            "Metric", "Every metric needs value/unit/source/status/reason. Unsupported or invalid values must be null with a cause.");
    }

    static void Metric(GpuDiagnosticMetric metric)
    {
        Require(Text(metric.Unit) && Text(metric.Source) && (metric.Status == GpuDiagnosticStatus.Available
            ? metric.Value != null && metric.Provenance != GpuDiagnosticProvenance.Unknown : metric.Value == null && Text(metric.Reason)),
            "Metric", "Native metric needs typed value/unit/source/status/reason/provenance. Unsupported or invalid values must be null with a cause.");
        if (metric.Value is not GpuDiagnosticValue value) return;
        int fields = (value.Boolean.HasValue ? 1 : 0) + (value.Int64.HasValue ? 1 : 0)
            + (value.UInt64.HasValue ? 1 : 0) + (value.Float64.HasValue ? 1 : 0);
        bool selected = value.Type switch
        {
            GpuDiagnosticValueType.Boolean => value.Boolean.HasValue,
            GpuDiagnosticValueType.Int64 => value.Int64.HasValue,
            GpuDiagnosticValueType.UInt64 => value.UInt64.HasValue,
            GpuDiagnosticValueType.Float64 => value.Float64.HasValue && double.IsFinite(value.Float64.Value),
            _ => false
        };
        Require(fields == 1 && selected, "Metric", "Native scalar must retain exactly its declared Boolean/Int64/UInt64/Float64 representation.");
    }
}
