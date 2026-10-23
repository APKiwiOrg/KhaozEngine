using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using Branch = KhaozEngine.Tests.Gpu.TemporalLockReleaseProbe.Branch;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Task 7g.3 step 0, the measurement that chose the barrier form. P (the <c>precise</c> lock release frozen D shipped)
/// and B (the barrier form production ships since 7g.3a) run as separate programs on the same cases, and B's float32
/// C# twin C, production's <see cref="KhaozEngine.Render3D.Internal.TemporalResolveMath.ReleaseLock"/>, runs beside
/// them. The
/// fact prints how many cases differ in bits between each pair, with the first inputs that do, and asserts only that
/// every case was written by both programs and that B equals C on the device. P against B is reported, never
/// asserted.
/// </summary>
public sealed class TemporalLockReleaseProbeGpuTests(ITestOutputHelper output)
{
    /// <summary>Cases a side, so 4194304 cases on every backend.</summary>
    internal const int Size = 2048;

    const int Shown = 8;

    [GpuFact]
    public void BarrierReleaseMatchesItsTwinWithPreciseReported()
    {
        using var probe = new TemporalLockReleaseProbe(Size);
        output.WriteLine($"{probe.Device.Backend} on {probe.Device.Capabilities.DeviceName}, software adapter "
            + $"{probe.Device.Diagnostics.SoftwareAdapter?.ToString() ?? "unknown"}, {Size}x{Size} cases");
        TemporalLockReleaseProbe.Cases cases = TemporalLockReleaseProbe.Build(Size * Size);
        long[] coverage = TemporalLockReleaseProbe.Coverage(cases);
        output.WriteLine("  branches: " + string.Join(", ", Enum.GetValues<Branch>().Select(b => $"{b} {coverage[(int)b]}")));
        (float[] p, float[] b) = probe.Run(cases);

        long writtenP = 0, writtenB = 0, pb = 0, pc = 0, bc = 0, pDivision = 0, cDivision = 0;
        var shownPb = new List<string>();
        var shownPc = new List<string>();
        var shownBc = new List<string>();
        for (int i = 0; i < cases.Count; i++)
        {
            float c = TemporalLockReleaseProbe.Barrier(cases.Value[i], cases.Motion[i], cases.Reactive[i], cases.Edge[i]);
            float division = TemporalLockReleaseProbe.Division(cases.Value[i], cases.Motion[i], cases.Reactive[i],
                cases.Edge[i]);
            if (p[i] != -1f) writtenP++;
            if (b[i] != -1f) writtenB++;
            if (Apart(p[i], b[i])) { pb++; Show(shownPb, cases, i, p[i], b[i], c); }
            if (Apart(p[i], c)) { pc++; Show(shownPc, cases, i, p[i], b[i], c); }
            if (Apart(b[i], c)) { bc++; Show(shownBc, cases, i, p[i], b[i], c); }
            if (Apart(p[i], division)) pDivision++;
            if (Apart(c, division)) cDivision++;
        }
        output.WriteLine($"  cases {cases.Count}, written by P {writtenP}, by B {writtenB}");
        output.WriteLine($"  P vs B bit mismatches {pb}, P vs C {pc}, B vs C {bc}");
        output.WriteLine($"  reported only: P vs the correctly rounded division twin {pDivision}, C vs that twin {cDivision}");
        foreach (string line in shownPb) output.WriteLine("  P vs B " + line);
        foreach (string line in shownPc) output.WriteLine("  P vs C " + line);
        foreach (string line in shownBc) output.WriteLine("  B vs C " + line);

        Assert.Equal(cases.Count, writtenP);
        Assert.Equal(cases.Count, writtenB);
        Assert.True(bc == 0, $"{bc} barrier results differ from the float32 twin");
    }

    static bool Apart(float a, float b) => BitConverter.SingleToUInt32Bits(a) != BitConverter.SingleToUInt32Bits(b);

    static void Show(List<string> lines, TemporalLockReleaseProbe.Cases cases, int i, float p, float b, float c)
    {
        if (lines.Count >= Shown) return;
        lines.Add($"case {i}: value {Hex(cases.Value[i])}, motionPixels {Hex(cases.Motion[i])}, reactive "
            + $"{Hex(cases.Reactive[i])}, edgeMotion {Hex(cases.Edge[i])}: P {Hex(p)}, B {Hex(b)}, C {Hex(c)}");
    }

    static string Hex(float v) => $"{v:R} (0x{BitConverter.SingleToUInt32Bits(v):x8})";
}
