using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileInteriorInputsTests
{
    [Fact]
    public void RelevantInputsChangeDigest()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        var region = new RegionCoord(0, 0);
        long Before() => TileInteriorInputs.Digest(doc, catalogs, region, 0);
        long empty = Before();
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        long indoor = Before();
        Assert.NotEqual(empty, indoor);
        doc.SetSettings(10, 10, 0, TileSettings.Indoors | TileSettings.Blocked | TileSettings.NoDraw);
        Assert.Equal(indoor, Before());

        TileObject tree = doc.AddObject("tree", 10, 10, 0, 0);
        doc.MoveObject(tree.Id, 11, 11, 0);
        doc.SetUnderlay(10, 10, 0, 2);
        doc.SetOverlay(10, 10, 0, 3);
        catalogs.Material(1)!.Color = "#112233";
        Assert.Equal(indoor, Before());

        TileObject wall = doc.AddObject("wall", 10, 10, 0, 0);
        long withWall = Before();
        Assert.NotEqual(indoor, withWall);
        wall.Rotation = 2;
        Assert.NotEqual(withWall, Before());
        doc.RemoveObject(wall.Id);
        Assert.Equal(indoor, Before());

        TileObject roof = doc.AddObject("roof_flat", 10, 10, 0, 0);
        long withRoof = Before();
        Assert.NotEqual(indoor, withRoof);
        doc.MoveObject(roof.Id, 11, 10, 0);
        Assert.NotEqual(withRoof, Before());
        doc.RemoveObject(roof.Id);
        Assert.Equal(indoor, Before());

        doc.SetCornerHeightCm(11, 11, 0, -50);
        Assert.NotEqual(indoor, Before());
    }

    [Fact]
    public void RoofCatalogAndPlacementInputsChangeDigest()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        TileObject roof = doc.AddObject("roof_flat", 10, 10, 1, 0);
        TileObjectArchetype archetype = catalogs.Archetype("roof_flat")!;
        long Digest() => TileInteriorInputs.Digest(doc, catalogs, new RegionCoord(0, 0), 1);
        long original = Digest();
        archetype.MeshRef = "other-roof";
        Assert.NotEqual(original, Digest());
        original = Digest();
        archetype.YawOffsetDegrees = 30f;
        Assert.NotEqual(original, Digest());
        original = Digest();
        archetype.SizeX = 2;
        Assert.NotEqual(original, Digest());
        original = Digest();
        roof.Rotation = 1;
        Assert.NotEqual(original, Digest());
        original = Digest();
        archetype.IsRoof = false;
        Assert.NotEqual(original, Digest());
    }

    [Fact]
    public void DerivedCeilingsAndSharedRegionCornersChangeDigest()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        doc.SetSettings(63, 10, 0, TileSettings.Indoors);
        long Digest(int plane) => TileInteriorInputs.Digest(doc, catalogs, new RegionCoord(0, 0), plane);
        long ground = Digest(0), ceiling = Digest(1);

        doc.SetCornerHeightCm(64, 11, 0, -50);

        Assert.NotEqual(ground, Digest(0));
        Assert.NotEqual(ceiling, Digest(1));
        ceiling = Digest(1);
        doc.SetCornerHeightCm(64, 11, 1, 275);
        Assert.NotEqual(ceiling, Digest(1));
    }

    [Fact]
    public void NeighbourWallsAndIntersectingRoofFootprintsAreInputs()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        doc.SetSettings(63, 10, 0, TileSettings.Indoors);
        long Digest(int plane) => TileInteriorInputs.Digest(doc, catalogs, new RegionCoord(0, 0), plane);
        long before = Digest(0);
        doc.AddObject("wall_corner", 64, 10, 0, 0);
        Assert.NotEqual(before, Digest(0));

        catalogs.Archetype("roof_flat")!.SizeX = 2;
        doc.AddObject("roof_flat", 63, 10, 1, 0);
        long across = TileInteriorInputs.Digest(doc, catalogs, new RegionCoord(1, 0), 1);
        doc.MoveObject(doc.AllObjects().Single(o => o.ArchetypeId == "roof_flat").Id, 62, 10, 1);
        Assert.NotEqual(across, TileInteriorInputs.Digest(doc, catalogs, new RegionCoord(1, 0), 1));
    }
}
