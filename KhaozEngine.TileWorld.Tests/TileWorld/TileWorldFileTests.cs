using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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

        byte[] manifest1 = File.ReadAllBytes(Path.Combine(dir, "world.json"));
        Assert.Contains((byte)'\n', manifest1);
        Assert.DoesNotContain((byte)'\r', manifest1);
        byte[] region1 = File.ReadAllBytes(Path.Combine(dir, "regions", "r_0_0.json"));
        Assert.DoesNotContain((byte)'\n', region1);
        Assert.DoesNotContain((byte)'\r', region1);
        TileWorldFile.Save(back, dir, force: true);
        Assert.Equal(manifest1, File.ReadAllBytes(Path.Combine(dir, "world.json")));
        Assert.Equal(region1, File.ReadAllBytes(Path.Combine(dir, "regions", "r_0_0.json")));
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
        byte[] original = File.ReadAllBytes(r00);
        string expected = Convert.ToHexStringLower(SHA256.HashData(original));
        File.WriteAllText(r00, File.ReadAllText(r00).Replace("\"rotation\":1", "\"rotation\":2"));
        string actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(r00)));
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(TileWorldRefusal.RegionHashMismatch, ex.Reason);
        Assert.Equal(new RegionCoord(0, 0), ex.Region);
        Assert.Equal(r00, ex.SourceName);
        Assert.Equal(expected, ex.ExpectedHash);
        Assert.Equal(actual, ex.ActualHash);
        File.WriteAllBytes(r00, original);
        Assert.NotNull(TileWorldFile.Load(dir).GetRegion(new RegionCoord(0, 0)));
    }

    [Fact]
    public void Load_names_a_missing_region_file_and_a_missing_manifest()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        var missing = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(TileWorldRefusal.ManifestReadFailed, missing.Reason);
        Assert.Equal(Path.Combine(dir, "world.json"), missing.SourceName);
        Assert.NotNull(missing.InnerException);
        TileWorldFile.Save(Authored(), dir);
        Assert.Equal("test", TileWorldFile.Load(dir).Id);
        string regionPath = Path.Combine(dir, "regions", "r_1_0.json");
        byte[] original = File.ReadAllBytes(regionPath);
        File.Delete(regionPath);
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(TileWorldRefusal.RegionReadFailed, ex.Reason);
        Assert.Equal(regionPath, ex.SourceName);
        Assert.Equal(new RegionCoord(1, 0), ex.Region);
        Assert.NotNull(ex.InnerException);
        File.WriteAllBytes(regionPath, original);
        Assert.NotNull(TileWorldFile.Load(dir).GetRegion(new RegionCoord(1, 0)));
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
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(TileWorldRefusal.ManifestMigrationMissing, ex.Reason);
        Assert.Equal(manifestPath, ex.SourceName);
        Assert.Equal(0, ex.FormatVersion);
        Assert.Equal(0, ex.MigrationFrom);
        Assert.Equal(1, ex.MigrationTo);
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

    [Theory]
    [InlineData("r_+1_2.json")]
    [InlineData("r_ 1_2.json")]
    [InlineData("r_01_2.json")]
    public void Region_file_names_that_are_not_the_canonical_form_do_not_parse(string name)
    {
        Assert.False(TileWorldFile.TryParseRegionFileName(name, out _));
    }

    [Fact]
    public void Dirty_survives_a_save_whose_manifest_never_lands()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldDocument doc = Authored();
        TileWorldFile.Save(doc, dir);

        string firstPath = TileWorldFile.RegionPath(dir, new RegionCoord(0, 0));
        string obstacle = TileWorldFile.RegionPath(dir, new RegionCoord(1, 0));
        byte[] firstBefore = File.ReadAllBytes(firstPath);

        // A directory sitting where the SECOND region file wants to be. The first region lands, the second
        // write throws, and the manifest is never reached, so the OLD world.json and its now-stale hashes stay
        // on disk. That is the shape that used to poison the NEXT save: clean regions, new bytes, old hashes.
        // The obstacle used to sit on the manifest's tmp file, which is a per-call name now (#790).
        File.Delete(obstacle);
        Directory.CreateDirectory(obstacle);
        doc.SetUnderlay(2, 2, 0, 9);
        doc.SetUnderlay(70, 6, 0, 9);
        TileRegion region = doc.Regions[new RegionCoord(0, 0)];
        Assert.True(region.Dirty);
        Assert.ThrowsAny<Exception>(() => TileWorldFile.Save(doc, dir));

        // The first region really did land, so this is the failure the test means rather than one that wrote
        // nothing. Regions go out in document order, and a flip should go red here rather than quietly stop
        // covering the early-clear bug.
        Assert.NotEqual(firstBefore, File.ReadAllBytes(firstPath));
        Assert.True(region.Dirty);
        Assert.True(doc.Regions[new RegionCoord(1, 0)].Dirty);

        Directory.Delete(obstacle);
        TileWorldFile.Save(doc, dir);
        TileWorldDocument back = TileWorldFile.Load(dir);
        Assert.Equal(9, back.GetUnderlay(2, 2, 0));
        Assert.Equal(9, back.GetUnderlay(70, 6, 0));
    }

    [Theory]
    [InlineData(true, true, TileWorldRefusal.RegionObjectOutsideBounds)]
    [InlineData(true, false, TileWorldRefusal.RegionObjectPlaneOutsideRange)]
    [InlineData(false, true, TileWorldRefusal.RegionMarkerOutsideBounds)]
    [InlineData(false, false, TileWorldRefusal.RegionMarkerPlaneOutsideRange)]
    public void Parse_refuses_an_object_or_marker_outside_its_region_or_planes(
        bool isObject, bool outsideBounds, TileWorldRefusal reason)
    {
        var coord = new RegionCoord(0, 0);
        JsonNode json = JsonNode.Parse(TileRegionFile.WriteCanonical(Authored().GetRegion(coord)!))!;
        JsonNode item = json[isObject ? "objects" : "markers"]![0]!;
        item[outsideBounds ? "x" : "plane"] = outsideBounds ? 200 : 9;
        byte[] bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var ex = Assert.Throws<TileWorldException>(() => TileRegionFile.Parse(bytes, coord, 4, "crafted.json"));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("crafted.json", ex.SourceName);
        Assert.Equal(coord, ex.Region);
        if (isObject)
        {
            Assert.Equal(1, ex.ObjectId);
            Assert.Equal("tree", ex.ArchetypeId);
        }
        else Assert.Equal("spawn", ex.MarkerName);
        if (outsideBounds) Assert.Equal(new TileCoord(200, isObject ? 4 : 8, 0), ex.Tile);
        else
        {
            Assert.Equal(9, ex.Plane);
            Assert.Equal(4, ex.PlaneCount);
        }

        item[outsideBounds ? "x" : "plane"] = outsideBounds ? (isObject ? 3 : 8) : 0;
        TileRegion back = TileRegionFile.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), coord, 4, "crafted.json");
        Assert.Equal(isObject ? new TileCoord(3, 4, 0) : new TileCoord(8, 8, 0),
            isObject ? back.Objects.Single(o => o.Id == 1).Coord : Assert.Single(back.Markers).Coord);
    }

    [Theory]
    [InlineData("null", TileWorldRefusal.RegionFileEmpty, null)]
    [InlineData("{\"rx\":\"bad\",\"rz\":0,\"planes\":[null,null,null,null]}", TileWorldRefusal.RegionJsonMalformed, "$.rx")]
    [InlineData("{\"rx\":1,\"rz\":2,\"planes\":[null,null,null,null]}", TileWorldRefusal.RegionCoordinateMismatch, null)]
    [InlineData("{\"rx\":0,\"rz\":0,\"planes\":[null,null]}", TileWorldRefusal.RegionPlaneCountMismatch, null)]
    public void Parse_refuses_a_region_header_with_its_source_and_structured_cause(
        string json, TileWorldRefusal reason, string? field)
    {
        var coord = new RegionCoord(0, 0);
        var ex = Assert.Throws<TileWorldException>(() =>
            TileRegionFile.Parse(Encoding.UTF8.GetBytes(json), coord, 4, "header.json"));

        Assert.Equal(reason, ex.Reason);
        Assert.Equal("header.json", ex.SourceName);
        if (reason == TileWorldRefusal.RegionJsonMalformed)
        {
            Assert.Equal(field, ex.Field);
            Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
        }
        if (reason == TileWorldRefusal.RegionCoordinateMismatch)
        {
            Assert.Equal(coord, ex.Region);
            Assert.Equal(new RegionCoord(1, 2), ex.ActualRegion);
        }
        if (reason == TileWorldRefusal.RegionPlaneCountMismatch)
        {
            Assert.Equal(coord, ex.Region);
            Assert.Equal(2, ex.ActualPlaneCount);
            Assert.Equal(4, ex.ExpectedPlaneCount);
        }

        TileRegion back = TileRegionFile.Parse(
            "{\"rx\":0,\"rz\":0,\"planes\":[null,null,null,null]}"u8, coord, 4, "header.json");
        Assert.Equal(coord, back.Coord);
        Assert.Equal(4, back.Planes.Length);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("[]")]
    public void Load_refuses_a_formatVersion_that_is_not_an_integer(string value)
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(Authored(), dir);
        string manifestPath = Path.Combine(dir, "world.json");
        JsonObject m = (JsonObject)JsonNode.Parse(File.ReadAllText(manifestPath))!;
        m["formatVersion"] = JsonNode.Parse(value);
        File.WriteAllText(manifestPath, m.ToJsonString());
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(TileWorldRefusal.ManifestFormatVersionNotInteger, ex.Reason);
        Assert.Equal(manifestPath, ex.SourceName);
        Assert.Equal("formatVersion", ex.Field);

        m["formatVersion"] = TileWorldFile.CurrentFormatVersion;
        File.WriteAllText(manifestPath, m.ToJsonString());
        Assert.Equal("test", TileWorldFile.Load(dir).Id);
    }

    [Fact]
    public void Save_refuses_a_document_whose_PlaneCount_no_longer_matches_its_regions()
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        // One region, so the refused region is the only candidate and the test pins no traversal order.
        TileWorldFile.Save(TileWorldTestData.FlatWorld(4, new RegionCoord(0, 0)), dir);
        TileWorldDocument back = TileWorldFile.Load(dir);
        string manifestBefore = File.ReadAllText(Path.Combine(dir, "world.json"));
        string regionBefore = File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json"));

        back.PlaneCount = 5;
        back.Regions[new RegionCoord(0, 0)].Dirty = true;
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Save(back, dir));
        Assert.Equal(TileWorldRefusal.RegionPlaneCountMismatch, ex.Reason);
        Assert.Equal(new RegionCoord(0, 0), ex.Region);
        Assert.Equal(4, ex.ActualPlaneCount);
        Assert.Equal(5, ex.ExpectedPlaneCount);
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(dir, "world.json")));
        Assert.Equal(regionBefore, File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json")));

        back.PlaneCount = 4;
        TileWorldFile.Save(back, dir);
        Assert.Equal(4, TileWorldFile.Load(dir).PlaneCount);
    }

    [Theory]
    [InlineData(true, true, TileWorldRefusal.RegionObjectOutsideBounds)]
    [InlineData(true, false, TileWorldRefusal.RegionObjectPlaneOutsideRange)]
    [InlineData(false, true, TileWorldRefusal.RegionMarkerOutsideBounds)]
    [InlineData(false, false, TileWorldRefusal.RegionMarkerPlaneOutsideRange)]
    public void Save_refuses_an_object_or_marker_a_load_would_reject_before_writing_anything(
        bool isObject, bool outsideBounds, TileWorldRefusal reason)
    {
        using var tmp = new TempDir();
        string dir = tmp.Sub("world");
        TileWorldDocument doc = Authored();
        TileWorldFile.Save(doc, dir);
        string manifestBefore = File.ReadAllText(Path.Combine(dir, "world.json"));
        string regionBefore = File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json"));

        // X and Z are public and settable, so an editor can walk an object out of its own region without ever
        // going through MoveObject. Parse refuses that on the way back in, so Save has to refuse it going out.
        TileObject tree = doc.FindObject(1)!;
        TileMarker marker = doc.FindMarker("spawn")!;
        if (isObject)
        {
            if (outsideBounds) tree.X = 200;
            else tree.Plane = 9;
        }
        else
        {
            if (outsideBounds) marker.X = 200;
            else marker.Plane = 9;
        }
        TileWorldException ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Save(doc, dir));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(new RegionCoord(0, 0), ex.Region);
        if (isObject)
        {
            Assert.Equal(1, ex.ObjectId);
            Assert.Equal("tree", ex.ArchetypeId);
        }
        else Assert.Equal("spawn", ex.MarkerName);
        if (outsideBounds) Assert.Equal(new TileCoord(200, isObject ? 4 : 8, 0), ex.Tile);
        else
        {
            Assert.Equal(9, ex.Plane);
            Assert.Equal(4, ex.PlaneCount);
        }

        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(dir, "world.json")));
        Assert.Equal(regionBefore, File.ReadAllText(Path.Combine(dir, "regions", "r_0_0.json")));
        Assert.Equal("tree", TileWorldFile.Load(dir).FindObject(1)!.ArchetypeId);

        tree.X = 3;
        tree.Plane = 0;
        marker.X = 8;
        marker.Plane = 0;
        TileWorldFile.Save(doc, dir, force: true);
        Assert.Equal(new TileCoord(8, 8, 0), TileWorldFile.Load(dir).FindMarker("spawn")!.Coord);
    }

    [Fact]
    public void Codec_round_trips_and_rejects_wrong_lengths()
    {
        short[] s = { -1, 0, 1, short.MaxValue };
        Assert.Equal(s, TileLayerCodec.DecodeShorts(TileLayerCodec.Encode(s), 4, "heights"));
        TileWorldException length = Assert.Throws<TileWorldException>(() => TileLayerCodec.DecodeShorts(TileLayerCodec.Encode(s), 3, "heights"));
        Assert.Equal(TileWorldRefusal.LayerByteCountMismatch, length.Reason);
        Assert.Equal("heights", length.LayerName);
        Assert.Equal(8, length.ActualByteCount);
        Assert.Equal(6, length.ExpectedByteCount);
        Assert.Null(length.SourceName);
        Assert.Null(length.Plane);

        Assert.Equal(new byte[] { 1 }, TileLayerCodec.DecodeBytes("AQ==", 1, "settings"));
        TileWorldException malformed = Assert.Throws<TileWorldException>(() => TileLayerCodec.DecodeBytes("not base64!", 1, "settings"));
        Assert.Equal(TileWorldRefusal.LayerBase64Malformed, malformed.Reason);
        Assert.Equal("settings", malformed.LayerName);
        Assert.Null(malformed.SourceName);
        Assert.Null(malformed.Plane);
        Assert.IsType<FormatException>(malformed.InnerException);
    }

    [Theory]
    [InlineData(false, "heights", "heights", 8192)]
    [InlineData(false, "underlay", "underlay", 8192)]
    [InlineData(false, "overlay", "overlay", 8192)]
    [InlineData(false, "overlayShape", "overlayShape", 4096)]
    [InlineData(false, "overlayRotation", "overlayRotation", 4096)]
    [InlineData(false, "settings", "settings", 4096)]
    [InlineData(true, "heightsRelative", "heights", 8)]
    [InlineData(true, "underlay", "underlay", 2)]
    [InlineData(true, "overlay", "overlay", 2)]
    [InlineData(true, "overlayShape", "overlayShape", 1)]
    [InlineData(true, "overlayRotation", "overlayRotation", 1)]
    [InlineData(true, "settings", "settings", 1)]
    public void Loaded_layers_keep_the_source_plane_and_layer_on_refusal(bool prefab, string field, string layerName, int expectedBytes)
    {
        using var tmp = new TempDir();
        string source = tmp.Sub("authored plane 9 settings.json");
        var plane = new JsonObject { [field] = "" };
        var json = new JsonObject { ["planes"] = new JsonArray(null, plane) };
        if (prefab)
        {
            json["width"] = 1;
            json["height"] = 1;
            json["planeCount"] = 2;
        }
        else
        {
            json["rx"] = 0;
            json["rz"] = 0;
        }

        void Load()
        {
            if (prefab)
            {
                File.WriteAllText(source, json.ToJsonString());
                TilePrefabFile.Load(source);
            }
            else TileRegionFile.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), new RegionCoord(0, 0), 2, source);
        }

        TileWorldException length = Assert.Throws<TileWorldException>(Load);
        Assert.Equal(TileWorldRefusal.LayerByteCountMismatch, length.Reason);
        Assert.Equal(source, length.SourceName);
        Assert.Equal(1, length.Plane);
        Assert.Equal(layerName, length.LayerName);
        Assert.Equal(0, length.ActualByteCount);
        Assert.Equal(expectedBytes, length.ExpectedByteCount);

        plane[field] = "not base64!";
        TileWorldException malformed = Assert.Throws<TileWorldException>(Load);
        Assert.Equal(TileWorldRefusal.LayerBase64Malformed, malformed.Reason);
        Assert.Equal(source, malformed.SourceName);
        Assert.Equal(1, malformed.Plane);
        Assert.Equal(layerName, malformed.LayerName);
        Assert.IsType<FormatException>(malformed.InnerException);

        plane[field] = Convert.ToBase64String(new byte[expectedBytes]);
        Load();
    }
}
