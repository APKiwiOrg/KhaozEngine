using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>A river crossing shared by surface picking and camera boom tests.</summary>
internal static class TileBridgeTestData
{
    public const int BridgeZ = 40;

    /// <summary>A 3x3 bridge whose deck reaches one tile beyond its footprint onto each bank.</summary>
    public static TileWorldCatalogs Catalogs => TileWorldCatalogs.Merge(
        TileRenderTestData.Catalogs,
        TileWorldCatalogs.LoadJson(
            """
            {
              "archetypes": [
                { "id": "bridge", "name": "Bridge", "meshRef": "test/bridge.glb", "sizeX": 3, "sizeZ": 3,
                  "walkSurfaces": [ { "height": 0.825, "minX": -2.5, "maxX": 2.5, "minZ": -1.5, "maxZ": 1.5 } ] }
              ]
            }
            """,
            "bridge-test-walk-surface"));

    /// <summary>The crossing anchored on a bed carved 80 cm down, with its deck at y = 0.025 m.</summary>
    public static TileWorldDocument World()
    {
        TileWorldDocument doc = TileRenderTestData.RiverWorld();
        for (int z = BridgeZ; z <= BridgeZ + 3; z++)
            for (int x = TileRenderTestData.RiverMinX + 1; x <= TileRenderTestData.RiverMaxX; x++)
                doc.SetCornerHeightCm(x, z, 0, -80);
        doc.AddObject("bridge", TileRenderTestData.RiverMinX, BridgeZ, 0, 0);
        return doc;
    }
}
