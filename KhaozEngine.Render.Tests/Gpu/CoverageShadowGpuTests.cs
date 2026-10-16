using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageShadowGpuTests(CoverageGpuScene scene) : IClassFixture<CoverageGpuScene>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Camera_culled_hosts_retain_all_shadow_face_surfaces(bool gpuSkinning)
    {
        var row = new CoverageCase(0) { Shadows = true, StaticLight = true, CameraCulled = true };
        CoveragePixelProof.AssertShadows(scene.Capture(row, true, gpuSkinning),
            scene.CaptureReference(row, gpuSkinning), staticLight: true);
    }

    [GpuFact]
    public void Shared_light_mode_changes_match_fresh_selected_rows()
    {
        scene.Capture(new CoverageCase(0) { Shadows = true, StaticLight = true }, true, true);
        var row = new CoverageCase(0) { Shadows = true };
        var actual = scene.Capture(row, true, false);
        using var fresh = new CoverageGpuScene();
        CoveragePixelProof.AssertShadows(actual, fresh.Capture(row, true, false), staticLight: false);
    }

    [GpuFact]
    public void Distant_point_projection_refuses_without_spending_the_clearance_margin()
    {
        scene.Capture(new CoverageCase(0)
        { Shadows = true, PointRadius = 30, ExpectRefusal = true }, true, true);
    }

    [GpuTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Every_shadow_face_uses_the_final_surface(bool staticLight, bool gpuSkinning)
    {
        var row = new CoverageCase(0) { Shadows = true, StaticLight = staticLight, ShadowOnly = gpuSkinning };
        var actual = scene.Capture(row, true, gpuSkinning);
        var expected = scene.CaptureReference(row, gpuSkinning);
        CoveragePixelProof.AssertShadows(actual, expected, staticLight);
    }
}
