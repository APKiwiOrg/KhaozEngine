using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageOutlineGpuTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_outline_masks_use_the_final_surface(bool gpuSkinning)
    {
        var row = new CoverageCase(2) { Outlines = true };
        var actual = scene.Capture(row, true, gpuSkinning);
        var expected = scene.CaptureReference(row, gpuSkinning);
        CoveragePixelProof.AssertOutlines(actual, expected);
    }
}
