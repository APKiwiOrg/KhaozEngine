using System;
using System.Linq;
using System.Text.RegularExpressions;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The lock release probe's text and cases, device-free: P cannot drift from what ships, B carries no
/// <c>precise</c>, both compile for every backend, and the cases meet every clamp branch.</summary>
public sealed class TemporalLockReleaseProbeTextTests
{
    static string Normalised(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void PreciseHelperIsTheProductionHelperVerbatim()
    {
        string production = Normalised(ShaderSources.TemporalAccumulateGlsl);
        string p = Normalised(TemporalLockReleaseProbe.PreciseHelperGlsl);
        const string Start = "float temporalReleaseLock(";
        int start = production.IndexOf(Start, StringComparison.Ordinal);
        Assert.True(start >= 0, "the production helper is missing");
        Assert.True(production.IndexOf(Start, start + Start.Length, StringComparison.Ordinal) < 0,
            "the production helper is defined twice");
        int end = production.IndexOf("\n}", start, StringComparison.Ordinal) + 2;
        Assert.Equal(production[start..end], p);
        Assert.Equal(2, Regex.Matches(p, @"\bprecise\b").Count);
    }

    [Fact]
    public void BarrierHelperHasNoPreciseAndTwelveBarriers()
    {
        string b = TemporalLockReleaseProbe.BarrierHelperGlsl;
        Assert.DoesNotMatch(@"\bprecise\b", b);
        Assert.DoesNotContain("/", b, StringComparison.Ordinal);
        // One definition and twelve calls.
        Assert.Equal(13, Regex.Matches(b, @"\btemporalOrdered\(").Count);
        Assert.Contains("(floatBitsToUint(Sizes.x) >> 31u)", b, StringComparison.Ordinal);
    }

    [Fact]
    public void MotionReleaseScaleIsTheRoundedThird()
    {
        Assert.Equal(0x3EAAAAABu, BitConverter.SingleToUInt32Bits(TemporalLockReleaseProbe.MotionReleaseScale));
        Assert.Equal(TemporalLockReleaseProbe.MotionReleaseScale,
            1f / (TemporalResolveTuning.LockMotionEndPixels - TemporalResolveTuning.LockMotionStartPixels));
        Assert.Contains("const float LockMotionReleaseScale = 0.3333333432674407958984375;",
            TemporalLockReleaseProbe.BarrierHelperGlsl, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeProgramsCompileForEveryBackend()
    {
        foreach ((string name, string source) in TemporalLockReleaseProbe.Sources)
            ShaderValidation.ValidatePair(ShaderSources.FullscreenVert, source, "lock release probe " + name);
    }

    [Fact]
    public void CasesMeetEveryBranchAndStayOffDenormals()
    {
        int size = TemporalLockReleaseProbeGpuTests.Size;
        TemporalLockReleaseProbe.Cases cases = TemporalLockReleaseProbe.Build(size * size);
        long[] coverage = TemporalLockReleaseProbe.Coverage(cases);
        foreach (TemporalLockReleaseProbe.Branch branch in Enum.GetValues<TemporalLockReleaseProbe.Branch>())
            Assert.True(coverage[(int)branch] >= 10_000, $"{branch}: {coverage[(int)branch]} cases");
        foreach (float[] column in new[] { cases.Value, cases.Motion, cases.Reactive, cases.Edge })
        {
            int odd = Array.FindIndex(column, v => !(v == 0f || (float.IsNormal(v) && v > 0f)));
            Assert.True(odd < 0, odd < 0 ? "" : $"case {odd}: {column[odd]:R} is not zero or a positive normal");
        }
        // On the half grid, at most 1, and no half denormal (below 2^-14).
        int offGrid = Array.FindIndex(cases.Value,
            v => v != (float)(Half)v || v > 1f || (v != 0f && v < 6.103515625e-5f));
        Assert.True(offGrid < 0, offGrid < 0 ? "" : $"case {offGrid}: value {cases.Value[offGrid]:R} is off the half grid");
        // Both clamp thresholds of the motion ramp, approached from either side.
        Assert.Contains(cases.Motion, v => v == 1f);
        Assert.Contains(cases.Motion, v => v == 4f);
        Assert.Contains(cases.Motion, v => v > 4f && v < 4.0001f);
        Assert.Contains(cases.Motion, v => v < 1f && v > 0.9999f);
        Assert.True(cases.Reactive.Count(v => v == 0.5f) > 0 && cases.Edge.Count(v => v == 1f) > 0);
    }
}
