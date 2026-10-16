using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMixedRouteTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Covered_cpu_and_uncovered_gpu_draws_coexist(bool reverse)
    {
        using var h = new CoverageSceneFixture();
        h.Begin();
        var frame = h.Stage();
        var covered = h.HostDraw(frame);
        var uncovered = h.UncoveredDraw();
        if (reverse) h.Scene.DrawSkinned(uncovered, h.Palette);
        h.Scene.DrawSkinned(covered, h.Palette);
        if (!reverse) h.Scene.DrawSkinned(uncovered, h.Palette);
        h.SubmitEquipment();
        h.Outline(covered);
        h.Outline(uncovered);
        h.Render();
        Assert.Equal(1, h.Scene.CoverageDiagnosticsForTests.CoveredCpuPreparations);
        Assert.Equal(1, h.Scene.CoverageDiagnosticsForTests.UncoveredGpuDraws);
        Assert.Equal(h.Expected, h.Scene.CoverageVerticesForTests(frame).ToArray());
        h.AssertCanonicalDraws(frame);
    }
}
