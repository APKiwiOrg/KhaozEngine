using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfManifest;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfCompilerValidation
{
    internal static void RequireScalarMembers(JsonTypeInfo type)
    {
        if (type.Type == typeof(GpuDiagnosticValue))
            foreach (var property in type.Properties) property.IsRequired = true;
    }

    internal static void Validate(RendererPerfReport report)
    {
        var compiler = report.Compiler;
        Require(compiler.State == RendererPerfCollectionState.Complete ? compiler.Reason is null && compiler.Errors.IsEmpty
            : Text(compiler.Reason), "Compiler", "Collection requires an explicit complete state or refusal cause.");
        Require(!report.Assessment.ValidForPromotion || compiler.State == RendererPerfCollectionState.Complete
            && report.Publication.FailureText.Complete, "Compiler", "Incomplete compiler or failure-text evidence cannot promote.");
        var text = report.Publication.FailureText;
        Require(text.OmittedFailures >= 0 && text.RequiredUtf8Bytes >= 0 && (!text.Complete || text.OmittedFailures == 0),
            "Compiler", "Invalid failure-text completeness/count evidence.");
        if (compiler.Production is { } production) _ = GpuDiagnosticIdentityEncoding.Configuration(production);
        if (compiler.Diagnostic is { } diagnostic) _ = GpuDiagnosticIdentityEncoding.Configuration(diagnostic);
        var counts = compiler.Counts;
        foreach (var n in new[] { counts.FactoryAttempts, counts.UniqueRecipes, counts.ReplayEligibleRecipes,
            counts.BoundOccurrences, counts.Executables, counts.Statistics, counts.OmittedFactoryEvents,
            counts.PipelineAttempts.Graphics, counts.PipelineAttempts.Compute, counts.SuccessfulCreations.Graphics,
            counts.SuccessfulCreations.Compute, counts.Occurrences.Graphics, counts.Occurrences.Compute,
            counts.ReplayAttempts.Graphics, counts.ReplayAttempts.Compute, counts.Reports.Graphics, counts.Reports.Compute })
            Require(n >= 0, "Compiler", "Compiler counts cannot be negative.");
        var recipes = new System.Collections.Generic.HashSet<int>();
        var indices = new System.Collections.Generic.HashSet<int>();
        foreach (var result in compiler.Results)
        {
            Require(result.RecipeId >= 0 && recipes.Add(result.RecipeId) && result.OccurrenceIds.All(i => i >= 0)
                && result.OccurrenceIds.Distinct().Count() == result.OccurrenceIds.Length,
                "Compiler", "Compiler result identities must be unique and nonnegative.");
            Require(result.State == RendererPerfCollectionState.Complete ? result.Reason is null : Text(result.Reason),
                "Compiler", "Result state requires its completion or refusal cause.");
            GpuPipelineCompilerReport? pipeline = null;
            if (result.PipelineIndex is { } index)
            {
                Require((uint)index < report.Pipelines.Length && indices.Add(index), "Compiler", "Pipeline association is invalid or duplicated.");
                pipeline = report.Pipelines[index];
            }
            if (result.Read is { } read) Read(read, pipeline);
            Require(result.State != RendererPerfCollectionState.Complete || result.Read is { Complete: true } && pipeline is not null,
                "Compiler", "Complete result requires an associated completed read.");
        }
        Require(compiler.State != RendererPerfCollectionState.Complete || compiler.Results.All(r => r.State == RendererPerfCollectionState.Complete)
            && indices.Count == report.Pipelines.Length && counts.OmittedFactoryEvents == 0,
            "Compiler", "Complete collection cannot contain unattempted or omitted evidence.");
        foreach (var pipeline in report.Pipelines) Pipeline(pipeline);
    }

    internal static void Pipeline(GpuPipelineCompilerReport pipeline)
    {
        var identity = pipeline.Identity;
        foreach (var digest in new[] { identity.PipelineSha256, identity.SourceSha256, identity.ShaderArtifactSha256,
            identity.LayoutSha256, identity.DiagnosticConfigurationSha256, identity.CodeGenerationSha256 }) Digest(digest);
        Require((Text(identity.CompilerFlags) || pipeline.Status == GpuDiagnosticStatus.Invalid) && GpuDiagnosticIdentityEncoding.Utf8.GetByteCount(identity.CompilerFlags) <= 16384 && (pipeline.Status == GpuDiagnosticStatus.Available
            ? !pipeline.Executables.IsEmpty && pipeline.Reason is null : Text(pipeline.Reason)), "Metric", "Compiler collection needs executable evidence or an unavailable reason.");
        Require(pipeline.Status == GpuDiagnosticStatus.Invalid || new[] { identity.PipelineSha256, identity.SourceSha256,
            identity.LayoutSha256, identity.DiagnosticConfigurationSha256, identity.CodeGenerationSha256 }
            .All(d => d.Status == GpuDiagnosticStatus.Available) && identity.ShaderArtifactSha256.Status != GpuDiagnosticStatus.Invalid,
            "Compiler", "Missing construction correspondence invalidates the pipeline row.");
        foreach (var stage in identity.Stages)
        {
            Require(Text(stage.Stage) && Text(stage.EntryPoint), "Compiler", "Stage identity is missing.");
            Digest(stage.SourceSha256); Digest(stage.ShaderArtifactSha256);
            if (stage.EmittedSourceSha256 is { } emitted) Digest(emitted);
        }
        Require(!identity.Stages.IsDefaultOrEmpty || pipeline.Status == GpuDiagnosticStatus.Invalid, "Compiler", "Construction identity requires retained stages.");
        Require((identity.SourceSha256.Status == GpuDiagnosticStatus.Invalid && pipeline.Status == GpuDiagnosticStatus.Invalid
                || identity.SourceSha256 == GpuDiagnosticIdentityEncoding.Stages("source", identity.Stages))
            && (identity.ShaderArtifactSha256.Status == GpuDiagnosticStatus.Invalid && pipeline.Status == GpuDiagnosticStatus.Invalid
                || identity.ShaderArtifactSha256 == GpuDiagnosticIdentityEncoding.Stages("artifact", identity.Stages)),
            "Compiler", "Stage and aggregate digests disagree.");
        foreach (var executable in pipeline.Executables)
        {
            Require(Text(executable.Name) && Text(executable.Stage) && Text(executable.EntryPoint), "Metric", "Executable identity is missing.");
            RendererPerfReportJson.Provenance(executable.PhysicalRegisters, GpuDiagnosticProvenance.VendorCompiler, "registers");
            RendererPerfReportJson.Provenance(executable.SpillBytes, GpuDiagnosticProvenance.VendorCompiler, "bytes");
            RendererPerfReportJson.Provenance(executable.AchievedOccupancy, GpuDiagnosticProvenance.RuntimeCounter, "ratio", ratio: true);
            RendererPerfReportJson.Provenance(executable.ExecutionWidth, GpuDiagnosticProvenance.HardwareLimit, "threads");
            RendererPerfReportJson.Provenance(executable.MaximumThreadsPerGroup, GpuDiagnosticProvenance.HardwareLimit, "threads");
            foreach (var statistic in executable.Statistics)
            {
                Require(Text(statistic.Name) && Text(statistic.Description), "Metric", "Raw statistics require their names and descriptions.");
                RendererPerfReportJson.Metric(statistic.Metric);
            }
        }
    }

    static void Digest(GpuDiagnosticDigest digest)
        => Require(Text(digest.Source) && (digest.Status == GpuDiagnosticStatus.Available
            ? GpuDiagnosticIdentityEncoding.ValidHash(digest.Sha256) && digest.Reason is null
            : digest.Status is GpuDiagnosticStatus.Unsupported or GpuDiagnosticStatus.Invalid && digest.Sha256 is null && Text(digest.Reason)),
            "Hash", "Digest availability, lowercase SHA256 or encoding provenance is invalid.");

    static void Read(RendererPerfCompilerReadEvidence read, GpuPipelineCompilerReport? pipeline)
    {
        var q = read.Request;
        bool vulkan = q.MaximumExecutables == 64 && q.MaximumStatisticsPerExecutable == 256
            && q.MaximumTotalStatistics is >= 0 and <= 16384 && q.MaximumTextUtf8Bytes == 256
            && q.MaximumCompilerFlagsUtf8Bytes == 16384 && q.MaximumEnumerationAttempts == 4;
        Require(vulkan || q == new GpuPipelineCompilerReadRequest(2, 30, 60, 4096, 16384, 4), "Compiler", "Read profile is not primary or frozen.");
        if (read.Counts is not { } c)
        {
            Require(!read.Complete && Text(read.RefusalReason) && pipeline is null, "Compiler", "Reserved construction refusal requires null Counts and no report.");
            return;
        }
        Require(c.RetainedExecutables >= 0 && c.RetainedExecutables <= q.MaximumExecutables && c.RetainedStatistics >= 0
            && c.RetainedStatistics <= q.MaximumTotalStatistics, "Compiler", "Retained native counts exceed the admitted request.");
        if (pipeline is not null)
            Require(c.RetainedExecutables == pipeline.Executables.Length && c.RetainedStatistics == pipeline.Executables.Sum(e => e.Statistics.Length)
                && pipeline.Executables.All(e => e.Statistics.Length <= q.MaximumStatisticsPerExecutable), "Compiler", "Retained report and count projection disagree.");
        if (!c.EnumerationRequired)
            Require(c.CountsComplete && c.RetainedExecutables == 0 && c.RetainedStatistics == 0 && c.ExecutableCount is null
                && c.TotalStatistics is null && c.OmittedExecutables is null && c.OmittedStatistics is null,
                "Compiler", "Capability absence requires not-applicable totals and zero retained native rows.");
        else
        {
            Require(c.ExecutableCount is null ? c.OmittedExecutables is null
                : c.ExecutableCount >= c.RetainedExecutables && (c.OmittedExecutables is null && !c.CountsComplete
                    || c.OmittedExecutables == c.ExecutableCount - c.RetainedExecutables),
                "Compiler", "Known executable totals and omissions disagree.");
            Require(c.TotalStatistics is null ? c.OmittedStatistics is null
                : c.TotalStatistics >= (ulong)c.RetainedStatistics && c.OmittedStatistics == c.TotalStatistics - (ulong)c.RetainedStatistics,
                "Compiler", "Known statistic totals and omissions disagree.");
        }
        Require(!c.EnumerationRequired || !c.CountsComplete || c.ExecutableCount is not null && c.TotalStatistics is not null,
            "Compiler", "Complete applicable counts require known totals.");
        Require(!read.Complete || (!c.EnumerationRequired || c.OmittedExecutables == 0 && c.OmittedStatistics == 0) && c.CountsComplete && c.RefusedExecutableIndex is null && c.RefusedCount is null
            && read.RefusalReason is null && pipeline is { Status: GpuDiagnosticStatus.Available or GpuDiagnosticStatus.Unsupported },
            "Compiler", "Complete read cannot contain refusal, invalid or unqueried evidence.");
    }
}
