using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalStabilityGpuTests
{
    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void The_history_modes_report_their_stability_values_and_existing_bounds()
    {
        var display = runs.For(TemporalHistoryDetail.Display);
        var fine = runs.For(TemporalHistoryDetail.Fine);
        foreach (var preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
        {
            Pan("fence", display.FencePan(TemporalStabilityRuns.SlowPan), fine.FencePan(TemporalStabilityRuns.SlowPan));
            Pan("wall", display.WallPan(TemporalStabilityRuns.SlowPan), fine.WallPan(TemporalStabilityRuns.SlowPan));
            var d = display.Zoom;
            var f = fine.Zoom;
            Cell("zoom/lag", d.Temporal[preset].LowPassedError / d.Lagged.LowPassedError,
                f.Temporal[preset].LowPassedError / f.Lagged.LowPassedError, MaxLowPassedShareOfLag);
            double offBound = (double)Rows(nameof(TheDefaultIsometricCameraZoomingDoesNotSwim))
                .Single(row => (TemporalUpscale)row[0] == preset)[1];
            Cell("zoom/off", d.Temporal[preset].LowPassedError / d.Off.LowPassedError,
                f.Temporal[preset].LowPassedError / f.Off.LowPassedError, offBound);
            foreach (object[] row in Rows(nameof(ThinGeometryKeepsItsSharpness)).Where(row => (TemporalUpscale)row[0] == preset))
            {
                bool pan = (bool)row[1];
                Cell($"sharpness/{pan}", pan ? display.FencePan(TemporalStabilityRuns.SlowPan).Temporal[preset].Sharpness : display.Still[preset].Sharpness,
                    pan ? fine.FencePan(TemporalStabilityRuns.SlowPan).Temporal[preset].Sharpness : fine.Still[preset].Sharpness, (double)row[2]);
            }

            void Pan(string name, StabilityPath d, StabilityPath f)
            {
                Cell(name + "/added", d.Temporal[preset].AddedChange / d.Msaa.AddedChange,
                    f.Temporal[preset].AddedChange / f.Msaa.AddedChange, MaxAddedShareOfMsaa);
                Cell(name + "/shimmer", d.Temporal[preset].TemporalError / d.Frozen,
                    f.Temporal[preset].TemporalError / f.Frozen, MaxErrorShareOfFrozen);
            }
            void Cell(string metric, double d, double f, double bound) => output.WriteLine(
                $"ACCEPT|stability|{preset}|{metric}|Display={d:R}|Fine={f:R}|bound={bound:R}");
        }
        foreach (bool crossfade in new[] { false, true })
        {
            var d = display.PropPan(crossfade);
            var f = fine.PropPan(crossfade);
            output.WriteLine($"ACCEPT|stability|Native|prop/{crossfade}|Display={d.Temporal[TemporalUpscale.Native].AddedChange / d.Off.AddedChange:R}"
                + $"|Fine={f.Temporal[TemporalUpscale.Native].AddedChange / f.Off.AddedChange:R}|bound={MaxPropAddedShareOfOff:R}");
        }
        var hd = display.Held;
        var hf = fine.Held;
        output.WriteLine($"ACCEPT|stability|Native|held coverage|Display={Math.Abs(hd.Coverage - hd.ReferenceCoverage):R}"
            + $"|Fine={Math.Abs(hf.Coverage - hf.ReferenceCoverage):R}|bound={MaxHeldCoverageError:R}");
        output.WriteLine($"ACCEPT|stability|Native|held flips|Display={hd.Flips:R}|Fine={hf.Flips:R}|bound={MaxHeldFlipRate:R}");
    }

    static IEnumerable<object[]> Rows(string name)
    {
        var method = typeof(TemporalStabilityGpuTests).GetMethod(name)
            ?? throw new InvalidOperationException("The stability report requires its public image-contract method.");
        return method.GetCustomAttributes<TemporalHistoryModeDataAttribute>().SelectMany(data => data.GetData(method))
            .Where(row => (TemporalHistoryDetail)row[^1] == TemporalHistoryDetail.Display);
    }
}
