using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalShadowEdgeLagGpuTests
{
    [GpuFact]
    public void An_aged_shadow_edge_scene_matches_a_fresh_scene()
    {
        byte[][] Run(TemporalShadowLagScene source, TemporalUpscale preset, float speed, bool reference = false)
        {
            var stage = new ShadowEdgeStage(W, H, speed, FirstMeasured);
            return source.Capture(s => stage.Setup(s, AntiAliasing.Temporal, preset, 1f), stage.Draw,
                FirstMeasured, Measured, preset, TemporalHistoryDetail.Fine, reference: reference);
        }
        Run(scene, TemporalUpscale.Native, -1f);
        Run(scene, TemporalUpscale.Quality, 0.25f, reference: true);
        Run(scene, TemporalUpscale.Native, 0f);
        var aged = Run(scene, TemporalUpscale.Quality, 1f);
        using var fresh = new TemporalShadowLagScene();
        var expected = Run(fresh, TemporalUpscale.Quality, 1f);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Length, aged.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], aged[i]);
    }
}

public sealed partial class TemporalMovingShadeLagGpuTests
{
    [GpuFact]
    public void An_aged_moving_shade_scene_matches_a_fresh_scene()
    {
        byte[][] Run(TemporalShadowLagScene source, TemporalUpscale preset, MovingShadeKind kind, float speed,
            bool reference = false, bool bilinear = false, TemporalDetailGate gate = TemporalDetailGate.Production)
        {
            var stage = new MovingShadeStage(W, H, kind, 1f, speed, FirstMeasured);
            return source.Capture(s => stage.Setup(s, AntiAliasing.Temporal, preset, 1f), stage.Draw,
                FirstMeasured, Measured, preset, TemporalHistoryDetail.Fine, gate, reference: reference, bilinear: bilinear);
        }
        Run(scene, TemporalUpscale.Native, MovingShadeKind.Shadow, 0f);
        Run(scene, TemporalUpscale.Quality, MovingShadeKind.Pan, 4f, reference: true);
        Run(scene, TemporalUpscale.Native, MovingShadeKind.Shadow, 0f, bilinear: true);
        Run(scene, TemporalUpscale.Native, MovingShadeKind.Shadow, 0f, gate: TemporalDetailGate.Wider);
        var aged = Run(scene, TemporalUpscale.Quality, MovingShadeKind.Object, 2f);
        using var fresh = new TemporalShadowLagScene();
        var expected = Run(fresh, TemporalUpscale.Quality, MovingShadeKind.Object, 2f);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Length, aged.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], aged[i]);
    }
}
