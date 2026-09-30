using System.Numerics;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Pins the per-frame camera query after its model bounds and hit list have warmed.</summary>
[Collection("AllocSensitive")]
public class TileWorldCameraProbeAllocationTests
{
    [Fact]
    public void A_warmed_probe_allocates_nothing_for_wall_and_upper_plane_roof_picks()
    {
        TileWorldDocument doc = TileRenderTestData.HouseWorld();
        var resolver = new GreyboxMeshResolver(doc.TileSize, doc.PlaneHeight);
        using var view = new TileWorldView(new RecordingTileWorldScene(), doc, TileRenderTestData.Catalogs, resolver);
        view.LoadRegion(TileRenderTestData.Region);
        view.RoofMode = RoofVisibility.AlwaysVisible;
        var bounds = new TileObjectBoundsCache(resolver);
        var probe = new TileWorldCameraProbe(view, bounds.TryGetBounds, static _ => true);
        var origin = new Vector3(11.5f, 1.5f, -10.5f);
        float wallReach = 0f, roofReach = 0f;
        for (int i = 0; i < 4; i++)
        {
            wallReach = probe.Reach(origin, -Vector3.UnitX, 10f, 0.25f);
            roofReach = probe.Reach(origin, Vector3.UnitY, 10f, 0.25f);
        }
        Assert.Equal(1.1f, wallReach, 1e-3f);
        Assert.Equal(1.25f, roofReach, 1e-3f);

        AllocAssert.NoPerCallAllocation("TileWorldCameraProbe.Reach", () =>
        {
            for (int i = 0; i < 64; i++)
            {
                wallReach = probe.Reach(origin, -Vector3.UnitX, 10f, 0.25f);
                roofReach = probe.Reach(origin, Vector3.UnitY, 10f, 0.25f);
            }
        });

        Assert.Equal(1.1f, wallReach, 1e-3f);
        Assert.Equal(1.25f, roofReach, 1e-3f);
    }
}
