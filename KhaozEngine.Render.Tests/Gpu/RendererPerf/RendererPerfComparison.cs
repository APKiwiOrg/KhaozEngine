using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfAnalysis;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal sealed record RendererPerfIdentityDifference(string Path, string Baseline, string Candidate, bool Incompatible);
internal sealed record RendererPerfDelta(RendererPerfMetric Mean, RendererPerfMetric Median, RendererPerfMetric P95,
    RendererPerfMetric P99, RendererPerfMetric Percent);
internal sealed record RendererPerfComparisonRow(string SceneId, RendererPerfLayout Layout, int Iteration,
    bool Compatible, ImmutableArray<string> Reasons, ImmutableDictionary<string, RendererPerfDelta> Deltas);
internal sealed record RendererPerfComparisonResult(bool Compatible, ImmutableArray<string> Reasons,
    ImmutableArray<RendererPerfIdentityDifference> Differences, ImmutableArray<RendererPerfComparisonRow> Rows,
    ImmutableArray<RendererPerfComparisonRow> CombinedRows, ImmutableArray<RendererPerfComparisonRow> ControlRows);

// Compatibility is a data-admission decision. It is never a percentage regression budget.
internal static class RendererPerfComparison
{
    internal static RendererPerfComparisonResult Compare(RendererPerfReport baseline, RendererPerfReport candidate)
        => CompareRaw(baseline, candidate, null, null);

    internal static RendererPerfComparisonResult Compare(RendererPerfSummary baseline, RendererPerfSummary candidate)
        => baseline.VerifiedArtifact is { } before && candidate.VerifiedArtifact is { } after
            && ReferenceEquals(baseline.Raw, before.Report) && ReferenceEquals(candidate.Raw, after.Report)
            ? CompareValidated(Analyze(before, baseline.References), Analyze(after, candidate.References))
            : CompareRaw(baseline.Raw, candidate.Raw, baseline.References, candidate.References);

    internal static RendererPerfComparisonResult Compare(RendererPerfVerifiedArtifact baseline, RendererPerfVerifiedArtifact candidate)
        => CompareValidated(Analyze(baseline), Analyze(candidate));

    static RendererPerfComparisonResult CompareRaw(RendererPerfReport baseline, RendererPerfReport candidate,
        ImmutableDictionary<string, RendererPerfReferenceRates>? baselineReferences,
        ImmutableDictionary<string, RendererPerfReferenceRates>? candidateReferences)
    {
        var failures = new List<string>();
        RendererPerfSummary? Read(RendererPerfReport report, ImmutableDictionary<string, RendererPerfReferenceRates>? references, string name)
        {
            try { return Analyze(report, references); }
            catch (RendererPerfProtocolException error) { failures.Add($"{name} protocol {error.Code}: {error.Message}"); return null; }
        }
        var before = Read(baseline, baselineReferences, "baseline");
        var after = Read(candidate, candidateReferences, "candidate");
        return before is null || after is null ? new(false, [.. failures], [], [], [], [])
            : CompareValidated(before, after);
    }

    static RendererPerfComparisonResult CompareValidated(RendererPerfSummary baseline, RendererPerfSummary candidate)
    {
        var differences = Differences(baseline.Raw, candidate.Raw, baseline.VerifiedArtifact?.Inventory, candidate.VerifiedArtifact?.Inventory);
        var reasons = differences.Where(d => d.Incompatible).Select(d => "Incompatible " + d.Path + ".").ToList();
        if (!baseline.PromotionEligible) reasons.AddRange(baseline.Reasons.Select(r => "baseline: " + r));
        if (!candidate.PromotionEligible) reasons.AddRange(candidate.Reasons.Select(r => "candidate: " + r));
        var global = reasons.Distinct(StringComparer.Ordinal).ToImmutableArray();
        var rows = Rows(baseline.Blocks, candidate.Blocks, global, baseline.Raw, candidate.Raw);
        var combined = Rows([.. baseline.Combined.Select(CombinedBlock)], [.. candidate.Combined.Select(CombinedBlock)], global,
            baseline.Raw, candidate.Raw);
        var controls = Rows(baseline.Controls, candidate.Controls, global, baseline.Raw, candidate.Raw);
        var allReasons = global.Concat(rows.Concat(combined).Concat(controls).SelectMany(r => r.Reasons)).Distinct(StringComparer.Ordinal).ToImmutableArray();
        return new(allReasons.IsEmpty, allReasons, differences, rows, combined, controls);
    }

    static RendererPerfBlockSummary CombinedBlock(RendererPerfCombinedSummary row)
        => new(new(row.SceneId, row.Layout, -1, 0, row.Metrics["gpu.frame"].ExpectedCount), row.Metrics, []);

    static ImmutableArray<RendererPerfComparisonRow> Rows(ImmutableArray<RendererPerfBlockSummary> baseline,
        ImmutableArray<RendererPerfBlockSummary> candidate, ImmutableArray<string> global,
        RendererPerfReport baselineRaw, RendererPerfReport candidateRaw)
    {
        var rows = ImmutableArray.CreateBuilder<RendererPerfComparisonRow>();
        static (string SceneId, RendererPerfLayout Layout, int Iteration) Key(RendererPerfBlockSummary b)
            => (b.Block.SceneId, b.Block.Layout, b.Block.Iteration);
        var before = baseline.ToDictionary(Key);
        var after = candidate.ToDictionary(Key);
        foreach (var key in before.Keys.Concat(after.Keys).Distinct().OrderBy(k => k.SceneId, StringComparer.Ordinal).ThenBy(k => k.Layout).ThenBy(k => k.Iteration))
        {
            before.TryGetValue(key, out var a);
            after.TryGetValue(key, out var b);
            var reasons = global.ToList();
            if (a is null) reasons.Add("baseline block missing");
            if (b is null) reasons.Add("candidate block missing");
            if (a is not null && b is not null && key.Layout == RendererPerfLayout.PerPass && key.Iteration >= 0)
            {
                var aFrames = baselineRaw.Blocks.Single(r => r.Block == a.Block).Measured;
                var bFrames = candidateRaw.Blocks.Single(r => r.Block == b.Block).Measured;
                if (aFrames.Length != bFrames.Length || !aFrames.Zip(bFrames).All(pair => Shape(pair.First).SequenceEqual(Shape(pair.Second))))
                    reasons.Add("Incompatible pass layout, marker or parent ownership.");
            }
            var deltas = ImmutableDictionary.CreateBuilder<string, RendererPerfDelta>(StringComparer.Ordinal);
            var gapReasons = new List<string>();
            foreach (string name in (a?.Metrics.Keys ?? []).Concat(b?.Metrics.Keys ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var left = a?.Metrics.GetValueOrDefault(name);
                var right = b?.Metrics.GetValueOrDefault(name);
                string? refusal = reasons.Count == 0 ? null : string.Join(" ", reasons);
                var refusalState = RendererPerfMetricStatus.ModelIncomplete;
                if (refusal is null && name.StartsWith("gap.", StringComparison.Ordinal))
                {
                    string prefix = name[..name.LastIndexOf('.')];
                    var modelA = a?.Metrics.GetValueOrDefault(prefix + ".excess")?.Mean;
                    var modelB = b?.Metrics.GetValueOrDefault(prefix + ".excess")?.Mean;
                    if (modelA?.Status != RendererPerfMetricStatus.Available || modelB?.Status != RendererPerfMetricStatus.Available)
                    {
                        refusal = string.Join(" ", new[] { "Incomplete or invalid references cannot supply gap deltas.", modelA?.Reason, modelB?.Reason }
                            .Where(r => r is not null));
                        if (modelA?.Status == RendererPerfMetricStatus.Invalid || modelB?.Status == RendererPerfMetricStatus.Invalid)
                            refusalState = RendererPerfMetricStatus.Invalid;
                    }
                    else if (modelA.Source != modelB.Source)
                    {
                        refusal = $"Unmatched reference/model provenance for {prefix}. Baseline {modelA.Source}. Candidate {modelB.Source}.";
                        gapReasons.Add(refusal);
                    }
                }
                RendererPerfMetric Delta(RendererPerfMetric? x, RendererPerfMetric? y)
                    => Difference(x, y, refusal, refusalState);
                var mean = Delta(left?.Mean, right?.Mean);
                RendererPerfMetric percentage = mean.Value is { } delta && left!.Mean.Value != 0
                    ? Available(100 * (delta / left.Mean.Value!.Value), "percent", "candidate minus baseline mean / baseline mean * 100")
                    : Unavailable("percent", "relative mean delta", mean.Reason ?? "The baseline mean is zero, so percentage is undefined.", mean.Status == RendererPerfMetricStatus.Available
                        ? RendererPerfMetricStatus.ModelIncomplete : mean.Status);
                deltas.Add(name, new(mean, Delta(left?.Median, right?.Median), Delta(left?.P95, right?.P95), Delta(left?.P99, right?.P99), percentage));
            }
            // A gap mismatch does not invalidate independently matched frame or counter evidence.
            var rowReasons = reasons.Concat(gapReasons).Distinct(StringComparer.Ordinal).ToImmutableArray();
            rows.Add(new(key.SceneId, key.Layout, key.Iteration, rowReasons.IsEmpty, rowReasons, deltas.ToImmutable()));
        }
        return rows.ToImmutable();
    }

    static RendererPerfMetric Difference(RendererPerfMetric? baseline, RendererPerfMetric? candidate, string? refusal,
        RendererPerfMetricStatus refusalState)
    {
        string unit = baseline?.Unit ?? candidate?.Unit ?? "unknown";
        const string source = "candidate minus baseline";
        if (refusal is not null) return Unavailable(unit, source, refusal, refusalState);
        if (baseline is null || candidate is null) return Unavailable(unit, source, "The metric is missing from baseline or candidate.");
        if (baseline.Status != RendererPerfMetricStatus.Available || candidate.Status != RendererPerfMetricStatus.Available)
            return Unavailable(unit, source, string.Join(" ", new[] { baseline.Reason, candidate.Reason }.Where(r => r is not null)),
                baseline.Status == RendererPerfMetricStatus.Invalid || candidate.Status == RendererPerfMetricStatus.Invalid
                    ? RendererPerfMetricStatus.Invalid : RendererPerfMetricStatus.ModelIncomplete);
        if (baseline.Unit != candidate.Unit || baseline.Source != candidate.Source)
            return Unavailable(unit, source, "Metric unit or source provenance differs.");
        return Available(candidate.Value!.Value - baseline.Value!.Value, unit, source);
    }

    static ImmutableArray<RendererPerfIdentityDifference> Differences(RendererPerfReport baseline, RendererPerfReport candidate,
        RendererPerfCompilerInventory? beforeInventory, RendererPerfCompilerInventory? afterInventory)
    {
        var changes = ImmutableArray.CreateBuilder<RendererPerfIdentityDifference>();
        void Compare<T>(string path, T a, T b, Func<string, bool> informational)
            => Walk(path, JsonSerializer.SerializeToElement(a, RendererPerfManifest.JsonOptions),
                JsonSerializer.SerializeToElement(b, RendererPerfManifest.JsonOptions), informational, changes);
        static bool IdentityInfo(string path) => path is "identity.unitId" or "identity.manifestSha256" or "identity.workloadSha"
            or "identity.baselineMainSha" or "identity.authorizationSha" or "identity.workflowSha" or "identity.runId"
            or "identity.attempt" or "identity.processId" or "identity.harnessSha256";
        static bool ManifestInfo(string path) => path is "manifest.unitId" or "manifest.workloadSha" or "manifest.baselineMainSha"
            || path.StartsWith("manifest.approval.", StringComparison.Ordinal) || path.StartsWith("manifest.budget.", StringComparison.Ordinal)
            || path.EndsWith(".shaderSha256", StringComparison.Ordinal);
        Compare("identity", baseline.Identity, candidate.Identity, IdentityInfo);
        Compare("manifest", baseline.Manifest, candidate.Manifest, ManifestInfo);
        Compare("recipes", baseline.Recipes, candidate.Recipes, p => p.EndsWith(".shaderSha256", StringComparison.Ordinal)
            || p.EndsWith(".sourceProvenance", StringComparison.Ordinal));
        Compare("passLayouts", baseline.Blocks.Select(b => new
        {
            b.Block,
            Shape = b.Measured.IsEmpty ? [] : Shape(b.Measured[0]).Select(p =>
                new { Id = p.Item1, Occurrence = p.Item2, ParentId = p.Item3, ParentOccurrence = p.Item4, Begin = p.Item5, End = p.Item6, DeviceOnly = p.Item7 }).ToArray()
        }).ToArray(),
            candidate.Blocks.Select(b => new
            {
                b.Block,
                Shape = b.Measured.IsEmpty ? [] : Shape(b.Measured[0]).Select(p =>
                new { Id = p.Item1, Occurrence = p.Item2, ParentId = p.Item3, ParentOccurrence = p.Item4, Begin = p.Item5, End = p.Item6, DeviceOnly = p.Item7 }).ToArray()
            }).ToArray(), _ => false);
        // A unique recipe can split or merge across builds. Logical occurrence roles are the join.
        if (beforeInventory is not null && afterInventory is not null)
        {
            Compare("compiler.operations", beforeInventory.Operations, afterInventory.Operations, _ => false);
            var before = beforeInventory.Occurrences.OrderBy(o => o.OperationOrdinal).ThenBy(o => o.Kind).ThenBy(o => o.CreationOrdinal).ToArray();
            var after = afterInventory.Occurrences.OrderBy(o => o.OperationOrdinal).ThenBy(o => o.Kind).ThenBy(o => o.CreationOrdinal).ToArray();
            Compare("compiler.roles", before.Select(o => new { o.OperationOrdinal, o.Kind, o.CreationOrdinal, o.BindCount }).ToArray(),
                after.Select(o => new { o.OperationOrdinal, o.Kind, o.CreationOrdinal, o.BindCount }).ToArray(), _ => false);
            for (int n = 0; n < Math.Min(before.Length, after.Length); n++)
            {
                var a = before[n].Production; var b = after[n].Production;
                string path = $"compiler.occurrences[{n}]";
                Compare(path + ".layout", a.LayoutSha256, b.LayoutSha256, _ => false);
                Compare(path + ".codeGeneration", a.CodeGenerationSha256, b.CodeGenerationSha256, _ => false);
                Compare(path + ".localSize", new { a.LocalSizeX, a.LocalSizeY, a.LocalSizeZ }, new { b.LocalSizeX, b.LocalSizeY, b.LocalSizeZ }, _ => false);
                Compare(path + ".pipeline", a.PipelineSha256, b.PipelineSha256, _ => true);
                Compare(path + ".source", a.SourceSha256, b.SourceSha256, _ => true);
                Compare(path + ".artifact", a.ShaderArtifactSha256, b.ShaderArtifactSha256, _ => true);
            }
            Compare("compiler.production", baseline.Compiler.Production, candidate.Compiler.Production, _ => false);
            Compare("compiler.diagnostic", baseline.Compiler.Diagnostic, candidate.Compiler.Diagnostic, _ => false);
        }
        return changes.ToImmutable();
    }

    static void Walk(string path, JsonElement a, JsonElement b, Func<string, bool> informational,
        ImmutableArray<RendererPerfIdentityDifference>.Builder changes)
    {
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            foreach (string name in a.EnumerateObject().Select(p => p.Name).Concat(b.EnumerateObject().Select(p => p.Name))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                a.TryGetProperty(name, out var left); b.TryGetProperty(name, out var right);
                Walk(path + "." + name, left, right, informational, changes);
            }
        }
        else if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            for (int i = 0; i < Math.Max(a.GetArrayLength(), b.GetArrayLength()); i++)
                Walk($"{path}[{i}]", i < a.GetArrayLength() ? a[i] : default, i < b.GetArrayLength() ? b[i] : default, informational, changes);
        }
        else
        {
            string left = a.ValueKind == JsonValueKind.Undefined ? "missing" : a.GetRawText();
            string right = b.ValueKind == JsonValueKind.Undefined ? "missing" : b.GetRawText();
            if (left != right) changes.Add(new(path, left, right, !informational(path)));
        }
    }

    internal static string Markdown(RendererPerfComparisonResult comparison) => RendererPerfMarkdown.Comparison(comparison);
}
