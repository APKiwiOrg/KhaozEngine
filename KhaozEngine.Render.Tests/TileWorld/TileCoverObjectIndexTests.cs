using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileCoverObjectIndexTests
{
    [Fact]
    public void Index_refresh_removes_old_bounds_and_queries_only_relevant_objects()
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        document.GetOrCreateRegion(new RegionCoord(20, 20));
        for (int i = 0; i < 32; i++) document.AddObject("tree", 1280 + i, 1280, 0, 0);
        TileObject door = document.AddObject("bush", 1280, 1282, 0, 0, ["door"]);
        var unrelatedObject = new TileCoverObject(new TileRect(1280, 1282, 1, 1), 0, false, false, true);
        var objects = new TileCoverObjectIndex(document, catalogs);
        IReadOnlyList<TileCoverObject> queried = objects.Query(TileCoverTestWorld.Region, document.FoliageLayers);
        Assert.DoesNotContain(unrelatedObject, queried);
        Assert.Equal(5, queried.Count);

        TileObject roof = document.AllObjects().Single(obj => obj.ArchetypeId == "roof_flat");
        var oldRoof = new TileCoverObject(new TileRect(-192, -42, 146, 4), 2, false, true, false);
        document.RemoveObject(roof.Id);
        objects.Refresh(new RegionCoord(-3, -1));
        Assert.DoesNotContain(oldRoof, objects.Query(TileCoverTestWorld.Region, document.FoliageLayers));

        document.MoveObject(door.Id, -5, -5, 0);
        objects.Refresh(new RegionCoord(20, 20));
        objects.Refresh(TileCoverTestWorld.Region);
        var movedDoor = new TileCoverObject(new TileRect(-5, -5, 1, 1), 0, false, false, true);
        Assert.Contains(movedDoor, objects.Query(TileCoverTestWorld.Region, document.FoliageLayers));
        document.MoveObject(door.Id, 1280, 1282, 0);
        objects.Refresh(TileCoverTestWorld.Region);
        objects.Refresh(new RegionCoord(20, 20));
        Assert.DoesNotContain(movedDoor, objects.Query(TileCoverTestWorld.Region, document.FoliageLayers));
        Assert.Equal(TileCoverTestWorld.Live(document, catalogs, true),
            TileCoverBuildInput.Capture(document, catalogs, TileCoverTestWorld.Region, true, objects).Build());

        document.RemoveRegion(new RegionCoord(-3, -1));
        objects.Refresh(new RegionCoord(-3, -1));
        Assert.DoesNotContain(objects.Query(TileCoverTestWorld.Region, document.FoliageLayers),
            obj => obj.Door && obj.Footprint.X < -128);
    }

    [Fact]
    public void Query_filters_plane_rules_and_uses_exact_door_clearance_in_metres()
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        TileObject diagonal = document.AddObject("bush", 1, 1, 1, 0, ["door"]);
        var diagonalDoor = new TileCoverObject(new TileRect(diagonal.X, diagonal.Z, 1, 1), 1, false, false, true);
        var objects = new TileCoverObjectIndex(document, catalogs);
        TileFoliageLayer layer = TileCoverTestWorld.Layer("upper", 1, 4.25f, false, false);
        Assert.DoesNotContain(diagonalDoor, objects.Query(TileCoverTestWorld.Region, [layer]));
        Assert.Single(objects.Query(TileCoverTestWorld.Region, [layer]));
        layer = TileCoverTestWorld.Layer("upper", 1, 5.5f, false, false);
        Assert.Contains(diagonalDoor, objects.Query(TileCoverTestWorld.Region, [layer]));
        Assert.DoesNotContain(objects.Query(TileCoverTestWorld.Region, [layer]), obj => obj.Plane != 1);
        Assert.Empty(objects.Query(TileCoverTestWorld.Region, []));
    }

    [Fact]
    public void Bounded_surface_clips_masks_but_preserves_original_door_centres()
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        TileFoliageLayer layer = TileCoverTestWorld.Layer("upper", 1, 2f);
        TileCoverObject[] exclusions =
        [
            new(new TileRect(-192, -42, 146, 4), 2, false, true, false),
            new(new TileRect(-65, -20, 4, 2), 1, false, false, true),
            new(new TileRect(0, -20, 1, 1), 1, true, false, false),
        ];
        var surface = new TileFoliageSurface(document, catalogs, layer, true, exclusions,
            TileCoverTestWorld.Region.Rect.Expand(1));
        float Density(float x, float z) => surface.Sample(x * document.TileSize, -z * document.TileSize).Density;
        Assert.Equal(0f, Density(-63, -19));
        Assert.True(Density(-62, -19) > 0f);
        Assert.Equal(0f, Density(-60, -40));
        Assert.True(Density(-100, -40) > 0f);
        Assert.InRange(Density(-0.25f, -19.5f), 0.01f, 0.4f);
    }
}
