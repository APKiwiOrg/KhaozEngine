using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Pure offline gate. It never creates resources or asks a live compiler for evidence.
internal static class RendererPerfCompilerCorrespondence
{
    internal static ImmutableArray<string> Validate(RendererPerfCompilerInventory inventory,
        ImmutableArray<GpuPipelineCompilerReport> pipelines, RendererPerfCompilerEvidence compiler,
        int maximumOperations, string conditioningOperation)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        try { Check(inventory, pipelines, compiler, maximumOperations, conditioningOperation, reasons); }
        catch (Exception error) { reasons.Add("Malformed compiler correspondence evidence: " + error.Message); }
        return reasons.ToImmutable();
    }
    static void Check(RendererPerfCompilerInventory i, ImmutableArray<GpuPipelineCompilerReport> pipelines,
        RendererPerfCompilerEvidence c, int maximumOperations, string conditioningOperation, ImmutableArray<string>.Builder reasons)
    {
        void Require(bool condition, string reason) { if (!condition) reasons.Add(reason); }
        Require(i.Schema == RendererPerfCompilerJson.Schema, "Unknown compiler inventory schema.");
        Require(i.Complete && i.Reason is null && i.Counts.OmittedFactoryEvents == 0, "Construction inventory is incomplete.");
        Require(c.State == RendererPerfCollectionState.Complete && c.Reason is null && c.Errors.IsEmpty, "Compiler collection is incomplete.");
        Require(i.Operations.Length > 0 && i.Operations.Length <= maximumOperations && i.Operations.All(s => !string.IsNullOrWhiteSpace(s))
            && i.Operations.Distinct(StringComparer.Ordinal).Count() == i.Operations.Length, "Operation membership is invalid.");
        Require(!i.Operations.IsEmpty && i.Operations[0] == conditioningOperation, "Conditioning operation differs from the admitted schedule.");
        Require(i.Shaders.Select(s => s.Id).SequenceEqual(Enumerable.Range(0, i.Shaders.Length))
            && i.Layouts.Select(l => l.Id).SequenceEqual(Enumerable.Range(0, i.Layouts.Length))
            && i.Recipes.Select(r => r.Id).SequenceEqual(Enumerable.Range(0, i.Recipes.Length))
            && i.Occurrences.Select(o => o.Id).SequenceEqual(Enumerable.Range(0, i.Occurrences.Length)), "Inventory IDs are not dense first occurrences.");
        Require(i.Recipes.Length <= RendererPerfConstructionAdmission.UniqueRecipes
            && i.FactoryEvents.Length <= checked(maximumOperations * RendererPerfConstructionAdmission.FactoriesPerOperation)
            && i.Occurrences.Length <= checked(maximumOperations * RendererPerfConstructionAdmission.PipelinesPerOperation), "Inventory count capacity exceeded.");
        RendererPerfConstructionInventoryChecks.Validate(i, reasons, maximumOperations);
        var eventRoles = new HashSet<(int, int)>();
        var outputs = new HashSet<int>();
        var successes = new int[2]; var attempts = new int[2];
        foreach (var e in i.FactoryEvents)
        {
            Require((uint)e.OperationOrdinal < (uint)i.Operations.Length && e.Ordinal >= 0 && e.Ordinal < 1024
                && eventRoles.Add((e.OperationOrdinal, e.Ordinal)), "Factory event role is foreign or duplicated.");
            Require(Enum.IsDefined(e.Kind) && e.InputId >= 0 && e.Failure is null && e.OutputId.HasValue,
                "Factory attempt failed or is incomplete.");
            if (e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline)
            {
                int kind = e.Kind == RendererPerfFactoryKind.GraphicsPipeline ? 0 : 1; attempts[kind]++;
                if (e.OutputId is { } id)
                {
                    successes[kind]++;
                    Require((uint)id < (uint)i.Occurrences.Length && outputs.Add(id), "Pipeline factory output is foreign or duplicated.");
                    if ((uint)id < (uint)i.Occurrences.Length)
                    {
                        var o = i.Occurrences[id];
                        Require(o.OperationOrdinal == e.OperationOrdinal && o.RecipeId == e.InputId && (int)o.Kind == kind,
                            "Factory event and occurrence disagree.");
                    }
                }
                Require((uint)e.InputId < (uint)i.Recipes.Length, "Factory pipeline recipe is missing.");
            }
            else
            {
                int length = e.Kind == RendererPerfFactoryKind.Shader ? i.Shaders.Length : i.Layouts.Length;
                Require((uint)e.InputId < (uint)length && e.OutputId == e.InputId, "Factory graph output is foreign.");
            }
        }
        foreach (var group in i.FactoryEvents.GroupBy(e => e.OperationOrdinal))
        {
            Require(group.Select(e => e.Ordinal).SequenceEqual(Enumerable.Range(0, group.Count())), "Factory order is incomplete.");
            Require(group.Count(e => e.Kind is RendererPerfFactoryKind.GraphicsPipeline or RendererPerfFactoryKind.ComputePipeline) <= 256,
                "Operation pipeline capacity exceeded.");
        }
        var roles = new HashSet<(int, RendererPerfPipelineKind, int)>();
        foreach (var o in i.Occurrences)
        {
            Require((uint)o.OperationOrdinal < (uint)i.Operations.Length && o.CreationOrdinal >= 0 && Enum.IsDefined(o.Kind)
                && roles.Add((o.OperationOrdinal, o.Kind, o.CreationOrdinal)) && o.BindCount >= 0
                && Enum.IsDefined(o.Stage) && (o.Stage == RendererPerfConstructionStage.Cold
                    ? o.FrameIndex is null || o.OperationOrdinal == 0 && o.FrameIndex >= 0 && i.Operations[0] == conditioningOperation
                    : o.FrameIndex is { } f && (o.Stage == RendererPerfConstructionStage.Warmup) == (f < 0)), "Occurrence role or frame membership is invalid.");
            Require(o.Stage != RendererPerfConstructionStage.Measured, "Measured first pipeline creation refuses steady-state promotion.");
            Require(outputs.Contains(o.Id) && (uint)o.RecipeId < (uint)i.Recipes.Length, "Occurrence has no successful factory association.");
        }
        foreach (var group in i.Occurrences.GroupBy(o => (o.OperationOrdinal, o.Kind)))
            Require(group.Select(o => o.CreationOrdinal).SequenceEqual(Enumerable.Range(0, group.Count())), "Pipeline creation roles are incomplete.");
        var expectedBase = new RendererPerfCompilerCounts(i.FactoryEvents.Length, new(attempts[0], attempts[1]), new(successes[0], successes[1]),
            Kind(i.Occurrences, o => o.Kind), new(0, 0), new(0, 0), i.Recipes.Length,
            i.Occurrences.Select(o => o.RecipeId).Distinct().Count(), i.Occurrences.Count(o => o.BindCount > 0), 0, 0, 0);
        Require(i.Counts == expectedBase, "Independent construction totals disagree.");
        if (c.Production is not { } production || c.Diagnostic is not { } diagnostic)
        { reasons.Add("Full actual production and diagnostic configurations are required."); return; }
        Configuration(production, diagnostic, reasons);
        var recipeIds = new HashSet<int>(); var pipelineIndices = new HashSet<int>(); var associated = new HashSet<int>();
        var replayKinds = new List<RendererPerfPipelineKind>(); var reportKinds = new List<RendererPerfPipelineKind>();
        foreach (var result in c.Results)
        {
            Require((uint)result.RecipeId < (uint)i.Recipes.Length && recipeIds.Add(result.RecipeId), "Result recipe is foreign or duplicated.");
            if ((uint)result.RecipeId >= (uint)i.Recipes.Length) continue;
            var r = i.Recipes[result.RecipeId];
            Require(result.State == RendererPerfCollectionState.Complete && result.Reason is null && result.Read is { Complete: true },
                "Recipe result is unattempted or incomplete.");
            var occurrences = i.Occurrences.Where(o => o.RecipeId == r.Id).ToArray();
            Require(!result.OccurrenceIds.IsDefault && result.OccurrenceIds.SequenceEqual(occurrences.Select(o => o.Id)),
                "Result occurrence membership is incomplete, foreign or duplicated.");
            foreach (int id in result.OccurrenceIds) Require(associated.Add(id), "Occurrence is associated more than once.");
            Require(occurrences.Length > 0, "Result has no successful production occurrence.");
            if (result.Read is not null) replayKinds.Add(r.Kind);
            if (result.PipelineIndex is not { } index) { reasons.Add("Result is missing a pipeline report."); continue; }
            Require((uint)index < (uint)pipelines.Length && pipelineIndices.Add(index), "Pipeline report index is foreign or duplicated.");
            if ((uint)index >= (uint)pipelines.Length) continue;
            reportKinds.Add(r.Kind);
            var report = pipelines[index]; RendererPerfCompilerValidation.Pipeline(report);
            Require(report.Status != GpuDiagnosticStatus.Invalid, "Replay compiler report is invalid.");
            Read(result.Read, report, diagnostic, reasons);
            Require(r.RecipeSha256 == RendererPerfConstructionEncoding.Digest(r, i.Shaders, i.Layouts), "Recipe digest disagrees with detached values.");
            foreach (var o in occurrences)
            {
                Require(o.Kind == r.Kind, "Occurrence and recipe kinds disagree.");
                Witness(r, i, o.Production, production, reasons);
                Witness(r, i, report.Identity, diagnostic, reasons);
                Require(Same(o.Production, report.Identity), "Production and replay construction witnesses disagree.");
            }
        }
        Require(recipeIds.Count == i.Recipes.Length && pipelineIndices.Count == pipelines.Length && associated.Count == i.Occurrences.Length,
            "Compiler association coverage is incomplete.");
        Require(c.Counts == expectedBase with
        {
            ReplayAttempts = Kind(replayKinds, k => k),
            Reports = Kind(reportKinds, k => k),
            Executables = pipelines.Sum(p => p.Executables.Length),
            Statistics = pipelines.Sum(p => p.Executables.Sum(e => e.Statistics.Length))
        },
            "Independent collection totals disagree.");
    }
    static RendererPerfKindCounts Kind<T>(IEnumerable<T> rows, Func<T, RendererPerfPipelineKind> kind)
        => new(rows.Count(o => kind(o) == RendererPerfPipelineKind.Graphics), rows.Count(o => kind(o) == RendererPerfPipelineKind.Compute));

    static bool Same(GpuPipelineConstructionIdentity a, GpuPipelineConstructionIdentity b)
        => a.PipelineSha256 == b.PipelineSha256 && a.SourceSha256 == b.SourceSha256 && a.ShaderArtifactSha256 == b.ShaderArtifactSha256
        && a.LayoutSha256 == b.LayoutSha256 && a.CodeGenerationSha256 == b.CodeGenerationSha256
        && a.LocalSizeX == b.LocalSizeX && a.LocalSizeY == b.LocalSizeY && a.LocalSizeZ == b.LocalSizeZ && a.Stages.SequenceEqual(b.Stages);

    static void Witness(RendererPerfPipelineRecipe r, RendererPerfCompilerInventory i, GpuPipelineConstructionIdentity w,
        GpuEffectiveDiagnosticConfiguration configuration, ImmutableArray<string>.Builder reasons)
    {
        void Require(bool condition, string reason) { if (!condition) reasons.Add(reason); }
        RendererPerfCompilerValidation.Pipeline(new(w, GpuDiagnosticStatus.Unsupported, "Construction witness only.", []));
        Require(w.DiagnosticConfigurationSha256 == GpuDiagnosticIdentityEncoding.Configuration(configuration),
            "Witness does not identify the full actual device configuration.");
        Require(w.CodeGenerationSha256.Source == "khaozengine.pipeline-code-generation/1", "Unknown code-generation encoding provenance.");
        Require(w.CodeGenerationSha256 == r.CodeGenerationSha256, "Recipe code-generation identity disagrees.");
        var stages = i.Shaders[r.ShaderId].Stages;
        Require(stages.Length == (r.Kind == RendererPerfPipelineKind.Compute ? 1 : 2) && stages.Length == w.Stages.Length,
            "Recipe stage membership disagrees.");
        for (int n = 0; n < Math.Min(stages.Length, w.Stages.Length); n++)
        {
            var s = stages[n]; var actual = w.Stages[n];
            if (configuration.Backend is GpuBackendKind.VulkanNative or GpuBackendKind.Direct3D11Native)
                Require(s.EntryPoint == "main" && s.Stage == (r.Kind == RendererPerfPipelineKind.Compute ? "compute"
                    : n == 0 ? "vertex" : configuration.Backend == GpuBackendKind.Direct3D11Native ? "pixel" : "fragment"),
                    "Primary backend stage or entry point is foreign.");
            Require(s.Stage == actual.Stage && s.EntryPoint == actual.EntryPoint && actual.SourceSha256 == GpuDiagnosticIdentityEncoding.Text(s.Source, "glsl.utf8/1"),
                "Captured final stage source or entry point disagrees.");
            if (r.Kind == RendererPerfPipelineKind.Graphics)
                Require(s.LocalX == 0 && s.LocalY == 0 && s.LocalZ == 0, "Graphics source carries compute local sizes.");
            if (r.Kind == RendererPerfPipelineKind.Compute)
                Require(s.LocalX > 0 && s.LocalY > 0 && s.LocalZ > 0 && s.LocalX == w.LocalSizeX && s.LocalY == w.LocalSizeY && s.LocalZ == w.LocalSizeZ,
                    "Resolved compute local size disagrees.");
        }
        if (r.Kind == RendererPerfPipelineKind.Graphics)
            Require(r.Graphics is not null && w.LocalSizeX == 0 && w.LocalSizeY == 0 && w.LocalSizeZ == 0, "Graphics state/local size disagrees.");
        else Require(r.Graphics is null, "Compute recipe has graphics state.");
        string layout = RendererPerfConstructionResolvedIdentity.Layout(r, i.Layouts);
        Require(w.LayoutSha256.Sha256 == layout, "Resolved ordered layout identity disagrees.");
        if (configuration.Backend is GpuBackendKind.VulkanNative or GpuBackendKind.Direct3D11Native)
        {
            string backend = configuration.Backend == GpuBackendKind.VulkanNative ? "vulkan" : "d3d11";
            Require(w.Stages.All(s => s.ShaderArtifactSha256.Source == (backend == "vulkan" ? "spirv.bytes/1" : "dxbc.bytes/1")),
                "Primary artifacts require actual compiled-byte encoding provenance.");
            Require(w.ShaderArtifactSha256.Status == GpuDiagnosticStatus.Available && w.Stages.All(s => s.ShaderArtifactSha256.Status == GpuDiagnosticStatus.Available),
                "Primary backend artifact identity is missing.");
            Require(w.LayoutSha256.Source == backend + ".layout/1" && w.PipelineSha256.Source == backend + ".pipeline/1"
                && w.PipelineSha256.Sha256 == RendererPerfConstructionResolvedIdentity.Pipeline(r, w, layout, configuration.Backend),
                "Resolved pipeline state identity disagrees.");
        }
    }

    internal static ImmutableArray<string> Recipe(RendererPerfCompilerInventory inventory, RendererPerfPipelineRecipe recipe,
        GpuPipelineCompilerReport report, RendererPerfCompilerReadEvidence read,
        GpuEffectiveDiagnosticConfiguration production, GpuEffectiveDiagnosticConfiguration diagnostic)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        RendererPerfCompilerValidation.Pipeline(report);
        Read(read, report, diagnostic, reasons);
        if (report.Status == GpuDiagnosticStatus.Invalid) reasons.Add("Replay compiler report is invalid.");
        if (recipe.RecipeSha256 != RendererPerfConstructionEncoding.Digest(recipe, inventory.Shaders, inventory.Layouts))
            reasons.Add("Recipe digest disagrees with detached values.");
        var occurrences = inventory.Occurrences.Where(o => o.RecipeId == recipe.Id).ToArray();
        if (occurrences.Length == 0) reasons.Add("Replay has no captured successful production occurrence.");
        foreach (var occurrence in occurrences)
        {
            Witness(recipe, inventory, occurrence.Production, production, reasons);
            Witness(recipe, inventory, report.Identity, diagnostic, reasons);
            if (!Same(occurrence.Production, report.Identity)) reasons.Add("Production and replay construction witnesses disagree.");
        }
        return reasons.ToImmutable();
    }

    internal static ImmutableArray<string> Configurations(GpuEffectiveDiagnosticConfiguration production,
        GpuEffectiveDiagnosticConfiguration diagnostic)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        Configuration(production, diagnostic, reasons);
        return reasons.ToImmutable();
    }

    static void Configuration(GpuEffectiveDiagnosticConfiguration p, GpuEffectiveDiagnosticConfiguration d, ImmutableArray<string>.Builder reasons)
    {
        if (p.Backend is not (GpuBackendKind.VulkanNative or GpuBackendKind.Direct3D11Native))
            reasons.Add("Compiler correspondence requires a primary named backend.");
        _ = GpuDiagnosticIdentityEncoding.Configuration(p); _ = GpuDiagnosticIdentityEncoding.Configuration(d);
        if (string.IsNullOrWhiteSpace(p.AdapterKey) || string.IsNullOrWhiteSpace(p.Driver) || p.Backend != d.Backend
            || p.AdapterKey != d.AdapterKey || p.Driver != d.Driver || p.Requested != new GpuPipelineDiagnosticRequest(false, false)
            || d.Requested != new GpuPipelineDiagnosticRequest(true, false) || p.CompilerMetadataEnabled || p.RuntimeInvocationsEnabled
            || d.RuntimeInvocationsEnabled || p.DebugLayerEnabled || d.DebugLayerEnabled || p.ValidationEnabled || d.ValidationEnabled
            || p.PipelineStatisticsSupported != d.PipelineStatisticsSupported || !p.CodeGenerationOptions.SequenceEqual(d.CodeGenerationOptions)
            || !p.ObservationOptions.SequenceEqual(d.ObservationOptions) || p.PipelineCaptureFlags != 0 || p.PipelineExecutableFeatureEnabled)
            reasons.Add("Effective configuration differs outside allowed diagnostic choices.");
        bool vulkan = p.Backend == GpuBackendKind.VulkanNative;
        bool supported = !vulkan || p.PipelineStatisticsSupported;
        if (d.CompilerMetadataEnabled != supported || d.PipelineExecutableFeatureEnabled != (vulkan && supported)
            || d.PipelineCaptureFlags != (vulkan && supported ? 0x40u : 0u))
            reasons.Add("Diagnostic capture was omitted or effective flags disagree with supported capability.");
    }
    static void Read(RendererPerfCompilerReadEvidence? read, GpuPipelineCompilerReport report, GpuEffectiveDiagnosticConfiguration diagnostic, ImmutableArray<string>.Builder reasons)
    {
        if (read is not { Complete: true, Counts: { CountsComplete: true } counts, RefusalReason: null })
        { reasons.Add("Completed bounded read evidence is missing."); return; }
        var q = read.Request; var backend = diagnostic.Backend;
        if (!counts.EnumerationRequired && backend == GpuBackendKind.VulkanNative && diagnostic.PipelineStatisticsSupported)
            reasons.Add("Supported Vulkan capture cannot claim not-applicable enumeration.");
        bool profile = backend == GpuBackendKind.VulkanNative ? q.MaximumExecutables == 64 && q.MaximumStatisticsPerExecutable == 256
            && q.MaximumTotalStatistics is >= 0 and <= 16384 && q.MaximumTextUtf8Bytes == 256 && q.MaximumCompilerFlagsUtf8Bytes == 16384
            && q.MaximumEnumerationAttempts == 4 : backend == GpuBackendKind.Direct3D11Native && q == new GpuPipelineCompilerReadRequest(2, 30, 60, 4096, 16384, 4);
        foreach (var executable in report.Executables)
        {
            if (RendererPerfConstructionEncoding.Utf8.GetByteCount(executable.Name) > q.MaximumTextUtf8Bytes
                || executable.Statistics.Any(s => RendererPerfConstructionEncoding.Utf8.GetByteCount(s.Name) > q.MaximumTextUtf8Bytes
                    || RendererPerfConstructionEncoding.Utf8.GetByteCount(s.Description) > q.MaximumTextUtf8Bytes))
                reasons.Add("Retained native text exceeds the frozen read profile.");
        }
        int executables = report.Executables.Length, statistics = report.Executables.Sum(e => e.Statistics.Length);
        if (!profile || counts.RetainedExecutables != executables || counts.RetainedStatistics != statistics
            || executables > q.MaximumExecutables || statistics > q.MaximumTotalStatistics || report.Executables.Any(e => e.Statistics.Length > q.MaximumStatisticsPerExecutable)
            || counts.RefusedCount.HasValue || counts.RefusedExecutableIndex.HasValue
            || (counts.EnumerationRequired ? counts.ExecutableCount != executables || counts.TotalStatistics != (ulong)statistics
                || counts.OmittedExecutables != 0 || counts.OmittedStatistics != 0 : executables != 0 || statistics != 0
                || counts.ExecutableCount.HasValue || counts.TotalStatistics.HasValue || counts.OmittedExecutables.HasValue || counts.OmittedStatistics.HasValue
                || report.Status != GpuDiagnosticStatus.Unsupported))
            reasons.Add("Read profile, native counts or unsupported completeness disagree.");
    }
}
