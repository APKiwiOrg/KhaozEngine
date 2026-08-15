using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldFileTests
{
    static TileWorldDocument Authored()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0), new RegionCoord(1, 0));
        doc.CatalogPaths.Add("../catalogs/greybox.json");
        doc.SetCornerHeightCm(10, 10, 0, 123);
        doc.SetOverlay(11, 11, 0, 6);
        doc.SetOverlayShape(11, 11, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(11, 11, 0, 3);
        doc.SetSettings(12, 12, 0, TileSettings.Indoors);
        doc.SetUnderlay(70, 5, 1, 2);
        doc.AddObject("tree", 3, 4, 0, 1, new[] { "forest" });
        doc.AddObject("wall", 65, 4, 0, 2);
        doc.SetMarker("spawn", 8, 8, 0, new[] { "player" });
        return doc;
    }

    [Fact]
    public void Save_then_Load_round_trips_every_field_and_a_second_save_is_byte_identical()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = Authored();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);
        Assert.True(TileWorldFile.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "regions", "r_0_0.json")));
        Assert.True(File.Exists(Path.Combine(dir, "regions", "r_1_0.json")));

        TileWorldDocument back = TileWorldFile.Load(dir);
        Assert.Equal("test", back.Id);
        Assert.Equal(new[] { "../catalogs/greybox.json" }, back.CatalogPaths);
        Assert.Equal(doc.NextObjectId, back.NextObjectId);
        Assert.Equal(123, back.CornerHeightCm(10, 10, 0));
        Assert.Equal(6, back.GetOverlay(11, 11, 0));
        Assert.Equal(TileOverlayShape.DiagonalHalf, back.GetOverlayShape(11, 11, 0));
        Assert.Equal(3, back.GetOverlayRotation(11, 11, 0));
        Assert.Equal(TileSettings.Indoors, back.GetSettings(12, 12, 0));
        Assert.Equal(2, back.GetUnderlay(70, 5, 1));
        Assert.Null(back.GetRegion(new RegionCoord(0, 0))!.Plane(2).Underlay);
        TileObject tree = back.FindObject(1)!;
        Assert.Equal(("tree", 3, 4, 0, 1), (tree.ArchetypeId, tree.X, tree.Z, tree.Plane, tree.Rotation));
        Assert.Equal(new[] { "forest" }, tree.Tags);
        Assert.Equal(new[] { "player" }, back.FindMarker("spawn")!.Tags);
        Assert.All(back.Regions.Values, r => Assert.False(r.Dirty));

        string manifest1 = File.ReadAllText(Path.Combine(dir, "world.json"));
        string region1 = File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json"));
        TileWorldFile.Save(back, dir, force: true);
        Assert.Equal(manifest1, File.ReadAllText(Path.Combine(dir, "world.json")));
        Assert.Equal(region1, File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json")));
    }

    [Fact]
    public void Save_rewrites_only_dirty_regions_and_removes_deleted_ones()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = Authored();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);
        string r10 = Path.Combine(dir, "regions", "r_1_0.json");
        File.WriteAllText(r10, "sentinel");
        doc.SetUnderlay(1, 1, 0, 3);
        TileWorldFile.Save(doc, dir);
        Assert.Equal("sentinel", File.ReadAllText(r10));
        doc.RemoveRegion(new RegionCoord(1, 0));
        TileWorldFile.Save(doc, dir);
        Assert.False(File.Exists(r10));
        JsonNode manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "world.json")))!;
        Assert.Single(manifest["regions"]!.AsArray());
    }

    [Fact]
    public void Load_refuses_a_region_whose_bytes_do_not_match_the_manifest_hash()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(Authored(), dir);
        string r00 = Path.Combine(dir, "regions", "r_0_0.json");
        File.WriteAllText(r00, File.ReadAllText(r00).Replace("\"rotation\":1", "\"rotation\":2"));
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Contains("(0, 0)", ex.Message);
        Assert.Contains("hash", ex.Message);
    }

    [Fact]
    public void Load_names_a_missing_region_file_and_a_missing_manifest()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        TileWorldFile.Save(Authored(), dir);
        File.Delete(Path.Combine(dir, "regions", "r_1_0.json"));
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Contains("r_1_0.json", ex.Message);
    }

    [Fact]
    public void Migrations_run_in_order_up_to_the_current_version()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(Authored(), dir);
        string manifestPath = Path.Combine(dir, "world.json");
        JsonObject m = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath))!;
        m["formatVersion"] = 0;
        m.Remove("planeHeight");
        File.WriteAllText(manifestPath, m.ToJsonString());
        var opts = new TileWorldLoadOptions();
        opts.RegisterMigration(0, root => { root["planeHeight"] = 2.5f; return root; });
        TileWorldDocument back = TileWorldFile.Load(dir, opts);
        Assert.Equal(2.5f, back.PlaneHeight);
        Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
    }

    [Fact]
    public void Codec_round_trips_and_rejects_wrong_lengths()
    {
        short[] s = { -1, 0, 1, short.MaxValue };
        Assert.Equal(s, TileLayerCodec.DecodeShorts(TileLayerCodec.Encode(s), 4, "heights"));
        Assert.Throws<TileWorldException>(() => TileLayerCodec.DecodeShorts(TileLayerCodec.Encode(s), 3, "heights"));
        Assert.Throws<TileWorldException>(() => TileLayerCodec.DecodeBytes("not base64!", 1, "settings"));
    }
}
