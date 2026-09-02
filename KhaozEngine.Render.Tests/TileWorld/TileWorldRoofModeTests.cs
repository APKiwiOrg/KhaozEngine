using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The roof rule once it is per building rather than per plane: two houses in one region, each with its
/// own roofs on the plane above, and an observer who is inside one of them, outside both, or looking at the world
/// through one of the two mode overrides. Every test drives the recording fake, so none of this needs a device.
/// </summary>
public class TileWorldRoofModeTests
{
    // Two indoor blocks in one region, five outdoor columns apart, so they are two separate 4-connected
    // interiors and not one. Both are 3 by 2 tiles with one 1x1 roof object per tile on the plane above.
    const int HouseAMinX = 4, HouseAMaxX = 6;
    const int HouseBMinX = 12, HouseBMaxX = 14;
    const int HouseMinZ = 4, HouseMaxZ = 5;
    const int RoofPlane = 1;
    const int RoofsPerHouse = 6;

    // World metres between the two houses. A 1x1 roof anchors at tile x + 0.5 m (TileWorldSpace.WorldX over the
    // default 1 m tile), so a drawn roof's x says which house it belongs to without a second archetype.
    const float HouseSplitX = 10f;

    // Big enough that the horizontal cull never fires, so a roof that did not draw was hidden by the rule.
    static TileWorldViewOptions Options(List<string>? log = null)
    {
        var options = new TileWorldViewOptions { PropDrawRadius = 1000f };
        if (log is not null) options.Log = log.Add;
        return options;
    }

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions? options = null) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options ?? Options());

    static TileWorldDocument TwoHouses()
    {
        var doc = new TileWorldDocument { Id = "roof-mode-tests", DisplayName = "Roof mode tests" };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size; x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        House(doc, HouseAMinX, HouseAMaxX);
        House(doc, HouseBMinX, HouseBMaxX);
        return doc;
    }

    static void House(TileWorldDocument doc, int minX, int maxX)
    {
        for (int z = HouseMinZ; z <= HouseMaxZ; z++)
            for (int x = minX; x <= maxX; x++)
            {
                doc.SetSettings(x, z, 0, TileSettings.Indoors);
                doc.AddObject("roof_flat", x, z, RoofPlane, 0);
            }
    }

    // A run of indoor tiles one wide, long enough to overrun the fill cap, each in whichever region it lands in.
    static TileWorldDocument Corridor(int length, int z)
    {
        var doc = new TileWorldDocument { Id = "roof-cap-tests", DisplayName = "Roof cap tests" };
        for (int rx = 0; rx <= (length - 1) / TileRegion.Size; rx++) doc.GetOrCreateRegion(new RegionCoord(rx, 0));
        for (int x = 0; x < length; x++) doc.SetSettings(x, z, 0, TileSettings.Indoors);
        return doc;
    }

    // How many roof placements of one house reached the scene this frame, counted over the placement lists the
    // fake recorded rather than over the returned totals, so the test names WHICH roofs drew.
    static int RoofsDrawn(RecordingTileWorldScene scene, bool houseA) =>
        scene.PropDraws.Sum(r => r.Placements.Count(
            p => p.Id == "roof_flat" && (houseA ? p.X < HouseSplitX : p.X > HouseSplitX)));

    static TileCoord Inside(int houseMinX) => new(houseMinX, HouseMinZ, 0);

    [Fact]
    public void Only_the_roofs_of_the_house_the_observer_stands_in_are_hidden()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoHouses());
        view.LoadRegion(TileRenderTestData.Region);

        view.Observer = Inside(HouseAMinX);
        Assert.True(view.ObserverIndoors);
        view.Draw(Vector3.Zero);

        // The whole point: the plane-wide rule hid B's roofs too, and walking into one building stripped every
        // roof in view. B is a separate 4-connected block of Indoors tiles, so it is a separate interior.
        Assert.Equal(0, RoofsDrawn(scene, houseA: true));
        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: false));
        Assert.Equal(RoofsPerHouse, view.LastDrawnProps);
        Assert.Equal(RoofsPerHouse, view.InteriorTileCount);
        Assert.False(view.InteriorTruncated);
    }

    [Fact]
    public void An_outdoor_observer_sees_every_roof()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoHouses());
        view.LoadRegion(TileRenderTestData.Region);

        view.Observer = new TileCoord(0, 0, 0);
        Assert.False(view.ObserverIndoors);
        view.Draw(Vector3.Zero);

        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: true));
        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: false));
        Assert.Equal(2 * RoofsPerHouse, view.LastDrawnProps);
        Assert.Equal(0, view.InteriorTileCount);
    }

    [Fact]
    public void Moving_the_observer_between_the_houses_swaps_which_roofs_hide()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoHouses());
        view.LoadRegion(TileRenderTestData.Region);

        view.Observer = Inside(HouseAMinX);
        view.Draw(Vector3.Zero);
        Assert.Equal(0, RoofsDrawn(scene, houseA: true));
        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: false));
        scene.ClearFrame();

        // Straight from one interior to the other, which is the refill path: the fill is seeded from the
        // observer tile, so nothing of house A may survive into house B's answer.
        view.Observer = Inside(HouseBMinX);
        view.Draw(Vector3.Zero);
        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: true));
        Assert.Equal(0, RoofsDrawn(scene, houseA: false));
        scene.ClearFrame();

        // And back out into the open, which clears it altogether.
        view.Observer = new TileCoord(0, 0, 0);
        view.Draw(Vector3.Zero);
        Assert.Equal(2 * RoofsPerHouse, view.LastDrawnProps);
    }

    [Fact]
    public void AlwaysHidden_hides_every_roof_indoors_and_out()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoHouses());
        view.LoadRegion(TileRenderTestData.Region);
        view.RoofMode = RoofVisibility.AlwaysHidden;

        // Outdoors, where the interior rule would hide nothing at all: the roofs-off setting does not care.
        view.Observer = new TileCoord(0, 0, 0);
        Assert.False(view.ObserverIndoors);
        view.Draw(Vector3.Zero);
        Assert.Equal(0, view.LastDrawnProps);
        Assert.DoesNotContain(scene.PropDraws, r => r.Placements.Any(p => p.Id == "roof_flat"));
        scene.ClearFrame();

        view.Observer = Inside(HouseAMinX);
        view.Draw(Vector3.Zero);
        Assert.Equal(0, view.LastDrawnProps);
    }

    [Fact]
    public void AlwaysVisible_shows_both_houses_roofs_from_inside_one_of_them()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoHouses());
        view.LoadRegion(TileRenderTestData.Region);
        view.RoofMode = RoofVisibility.AlwaysVisible;

        view.Observer = Inside(HouseAMinX);
        Assert.True(view.ObserverIndoors);
        view.Draw(Vector3.Zero);

        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: true));
        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: false));
        Assert.Equal(2 * RoofsPerHouse, view.LastDrawnProps);
        Assert.False(view.IsRoofHidden(new TileRect(HouseAMinX, HouseMinZ, 1, 1), RoofPlane));
    }

    [Fact]
    public void A_roof_on_the_observers_own_plane_belongs_to_the_storey_they_are_on()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TwoHouses();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);

        view.Observer = Inside(HouseAMinX);
        // Same footprint, same interior, plane 0 rather than 1: it is the floor of the storey the observer is
        // standing on rather than the ceiling over them, so it stays.
        Assert.True(view.IsRoofHidden(new TileRect(HouseAMinX, HouseMinZ, 1, 1), RoofPlane));
        Assert.False(view.IsRoofHidden(new TileRect(HouseAMinX, HouseMinZ, 1, 1), 0));
    }

    [Fact]
    public void A_multi_tile_roof_hides_when_any_of_its_footprint_is_the_observers_interior()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TwoHouses());
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = Inside(HouseAMinX);

        // Overhanging house A's west wall by one tile: a roof belongs to the building it covers any of.
        Assert.True(view.IsRoofHidden(new TileRect(HouseAMinX - 1, HouseMinZ, 2, 1), RoofPlane));
        // Wholly over the ground between the two, touching neither.
        Assert.False(view.IsRoofHidden(new TileRect(HouseAMaxX + 2, HouseMinZ, 2, 1), RoofPlane));
        // Over house B, which the observer is not in.
        Assert.False(view.IsRoofHidden(new TileRect(HouseBMinX, HouseMinZ, 3, 2), RoofPlane));
    }

    [Fact]
    public void An_interior_over_the_cap_stops_there_and_hides_nothing_past_it()
    {
        var scene = new RecordingTileWorldScene();
        const int corridorZ = 10;
        int length = TileWorldView.MaxInteriorTiles + 200;
        var log = new List<string>();
        using TileWorldView view = View(scene, Corridor(length, corridorZ), Options(log));

        view.Observer = new TileCoord(0, corridorZ, 0);
        Assert.True(view.ObserverIndoors);
        Assert.Equal(TileWorldView.MaxInteriorTiles, view.InteriorTileCount);
        Assert.True(view.InteriorTruncated);

        // A roof over a tile the walk reached is hidden as usual, and one past the cap is not: the bound fails
        // towards a visible roof rather than a stalled frame or a throw.
        Assert.True(view.IsRoofHidden(new TileRect(10, corridorZ, 1, 1), RoofPlane));
        Assert.False(view.IsRoofHidden(new TileRect(length - 1, corridorZ, 1, 1), RoofPlane));
        Assert.Contains(log, line => line.Contains("interior", StringComparison.Ordinal));

        // One line for the view's life, not one a frame while the observer walks the corridor.
        view.Observer = new TileCoord(1, corridorZ, 0);
        Assert.True(view.InteriorTruncated);
        Assert.Single(log);
    }

    [Fact]
    public void An_edit_that_joins_the_two_houses_makes_them_one_interior()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TwoHouses();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);

        view.Observer = Inside(HouseAMinX);
        view.Draw(Vector3.Zero);
        Assert.Equal(RoofsPerHouse, RoofsDrawn(scene, houseA: false));
        scene.ClearFrame();

        // A corridor of indoor tiles between them, announced the only way the document has: MarkDirty. The
        // observer has not moved, so the refill has to come from the edit.
        for (int x = HouseAMaxX + 1; x < HouseBMinX; x++) doc.SetSettings(x, HouseMinZ, 0, TileSettings.Indoors);
        view.MarkDirty(new TileRect(HouseAMaxX + 1, HouseMinZ, HouseBMinX - HouseAMaxX - 1, 1), 0);
        view.Draw(Vector3.Zero);

        Assert.Equal(0, RoofsDrawn(scene, houseA: false));
        Assert.Equal(0, view.LastDrawnProps);
        Assert.Equal(2 * RoofsPerHouse + (HouseBMinX - HouseAMaxX - 1), view.InteriorTileCount);
    }

    [Fact]
    public void A_silhouetted_roof_over_another_building_still_draws_its_hull()
    {
        var scene = new RecordingTileWorldScene();
        TileWorldDocument doc = TwoHouses();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        view.Observer = Inside(HouseAMinX);

        List<TileObject> roofs = doc.GetOrCreateRegion(TileRenderTestData.Region).Objects
            .Where(o => o.Plane == RoofPlane).ToList();
        TileObject overA = roofs.First(o => o.X <= HouseAMaxX);
        TileObject overB = roofs.First(o => o.X >= HouseBMinX);

        // The hull follows the model: a roof the interior rule hid has nothing to eat its middle, and one over
        // the building next door is drawn and so silhouettes.
        view.SetSilhouettedObject(overA.Id, new KhaozEngine.Primitives.Color(1f, 1f, 1f, 1f));
        view.Draw(Vector3.Zero);
        Assert.Empty(scene.Silhouettes);

        view.SetSilhouettedObject(overB.Id, new KhaozEngine.Primitives.Color(1f, 1f, 1f, 1f));
        view.Draw(Vector3.Zero);
        Assert.NotEmpty(scene.Silhouettes);
    }
}
