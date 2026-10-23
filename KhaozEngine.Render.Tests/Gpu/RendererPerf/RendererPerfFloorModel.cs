using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfLaunchRelation { MayOverlap, DisjointSerial }
internal sealed record RendererPerfReferenceRates(double? PeakBandwidthBytesPerSecond, double? PeakFp32OpsPerSecond,
    double? MeasuredBandwidthBytesPerSecond, double? MatchedOpsPerSecond, double? MatchedPixelsPerSecond,
    double? MatchedLaunchNs, string InstructionClass, string Source);
internal sealed record RendererPerfFloorAssessment(RendererPerfMetricStatus Status, RendererPerfMetric PeakLowerBoundNs,
    RendererPerfMetric CalibratedReferenceNs, RendererPerfMetric AbsoluteExcessNs, RendererPerfMetric Ratio,
    string DominantResource, ImmutableArray<string> MissingEvidence)
{
    internal RendererPerfWorkRange? CalibratedRange { get; init; }
    internal double? ClaimedPeakLowerBoundNs { get; init; }
    internal double? UnclampedExcessNs { get; init; }
}

internal static class RendererPerfFloorModel
{
    internal static long TextureBytes(uint width, uint height, uint mips, uint layers, uint samples, GpuPixelFormat format)
    {
        if (width == 0 || height == 0 || mips == 0 || layers == 0 || samples == 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Texture dimensions, levels, layers and samples must be positive.");
        uint maximumMips = 1;
        for (uint edge = Math.Max(width, height); edge > 1; edge >>= 1) maximumMips++;
        if (mips > maximumMips) throw new ArgumentOutOfRangeException(nameof(mips));
        long texels = 0;
        for (uint mip = 0; mip < mips; mip++)
            texels = checked(texels + (long)Math.Max(1, width >> (int)mip) * Math.Max(1, height >> (int)mip));
        return checked(texels * layers * samples * RendererPerfRecorder.BytesPerPixel(format));
    }
    internal static RendererPerfFloorAssessment Evaluate(RendererPassWorkModel model, RendererPerfReferenceRates rates,
        double observedNs, RendererPerfLaunchRelation launch = RendererPerfLaunchRelation.MayOverlap)
    {
        if (!double.IsFinite(observedNs) || observedNs < 0) throw new ArgumentOutOfRangeException(nameof(observedNs));
        if (string.IsNullOrWhiteSpace(rates.Source)) throw new ArgumentException("Reference inputs require a source.", nameof(rates));
        foreach (double? rate in new[] { rates.PeakBandwidthBytesPerSecond, rates.PeakFp32OpsPerSecond,
            rates.MeasuredBandwidthBytesPerSecond, rates.MatchedOpsPerSecond, rates.MatchedPixelsPerSecond })
            if (rate is { } value && (!double.IsFinite(value) || value <= 0)) throw new ArgumentOutOfRangeException(nameof(rates));
        if (rates.MatchedLaunchNs is { } launchNs && (!double.IsFinite(launchNs) || launchNs < 0))
            throw new ArgumentOutOfRangeException(nameof(rates));
        RendererPerfFloorAssessment Unexecuted(RendererPerfMetricStatus state, string reason)
        {
            var ns = new RendererPerfMetric(null, "ns", "frozen registry ownership", state, reason);
            return new(state, ns, ns, ns, ns with { Unit = "ratio" }, "not applicable", [reason]);
        }
        if (model.Components.TryGetValue("execution", out var execution) && execution.Minimum == 0)
            return Unexecuted(RendererPerfMetricStatus.NotExecuted, "The cached or inactive occurrence did not execute.");
        if (model.Components.TryGetValue("gpuEligible", out var gpu) && gpu.Minimum == 0)
            return Unexecuted(RendererPerfMetricStatus.Unsupported, "The CPU-only owner has no GPU duration or hardware reference.");
        var missing = model.MissingEvidence.ToBuilder();
        double? Read(string key, string unit)
        {
            if (!model.Components.TryGetValue(key, out var work) || work.Minimum is null || work.Provenance == "unknown")
            { missing.Add($"Missing minimum {key} evidence."); return null; }
            if (work.Unit != unit || !double.IsFinite(work.Minimum.Value) || work.Minimum < 0
                || work.Maximum is { } max && (!double.IsFinite(max) || max < work.Minimum))
                throw new ArgumentException($"Invalid {key} units or work range.", nameof(model));
            if (work.Maximum is null) missing.Add(work.Reason ?? $"Missing maximum {key} evidence.");
            return work.Minimum;
        }
        double? reads = Read("readBytes", "bytes"), writes = Read("writeBytes", "bytes");
        // Preserve known portions without assuming an unknown component is zero.
        double? bytes = reads is null && writes is null ? null : (reads ?? 0) + (writes ?? 0);
        double? ops = model.Components.ContainsKey("fp32-fma") ? Read("fp32-fma", "operations") : null;
        double? pixels = model.Components.ContainsKey("pixels") ? Read("pixels", "pixels") : null;
        var peak = new List<(double Value, string Resource)>();
        var calibrated = new List<(double Value, string Resource)>();
        void Rate(List<(double, string)> terms, double? work, double? rate, string name, bool required)
        {
            if (work is null) return;
            if (work == 0) { terms.Add((0, name)); return; }
            if (rate is null) { if (required) missing.Add($"Missing matched {name} rate."); return; }
            double time = work.Value / rate.Value * 1e9;
            if (!double.IsFinite(time)) throw new ArgumentOutOfRangeException(nameof(model), "Reference arithmetic overflowed.");
            terms.Add((time, name));
        }
        Rate(peak, bytes, rates.PeakBandwidthBytesPerSecond, "bandwidth", false);
        Rate(peak, ops, rates.PeakFp32OpsPerSecond, "fp32-fma", false);
        Rate(calibrated, bytes, rates.MeasuredBandwidthBytesPerSecond, "bandwidth", true);
        if (ops > 0 && rates.InstructionClass != "fp32-fma") missing.Add("The matched instruction class differs from fp32-fma.");
        else Rate(calibrated, ops, rates.MatchedOpsPerSecond, "fp32-fma", true);
        Rate(calibrated, pixels, rates.MatchedPixelsPerSecond, "fill", true);
        foreach (string key in model.Components.Keys.Where(k => k.StartsWith("operations.", StringComparison.Ordinal)))
        {
            double? work = Read(key, "operations");
            if (rates.InstructionClass != key[11..]) missing.Add($"No matched instruction throughput for {key}.");
            else Rate(calibrated, work, rates.MatchedOpsPerSecond, key, true);
        }
        double? Upper(string key) => model.Components.TryGetValue(key, out var work) ? work.Maximum : null;
        var upperTerms = new List<double>();
        bool upperKnown = true;
        void UpperRate(double? work, double? rate)
        {
            if (work == 0) { upperTerms.Add(0); return; }
            if (work is null || rate is null) { upperKnown = false; return; }
            double value = work.Value / rate.Value * 1e9;
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(model));
            upperTerms.Add(value);
        }
        UpperRate(Upper("readBytes") + Upper("writeBytes"), rates.MeasuredBandwidthBytesPerSecond);
        if (model.Components.ContainsKey("fp32-fma"))
            UpperRate(Upper("fp32-fma"), rates.InstructionClass == "fp32-fma" ? rates.MatchedOpsPerSecond : null);
        if (model.Components.ContainsKey("pixels")) UpperRate(Upper("pixels"), rates.MatchedPixelsPerSecond);
        foreach (string key in model.Components.Keys.Where(k => k.StartsWith("operations.", StringComparison.Ordinal)))
            UpperRate(Upper(key), rates.InstructionClass == key[11..] ? rates.MatchedOpsPerSecond : null);
        double? upper = upperKnown && upperTerms.Count != 0 ? upperTerms.Max() : null;
        double? bound = peak.Count == 0 ? null : peak.Max(t => t.Value);
        double? reference = calibrated.Count == 0 ? null : calibrated.Max(t => t.Value);
        if (rates.MatchedLaunchNs is { } overhead)
        {
            reference = reference is null ? overhead : launch == RendererPerfLaunchRelation.DisjointSerial
                ? reference + overhead : Math.Max(reference.Value, overhead);
            upper = upper is null ? null : launch == RendererPerfLaunchRelation.DisjointSerial
                ? upper + overhead : Math.Max(upper.Value, overhead);
        }
        else missing.Add("Missing matched empty-pass or dispatch/dependency overhead.");
        if (reference is { } r && !double.IsFinite(r) || upper is { } u && !double.IsFinite(u)) throw new ArgumentOutOfRangeException(nameof(rates));
        if (bound is null) missing.Add("No defensible peak reference component is available.");
        if (reference is null) missing.Add("No calibrated reference component is available.");
        bool invalid = bound is { } b && observedNs < b;
        if (invalid) missing.Add($"The observed duration {observedNs:R} ns is below the claimed lower bound {bound:R} ns. The work model is invalid.");
        var reasons = missing.Distinct(StringComparer.Ordinal).ToImmutableArray();
        var status = invalid ? RendererPerfMetricStatus.Invalid : reasons.Length != 0
            ? RendererPerfMetricStatus.ModelIncomplete : RendererPerfMetricStatus.Available;
        RendererPerfMetric Metric(double? value, string unit, string source)
        {
            if (invalid || value is null)
            {
                var state = invalid ? RendererPerfMetricStatus.Invalid : status == RendererPerfMetricStatus.Available
                    ? RendererPerfMetricStatus.Unsupported : status;
                string reason = reasons.Length == 0 ? "The calibrated reference is zero, so the ratio is undefined." : string.Join(" ", reasons);
                return new(null, unit, source, state, reason);
            }
            if (!double.IsFinite(value.Value)) throw new ArgumentOutOfRangeException(nameof(model), "Reference arithmetic overflowed.");
            return new(value, unit, source, RendererPerfMetricStatus.Available, null);
        }
        double? excess = invalid ? observedNs - bound : status == RendererPerfMetricStatus.Available && reference is { } basis
            ? observedNs - basis : null;
        return new(status, Metric(bound, "ns", "loose peak lower bound, explicit reference inputs: " + rates.Source),
            Metric(reference, "ns", "empirical reference, logical traffic, overlap policy " + launch + ": " + rates.Source),
            Metric(excess, "ns", invalid ? "observed minus violated peak bound" : "observed minus calibrated reference"),
            Metric(status == RendererPerfMetricStatus.Available && reference > 0 ? observedNs / reference : null, "ratio", "observed / calibrated"),
            rates.MatchedLaunchNs is { } latency && (calibrated.Count == 0 || latency >= calibrated.Max(t => t.Value))
                ? "launch/dependency" : calibrated.Count == 0 ? "unknown" : calibrated.MaxBy(t => t.Value).Resource, reasons)
        {
            ClaimedPeakLowerBoundNs = bound,
            UnclampedExcessNs = excess,
            CalibratedRange = new(reference, upper, "ns", "empirical reference with " + launch + " launch relation: " + rates.Source,
                upper is null ? "unknown" : "estimated", upper is null ? "One or more work or rate maxima are unavailable." : null)
        };
    }
}
