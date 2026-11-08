using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class TileWaterIslands
{
    internal static TileWorldDocument Create()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, -1));
        for (int z = -64; z < 0; z++)
            for (int x = 0; x < 64; x++) doc.SetCornerHeightCm(x, z, 0, -400);
        for (int z = -62; z < 0; z += 4)
            for (int x = 0; x < 64; x += 4)
            {
                doc.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
                doc.SetUnderlay(x + 1, z, 0, TileWorldPhysicsTestData.Water);
                doc.SetUnderlay(x, z + 1, 0, TileWorldPhysicsTestData.Water);
                doc.SetUnderlay(x + 1, z + 1, 0, TileWorldPhysicsTestData.Water);
                doc.SetCornerHeightCm(x, z, 0, 2);
            }
        return doc;
    }
}
