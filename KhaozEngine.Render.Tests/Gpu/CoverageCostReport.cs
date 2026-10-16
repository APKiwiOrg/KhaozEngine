using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Tests.Gpu;

internal enum CoverageCostRunStatus { Completed, Refused, Failed }

/// <summary>Measured values were observed directly. Derived values are arithmetic over measured spans. Estimated
/// values are engine estimates or capacity products, not observations. NotApplicable stages did not exist in this
/// case, and Unavailable stages exist but nothing can observe them. The last two never carry a value.</summary>
internal enum CoverageCostStatus { Measured, Derived, Estimated, NotApplicable, Unavailable }

internal enum CoverageCostBudgetStatus { Unproved, Pass, Fail }

internal readonly record struct CoverageCostMetric(string Name, string Unit, CoverageCostStatus Status, double? Value, string Provenance)
{
    internal static CoverageCostMetric Measured(string name, string unit, double value, string provenance) =>
        new(name, unit, CoverageCostStatus.Measured, value, provenance);
    internal static CoverageCostMetric Estimated(string name, string unit, double value, string provenance) =>
        new(name, unit, CoverageCostStatus.Estimated, value, provenance);
    internal static CoverageCostMetric Missing(string name, string unit, CoverageCostStatus status, string reason) =>
        new(name, unit, status, null, reason);
}

/// <summary>Per-frame distribution of one CPU or wall span in milliseconds, with what the span includes.</summary>
internal sealed record CoverageCostSpan(string Name, CoverageCostStatus Status, int Samples, double? Mean, double? Min,
    double? P50, double? P95, double? Max, string Includes)
{
    internal const string Unit = "ms/frame";
}

internal sealed record CoverageCostBudget(string Name, CoverageCostBudgetStatus Status, double? Observed, double Threshold,
    string Unit, string Reason);

/// <summary>Observed host topology. Requested fixture labels are never substituted for these counts.</summary>
internal sealed record CoverageCostTopology(int SourceVertices, int SourceTriangles, int EligibleTriangles,
    int RefinedSourceTriangles, int FinalVertices, int FinalTriangles, int Commands, int Materials, string RefinementLevels,
    int Regions);

internal sealed record CoverageCostSessionInfo(string Backend, string Adapter, string AdapterClass, string Camera,
    string Light, string Layout, string Timing, CoverageCostTopology Topology);

/// <summary>A complete cost report, materialized after the measurement window. Refused and failed runs keep every
/// counted frame and the reason, so a partial run is never mistaken for a complete one.</summary>
internal sealed record CoverageCostReport(
    CoverageCostRunStatus Status, string Reason, CoverageCostManifest? Manifest, string ManifestSha256, bool Synthetic,
    IReadOnlyList<KeyValuePair<string, string>> Environment, CoverageCostSessionInfo? Session, int WarmupFrames,
    int MeasuredFrames, IReadOnlyList<CoverageCostSpan> Spans, IReadOnlyList<CoverageCostMetric> Metrics,
    IReadOnlyList<CoverageCostMetric> Capacities, IReadOnlyList<KeyValuePair<string, int>> Refusals,
    IReadOnlyList<CoverageCostBudget> Budgets)
{
    internal const string Schema = "khaozengine.coverage-cost-report/1";

    internal CoverageCostSpan Span(string name) => Spans.Single(s => s.Name == name);
    internal CoverageCostMetric Metric(string name) => Metrics.Single(m => m.Name == name);

    internal string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("status", Token(Status));
            w.WriteString("reason", Reason);
            w.WriteBoolean("synthetic", Synthetic);
            w.WriteStartObject("manifest");
            w.WriteString("sha256", ManifestSha256);
            if (Manifest is { } m)
            {
                w.WriteString("case", m.Case);
                w.WriteNumber("actors", m.Actors);
                w.WriteString("coverage", m.Coverage ? "on" : "off");
                w.WriteBoolean("gpuSkinning", m.GpuSkinning);
                w.WriteString("fixture", m.Fixture == CoverageCostFixtureMode.Typical ? "typical" : "limit");
                w.WriteString("passes", m.Passes == CoverageCostPassSet.Full ? "full" : "colour-only");
                w.WriteString("backend", m.Backend?.ToString() ?? "recording-cpu");
                w.WriteNumber("warmupFrames", m.WarmupFrames);
                w.WriteNumber("measuredFrames", m.MeasuredFrames);
                w.WriteString("authorizationIdentity", m.AuthorizationIdentity);
                w.WriteString("authorizedBy", m.AuthorizedBy);
                w.WriteString("authorizationReference", m.AuthorizationReference);
            }
            w.WriteEndObject();
            w.WriteStartArray("environment");
            foreach (var (name, value) in Environment)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            if (Session is { } s)
            {
                w.WriteStartObject("session");
                w.WriteString("backend", s.Backend);
                w.WriteString("adapter", s.Adapter);
                w.WriteString("adapterClass", s.AdapterClass);
                w.WriteString("camera", s.Camera);
                w.WriteString("light", s.Light);
                w.WriteString("layout", s.Layout);
                w.WriteString("timing", s.Timing);
                var t = s.Topology;
                w.WriteStartObject("topology");
                w.WriteNumber("sourceVertices", t.SourceVertices);
                w.WriteNumber("sourceTriangles", t.SourceTriangles);
                w.WriteNumber("eligibleTriangles", t.EligibleTriangles);
                w.WriteNumber("refinedSourceTriangles", t.RefinedSourceTriangles);
                w.WriteNumber("finalVertices", t.FinalVertices);
                w.WriteNumber("finalTriangles", t.FinalTriangles);
                w.WriteNumber("commands", t.Commands);
                w.WriteNumber("materials", t.Materials);
                w.WriteString("refinementLevels", t.RefinementLevels);
                w.WriteNumber("regions", t.Regions);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            else w.WriteNull("session");
            w.WriteNumber("warmupFrames", WarmupFrames);
            w.WriteNumber("measuredFrames", MeasuredFrames);
            w.WriteStartArray("spans");
            foreach (var span in Spans)
            {
                w.WriteStartObject();
                w.WriteString("name", span.Name);
                w.WriteString("unit", CoverageCostSpan.Unit);
                w.WriteString("status", Token(span.Status));
                w.WriteNumber("samples", span.Samples);
                Number(w, "mean", span.Mean);
                Number(w, "min", span.Min);
                Number(w, "p50", span.P50);
                Number(w, "p95", span.P95);
                Number(w, "max", span.Max);
                w.WriteString("includes", span.Includes);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            WriteMetrics(w, "metrics", Metrics);
            WriteMetrics(w, "capacities", Capacities);
            w.WriteStartArray("refusals");
            foreach (var (code, count) in Refusals)
            {
                w.WriteStartObject();
                w.WriteString("code", code);
                w.WriteNumber("count", count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("budgets");
            foreach (var budget in Budgets)
            {
                w.WriteStartObject();
                w.WriteString("name", budget.Name);
                w.WriteString("status", budget.Status.ToString().ToLowerInvariant());
                Number(w, "observed", budget.Observed);
                w.WriteNumber("threshold", budget.Threshold);
                w.WriteString("unit", budget.Unit);
                w.WriteString("reason", budget.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteMetrics(Utf8JsonWriter w, string name, IReadOnlyList<CoverageCostMetric> metrics)
    {
        w.WriteStartArray(name);
        foreach (var metric in metrics)
        {
            w.WriteStartObject();
            w.WriteString("name", metric.Name);
            w.WriteString("unit", metric.Unit);
            w.WriteString("status", Token(metric.Status));
            Number(w, "value", metric.Value);
            w.WriteString("provenance", metric.Provenance);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static void Number(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    static string Token(CoverageCostRunStatus status) => status.ToString().ToLowerInvariant();
    static string Token(CoverageCostStatus status) => status switch
    {
        CoverageCostStatus.NotApplicable => "not-applicable",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>The proposed acceptance gates. None of them can pass on synthetic, refused, failed, mismatched or
/// unavailable inputs. The native GPU gate has no selectable metric yet, so it is always unproved.</summary>
internal static class CoverageCostBudgets
{
    internal const double AddedCpuMilliseconds = 2, GpuOverheadPercent = 15;

    /// <summary>At most 2 ms added preparation and validation for 32 typical actors. Added means the on report's
    /// prepare.total plus encode.resolution, less the matching off report's, as per-frame means.</summary>
    internal static CoverageCostBudget TypicalAddedCpu(CoverageCostReport on, CoverageCostReport off)
    {
        const string Name = "cpu.addedPreparationValidation.typical32";
        string? refusal = Comparable(on, off);
        if (refusal is null && on.Manifest!.Fixture != CoverageCostFixtureMode.Typical) refusal = "the budget applies to the typical fixture";
        if (refusal is null && on.Manifest!.Actors != 32) refusal = "the budget applies to 32 actors";
        double? onCost = Sum(on, "prepare.total", "encode.resolution"), offCost = Sum(off, "prepare.total", "encode.resolution");
        if (refusal is null && (onCost is null || offCost is null)) refusal = "a compared span is not measured";
        if (refusal is not null) return new(Name, CoverageCostBudgetStatus.Unproved, null, AddedCpuMilliseconds, "ms/frame", refusal);
        double added = onCost!.Value - offCost!.Value;
        return new(Name, added <= AddedCpuMilliseconds ? CoverageCostBudgetStatus.Pass : CoverageCostBudgetStatus.Fail, added,
            AddedCpuMilliseconds, "ms/frame", "on minus off per-frame means of prepare.total plus encode.resolution");
    }

    /// <summary>At most 15 percent completed-frame GPU overhead on each native reference GPU. Completed
    /// Submit+WaitForIdle wall time includes submission and CPU waits, so it is not accepted as GPU execution.</summary>
    internal static CoverageCostBudget NativeGpuOverhead(CoverageCostReport on, CoverageCostReport off) =>
        new("gpu.completedFrameOverhead.native", CoverageCostBudgetStatus.Unproved, null, GpuOverheadPercent, "percent",
            Comparable(on, off) ?? "no GPU timestamp-query metric exists and no native GPU route is authorized");

    /// <summary>Single-report gates. Warm allocation is checked against the measured windows of a native run.</summary>
    internal static IReadOnlyList<CoverageCostBudget> ForReport(CoverageCostReport report)
    {
        var budgets = new List<CoverageCostBudget>
        {
            new("cpu.addedPreparationValidation.typical32", CoverageCostBudgetStatus.Unproved, null, AddedCpuMilliseconds,
                "ms/frame", "needs a paired on and off native-route report, see CoverageCostBudgets.TypicalAddedCpu"),
            new("gpu.completedFrameOverhead.native", CoverageCostBudgetStatus.Unproved, null, GpuOverheadPercent, "percent",
                "no GPU timestamp-query metric exists and no native GPU route is authorized"),
        };
        string[] windows = ["alloc.prepareSubmit.bytesPerFrame", "alloc.reserve.bytesPerFrame", "alloc.resolution.bytesPerFrame",
            "alloc.surface.bytesPerFrame"];
        string? refusal = report.Synthetic ? "synthetic session" : report.Status != CoverageCostRunStatus.Completed
            ? "run did not complete" : report.Manifest?.Coverage != true ? "coverage is off" : null;
        double total = 0;
        foreach (string name in windows)
            if (report.Metrics.FirstOrDefault(m => m.Name == name) is { Status: CoverageCostStatus.Measured, Value: { } v }) total += v;
            else refusal ??= $"{name} is not measured";
        budgets.Add(refusal is not null
            ? new("alloc.warmCoverage", CoverageCostBudgetStatus.Unproved, null, 0, "bytes/frame", refusal)
            : new("alloc.warmCoverage", total == 0 ? CoverageCostBudgetStatus.Pass : CoverageCostBudgetStatus.Fail, total, 0,
                "bytes/frame", "measured preparation, submission, reserve, resolution and surface windows"));
        return budgets;
    }

    static string? Comparable(CoverageCostReport on, CoverageCostReport off)
    {
        if (on.Synthetic || off.Synthetic) return "a synthetic session cannot prove a budget";
        if (on.Status != CoverageCostRunStatus.Completed || off.Status != CoverageCostRunStatus.Completed) return "both runs must complete";
        if (on.Manifest is not { Coverage: true } a || off.Manifest is not { Coverage: false } b) return "needs one coverage-on and one coverage-off run";
        if (a.Actors != b.Actors || a.Fixture != b.Fixture || a.Passes != b.Passes || a.GpuSkinning != b.GpuSkinning
            || a.Backend != b.Backend || a.WarmupFrames != b.WarmupFrames || a.MeasuredFrames != b.MeasuredFrames)
            return "runs differ in actors, fixture, passes, skinning, backend or frame counts";
        if (on.Session?.Adapter != off.Session?.Adapter) return "runs used different adapters";
        return null;
    }

    static double? Sum(CoverageCostReport report, params string[] spans)
    {
        double total = 0;
        foreach (string name in spans)
        {
            if (report.Spans.FirstOrDefault(s => s.Name == name) is not { Status: CoverageCostStatus.Measured, Mean: { } mean }) return null;
            total += mean;
        }
        return total;
    }
}
