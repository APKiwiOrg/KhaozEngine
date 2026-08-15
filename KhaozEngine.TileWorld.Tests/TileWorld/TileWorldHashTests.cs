using System.Linq;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldHashTests
{
    static TileWorldDocument World(bool objectsReversed)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.SetCornerHeightCm(5, 5, 0, 40);
        if (objectsReversed)
        {
            doc.AddObject("tree", 9, 9, 0, 0);
            doc.AddObject("tree", 3, 3, 0, 0);
            TileObject first = doc.FindObject(1)!, second = doc.FindObject(2)!;
            first.Id = 2; second.Id = 1;
            doc.RebuildObjectIndex();
        }
        else
        {
            doc.AddObject("tree", 3, 3, 0, 0);
            doc.AddObject("tree", 9, 9, 0, 0);
        }
        return doc;
    }

    [Fact]
    public void World_hash_is_independent_of_insertion_and_object_list_order()
    {
        Assert.Equal(TileWorldHash.OfWorld(World(false)), TileWorldHash.OfWorld(World(true)));
    }

    [Fact]
    public void World_hash_ignores_name_and_catalogs_but_not_content()
    {
        TileWorldDocument a = World(false);
        string h = TileWorldHash.OfWorld(a);
        a.DisplayName = "renamed"; a.Id = "other"; a.CatalogPaths.Add("x.json"); a.NextObjectId = 999;
        Assert.Equal(h, TileWorldHash.OfWorld(a));
        a.SetUnderlay(2, 2, 0, 3);
        Assert.NotEqual(h, TileWorldHash.OfWorld(a));
    }

    [Fact]
    public void World_hash_survives_a_save_and_load_and_matches_the_manifest_composition()
    {
        using var tmp = new TempDir();
        TileWorldDocument a = World(false);
        string before = TileWorldHash.OfWorld(a);
        TileWorldFile.Save(a, tmp.Sub("w"));
        TileWorldDocument b = TileWorldFile.Load(tmp.Sub("w"));
        Assert.Equal(before, TileWorldHash.OfWorld(b));
        TileWorldSource s = TileWorldSource.Open(tmp.Sub("w"));
        Assert.Equal(before, TileWorldHash.OfWorld(s.Document));
        Assert.Equal(before, TileWorldHash.OfManifestRegions(1f, 4, 3f, s.Document.UnloadedRegionHashes.Select(k => (k.Key, k.Value))));
    }

    [Fact]
    public void Region_hash_changes_with_any_layer_or_object()
    {
        TileWorldDocument a = World(false);
        TileRegion r = a.GetRegion(new RegionCoord(0, 0))!;
        string h = TileWorldHash.OfRegion(r);
        a.SetSettings(1, 1, 0, TileSettings.Indoors);
        Assert.NotEqual(h, TileWorldHash.OfRegion(r));
    }
}
