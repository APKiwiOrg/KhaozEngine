using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
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
    public void Region_files_of_negative_regions_round_trip_through_their_names()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, new RegionCoord(-1, -1), new RegionCoord(0, 0));
        doc.SetUnderlay(-3, -3, 0, 7);
        TileWorldFile.Save(doc, dir);
        Assert.True(File.Exists(Path.Combine(dir, "regions", "r_-1_-1.json")));

        Assert.True(TileWorldFile.TryParseRegionFileName("r_-1_-1.json", out RegionCoord c));
        Assert.Equal(new RegionCoord(-1, -1), c);

        TileWorldDocument back = TileWorldFile.Load(dir);
        Assert.Equal(7, back.GetUnderlay(-3, -3, 0));
        Assert.True(back.Regions.ContainsKey(new RegionCoord(-1, -1)));
    }

    [Fact]
    public void Region_file_names_ignore_the_ambient_culture()
    {
        // A culture whose minus sign is not ASCII is what breaks an interpolated name. The check runs on its
        // own thread so the hostile culture cannot leak into another test through a pooled one.
        var hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "\u2212";
        string? name = null;
        bool parsed = false;
        RegionCoord parsedCoord = default;
        var t = new Thread(() =>
        {
            CultureInfo.CurrentCulture = hostile;
            name = TileWorldFile.RegionFileName(new RegionCoord(-1, -2));
            parsed = TileWorldFile.TryParseRegionFileName("r_-1_-2.json", out parsedCoord);
        });
        t.Start();
        t.Join();
        Assert.Equal("r_-1_-2.json", name);
        Assert.True(parsed);
        Assert.Equal(new RegionCoord(-1, -2), parsedCoord);
    }

    [Fact]
    public void Parse_refuses_a_marker_on_a_plane_the_world_does_not_have()
    {
        string json = "{\"rx\":0,\"rz\":0,\"planes\":[null,null,null,null],\"objects\":[]," +
                      "\"markers\":[{\"name\":\"spawn\",\"x\":5,\"z\":5,\"plane\":9}]}";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var ex = Assert.Throws<TileWorldException>(() => TileRegionFile.Parse(bytes, new RegionCoord(0, 0), 4, "crafted.json"));
        Assert.Contains("spawn", ex.Message);
        Assert.Contains("plane 9", ex.Message);
    }

    [Fact]
    public void Load_refuses_a_formatVersion_that_is_not_an_integer()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(Authored(), dir);
        string manifestPath = Path.Combine(dir, "world.json");
        JsonObject m = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath))!;
        m["formatVersion"] = "1";
        File.WriteAllText(manifestPath, m.ToJsonString());
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Contains("world.json", ex.Message);
        Assert.Contains("formatVersion", ex.Message);
    }

    [Fact]
    public void Save_refuses_a_document_whose_PlaneCount_no_longer_matches_its_regions()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(Authored(), dir);
        TileWorldDocument back = TileWorldFile.Load(dir);
        string manifestBefore = File.ReadAllText(Path.Combine(dir, "world.json"));
        string regionBefore = File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json"));

        back.PlaneCount = 5;
        back.Regions[new RegionCoord(0, 0)].Dirty = true;
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Save(back, dir));
        Assert.Contains("4 planes", ex.Message);
        Assert.Contains("the document has 5", ex.Message);
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(dir, "world.json")));
        Assert.Equal(regionBefore, File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json")));
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
