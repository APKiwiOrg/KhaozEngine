using System;
using System.Globalization;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
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

    [Fact]
    public void Header_fields_are_part_of_the_world_hash()
    {
        // Without this, dropping any of the three header fields (or the scheme version) from the composition
        // still passes every other test in this file, because they all hold the header constant.
        (RegionCoord, string)[] regions = { (new RegionCoord(0, 0), "aa") };
        string baseline = TileWorldHash.OfManifestRegions(1f, 4, 3f, regions);
        Assert.Equal(baseline, TileWorldHash.OfManifestRegions(1f, 4, 3f, regions));
        Assert.NotEqual(baseline, TileWorldHash.OfManifestRegions(2f, 4, 3f, regions));
        Assert.NotEqual(baseline, TileWorldHash.OfManifestRegions(1f, 5, 3f, regions));
        Assert.NotEqual(baseline, TileWorldHash.OfManifestRegions(1f, 4, 4f, regions));
    }

    [Fact]
    public void World_hash_ignores_the_ambient_culture()
    {
        // A negative region coordinate is what picks up a culture's own minus sign. The hostile culture runs
        // on its own thread so it cannot leak into another test through a pooled one.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "\u2212";
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(-1, -2));
        string here = TileWorldHash.OfWorld(doc);
        string? there = null;
        ExceptionDispatchInfo? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = hostile;
                there = TileWorldHash.OfWorld(doc);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        t.Start();
        t.Join();
        failure?.Throw();
        Assert.Equal(here, there);
    }

    // The catalogs the connect gate could not see. A wall archetype that gains a blocked tile bakes a different
    // collision map on both heads, and OfWorld composes only the regions and the header, so two heads over one
    // world directory with independently updated catalogs pass the gate and then disagree on every step.
    [Fact]
    public void Catalog_hash_moves_when_an_archetype_starts_blocking()
    {
        TileWorldCatalogs before = TileWorldCatalogs.Greybox();
        string h = TileWorldHash.OfCatalogs(before);
        Assert.Equal(h, TileWorldHash.OfCatalogs(TileWorldCatalogs.Greybox()));

        TileWorldCatalogs after = TileWorldCatalogs.Greybox();
        after.Archetype("doorway")!.CollisionKind = TileCollisionKind.Solid;
        Assert.NotEqual(h, TileWorldHash.OfCatalogs(after));
    }

    // Load order is a caller's business, the digest is not. Two catalogs holding the same content merged from parts
    // in either order are one identity, exactly as the region composition sorts its rows.
    [Fact]
    public void Catalog_hash_is_independent_of_merge_order()
    {
        TileWorldCatalogs one = TileWorldCatalogs.LoadJson(
            "{\"archetypes\":[{\"id\":\"a\",\"name\":\"A\",\"meshRef\":\"a.glb\"}]}", "one");
        TileWorldCatalogs two = TileWorldCatalogs.LoadJson(
            "{\"materials\":[{\"id\":2,\"name\":\"dirt\",\"color\":\"#8a6a3a\"}]}", "two");
        Assert.Equal(TileWorldHash.OfCatalogs(TileWorldCatalogs.Merge(one, two)),
            TileWorldHash.OfCatalogs(TileWorldCatalogs.Merge(two, one)));
    }

    // The composed gate digest. It has to move when EITHER half moves, and it must not be either half's own digest,
    // so a head that gates on one and a head that gates on the other can never accidentally agree.
    [Fact]
    public void World_and_catalog_hash_moves_with_both_halves_and_is_neither()
    {
        TileWorldDocument doc = World(false);
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        string h = TileWorldHash.OfWorldAndCatalogs(doc, catalogs);
        Assert.NotEqual(TileWorldHash.OfWorld(doc), h);
        Assert.NotEqual(TileWorldHash.OfCatalogs(catalogs), h);

        catalogs.Archetype("bush")!.SizeX = 2;
        string moved = TileWorldHash.OfWorldAndCatalogs(doc, catalogs);
        Assert.NotEqual(h, moved);

        doc.SetUnderlay(2, 2, 0, 3);
        Assert.NotEqual(moved, TileWorldHash.OfWorldAndCatalogs(doc, catalogs));
    }

    // OfWorld is left exactly as it was, which is what keeps a deployed client's gate answering the same string.
    // The catalogs are a SEPARATE digest a game opts into on both heads at once.
    [Fact]
    public void World_hash_still_ignores_the_catalogs_it_always_ignored()
    {
        TileWorldDocument doc = World(false);
        string h = TileWorldHash.OfWorld(doc);
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        catalogs.Archetype("wall")!.CollisionKind = TileCollisionKind.Solid;
        Assert.Equal(h, TileWorldHash.OfWorld(doc));
    }

    [Fact]
    public void Catalog_hash_is_culture_independent()
    {
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        catalogs.Archetype("wall")!.YawOffsetDegrees = -22.5f;
        catalogs.Material(1)!.TilesPerMetre = 0.25f;
        string here = TileWorldHash.OfCatalogs(catalogs);
        string? there = null;
        ExceptionDispatchInfo? failure = null;
        var hostile = new CultureInfo("sv-SE");
        var t = new Thread(() =>
        {
            try
            {
                CultureInfo.CurrentCulture = hostile;
                there = TileWorldHash.OfCatalogs(catalogs);
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        t.Start();
        t.Join();
        failure?.Throw();
        Assert.Equal(here, there);
    }

    [Fact]
    public void Manifest_composition_refuses_a_null_hash_and_a_repeated_region()
    {
        (RegionCoord, string)[] nulled = { (new RegionCoord(0, 0), null!) };
        Assert.Throws<TileWorldException>(() => TileWorldHash.OfManifestRegions(1f, 4, 3f, nulled));
        (RegionCoord, string)[] repeated = { (new RegionCoord(2, 3), "aa"), (new RegionCoord(2, 3), "bb") };
        Assert.Throws<TileWorldException>(() => TileWorldHash.OfManifestRegions(1f, 4, 3f, repeated));
    }
}
