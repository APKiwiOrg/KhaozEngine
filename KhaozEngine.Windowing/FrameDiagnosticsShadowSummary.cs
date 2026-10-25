using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.Windowing;

internal sealed record ShadowStageCoverage(ShadowStageProbeStatus Status, int Rows);
internal sealed record ShadowStageTimingSummary(string Stage, int SampleCount, double? MeanMs, double? MaxMs);
internal sealed record FrameDiagnosticsShadowSummary(ShadowStageProbeMode Mode, int ValidRows, int InvalidRows,
    bool Comparable, double? MeanScenePrefixMs, ShadowStageCoverage[] Coverage, ShadowStageTimingSummary[] Timings)
{
    // Publication only. Reflection and temporary lists never run in the sampling path.
    internal static FrameDiagnosticsShadowSummary Create(FrameDiagnosticsSample[] samples, ShadowStageProbeMode mode)
    {
        int valid = samples.Count(s => s.ShadowStage.Status == ShadowStageProbeStatus.Valid);
        var coverage = Enum.GetValues<ShadowStageProbeStatus>()
            .Select(status => new ShadowStageCoverage(status, samples.Count(s => s.ShadowStage.Status == status))).ToArray();
        var summaries = new List<ShadowStageTimingSummary>();
        foreach (var property in typeof(ShadowStageProbeTicks).GetProperties())
        {
            if (property.PropertyType != typeof(long?)) continue;
            int count = 0;
            double sum = 0, max = 0;
            foreach (var sample in samples)
            {
                if (sample.ShadowStage.Status != ShadowStageProbeStatus.Valid
                    || sample.ShadowStage.Callback.Snapshot?.Timings is not { } ticks
                    || property.GetValue(ticks) is not long value) continue;
                double ms = value * 1000.0 / ticks.Frequency;
                sum += ms;
                max = Math.Max(max, ms);
                count++;
            }
            summaries.Add(new(property.Name, count, count > 0 ? sum / count : null, count > 0 ? max : null));
        }
        return new(mode, valid, mode == ShadowStageProbeMode.Off ? 0 : samples.Length - valid,
            mode == ShadowStageProbeMode.Off || valid == samples.Length,
            summaries.Single(s => s.Stage == nameof(ShadowStageProbeTicks.ScenePrefix)).MeanMs, coverage, summaries.ToArray());
    }
}
