using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class PreparePhaseAtmosphereGpuTests(PreparePhaseSplitFixture fixture)
    : IClassFixture<PreparePhaseSplitFixture>
{
    const int Frames = 8;

    [GpuTheory]
    [InlineData(BillboardBlend.Alpha)]
    [InlineData(BillboardBlend.Additive)]
    public void Atmosphere_billboards_match_across_changing_views_and_recording_wraps(BillboardBlend blend)
    {
        byte[][] legacy = fixture.Render(false, false, true, true, true, atmosphereBillboards: blend);
        byte[][] split = fixture.Render(true, false, true, true, true, atmosphereBillboards: blend);
        for (int i = 0; i < Frames; i++) Assert.Equal(legacy[i], split[i]);
    }

    [GpuFact]
    public void The_aged_atmosphere_scene_matches_a_fresh_scene()
    {
        fixture.Render(false, false, true, true, true, atmosphereBillboards: BillboardBlend.Alpha);
        byte[][] aged = fixture.Render(true, false, true, true, true, atmosphereBillboards: BillboardBlend.Additive);
        using var fresh = new PreparePhaseSplitFixture();
        byte[][] isolated = fresh.Render(true, false, true, true, true, atmosphereBillboards: BillboardBlend.Additive);
        for (int i = 0; i < Frames; i++) Assert.Equal(isolated[i], aged[i]);
    }
}
