using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.Locomotion.Contacts;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

// #1755. Report-only paired timing of shared parent projections on the advancing moving-shore histories.
// It lives beside its internal bypass because the shipped physics assembly grants the benchmark project no
// friend access. Nothing here asserts time. Run with DOTNET_TieredCompilation=0 and Category=Diagnostic.
[Collection("AllocSensitive")]
public sealed class WaterSharedProjectionPairedDiagnosticTests(ITestOutputHelper output)
{
    const int Warmup = 8;

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportPairedSharedProjectionSteps()
    {
#if WATER_PRISM_CENSUS
        // The census build counts work inside the kernel being timed, so it is refused before any scene or clock.
        output.WriteLine("Paired shared projection timing refuses the census build, which instruments the kernel. Nothing measured.");
#else
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0")
        {
            output.WriteLine("Paired shared projection timing requires DOTNET_TieredCompilation=0. Nothing measured.");
            return;
        }
        using var restore = new SharedProjectionBypass(false);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            mode = "shared-projections",
            sides = "0,1 original path; 2,3 shared",
            warmup = Warmup,
            tieredCompilation = false,
            utc = DateTimeOffset.UtcNow,
            framework = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString()
        }));
        foreach (string name in new[] { "bank-along", "shore-traverse" }) Measure(name);
#endif
    }

    void Measure(string name)
    {
        // Four separate captures, so neither path warms the other's geometry terms, memos or preparations.
        var scenes = Enumerable.Range(0, 4).Select(_ => new WaterPrismScenario(name)).ToArray();
        try
        {
            var command = new MoveCommand(name == "bank-along" ? Vector2.UnitX : Vector2.UnitY, false, 0);
            for (int side = 0; side < 4; side++)
            {
                TileWaterSharedProjections.BypassForTests = side < 2;
                for (int i = 0; i < Warmup; i++) _ = scenes[side].Step(scenes[side].Basis, command);
            }
            var samples = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray();
            long sharedCopies = 0, originalCopies = 0;
            var state = scenes[0].Basis;
            bool outward = false;
            int limit = name == "bank-along" ? 32 : 400;
            for (int tick = 0; tick < limit; tick++)
            {
                if (name == "shore-traverse" && state.Move.Position.Z < -244) outward = true;
                if (outward) command = new(-Vector2.UnitY, false, 0);
                var next = new PlayerMoveState[4];
                var us = new double[4];
                // Alternate the order so neither path always runs first in a tick.
                int[] order = (tick & 1) == 0 ? [0, 2, 1, 3] : [3, 1, 2, 0];
                foreach (int side in order)
                {
                    TileWaterSharedProjections.BypassForTests = side < 2;
                    var before = TileWaterPrismCoverage.CaptureSharedProjectionsForTests();
                    long begin = Stopwatch.GetTimestamp();
                    next[side] = scenes[side].Step(state, command);
                    us[side] = Stopwatch.GetElapsedTime(begin).TotalMicroseconds;
                    // The snapshot describes the thread's last prism trace. A step served by its sampler memo
                    // leaves another side's snapshot in place, so only a changed snapshot belongs to this side.
                    var after = TileWaterPrismCoverage.CaptureSharedProjectionsForTests();
                    if (after == before) continue;
                    if (side < 2) originalCopies += after.Copies;
                    else sharedCopies += after.Copies;
                }
                // Separate simulators issue their own continuity handles. Every other state bit must agree.
                for (int side = 1; side < 4; side++)
                    LocomotionStateBits.AssertEqual(next[0], next[side], LocomotionStateBits.Policy.ExcludeContinuityHandle);
                for (int side = 0; side < 4; side++) samples[side].Add(us[side]);
                output.WriteLine(JsonSerializer.Serialize(new { name, tick, outward, swimming = state.Move.Swimming, stepUs = us }));
                state = next[0];
                if (outward && state.Move.Position.Z >= -235.8f) break;
            }
            Assert.Equal(0, originalCopies);
            Assert.True(sharedCopies > 0);
            output.WriteLine(JsonSerializer.Serialize(Summary(name, samples, sharedCopies)));
        }
        finally
        {
            foreach (var scene in scenes) scene.Dispose();
        }
    }

    static object Summary(string name, List<double>[] samples, long sharedCopies)
    {
        double[] sums = samples.Select(s => s.Sum()).ToArray();
        double[] medians = samples.Select(s => Percentile(s, 0.5)).ToArray();
        // The repeat floor is the larger relative difference between the two identical replicates of a path.
        double meanSaving = 1 - (sums[2] + sums[3]) / (sums[0] + sums[1]);
        double meanFloor = Math.Max(Math.Abs(sums[1] / sums[0] - 1), Math.Abs(sums[3] / sums[2] - 1));
        double medianSaving = 1 - (medians[2] + medians[3]) / (medians[0] + medians[1]);
        double medianFloor = Math.Max(Math.Abs(medians[1] / medians[0] - 1), Math.Abs(medians[3] / medians[2] - 1));
        return new
        {
            summary = name,
            ticks = samples[0].Count,
            sharedCopies,
            meanUs = samples.Select(s => s.Average()).ToArray(),
            medianUs = medians,
            p10Us = samples.Select(s => Percentile(s, 0.1)).ToArray(),
            p90Us = samples.Select(s => Percentile(s, 0.9)).ToArray(),
            meanSavingPercent = meanSaving * 100,
            meanRepeatFloorPercent = meanFloor * 100,
            meanResolved = Math.Abs(meanSaving) > meanFloor,
            medianSavingPercent = medianSaving * 100,
            medianRepeatFloorPercent = medianFloor * 100,
            medianResolved = Math.Abs(medianSaving) > medianFloor
        };
    }

    static double Percentile(List<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        double position = (sorted.Length - 1) * fraction;
        int low = (int)Math.Floor(position), high = (int)Math.Ceiling(position);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }
}
