using System;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using Xunit;

namespace KhaozEngine.Tests.TileNetcode;

/// <summary>
/// A pose over a bridge deck. The crossing is the one that asked for walk surfaces: a 3x3 bridge anchored on a bed
/// carved 80 cm down, whose deck stands 0.825 m above the anchor and reaches one tile past the footprint west and
/// east onto the sloping banks. The terrain-only presenter is pinned beside it, unchanged.
/// </summary>
public class TilePresenterWalkSurfaceTests
{
    const float Tolerance = 1e-4f;
    const int BridgeX = 20, BridgeZ = 20;
    const float Bed = -0.8f, Deck = -0.8f + 0.825f;

    static TileWorldCatalogs Catalogs() => TileWorldCatalogs.Merge(
        TileWorldCatalogs.Greybox(),
        TileWorldCatalogs.LoadJson(
            """
            {
              "archetypes": [
                { "id": "bridge", "name": "Bridge", "meshRef": "test/bridge.glb", "sizeX": 3, "sizeZ": 3,
                  "walkSurfaces": [ { "height": 0.825, "minX": -2.5, "maxX": 2.5, "minZ": -1.5, "maxZ": 1.5 } ] },
                { "id": "cellar", "name": "Cellar", "meshRef": "test/cellar.glb",
                  "walkSurfaces": [ { "height": -0.5 } ] }
              ]
            }
            """,
            "presenter-walk-surface-tests"));

    static TileWorldDocument BridgeWorld()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        for (int z = BridgeZ; z <= BridgeZ + 3; z++)
            for (int x = BridgeX; x <= BridgeX + 3; x++)
                doc.SetCornerHeightCm(x, z, 0, -80);
        doc.AddObject("bridge", BridgeX, BridgeZ, 0, 0);
        return doc;
    }

    static float Terrain(TileWorldDocument doc, float tileX, float tileZ) =>
        doc.HeightAt(TileWorldSpace.WorldX(tileX, doc.TileSize), TileWorldSpace.WorldZ(tileZ, doc.TileSize), 0);

    [Fact]
    public void The_two_argument_ground_answers_the_deck_over_the_bridge_and_terrain_elsewhere()
    {
        TileWorldDocument doc = BridgeWorld();
        var ground = new TileDocumentGroundHeight(doc, Catalogs());

        // The footprint centre, over the bed.
        Assert.Equal(Deck, ground.HeightAt(21.5f, 21.5f, 0), Tolerance);
        Assert.Equal(Bed, Terrain(doc, 21.5f, 21.5f), Tolerance);
        // The overhang on both banks, over a slope that is below the deck.
        Assert.Equal(Deck, ground.HeightAt(19.5f, 21.5f, 0), Tolerance);
        Assert.Equal(Deck, ground.HeightAt(23.5f, 21.5f, 0), Tolerance);
        Assert.Equal(-0.4f, Terrain(doc, 19.5f, 21.5f), Tolerance);
        // Off the deck: the bank the deck does not reach, the bed beyond the rect, and open grass.
        Assert.Equal(Terrain(doc, 18.5f, 21.5f), ground.HeightAt(18.5f, 21.5f, 0));
        Assert.Equal(Terrain(doc, 21.5f, 23.5f), ground.HeightAt(21.5f, 23.5f, 0));
        Assert.Equal(0f, ground.HeightAt(5.5f, 5.5f, 0));
        // Pure: the same point twice answers the same.
        Assert.Equal(ground.HeightAt(19.5f, 21.5f, 0), ground.HeightAt(19.5f, 21.5f, 0));
    }

    [Fact]
    public void A_surface_buried_under_the_terrain_loses_to_the_terrain()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld();
        doc.AddObject("cellar", 5, 5, 0, 0);
        TileWorldCatalogs catalogs = Catalogs();
        var ground = new TileDocumentGroundHeight(doc, catalogs);

        Assert.True(TileWalkSurfaces.TryHeightAt(doc, catalogs, 5.5f, -5.5f, 0, out float buried));
        Assert.Equal(-0.5f, buried, Tolerance);
        Assert.Equal(0f, ground.HeightAt(5.5f, 5.5f, 0));
    }

    [Fact]
    public void A_plane_above_the_stack_clamps_for_the_surfaces_too()
    {
        TileWorldDocument doc = TileMoveSimulatorTests.FlatWorld(planeCount: 2);
        doc.AddObject("bridge", 40, 40, 1, 0);
        var ground = new TileDocumentGroundHeight(doc, Catalogs());

        float top = doc.PlaneHeight + 0.825f;
        Assert.Equal(top, ground.HeightAt(41.5f, 41.5f, 1), Tolerance);
        Assert.Equal(top, ground.HeightAt(41.5f, 41.5f, 7), Tolerance);
        Assert.Equal(0f, ground.HeightAt(41.5f, 41.5f, 0));
    }

    [Fact]
    public void A_presenter_built_with_the_catalogs_stands_a_body_on_the_deck()
    {
        TileWorldDocument doc = BridgeWorld();
        var presenter = new TilePresenter(doc, Catalogs());

        Assert.IsType<TileDocumentGroundHeight>(presenter.Ground);
        Assert.Equal(Deck, presenter.PoseAt(new TileCoord(21, 21, 0)).Position.Y, Tolerance);
        Assert.Equal(Deck, presenter.PoseAt(new TileCoord(19, 21, 0)).Position.Y, Tolerance);
        Assert.Equal(Deck, presenter.Pose(TileMoveState.At(new TileCoord(22, 21, 0), TileDirection.W)).Position.Y,
            Tolerance);

        // A body mid-step from the bank onto the overhang stands on the deck the whole way over it, rather than
        // following the bank's slope down.
        TileMoveState stepping = TileMoveState.At(new TileCoord(19, 21, 0), TileDirection.E);
        stepping.StepFrom = new TileCoord(18, 21, 0);
        stepping.StepTotal = 4;
        stepping.StepTicks = 2;
        TilePose onDeck = presenter.Pose(stepping, extraTicks: 0.5f);
        // Five eighths of the way from the centre of tile 18 to the centre of tile 19, past the deck's west edge.
        Assert.Equal(19.125f, onDeck.Position.X, Tolerance);
        Assert.Equal(Deck, onDeck.Position.Y, Tolerance);
    }

    [Fact]
    public void The_one_argument_presenter_still_stands_on_the_terrain_alone()
    {
        TileWorldDocument doc = BridgeWorld();
        var presenter = new TilePresenter(doc);

        Assert.Equal(Bed, presenter.PoseAt(new TileCoord(21, 21, 0)).Position.Y, Tolerance);
        Assert.Equal(Terrain(doc, 19.5f, 21.5f), presenter.PoseAt(new TileCoord(19, 21, 0)).Position.Y);
        Assert.Equal(Terrain(doc, 21.5f, 21.5f), new TileDocumentGroundHeight(doc).HeightAt(21.5f, 21.5f, 0));
    }

    [Fact]
    public void The_catalogs_are_required_by_the_two_argument_constructors()
    {
        TileWorldDocument doc = BridgeWorld();

        Assert.Equal("catalogs",
            Assert.Throws<ArgumentNullException>(() => new TileDocumentGroundHeight(doc, null!)).ParamName);
        Assert.Equal("catalogs", Assert.Throws<ArgumentNullException>(() => new TilePresenter(doc, null!)).ParamName);
        Assert.Equal("document",
            Assert.Throws<ArgumentNullException>(() => new TilePresenter(null!, Catalogs())).ParamName);
    }
}
