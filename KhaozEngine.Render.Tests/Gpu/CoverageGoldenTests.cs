using Xunit;

namespace KhaozEngine.Tests.Gpu;

// Invariant proof runs before the backend-owned reference check.
public sealed class CoverageGoldenTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuTheory]
    [InlineData(0, "coverage-front")]
    [InlineData(1, "coverage-rear")]
    [InlineData(2, "coverage-grazing-left")]
    [InlineData(3, "coverage-grazing-right")]
    [InlineData(4, "coverage-high")]
    [InlineData(5, "coverage-low-backlit")]
    public void Opaque_appearance_packet_has_native_protected_skin_and_a_cleared_core(int view, string goldenName)
    {
        var row = new CoverageCase(view);
        var baseline = scene.Capture(row, false, true);
        var accepted = scene.Capture(row, true, true);
        var reference = scene.CaptureReference(row, true);
        CoveragePixelProof.AssertSurface(baseline, accepted, reference, requireBreakthrough: view == 0);
        GoldenCompare.AssertOrUpdate(goldenName, accepted.Colour, CoverageGpuScene.Width, CoverageGpuScene.Height);
    }
}
