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

/// <summary>Observed host topology. Requested fixture labels are never substituted for these counts. Fingerprint is a
/// SHA-256 over the compiled refinement, so two runs can prove they measured one fixture.</summary>
internal sealed record CoverageCostTopology(int SourceVertices, int SourceTriangles, int EligibleTriangles,
    int RefinedSourceTriangles, int FinalVertices, int FinalTriangles, int Commands, int Materials, string RefinementLevels,
    int Regions, string Fingerprint);

internal sealed record CoverageCostSessionInfo(string Backend, string Adapter, string AdapterClass, string Camera,
    string Light, string Layout, string Timing, CoverageCostTopology Topology);

/// <summary>A complete cost report, materialized after the measurement window. Refused and failed runs keep every
/// counted frame and the reason, so a partial run is never mistaken for a complete one. Unit names the paired unit a
/// condition belongs to, or null for a single diagnostic case.</summary>
internal sealed record CoverageCostReport(
    CoverageCostRunStatus Status, string Reason, CoverageCostManifest? Manifest, string ManifestSha256, bool Synthetic,
    IReadOnlyList<KeyValuePair<string, string>> Environment, CoverageCostSessionInfo? Session, int WarmupFrames,
    int MeasuredFrames, IReadOnlyList<CoverageCostSpan> Spans, IReadOnlyList<CoverageCostMetric> Metrics,
    IReadOnlyList<CoverageCostMetric> Capacities, IReadOnlyList<KeyValuePair<string, int>> Refusals,
    IReadOnlyList<CoverageCostMetric> Evidence, IReadOnlyList<CoverageCostBudget> Budgets, string? Unit = null)
{
    internal const string Schema = "khaozengine.coverage-cost-report/1";

    internal CoverageCostSpan Span(string name) => Spans.Single(s => s.Name == name);
    internal CoverageCostMetric Metric(string name) => Metrics.Single(m => m.Name == name);

    internal string ToJson() => CoverageCostJson.Write(WriteTo);

    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        if (Unit is { } unit) w.WriteString("unit", unit);
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
            w.WriteString("fingerprint", t.Fingerprint);
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
        WriteMetrics(w, "evidence", Evidence);
        w.WriteStartArray("refusals");
        foreach (var (code, count) in Refusals)
        {
            w.WriteStartObject();
            w.WriteString("code", code);
            w.WriteNumber("count", count);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        CoverageCostJson.WriteBudgets(w, Budgets);
        w.WriteEndObject();
    }

    internal static void WriteMetrics(Utf8JsonWriter w, string name, IReadOnlyList<CoverageCostMetric> metrics)
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

    internal static void Number(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }

    internal static string Token(CoverageCostRunStatus status) => status.ToString().ToLowerInvariant();
    internal static string Token(CoverageCostStatus status) => status switch
    {
        CoverageCostStatus.NotApplicable => "not-applicable",
        _ => status.ToString().ToLowerInvariant(),
    };
}

/// <summary>Shared deterministic JSON writing for single and paired reports.</summary>
internal static class CoverageCostJson
{
    internal static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true })) write(w);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void WriteBudgets(Utf8JsonWriter w, IReadOnlyList<CoverageCostBudget> budgets)
    {
        w.WriteStartArray("budgets");
        foreach (var budget in budgets)
        {
            w.WriteStartObject();
            w.WriteString("name", budget.Name);
            w.WriteString("status", budget.Status.ToString().ToLowerInvariant());
            CoverageCostReport.Number(w, "observed", budget.Observed);
            w.WriteNumber("threshold", budget.Threshold);
            w.WriteString("unit", budget.Unit);
            w.WriteString("reason", budget.Reason);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
