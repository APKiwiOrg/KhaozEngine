using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalTextureLightGpuTests
{
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    readonly Dictionary<(string, TemporalUpscale), (double Error, double Grain, double Change)> _reported = new();
    GameShadowGrainScene ActiveScene => _detail == TemporalHistoryDetail.Display ? scene : scene.For(_detail, _gate);

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_local_light_detail_against_the_recorded_controls()
    {
        var d = new TemporalTextureLightGpuTests(scene, output) { _reportOnly = true };
        d.Dark_texture_and_animated_local_light_keep_detail_stable();
        foreach (var gate in new[] { TemporalDetailGate.Production, TemporalDetailGate.Narrower, TemporalDetailGate.Wider })
        {
            var f = new TemporalTextureLightGpuTests(scene, output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Dark_texture_and_animated_local_light_keep_detail_stable();
            foreach (var (cell, display) in d._reported)
            {
                var fine = f._reported[cell];
                var before = Before[cell];
                output.WriteLine($"REPORT|texture light|{gate}|{cell.Item1}/{cell.Item2}/error|Display={display.Error:R}|Fine={fine.Error:R}|bound={1.1 * before.Error:R}");
                output.WriteLine($"REPORT|texture light|{gate}|{cell.Item1}/{cell.Item2}/grain|Display={display.Grain:R}|Fine={fine.Grain:R}|bound={1.1 * before.Grain:R}");
                output.WriteLine($"REPORT|texture light|{gate}|{cell.Item1}/{cell.Item2}/change|Display={display.Change:R}|Fine={fine.Change:R}|bound={1.1 * before.Change:R}");
            }
        }
    }
}
