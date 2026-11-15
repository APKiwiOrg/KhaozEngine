using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalStillDetailGpuTests
{
    StillRun SparseLine(TemporalUpscale preset, TemporalHistoryDetail detail = TemporalHistoryDetail.Display,
        TemporalDetailGate gate = TemporalDetailGate.Production) =>
        measurements.Get($"sparse/{gate}", preset, detail, () => RunSparseHitLine(preset, detail, gate));

    StillRun Step(TemporalUpscale preset, bool trim, TemporalHistoryDetail detail = TemporalHistoryDetail.Display) =>
        measurements.Get($"step/{trim}", preset, detail, () => RunStillStep(preset, trim, detail));

    StillRun CycleLine(TemporalUpscale preset, TemporalHistoryDetail detail = TemporalHistoryDetail.Display) =>
        measurements.Get("cycle", preset, detail, () => RunOncePerCycleLine(preset, detail));

    TrackRun Ramp(TemporalUpscale preset, bool withLine, int hitInterval = 8,
        TemporalHistoryDetail detail = TemporalHistoryDetail.Display, TemporalDetailGate gate = TemporalDetailGate.Production) =>
        measurements.Get($"ramp/{withLine}/{hitInterval}/{gate}", preset, detail,
            () => RunRampUnderLine(preset, withLine, hitInterval, detail, gate));

    TrailRun Drifting(TemporalUpscale preset, float texelsPerFrame,
        TemporalHistoryDetail detail = TemporalHistoryDetail.Display) =>
        measurements.Get($"drifting/{texelsPerFrame}", preset, detail,
            () => RunDriftingLine(preset, texelsPerFrame, detail));

    SettleRun LightStep(TemporalUpscale preset, TemporalHistoryDetail detail = TemporalHistoryDetail.Display,
        TemporalDetailGate gate = TemporalDetailGate.Production) =>
        measurements.Get($"light/{gate}", preset, detail, () => RunLightStep(preset, detail, gate));

    SettleRun LineLightStep(TemporalUpscale preset, int hitInterval,
        TemporalHistoryDetail detail = TemporalHistoryDetail.Display, TemporalDetailGate gate = TemporalDetailGate.Production) =>
        measurements.Get($"line light/{hitInterval}/{gate}", preset, detail,
            () => RunLightStepOverLine(preset, hitInterval, detail, gate));

    (float LowestConfidence, float MaxChange) Tripped(TemporalUpscale preset,
        TemporalHistoryDetail detail = TemporalHistoryDetail.Display) =>
        measurements.Get("tripped", preset, detail, () => RunTrippedFeature(preset, detail));
}
