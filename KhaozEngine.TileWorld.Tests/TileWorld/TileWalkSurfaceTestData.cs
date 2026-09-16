using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>A river crossing shaped like the one that asked for walk surfaces: a 3x3 bridge whose mesh is centred on
/// its footprint with its base at y 0, anchored on a bed carved 80 cm down, with a deck 0.825 m above the anchor that
/// reaches one tile past the footprint west and east onto the sloping banks.</summary>
public static class TileWalkSurfaceTestData
{
    /// <summary>The bridge's SW anchor tile.</summary>
    public const int BridgeX = 20, BridgeZ = 20;
    /// <summary>The bed the bridge is anchored on, in centimetres and metres.</summary>
    public const short BedCm = -80;
    /// <summary>The deck's height above the anchor.</summary>
    public const float DeckHeight = 0.825f;
    /// <summary>World x and z of the bridge's anchor, the centre of its 3x3 footprint.</summary>
    public const float CentreX = 21.5f, CentreZ = -21.5f;

    /// <summary>The deck's top in world metres, computed the way the query computes it.</summary>
    public static float DeckTop => BedCm * 0.01f + DeckHeight;

    /// <summary>Greybox plus the walk-surface archetypes: <c>bridge</c> (the crossing above), <c>platform</c> (a 1x1
    /// top 1.5 m up), <c>stepped</c> (a 3x1 with a full top 1 m up and a middle strip 1.2 m up), and <c>cellar</c> (a 1x1
    /// top sunk half a metre below its anchor).</summary>
    public static TileWorldCatalogs Catalogs() => TileWorldCatalogs.Merge(
        TileWorldCatalogs.Greybox(),
        TileWorldCatalogs.LoadJson(
            """
            {
              "archetypes": [
                { "id": "bridge", "name": "Bridge", "meshRef": "test/bridge.glb", "sizeX": 3, "sizeZ": 3,
                  "walkSurfaces": [ { "height": 0.825, "minX": -2.5, "maxX": 2.5, "minZ": -1.5, "maxZ": 1.5 } ] },
                { "id": "platform", "name": "Platform", "meshRef": "test/platform.glb",
                  "walkSurfaces": [ { "height": 1.5 } ] },
                { "id": "stepped", "name": "Stepped", "meshRef": "test/stepped.glb", "sizeX": 3,
                  "walkSurfaces": [ { "height": 1.0 }, { "height": 1.2, "minX": -0.5, "maxX": 0.5 } ] },
                { "id": "cellar", "name": "Cellar", "meshRef": "test/cellar.glb",
                  "walkSurfaces": [ { "height": -0.5 } ] }
              ]
            }
            """,
            "walk-surface-tests"));

    /// <summary>Flat grass with the bed carved under the bridge's footprint: every corner from x 20 to 23 and z 20 to
    /// 23 sits at <see cref="BedCm"/>, so the footprint is flat at the bed and the tiles west and east of it slope from
    /// 0 up on the bank to the bed.</summary>
    public static TileWorldDocument CarvedWorld(params RegionCoord[] regions)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, regions);
        for (int z = BridgeZ; z <= BridgeZ + 3; z++)
            for (int x = BridgeX; x <= BridgeX + 3; x++)
                doc.SetCornerHeightCm(x, z, 0, BedCm);
        return doc;
    }

    /// <summary><see cref="CarvedWorld"/> with the bridge placed at its anchor.</summary>
    public static TileWorldDocument BridgeWorld(int rotation = 0, string archetype = "bridge")
    {
        TileWorldDocument doc = CarvedWorld();
        doc.AddObject(archetype, BridgeX, BridgeZ, 0, rotation);
        return doc;
    }
}
