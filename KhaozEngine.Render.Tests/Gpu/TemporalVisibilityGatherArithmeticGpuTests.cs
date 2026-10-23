using System;
using System.Numerics;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;
using Flag = KhaozEngine.Tests.Gpu.TemporalVisibilityEquivalenceProbe.Flag;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Isolates which substitution changes the later arithmetic, independently of raw gather exactness.
/// Every enabled substitution must match D. Metal's withdrawn current-gather candidate is reported without being
/// shipped or changing the strict production equivalence and A/B assertions.</summary>
public sealed class TemporalVisibilityGatherArithmeticGpuTests(ITestOutputHelper output)
{
    [GpuTheory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void EnabledGatherSubstitutionsMatchFrozenD(bool current, bool previous, bool depth)
    {
        string decision = TemporalVisibilityFastPath.Generate(current, previous, depth);
        using var run = new TemporalVisibilityEquivalenceProbe(512, 256, source => source.Replace(
            ShaderSources.TemporalVisibilityReleasesGlsl, decision, StringComparison.Ordinal));
        var inputs = new TemporalVisibilityEquivalenceInputs(2560, 1440, 2560UL * 7919 + 1440);
        using var textures = new TemporalVisibilityEquivalenceProbe.Textures(run.Device, inputs);
        var result = run.Run(textures, new Vector2(2560, 1440), new(-0.375f, -0.055555552f),
            new(0.25f, -0.3888889f), 1);
        long decisions = 0, changes = 0, paths = 0;
        for (int i = 0; i < result.Centre.Length; i++)
        {
            ulong flags = TemporalVisibilityEquivalenceProbe.Combine(result, i);
            if ((flags & (1UL << (int)Flag.DecisionMismatch)) != 0) decisions++;
            if ((flags & (1UL << (int)Flag.ChangeMismatch)) != 0) changes++;
            if ((flags & (1UL << (int)Flag.ReportedPathMismatch)) != 0) paths++;
        }
        output.WriteLine($"{run.Device.Backend}: current {current}, previous {previous}, depth {depth}: "
            + $"{result.Centre.Length} cases, decisions {decisions}, change bits {changes}, paths {paths}");
        if (!current || TemporalVisibilityGatherPolicy.GatherCurrent(run.Device.Backend))
            Assert.True(decisions == 0 && changes == 0 && paths == 0, "Every enabled gather substitution must remain exact.");
    }
}
