using KhaozEngine.TileWorld;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Shared greybox worlds for the tile renderer tests: a hill, a road and a small house, each authored
/// through the document's public API over one region of grass.</summary>
public static class TileRenderTestData
{
    /// <summary>The single region every world here authors into.</summary>
    public static RegionCoord Region { get; } = new(0, 0);

    /// <summary>Greybox ground material id for grass.</summary>
    public const ushort Grass = 1;
    /// <summary>Greybox ground material id for the wood floor the house stands on.</summary>
    public const ushort WoodFloor = 5;
    /// <summary>Greybox ground material id for the road.</summary>
    public const ushort Road = 6;

    /// <summary>Height in centimetres the hill's raised corners sit at.</summary>
    public const short HillHeightCm = 200;
    /// <summary>Lowest x and z of the hill's raised corner block.</summary>
    public const int HillMin = 20;
    /// <summary>Highest x and z of the hill's raised corner block, inclusive.</summary>
    public const int HillMax = 22;

    /// <summary>Tile z the road runs along.</summary>
    public const int RoadZ = 10;
    /// <summary>Lowest tile x of the road's full-tile run.</summary>
    public const int RoadMinX = 5;
    /// <summary>Highest tile x of the road's full-tile run, inclusive.</summary>
    public const int RoadMaxX = 8;
    /// <summary>Tile x of the road's diagonal-half end, rotation 0.</summary>
    public const int RoadDiagonalX = 9;

    /// <summary>Lowest tile x of the house floor.</summary>
    public const int HouseMinX = 10;
    /// <summary>Highest tile x of the house floor, inclusive.</summary>
    public const int HouseMaxX = 12;
    /// <summary>Lowest tile z of the house floor, its south row.</summary>
    public const int HouseMinZ = 10;
    /// <summary>Highest tile z of the house floor, its north row.</summary>
    public const int HouseMaxZ = 11;
    /// <summary>Tile x of the doorway in the house's south wall.</summary>
    public const int HouseDoorX = 11;
    /// <summary>Plane the house roof sits on.</summary>
    public const int RoofPlane = 1;

    /// <summary>A fresh copy of the engine's greybox catalogs, which every world here is authored against.
    /// Fresh per call because the catalogs are mutable and test classes run in parallel.</summary>
    public static TileWorldCatalogs Catalogs => TileWorldCatalogs.Greybox();

    /// <summary>Grass with a nine-corner block raised to 200 cm: the mesher's slope and lattice-normal case.</summary>
    public static TileWorldDocument HillWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddHill(doc);
        return doc;
    }

    /// <summary>Grass with a road overlay: a run of full tiles plus one diagonal-half end, the shaped-overlay case.</summary>
    public static TileWorldDocument RoadWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddRoad(doc);
        return doc;
    }

    /// <summary>Grass with a six-tile indoor wood floor, walls and a doorway around it, and a roof on plane 1:
    /// the object, rotation and roof-rule case.</summary>
    public static TileWorldDocument HouseWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddHouse(doc);
        return doc;
    }

    /// <summary>Hill, road and house in one world, the subject of the cross-backend golden.</summary>
    public static TileWorldDocument GreyboxWorld()
    {
        TileWorldDocument doc = GrassWorld();
        AddHill(doc);
        AddRoad(doc);
        AddHouse(doc);
        return doc;
    }

    // One region of flat grass on plane 0, the base every world above decorates. The three features occupy
    // disjoint tiles, so GreyboxWorld can apply all of them to one base.
    static TileWorldDocument GrassWorld()
    {
        var doc = new TileWorldDocument { Id = "tile-render-tests", DisplayName = "Tile render tests" };
        doc.GetOrCreateRegion(Region);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, Grass);
        return doc;
    }

    static void AddHill(TileWorldDocument doc)
    {
        for (int z = HillMin; z <= HillMax; z++)
            for (int x = HillMin; x <= HillMax; x++)
                doc.SetCornerHeightCm(x, z, 0, HillHeightCm);
    }

    static void AddRoad(TileWorldDocument doc)
    {
        for (int x = RoadMinX; x <= RoadMaxX; x++)
        {
            doc.SetOverlay(x, RoadZ, 0, Road);
            doc.SetOverlayShape(x, RoadZ, 0, TileOverlayShape.Full);
        }

        doc.SetOverlay(RoadDiagonalX, RoadZ, 0, Road);
        doc.SetOverlayShape(RoadDiagonalX, RoadZ, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(RoadDiagonalX, RoadZ, 0, 0);
    }

    static void AddHouse(TileWorldDocument doc)
    {
        for (int z = HouseMinZ; z <= HouseMaxZ; z++)
            for (int x = HouseMinX; x <= HouseMaxX; x++)
            {
                doc.SetOverlay(x, z, 0, WoodFloor);
                doc.SetOverlayShape(x, z, 0, TileOverlayShape.Full);
                doc.SetSettings(x, z, 0, TileSettings.Indoors);
                doc.AddObject("roof_flat", x, z, RoofPlane, 0);
            }

        // A wall occupies one EDGE of its tile (rotation 0 west, 1 north, 2 east, 3 south), so a tile can hold
        // several of them. The middle of the south run is a doorway rather than a wall.
        for (int x = HouseMinX; x <= HouseMaxX; x++)
        {
            doc.AddObject(x == HouseDoorX ? "doorway" : "wall", x, HouseMinZ, 0, 3);
            doc.AddObject("wall", x, HouseMaxZ, 0, 1);
        }

        for (int z = HouseMinZ; z <= HouseMaxZ; z++)
        {
            doc.AddObject("wall", HouseMinX, z, 0, 0);
            doc.AddObject("wall", HouseMaxX, z, 0, 2);
        }
    }
}
