using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The worlds and catalogs the tile-world physics tests build on. Every world has one region at (0, 0)
/// unless it says otherwise, four planes, a 1 m tile and grass (underlay 1) on every tile of plane 0.</summary>
public static class TileWorldPhysicsTestData
{
    /// <summary>How fast <see cref="SlopedWorld"/> rises east, in centimetres per tile.</summary>
    public const short SlopeCmPerTile = 10;

    /// <summary>One archetype per collider rule, each with the height the rule needs:
    /// <list type="bullet">
    /// <item><c>wall</c>, a 1x1 <c>Wall</c> 2.5 m tall.</item>
    /// <item><c>wall_corner</c>, a 1x1 <c>WallCorner</c> 2.5 m tall.</item>
    /// <item><c>bench</c>, a 1x2 <c>Solid</c> 0.8 m tall, so a quarter turn moves its footprint.</item>
    /// <item><c>diag_wall</c>, a 2x1 <c>Diagonal</c> 2 m tall, wider than the anchor tile it blocks.</item>
    /// <item><c>roof</c>, a <c>Solid</c> roof 0.5 m tall.</item>
    /// <item><c>deck</c>, a 3x1 with no collision and a walk surface 1.2 m up from local x -2 to 1, off centre so a
    /// turn moves it.</item>
    /// <item><c>deck_skewed</c>, the same deck with a 30 degree yaw offset.</item>
    /// <item><c>unmeasured</c>, a <c>Solid</c> with no collision height.</item>
    /// </list></summary>
    public static TileWorldCatalogs Catalogs() => TileWorldCatalogs.LoadJson(
        """
        {
          "archetypes": [
            { "id": "wall", "name": "wall", "meshRef": "test/wall.glb", "collisionKind": "Wall", "collisionHeight": 2.5 },
            { "id": "wall_corner", "name": "wall_corner", "meshRef": "test/wall_corner.glb",
              "collisionKind": "WallCorner", "collisionHeight": 2.5 },
            { "id": "bench", "name": "bench", "meshRef": "test/bench.glb", "sizeX": 1, "sizeZ": 2,
              "collisionKind": "Solid", "collisionHeight": 0.8 },
            { "id": "diag_wall", "name": "diag_wall", "meshRef": "test/diag_wall.glb", "sizeX": 2, "sizeZ": 1,
              "collisionKind": "Diagonal", "collisionHeight": 2 },
            { "id": "roof", "name": "roof", "meshRef": "test/roof.glb", "isRoof": true,
              "collisionKind": "Solid", "collisionHeight": 0.5 },
            { "id": "deck", "name": "deck", "meshRef": "test/deck.glb", "sizeX": 3, "sizeZ": 1,
              "walkSurfaces": [ { "height": 1.2, "minX": -2, "maxX": 1 } ] },
            { "id": "deck_skewed", "name": "deck_skewed", "meshRef": "test/deck.glb", "sizeX": 3, "sizeZ": 1,
              "yawOffsetDegrees": 30, "walkSurfaces": [ { "height": 1.2, "minX": -2, "maxX": 1 } ] },
            { "id": "unmeasured", "name": "unmeasured", "meshRef": "test/unmeasured.glb", "collisionKind": "Solid" }
          ]
        }
        """,
        "tileworld-physics-tests");

    /// <summary>Flat grass at height 0 over the given regions, (0, 0) when none are given, created in the order
    /// given.</summary>
    public static TileWorldDocument FlatWorld(params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "physics-test", DisplayName = "Physics test" };
        if (regions.Length == 0) regions = new[] { new RegionCoord(0, 0) };
        foreach (RegionCoord c in regions)
        {
            doc.GetOrCreateRegion(c);
            for (int z = c.OriginZ; z < c.OriginZ + TileRegion.Size; z++)
                for (int x = c.OriginX; x < c.OriginX + TileRegion.Size; x++)
                    doc.SetUnderlay(x, z, 0, 1);
        }
        return doc;
    }

    /// <summary><see cref="FlatWorld"/> with every corner of plane 0 at x times <see cref="SlopeCmPerTile"/>, so the
    /// ground rises 10 cm a tile eastward and an anchor height differs from the lowest corner under it.</summary>
    public static TileWorldDocument SlopedWorld(params RegionCoord[] regions)
    {
        TileWorldDocument doc = FlatWorld(regions);
        foreach (TileRegion region in doc.Regions.Values)
            for (int z = region.Coord.OriginZ; z < region.Coord.OriginZ + TileRegion.Size; z++)
                for (int x = region.Coord.OriginX; x < region.Coord.OriginX + TileRegion.Size; x++)
                    doc.SetCornerHeightCm(x, z, 0, (short)(x * SlopeCmPerTile));
        return doc;
    }

    /// <summary><see cref="SlopedWorld"/> with bumps of up to 80 cm on top of the slope and, in each region's first
    /// rows, an overlay of every cut shape at every rotation, so the ground mesh carries every triangle layout the
    /// triangulation writes.</summary>
    public static TileWorldDocument RoughWorld(params RegionCoord[] regions)
    {
        TileWorldDocument doc = SlopedWorld(regions);
        foreach (TileRegion region in doc.Regions.Values)
        {
            int originX = region.Coord.OriginX, originZ = region.Coord.OriginZ;
            for (int z = originZ; z < originZ + TileRegion.Size; z++)
                for (int x = originX; x < originX + TileRegion.Size; x++)
                {
                    int bump = ((x * 7 + z * 13) % 5 + 5) % 5 * 20;
                    doc.SetCornerHeightCm(x, z, 0, (short)(doc.CornerHeightCm(x, z, 0) + bump));
                }
            for (int shape = 0; shape < 4; shape++)
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    int x = originX + shape * 4 + rotation, z = originZ + shape;
                    doc.SetOverlay(x, z, 0, 2);
                    doc.SetOverlayShape(x, z, 0, (TileOverlayShape)shape);
                    doc.SetOverlayRotation(x, z, 0, rotation);
                }
        }
        return doc;
    }

    /// <summary><see cref="SlopedWorld"/> with tile (2, 2) void (underlay 0) and tile (6, 4) marked
    /// <see cref="TileSettings.Blocked"/>.</summary>
    public static TileWorldDocument VoidAndBlockedWorld()
    {
        TileWorldDocument doc = SlopedWorld();
        doc.SetUnderlay(2, 2, 0, 0);
        doc.SetSettings(6, 4, 0, TileSettings.Blocked);
        return doc;
    }

    /// <summary><see cref="SlopedWorld"/> with a <c>wall</c> at (5, 5) facing west and a <c>wall_corner</c> at
    /// (8, 5) facing north and east.</summary>
    public static TileWorldDocument WallsWorld()
    {
        TileWorldDocument doc = SlopedWorld();
        doc.AddObject("wall_corner", 8, 5, 0, 1);
        doc.AddObject("wall", 5, 5, 0, 0);
        return doc;
    }

    /// <summary><see cref="SlopedWorld"/> with one object of the given archetype placed at (x, z) on plane 0.</summary>
    public static TileWorldDocument SlopedWorldWith(string archetype, int x, int z, int rotation = 0)
    {
        TileWorldDocument doc = SlopedWorld();
        doc.AddObject(archetype, x, z, 0, rotation);
        return doc;
    }

    /// <summary><see cref="SlopedWorld"/> holding one collider of every kind: void and blocked tiles, both wall kinds,
    /// a turned bench, a diagonal, a roof, and a deck.</summary>
    public static TileWorldDocument EveryKindWorld()
    {
        TileWorldDocument doc = VoidAndBlockedWorld();
        doc.AddObject("wall", 5, 5, 0, 0);
        doc.AddObject("wall_corner", 8, 5, 0, 1);
        doc.AddObject("bench", 10, 10, 0, 1);
        doc.AddObject("diag_wall", 3, 3, 0, 0);
        doc.AddObject("roof", 12, 12, 0, 0);
        doc.AddObject("deck", 20, 20, 0, 0);
        return doc;
    }
}
