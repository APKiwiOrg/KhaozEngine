using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// One authorized comparison unit: the same case with coverage off and on, run sequentially in the listed order in one
/// process on one runner. Every shared field is validated once for both conditions before either session opens. The
/// conditions differ only in coverage, so warm-up and measured counts are matched by construction. Shared process
/// state is a disclosed tradeoff, controlled by the explicit order and the matched warm-ups.
/// </summary>
internal sealed class CoverageCostPairManifest
{
    internal const string Schema = "khaozengine.coverage-cost-pair/1";

    CoverageCostPairManifest(string unit, CoverageCostManifest[] conditions, string sha256)
    {
        Unit = unit; Conditions = conditions; Sha256 = sha256;
        Order = conditions.Select(c => c.Coverage ? "on" : "off").ToArray();
    }

    internal string Unit { get; }
    /// <summary>Both conditions in their authorized run order.</summary>
    internal CoverageCostManifest[] Conditions { get; }
    internal string[] Order { get; }
    /// <summary>Lowercase SHA-256 of the exact pair bytes, shared by both conditions.</summary>
    internal string Sha256 { get; }

    internal static CoverageCostPairManifest Parse(ReadOnlySpan<byte> utf8)
    {
        var (unit, conditions, sha) = CoverageCostManifest.ParsePair(utf8);
        return new(unit, conditions, sha);
    }
}

/// <summary>Both condition reports in run order, the pair verdicts and the informational deltas. A condition after a
/// failed one is null and named as not run.</summary>
internal sealed record CoverageCostPairReport(CoverageCostRunStatus Status, string Reason, string Unit, string Sha256, string[] Order,
    CoverageCostReport?[] Reports, IReadOnlyList<CoverageCostBudget> Budgets, IReadOnlyList<CoverageCostMetric> Deltas)
{
    internal const string Schema = "khaozengine.coverage-cost-pair-report/1";

    internal static CoverageCostPairReport Refused(string reason, string sha) => new(CoverageCostRunStatus.Refused, reason, "", sha, [],
        [null, null],
        [
            new(CoverageCostBudgets.AddedCpuName, CoverageCostBudgetStatus.Unproved, null, CoverageCostBudgets.AddedCpuMilliseconds, "ms/frame", "the pair was refused"),
            new(CoverageCostBudgets.GpuName, CoverageCostBudgetStatus.Unproved, null, CoverageCostBudgets.GpuOverheadPercent, "percent", "the pair was refused"),
            new(CoverageCostBudgets.AllocationName, CoverageCostBudgetStatus.Unproved, null, 0, "bytes/frame", "the pair was refused"),
        ], []);

    internal string ToJson() => CoverageCostJson.Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        w.WriteString("status", CoverageCostReport.Token(Status));
        w.WriteString("reason", Reason);
        w.WriteString("unit", Unit);
        w.WriteString("sha256", Sha256);
        w.WriteStartArray("order");
        foreach (string condition in Order) w.WriteStringValue(condition);
        w.WriteEndArray();
        CoverageCostJson.WriteBudgets(w, Budgets);
        CoverageCostReport.WriteMetrics(w, "deltas", Deltas);
        w.WriteStartArray("reports");
        foreach (var report in Reports)
            if (report is null) w.WriteNullValue();
            else report.WriteTo(w);
        w.WriteEndArray();
        w.WriteEndObject();
    });
}
