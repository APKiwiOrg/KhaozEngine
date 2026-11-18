using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalGameShadowGrainGpuTests
{
    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_textured_shadow_grain_against_the_recorded_controls()
    {
        var display = new Dictionary<TemporalUpscale, (double Value, double? Bound)>();
        TexturedShadowGrainCase.AssertGrain(scene, output, reportOnly: true,
            record: (preset, value, bound) => display[preset] = (value, bound));
        foreach (var gate in TemporalDetailGateVariants.ReportGates)
        {
            var fine = new Dictionary<TemporalUpscale, double>();
            TexturedShadowGrainCase.AssertGrain(scene.For(TemporalHistoryDetail.Fine, gate), output,
                reportOnly: true, record: (preset, value, _) => fine[preset] = value);
            foreach (var (preset, d) in display)
                output.WriteLine($"REPORT|textured shadow grain|{gate}|{preset}|Display={d.Value:R}|Fine={fine[preset]:R}|bound={d.Bound:R}");
        }
    }
}
