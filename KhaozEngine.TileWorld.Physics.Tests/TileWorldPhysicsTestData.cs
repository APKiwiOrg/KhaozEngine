using System;
using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The worlds and catalogs the tile-world physics tests build on. Every world has one region at (0, 0)
/// unless it says otherwise, four planes, a 1 m tile and grass (underlay 1) on every tile of plane 0.</summary>
public static class TileWorldPhysicsTestData
{
    /// <summary>How fast <see cref="SlopedWorld"/> rises east, in centimetres per tile.</summary>
    public const short SlopeCmPerTile = 10;

    /// <summary>The water material's id in <see cref="Catalogs"/>.</summary>
    public const ushort Water = 3;

    /// <summary>The first and last rows of <see cref="BridgedRiverWorld"/>'s river.</summary>
    public const int RiverFirstRow = 10, RiverLastRow = 12;

    /// <summary>Where <see cref="BridgedRiverWorld"/>'s deck stands.</summary>
    public const int DeckX = 20, DeckZ = 10;

    /// <summary>How fast <see cref="RampWorld"/> rises east, in centimetres per tile.</summary>
    public const short RampCmPerTile = 40;

    /// <summary>The row whose south edge <see cref="DoorwayWorld"/>'s walls stand on, and the one tile x along it left
    /// open as the door.</summary>
    public const int DoorRow = 20, DoorX = 20;

    /// <summary>The first and last tile x of <see cref="DoorwayWorld"/>'s wall run, the door included.</summary>
    public const int WallRunFirstX = 10, WallRunLastX = 30;

    /// <summary>Where <see cref="HighDeckWorld"/>'s <c>high_deck</c> is anchored.</summary>
    public const int HighDeckX = 20, HighDeckZ = 20;

    /// <summary>The height of the <c>high_deck</c> walk surface above the ground it stands on.</summary>
    public const float HighDeckHeight = 2.5f;

    /// <summary>The blocked cliff column of <see cref="CliffWorld"/>.</summary>
    public const int CliffX = 10;

    /// <summary>The height of <see cref="CliffWorld"/>'s plateau in centimetres, taller than the default blocked
    /// height.</summary>
    public const short PlateauCm = 400;

    /// <summary>One archetype per collider rule, each with the height the rule needs:
    /// <list type="bullet">
    /// <item><c>wall</c>, a 1x1 <c>Wall</c> 2.5 m tall.</item>
    /// <item><c>wall_corner</c>, a 1x1 <c>WallCorner</c> 2.5 m tall.</item>
    /// <item><c>bench</c>, a 1x2 <c>Solid</c> 0.8 m tall, so a quarter turn moves its footprint.</item>
    /// <item><c>diag_wall</c>, a 2x1 <c>Diagonal</c> 2 m tall, wider than the anchor tile it blocks.</item>
    /// <item><c>diag_long</c>, a 3x1 <c>Diagonal</c> 2 m tall, whose anchor stands a full tile past its anchor
    /// tile.</item>
    /// <item><c>roof</c>, a <c>Solid</c> roof 0.5 m tall.</item>
    /// <item><c>deck</c>, a 3x1 with no collision and a walk surface 1.2 m up from local x -2 to 1, off centre so a
    /// turn moves it.</item>
    /// <item><c>deck_skewed</c>, the same deck with a 30 degree yaw offset.</item>
    /// <item><c>high_deck</c>, a 3x3 with no collision and a walk surface <see cref="HighDeckHeight"/> up over its
    /// whole footprint, high enough for a standing body to pass under.</item>
    /// <item><c>unmeasured</c>, a <c>Solid</c> with no collision height.</item>
    /// </list>
    /// And three ground materials: grass (1) and dirt (2), both ground, and water (<see cref="Water"/>).</summary>
    public static TileWorldCatalogs Catalogs() => TileWorldCatalogs.LoadJson(
        """
        {
          "materials": [
            { "id": 1, "name": "grass", "color": "#4d8a3a", "kind": "Ground" },
            { "id": 2, "name": "dirt", "color": "#7a5a3a", "kind": "Ground" },
            { "id": 3, "name": "water", "color": "#2a5a9a", "kind": "Water" }
          ],
          "archetypes": [
            { "id": "wall", "name": "wall", "meshRef": "test/wall.glb", "collisionKind": "Wall", "collisionHeight": 2.5 },
            { "id": "wall_corner", "name": "wall_corner", "meshRef": "test/wall_corner.glb",
              "collisionKind": "WallCorner", "collisionHeight": 2.5 },
            { "id": "bench", "name": "bench", "meshRef": "test/bench.glb", "sizeX": 1, "sizeZ": 2,
              "collisionKind": "Solid", "collisionHeight": 0.8 },
            { "id": "diag_wall", "name": "diag_wall", "meshRef": "test/diag_wall.glb", "sizeX": 2, "sizeZ": 1,
              "collisionKind": "Diagonal", "collisionHeight": 2 },
            { "id": "diag_long", "name": "diag_long", "meshRef": "test/diag_long.glb", "sizeX": 3, "sizeZ": 1,
              "collisionKind": "Diagonal", "collisionHeight": 2 },
            { "id": "roof", "name": "roof", "meshRef": "test/roof.glb", "isRoof": true,
              "collisionKind": "Solid", "collisionHeight": 0.5 },
            { "id": "deck", "name": "deck", "meshRef": "test/deck.glb", "sizeX": 3, "sizeZ": 1,
              "walkSurfaces": [ { "height": 1.2, "minX": -2, "maxX": 1 } ] },
            { "id": "deck_skewed", "name": "deck_skewed", "meshRef": "test/deck.glb", "sizeX": 3, "sizeZ": 1,
              "yawOffsetDegrees": 30, "walkSurfaces": [ { "height": 1.2, "minX": -2, "maxX": 1 } ] },
            { "id": "high_deck", "name": "high_deck", "meshRef": "test/high_deck.glb", "sizeX": 3, "sizeZ": 3,
              "walkSurfaces": [ { "height": 2.5 } ] },
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

    /// <summary><see cref="SlopedWorld"/> with a <c>wall</c> at every rotation along row 5 and a <c>wall_corner</c>
    /// at every rotation along row 10, at x 5, 8, 11 and 14 for rotations 0 to 3. Three tiles apart, so no two share
    /// an edge. The corners are placed first.</summary>
    public static TileWorldDocument WallsWorld()
    {
        TileWorldDocument doc = SlopedWorld();
        for (int rotation = 0; rotation < 4; rotation++) doc.AddObject("wall_corner", 5 + 3 * rotation, 10, 0, rotation);
        for (int rotation = 0; rotation < 4; rotation++) doc.AddObject("wall", 5 + 3 * rotation, 5, 0, rotation);
        return doc;
    }

    /// <summary><see cref="FlatWorld"/> falling 6 m a tile eastward (every corner at x times -600 cm, level past
    /// x 40), steeper than twice any collision height here, with a west-facing <c>wall</c> at (5, 5) and a
    /// <c>diag_long</c> at (5, 10). Both stand lower than the ground their boxes sit on.</summary>
    public static TileWorldDocument SteepWorld()
    {
        TileWorldDocument doc = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(-600 * Math.Min(x, 40)));
        doc.AddObject("wall", 5, 5, 0, 0);
        doc.AddObject("diag_long", 5, 10, 0, 0);
        return doc;
    }

    /// <summary><see cref="FlatWorld"/> with a plateau <see cref="PlateauCm"/> high on every corner up to x
    /// <see cref="CliffX"/>, so every tile of column <see cref="CliffX"/> falls from the plateau to the grass at 0 in one
    /// tile, and that whole column marked <see cref="TileSettings.Blocked"/>.</summary>
    public static TileWorldDocument CliffWorld()
    {
        TileWorldDocument doc = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
        {
            for (int x = 0; x <= CliffX; x++) doc.SetCornerHeightCm(x, z, 0, PlateauCm);
            doc.SetSettings(CliffX, z, 0, TileSettings.Blocked);
        }
        return doc;
    }

    /// <summary><see cref="SlopedWorld"/> over regions (0, 0), (1, 0), (0, -1) and (-1, 1), created in the order
    /// given, with the same content whatever the order: a wall in (0, 0), a bench in (1, 0), a deck in (0, -1) and a
    /// blocked tile in (-1, 1), placed in that order.</summary>
    public static TileWorldDocument MultiRegionWorld(params RegionCoord[] order)
    {
        TileWorldDocument doc = SlopedWorld(order);
        doc.AddObject("wall", 5, 5, 0, 0);
        doc.AddObject("bench", 70, 3, 0, 1);
        doc.AddObject("deck", 10, -20, 0, 0);
        doc.SetSettings(-10, 70, 0, TileSettings.Blocked);
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

    /// <summary><see cref="FlatWorld"/> with a river of <see cref="Water"/> across the whole region over rows
    /// <see cref="RiverFirstRow"/> to <see cref="RiverLastRow"/>, its bed sunk to -1 m on every corner inside it, so
    /// its banks stand at 0 and it is one body whose surface sits at -0.02 m. A <c>deck</c> turned a quarter stands
    /// on the bed at (<see cref="DeckX"/>, <see cref="DeckZ"/>), its walk surface above the water from tile z 10.5 to
    /// 13.5.</summary>
    public static TileWorldDocument BridgedRiverWorld()
    {
        TileWorldDocument doc = FlatWorld();
        for (int z = RiverFirstRow; z <= RiverLastRow; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, Water);
        for (int z = RiverFirstRow + 1; z <= RiverLastRow; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, -100);
        doc.AddObject("deck", DeckX, DeckZ, 0, 1);
        return doc;
    }

    /// <summary><see cref="FlatWorld"/> with every corner of plane 0 at x times <see cref="RampCmPerTile"/>, a 22 degree
    /// climb eastward under the 45 degree walkable slope.</summary>
    public static TileWorldDocument RampWorld()
    {
        TileWorldDocument doc = FlatWorld();
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)(x * RampCmPerTile));
        return doc;
    }

    /// <summary><see cref="FlatWorld"/> with a south-facing <c>wall</c> on every tile of row <see cref="DoorRow"/> from
    /// <see cref="WallRunFirstX"/> to <see cref="WallRunLastX"/> except <see cref="DoorX"/>, so the run is broken by
    /// one door exactly a tile wide.</summary>
    public static TileWorldDocument DoorwayWorld()
    {
        TileWorldDocument doc = FlatWorld();
        for (int x = WallRunFirstX; x <= WallRunLastX; x++)
            if (x != DoorX) doc.AddObject("wall", x, DoorRow, 0, 3);
        return doc;
    }

    /// <summary><see cref="FlatWorld"/> with a <c>high_deck</c> at (<see cref="HighDeckX"/>, <see cref="HighDeckZ"/>),
    /// its walk surface <see cref="HighDeckHeight"/> above the grass over tiles x and z 20 to 23.</summary>
    public static TileWorldDocument HighDeckWorld()
    {
        TileWorldDocument doc = FlatWorld();
        doc.AddObject("high_deck", HighDeckX, HighDeckZ, 0, 0);
        return doc;
    }
}
