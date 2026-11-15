using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalStillDetailGpuTests
{
    [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_the_recorded_still_detail_controls_without_a_new_bound(TemporalUpscale preset)
    {
        foreach (var gate in TemporalDetailGateVariants.ReportGates)
        {
            var displaySparse = SparseLine(preset);
            var fineSparse = SparseLine(preset, TemporalHistoryDetail.Fine, gate);
            Print("sparse line change", displaySparse.MaxChange, fineSparse.MaxChange,
                preset == TemporalUpscale.Native ? MainSparseChange(preset) + 0.02f : 0.1f);
            Print("sparse ramp error", Ramp(preset, true, SparseHitInterval).MeanError,
                Ramp(preset, true, SparseHitInterval, TemporalHistoryDetail.Fine, gate).MeanError,
                MainSparseCreepingError(preset) + 0.02f);
            Print("shimmering line ramp error", Ramp(preset, true).MeanError,
                Ramp(preset, true, detail: TemporalHistoryDetail.Fine, gate: gate).MeanError,
                MainCreepingError(preset) + 0.02f);
            Print("light step recovery", LightStep(preset).Frames,
                LightStep(preset, TemporalHistoryDetail.Fine, gate).Frames, MainSettleFrames(preset));
            Print("light step under line recovery", LineLightStep(preset, 8).Frames,
                LineLightStep(preset, 8, TemporalHistoryDetail.Fine, gate).Frames, MainLightStepOverLineFrames(preset));

            void Print(string cell, double display, double fine, double bound) => output.WriteLine(
                $"REPORT|still detail|{gate}|{preset}|{cell}|Display={display:R}|Fine={fine:R}|bound={bound:R}");
        }
    }
}
