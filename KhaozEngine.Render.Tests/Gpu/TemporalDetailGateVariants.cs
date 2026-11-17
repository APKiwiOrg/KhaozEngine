using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit.Sdk;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// Original is the first production gate: it also distrusted the followed and band marks (mask 3) and kept the lock
// rule, so the gate study can regenerate that gate's row.
public enum TemporalDetailGate { Production, Narrower, Wider, Original, MarksOnly }

/// <summary>Diagnostic interventions in the test source seam. The shipped gate and setting stay unchanged.</summary>
internal static class TemporalDetailGateVariants
{
    // Test-process selection reaches every fixture and resolve-only rig. No engine setting is added.
    internal static TemporalDetailGate Selected => Enum.TryParse<TemporalDetailGate>(
        Environment.GetEnvironmentVariable("KE_TEMPORAL_DETAIL_TEST_GATE"), out var gate) ? gate : TemporalDetailGate.Production;

    internal static IEnumerable<TemporalDetailGate> ReportGates
    {
        get
        {
            string? selection = Environment.GetEnvironmentVariable("KE_TEMPORAL_DETAIL_REPORT_GATES");
            if (selection is null) return Enum.GetValues<TemporalDetailGate>();
            return Array.ConvertAll(selection.Split(','), Enum.Parse<TemporalDetailGate>);
        }
    }

    internal static string Rewrite(string source, TemporalDetailGate gate)
    {
        if (gate == TemporalDetailGate.Production) gate = Selected;
        if (gate == TemporalDetailGate.Production || !source.Contains(TemporalDetailMath.GateGlsl, StringComparison.Ordinal))
            return source;
        string replacement = gate switch
        {
            TemporalDetailGate.Narrower => TemporalDetailMath.GateGlsl
                .Replace($"const uint distrustMask = {TemporalDetailMath.DistrustMask}u;", "const uint distrustMask = 1u;", StringComparison.Ordinal)
                .Replace(" && !(locked && motion <= ShimmerStillPixels)", "", StringComparison.Ordinal),
            TemporalDetailGate.Original => TemporalDetailMath.GateGlsl
                .Replace($"const uint distrustMask = {TemporalDetailMath.DistrustMask}u;", "const uint distrustMask = 3u;", StringComparison.Ordinal),
            TemporalDetailGate.MarksOnly => TemporalDetailMath.GateGlsl
                .Replace($"const uint distrustMask = {TemporalDetailMath.DistrustMask}u;", "const uint distrustMask = 3u;", StringComparison.Ordinal)
                .Replace(" && !(locked && motion <= ShimmerStillPixels)", "", StringComparison.Ordinal),
            TemporalDetailGate.Wider => TemporalDetailMath.GateGlsl
                .Replace($"const uint distrustMask = {TemporalDetailMath.DistrustMask}u;", "const uint distrustMask = 31u;", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(gate)),
        };
        return source.Replace(TemporalDetailMath.GateGlsl, replacement, StringComparison.Ordinal);
    }
}

/// <summary>Gate rows can be selected for a single measurement pass without rerunning previous captures.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TemporalDetailReportGatesAttribute : DataAttribute
{
    public override IEnumerable<object[]> GetData(MethodInfo testMethod)
    {
        foreach (var gate in TemporalDetailGateVariants.ReportGates) yield return [gate];
    }
}
