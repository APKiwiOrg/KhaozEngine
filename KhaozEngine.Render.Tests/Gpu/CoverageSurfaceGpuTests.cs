using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageSurfaceGpuTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuTheory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public void Accepted_surface_has_no_breakthrough_or_new_hole(int view, bool gpuSkinning)
    {
        var row = new CoverageCase(view);
        var baseline = scene.Capture(row, false, gpuSkinning);
        var conformed = scene.Capture(row, true, gpuSkinning);
        var oracle = scene.CaptureReference(row, gpuSkinning);
        CoveragePixelProof.AssertSurface(baseline, conformed, oracle, requireBreakthrough: view == 0);
    }

    [GpuTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Captured_camera_contract_uses_the_final_surface(int cameraMode)
    {
        var row = new CoverageCase(4) { CameraMode = cameraMode };
        var baseline = scene.Capture(row, false, true);
        var actual = scene.Capture(row, true, true);
        var expected = scene.CaptureReference(row, true);
        CoveragePixelProof.AssertSurface(baseline, actual, expected, requireBreakthrough: false);
    }

    [GpuFact]
    public void Original_skin_control_fails_the_temporal_interior_assertion()
    {
        var row = new CoverageCase(0);
        var original = scene.Capture(row, false, true);
        var current = scene.Capture(row, true, true);
        Assert.Throws<Xunit.Sdk.TrueException>(() => CoveragePixelProof.AssertNoGhostColour(original, current));
    }

    [GpuFact]
    public void Removal_only_control_opens_a_visible_grazing_hole()
    {
        var row = new CoverageCase(2);
        var conformed = scene.Capture(row, true, false);
        var removed = scene.CaptureRemoval(row);
        Assert.True(CoveragePixelProof.NewHoles(removed, conformed) > 0);
    }

    [GpuFact]
    public void Aged_shared_scene_matches_a_fresh_scene()
    {
        scene.Capture(new CoverageCase(5), true, true);
        scene.Capture(new CoverageCase(1), false, false);
        var row = new CoverageCase(0);
        var aged = scene.Capture(row, true, true);
        using var fresh = new CoverageGpuScene();
        CoveragePixelProof.AssertTargetsEqual(aged, fresh.Capture(row, true, true));
    }
}
