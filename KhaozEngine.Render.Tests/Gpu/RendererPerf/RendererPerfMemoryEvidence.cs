using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfManifest;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// Outside-frame observations retain their source and batch identity, even if a later read fails.
internal sealed class RendererPerfMemoryEvidence
{
    readonly List<RendererPerfMemoryObservation> _observations;
    internal RendererPerfMemoryEvidence(IEnumerable<RendererPerfMemoryObservation>? prior = null)
        => _observations = prior is null ? [] : [.. prior];

    internal void Observe(string operation, string stage, int? firstFrame, long ticks, long frequency,
        RendererPerfMemoryReport? memory, string? failure = null)
        => _observations.Add(new(operation, stage, firstFrame, ticks, frequency, memory?.Native ?? [],
            memory?.Process ?? ImmutableDictionary<string, RendererPerfMetric>.Empty,
            memory is null ? failure ?? "The session supplied no memory observation." : null));

    internal RendererPerfMemoryReport Report()
    {
        var last = _observations.LastOrDefault(o => o.Reason is null);
        return new(last?.Native ?? [], last?.Process ?? ImmutableDictionary<string, RendererPerfMetric>.Empty)
        { Observations = [.. _observations], Peaks = Peaks(_observations) };
    }

    static ImmutableDictionary<string, RendererPerfMemoryPeak> Peaks(IReadOnlyList<RendererPerfMemoryObservation> observations)
    {
        var peaks = ImmutableDictionary.CreateBuilder<string, RendererPerfMemoryPeak>(StringComparer.Ordinal);
        for (int i = 0; i < observations.Count; i++)
            foreach (var pair in observations[i].Process)
                if (pair.Value.Status == RendererPerfMetricStatus.Available
                    && (!peaks.TryGetValue(pair.Key, out var peak) || pair.Value.Value > peak.Metric.Value))
                    peaks[pair.Key] = new(pair.Value, i);
        return peaks.ToImmutable();
    }

    internal static void Validate(RendererPerfReport report)
    {
        var observations = report.Memory.Observations;
        int operations = 1 + report.Manifest.Blocks.Length + report.Manifest.Controls.Length;
        Require(observations.Length <= report.Batches.Length + operations + 1, "Metric", "Memory observation capacity was exceeded.");
        var identities = new HashSet<(string, string, int?)>();
        var operationIds = new HashSet<string>(report.Batches.Select(b => b.Operation), StringComparer.Ordinal)
        { report.Manifest.Schedule.ConditioningOperation, "unit" };
        operationIds.UnionWith(report.Blocks.Select(b => $"{b.Block.SceneId}/{b.Block.Layout}/{b.Block.Iteration}"));
        operationIds.UnionWith(report.Controls.Select(c => $"control:{c.Control.Id}/TotalOnly/0"));
        foreach (var observation in observations)
        {
            if (observation.Stage == "unit-final")
                Require(observation == observations[^1] && report.Batches.All(b => b.EndTicks <= observation.AtTicks),
                    "Metric", "Final production observation must follow every retained batch.");
            Require(operationIds.Contains(observation.Operation) && observation.Frequency > 0
                && identities.Add((observation.Operation, observation.Stage, observation.FirstFrame)), "Metric", "Memory observation identity is invalid or duplicated.");
            Require(observation.Operation != "unit" && observation.Stage == "startup" && observation.FirstFrame is null
                || observation.Operation == "unit" && observation.Stage == "unit-final" && observation.FirstFrame is null
                || observation.Stage == "batch" && report.Batches.Any(b => b.Operation == observation.Operation
                    && b.FirstFrame == observation.FirstFrame && b.EndTicks >= observation.AtTicks
                    && b.StartTicks <= observation.AtTicks && b.Frequency == observation.Frequency),
                "Metric", "Memory observation is not associated with its operation or batch.");
            Require(observation.Reason is null || Text(observation.Reason) && observation.Process.IsEmpty && observation.Native.IsEmpty,
                "Metric", "Unavailable memory observations need a reason and cannot carry stale values.");
            foreach (var pair in observation.Process)
            {
                RendererPerfReportJson.Metric(pair.Value);
                Require(Text(pair.Key) && pair.Value.Unit == "bytes"
                    && (pair.Value.Value is null || pair.Value.Value >= 0), "Metric", "Process and managed memory require nonnegative bytes.");
            }
            RendererPerfReportJson.ValidateMemory(observation.Native);
        }
        var expected = Peaks(observations);
        Require(expected.Count == report.Memory.Peaks.Count && expected.All(p => report.Memory.Peaks.TryGetValue(p.Key, out var actual)
            && actual == p.Value), "Metric", "Memory peaks must match retained observations and their exact sources.");
    }
}
