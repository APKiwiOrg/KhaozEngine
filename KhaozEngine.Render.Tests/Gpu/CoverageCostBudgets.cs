using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The proposed acceptance gates. A gate passes or fails only on valid measured inputs, otherwise it is Unproved
/// with the reason. Valid means non-synthetic, completed, every configured warm-up and measured frame present, the
/// expected actor frames, no errors, and on the coverage-on condition every actor frame accepted and Covered with no
/// declines or fallbacks. The native GPU gate has no selectable metric yet, so it is always Unproved.
/// </summary>
internal static class CoverageCostBudgets
{
    internal const double AddedCpuMilliseconds = 2, GpuOverheadPercent = 15;
    internal const string AddedCpuName = "cpu.addedPreparationValidation.typical32", GpuName = "gpu.completedFrameOverhead.native",
        AllocationName = "alloc.warmCoverage";
    /// <summary>Disjoint per-frame CPU spans summed on each condition. Their nesting is resolved by the driver, so nothing
    /// is counted twice. Coverage-on pays staging, validation and surface work here, coverage-off pays the ordinary
    /// skinned preparation it displaces.</summary>
    internal static readonly string[] AddedCpuSpans = ["prepare.total", "submission", "reserve", "encode.resolution",
        "encode.surfaceAppend", "encode.ordinarySkinning", "encode.skinnedUpload"];
    internal static string Formula => string.Join(" + ", AddedCpuSpans);
    // Same-run identity. Every key must match. The required ones must also be readable, because two unavailable values
    // match without proving anything.
    static readonly string[] IdentityKeys = ["runId", "processId", "commit", "runtime", "os", "architecture", "cpu", "processorCount",
        "runner", "gc", "tieredCompilation", "tieredPgo", "stopwatchFrequency", "stopwatchHighResolution"];
    static readonly string[] RequiredIdentity = ["runId", "processId", "commit", "runtime", "os", "cpu", "processorCount", "runner",
        "stopwatchFrequency"];
    static readonly string[] AllocationWindows = ["alloc.prepareSubmit.bytesPerFrame", "alloc.reserve.bytesPerFrame",
        "alloc.resolution.bytesPerFrame", "alloc.surface.bytesPerFrame"];

    /// <summary>Single-case gates. An added cost needs a paired unit, so only warm allocation can be decided here.</summary>
    internal static IReadOnlyList<CoverageCostBudget> ForReport(CoverageCostReport report) =>
    [
        Unproved(AddedCpuName, "a single case cannot prove an added cost, it needs a paired unit"),
        Gpu(),
        WarmAllocation(report),
    ];

    internal static IReadOnlyList<CoverageCostBudget> ForPair(CoverageCostPairManifest pair, IReadOnlyList<CoverageCostReport?> reports)
    {
        var (on, off) = Conditions(pair, reports);
        return
        [
            on is null || off is null ? Unproved(AddedCpuName, "a condition was not run") : TypicalAddedCpu(on, off),
            Gpu(),
            on is null ? Unproved(AllocationName, "the coverage-on condition was not run") : WarmAllocation(on),
        ];
    }

    /// <summary>Informational on-minus-off deltas. They include everything their spans include, observer overhead too.</summary>
    internal static IReadOnlyList<CoverageCostMetric> Deltas(CoverageCostPairManifest pair, IReadOnlyList<CoverageCostReport?> reports)
    {
        var (on, off) = Conditions(pair, reports);
        CoverageCostMetric Delta(string span, string includes) =>
            on?.Spans.FirstOrDefault(s => s.Name == span) is { Status: CoverageCostStatus.Measured, Mean: { } a }
                && off?.Spans.FirstOrDefault(s => s.Name == span) is { Status: CoverageCostStatus.Measured, Mean: { } b }
                ? new($"delta.{span}", "ms/frame", CoverageCostStatus.Derived, a - b, $"on minus off per-frame means, {includes}, informational")
                : CoverageCostMetric.Missing($"delta.{span}", "ms/frame", CoverageCostStatus.Unavailable, $"{span} is not measured on both conditions");
        return [Delta("encode.total", "the whole CPU recording"), Delta("frame.wall", "the whole session frame including submission")];
    }

    /// <summary>At most 2 ms added CPU for 32 typical actors: the on condition's sum of <see cref="AddedCpuSpans"/> less the
    /// off condition's, as per-frame means. Instrumentation overhead stays inside and is disclosed, never subtracted.</summary>
    internal static CoverageCostBudget TypicalAddedCpu(CoverageCostReport on, CoverageCostReport off)
    {
        string? refusal = Valid(on, true) ?? Valid(off, false) ?? Comparable(on, off);
        if (refusal is null && on.Manifest!.Fixture != CoverageCostFixtureMode.Typical) refusal = "the budget applies to the typical fixture";
        if (refusal is null && on.Manifest!.Actors != 32) refusal = "the budget applies to 32 actors";
        double onCost = 0, offCost = 0;
        if (refusal is null)
            foreach (string name in AddedCpuSpans)
            {
                if (Mean(on, name) is not { } a || Mean(off, name) is not { } b) { refusal = $"{name} is not measured on both conditions"; break; }
                onCost += a;
                offCost += b;
            }
        if (refusal is not null) return Unproved(AddedCpuName, refusal);
        double added = onCost - offCost;
        return new(AddedCpuName, added <= AddedCpuMilliseconds ? CoverageCostBudgetStatus.Pass : CoverageCostBudgetStatus.Fail, added,
            AddedCpuMilliseconds, "ms/frame", $"on minus off per-frame means of {Formula}");
    }

    /// <summary>At most 15 percent completed-frame GPU overhead on each native reference GPU. Completed Submit plus
    /// WaitForIdle wall time includes submission and CPU waits, so it is not accepted as GPU execution.</summary>
    internal static CoverageCostBudget NativeGpuOverhead(CoverageCostReport on, CoverageCostReport off) => Gpu();

    internal static CoverageCostBudget WarmAllocation(CoverageCostReport report)
    {
        string? refusal = Valid(report, true);
        double total = 0;
        if (refusal is null)
            foreach (string name in AllocationWindows)
                if (report.Metrics.FirstOrDefault(m => m.Name == name) is { Status: CoverageCostStatus.Measured, Value: { } v } && double.IsFinite(v) && v >= 0)
                    total += v;
                else { refusal = $"{name} is not measured"; break; }
        return refusal is not null
            ? new(AllocationName, CoverageCostBudgetStatus.Unproved, null, 0, "bytes/frame", refusal)
            : new(AllocationName, total == 0 ? CoverageCostBudgetStatus.Pass : CoverageCostBudgetStatus.Fail, total, 0, "bytes/frame",
                "measured preparation, submission, reserve, resolution and surface windows");
    }

    /// <summary>Null when the report is a complete, non-synthetic measurement of its condition, otherwise the reason.</summary>
    internal static string? Valid(CoverageCostReport report, bool coverage)
    {
        string condition = coverage ? "coverage-on" : "coverage-off";
        if (report.Synthetic) return $"the {condition} report is synthetic";
        if (report.Status != CoverageCostRunStatus.Completed) return $"the {condition} run is {CoverageCostReport.Token(report.Status)}";
        if (report.Manifest is not { } manifest || manifest.Coverage != coverage) return $"the {condition} report has the wrong condition";
        if (report.WarmupFrames != manifest.WarmupFrames || report.MeasuredFrames != manifest.MeasuredFrames)
            return $"the {condition} run completed {report.WarmupFrames} of {manifest.WarmupFrames} warm-up and {report.MeasuredFrames} of {manifest.MeasuredFrames} measured frames";
        long? Count(string name) => report.Metrics.FirstOrDefault(m => m.Name == name) is { Status: CoverageCostStatus.Measured, Value: { } v }
            && double.IsFinite(v) && v >= 0 && v == Math.Floor(v) ? (long)v : null;
        if (Count("outcome.actorFrames") is not { } frames || Count("outcome.accepted") is not { } accepted
            || Count("outcome.declined") is not { } declined || Count("outcome.covered") is not { } covered
            || Count("outcome.fallback") is not { } fallback || Count("outcome.errors") is not { } errors)
            return $"the {condition} outcome counters are missing or invalid";
        long expected = (long)manifest.Actors * manifest.MeasuredFrames;
        if (frames != expected) return $"the {condition} run has {frames} actor frames, expected {expected}";
        if (errors != 0) return $"the {condition} run has {errors} errors";
        if (coverage && (accepted != frames || covered != frames || declined != 0 || fallback != 0))
            return $"the coverage-on run has {declined} declined, {fallback} fallback and {covered} covered of {frames} actor frames";
        if (!coverage && (accepted != 0 || covered != 0 || declined != 0 || fallback != 0))
            return "the coverage-off run reports coverage outcomes";
        return null;
    }

    /// <summary>Null when both reports are one authorized unit, one run identity and one fixture and configuration.</summary>
    internal static string? Comparable(CoverageCostReport on, CoverageCostReport off)
    {
        if (on.Unit is null || on.Unit != off.Unit || on.ManifestSha256 != off.ManifestSha256)
            return "the conditions are not one authorized paired unit";
        CoverageCostManifest a = on.Manifest!, b = off.Manifest!;
        if (a.Actors != b.Actors || a.Fixture != b.Fixture || a.Passes != b.Passes || a.GpuSkinning != b.GpuSkinning
            || a.Backend != b.Backend || a.WarmupFrames != b.WarmupFrames || a.MeasuredFrames != b.MeasuredFrames
            || a.MaxFrameMilliseconds != b.MaxFrameMilliseconds || a.MaxWallSeconds != b.MaxWallSeconds)
            return "the conditions differ in configuration";
        var left = on.Environment.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        var right = off.Environment.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        foreach (string key in IdentityKeys)
        {
            if (!left.TryGetValue(key, out var x) || !right.TryGetValue(key, out var y)) return $"run identity {key} is missing";
            if (x != y) return $"run identity {key} differs: {y} then {x}";
            if (Array.IndexOf(RequiredIdentity, key) >= 0 && (x.Length == 0 || x == CoverageCostEnvironment.Unavailable))
                return $"run identity {key} is unavailable";
        }
        if (on.Session is not { } s || off.Session is not { } t) return "session info is unavailable";
        if (s.Backend != t.Backend || s.AdapterClass != t.AdapterClass || s.Adapter != t.Adapter) return "the backend or adapter differs";
        // A recording device names no hardware. A session claiming a GPU must name its adapter.
        if (s.AdapterClass != "recording" && (s.Adapter.Length == 0 || s.Adapter == "none")) return "the adapter is unnamed";
        if (s.Topology != t.Topology) return "the fixture topology differs";
        if (s.Camera != t.Camera || s.Light != t.Light || s.Layout != t.Layout || s.Timing != t.Timing)
            return "the display, pass or instrumentation settings differ";
        return null;
    }

    static (CoverageCostReport? On, CoverageCostReport? Off) Conditions(CoverageCostPairManifest pair, IReadOnlyList<CoverageCostReport?> reports)
    {
        int on = Array.IndexOf(pair.Order, "on"), off = Array.IndexOf(pair.Order, "off");
        return (on >= 0 && on < reports.Count ? reports[on] : null, off >= 0 && off < reports.Count ? reports[off] : null);
    }

    static double? Mean(CoverageCostReport report, string span) =>
        report.Spans.FirstOrDefault(s => s.Name == span) is { Status: CoverageCostStatus.Measured, Mean: { } mean } && double.IsFinite(mean) && mean >= 0
            ? mean : null;

    static CoverageCostBudget Gpu() => Unproved(GpuName, "no GPU timestamp-query metric exists and no native GPU route is authorized",
        GpuOverheadPercent, "percent");

    static CoverageCostBudget Unproved(string name, string reason, double threshold = AddedCpuMilliseconds, string unit = "ms/frame") =>
        new(name, CoverageCostBudgetStatus.Unproved, null, name == AllocationName ? 0 : threshold, name == AllocationName ? "bytes/frame" : unit, reason);
}
