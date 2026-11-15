using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalDiagonalDetailGpuTests
{
    [GpuFact]
    public void An_aged_diagonal_scene_matches_a_fresh_scene()
    {
        _detail = TemporalHistoryDetail.Fine;
        Capture(TemporalUpscale.Native, 45, 2, 0.3f);
        _gate = TemporalDetailGate.Narrower;
        Capture(TemporalUpscale.Quality, 30, 10, 0f);
        _detail = TemporalHistoryDetail.Display;
        Capture(TemporalUpscale.Native, 30, 5, 0f);
        _gate = TemporalDetailGate.Production;
        _detail = TemporalHistoryDetail.Fine;
        var aged = new List<float[]>();
        var actual = Capture(TemporalUpscale.Quality, 45, 5, 0.3f, (color, state) => { aged.Add(color); aged.Add(state); });
        using var fresh = new TemporalDiagonalScene();
        var test = new TemporalDiagonalDetailGpuTests(fresh, output) { _detail = TemporalHistoryDetail.Fine };
        var expected = new List<float[]>();
        var figures = test.Capture(TemporalUpscale.Quality, 45, 5, 0.3f,
            (color, state) => { expected.Add(color); expected.Add(state); });
        Assert.Equal(figures, actual);
        Assert.NotEmpty(expected);
        Assert.Equal(expected.Count, aged.Count);
        for (int i = 0; i < expected.Count; i++) Assert.Equal(expected[i], aged[i]);
    }
}
