using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldManifestRefusalTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void Load_refuses_a_manifest_that_is_not_an_object(string json)
    {
        using var tmp = new TempDir();
        TileWorldFile.Save(TileWorldTestData.FlatWorld(), tmp.Path);
        string path = TileWorldFile.ManifestPath(tmp.Path);
        string original = File.ReadAllText(path);
        File.WriteAllText(path, json);

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(tmp.Path));
        Assert.Equal(TileWorldRefusal.ManifestNotObject, ex.Reason);
        Assert.Equal(path, ex.SourceName);

        File.WriteAllText(path, original);
        Assert.Equal("test", TileWorldFile.Load(tmp.Path).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_keeps_the_parser_cause_for_malformed_manifest_json(bool malformedSyntax)
    {
        using var tmp = new TempDir();
        TileWorldFile.Save(TileWorldTestData.FlatWorld(), tmp.Path);
        string path = TileWorldFile.ManifestPath(tmp.Path);
        string original = File.ReadAllText(path);
        JsonObject root = JsonNode.Parse(original)!.AsObject();
        root["planeCount"] = "bad";
        File.WriteAllText(path, malformedSyntax ? "{" : root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(tmp.Path));
        Assert.Equal(TileWorldRefusal.ManifestJsonMalformed, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        var cause = Assert.IsAssignableFrom<JsonException>(ex.InnerException);
        Assert.Equal(cause.Path, ex.Field);
        if (!malformedSyntax) Assert.Equal("$.planeCount", ex.Field);

        File.WriteAllText(path, original);
        Assert.Equal("test", TileWorldFile.Load(tmp.Path).Id);
    }

    [Theory]
    [InlineData("formatVersion", null, TileWorldRefusal.ManifestFormatVersionMissing)]
    [InlineData("planeCount", "0", TileWorldRefusal.ManifestPlaneCountInvalid)]
    [InlineData("tileSize", "0", TileWorldRefusal.ManifestTileSizeInvalid)]
    [InlineData("id", "\" \"", TileWorldRefusal.ManifestIdMissing)]
    public void Load_identifies_an_invalid_manifest_field(string field, string? value, TileWorldRefusal reason)
    {
        using var tmp = new TempDir();
        TileWorldFile.Save(TileWorldTestData.FlatWorld(), tmp.Path);
        string path = TileWorldFile.ManifestPath(tmp.Path);
        string original = File.ReadAllText(path);
        JsonObject root = JsonNode.Parse(original)!.AsObject();
        if (value is null) root.Remove(field);
        else root[field] = JsonNode.Parse(value);
        File.WriteAllText(path, root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(tmp.Path));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.Equal(field, ex.Field);
        if (reason == TileWorldRefusal.ManifestPlaneCountInvalid) Assert.Equal(0, ex.PlaneCount);
        if (reason == TileWorldRefusal.ManifestTileSizeInvalid) Assert.Equal(0f, ex.TileSize);

        File.WriteAllText(path, original);
        Assert.Equal("test", TileWorldFile.Load(tmp.Path).Id);
    }

    [Fact]
    public void Load_identifies_the_requested_and_supported_manifest_versions()
    {
        using var tmp = new TempDir();
        TileWorldFile.Save(TileWorldTestData.FlatWorld(), tmp.Path);
        string path = TileWorldFile.ManifestPath(tmp.Path);
        string original = File.ReadAllText(path);
        JsonObject root = JsonNode.Parse(original)!.AsObject();
        int requested = TileWorldFile.CurrentFormatVersion + 1;
        root["formatVersion"] = requested;
        File.WriteAllText(path, root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(tmp.Path));
        Assert.Equal(TileWorldRefusal.ManifestFormatVersionUnsupported, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.Equal(requested, ex.FormatVersion);
        Assert.Equal(TileWorldFile.CurrentFormatVersion, ex.SupportedFormatVersion);

        File.WriteAllText(path, original);
        Assert.Equal("test", TileWorldFile.Load(tmp.Path).Id);
    }

    [Fact]
    public void Load_identifies_the_migration_that_returns_no_manifest()
    {
        using var tmp = new TempDir();
        TileWorldFile.Save(TileWorldTestData.FlatWorld(), tmp.Path);
        string path = TileWorldFile.ManifestPath(tmp.Path);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["formatVersion"] = 0;
        File.WriteAllText(path, root.ToJsonString());
        var options = new TileWorldLoadOptions();
        options.RegisterMigration(0, _ => null!);

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(tmp.Path, options));
        Assert.Equal(TileWorldRefusal.ManifestMigrationReturnedNull, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.Equal(0, ex.MigrationFrom);
        Assert.Equal(1, ex.MigrationTo);

        var valid = new TileWorldLoadOptions();
        valid.RegisterMigration(0, manifest => manifest);
        Assert.Equal("test", TileWorldFile.Load(tmp.Path, valid).Id);
    }

    [Fact]
    public void An_empty_deserialization_result_keeps_the_manifest_source()
    {
        const string source = "authored-world.json";
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.ReadEmptyManifestForTests(null, source));
        Assert.Equal(TileWorldRefusal.ManifestEmpty, ex.Reason);
        Assert.Equal(source, ex.SourceName);

        TileWorldFile.ReadEmptyManifestForTests(new TileWorldManifest { Id = "valid" }, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_stale_region_delete_keeps_the_file_region_and_original_cause(bool unauthorized)
    {
        const string source = "regions/r_-2_3.json";
        var region = new RegionCoord(-2, 3);
        Exception cause = unauthorized ? new UnauthorizedAccessException("denied") : new IOException("blocked");
        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.FailStaleRegionDeleteForTests(source, region, cause));
        Assert.Equal(TileWorldRefusal.WorldStaleRegionDeleteFailed, ex.Reason);
        Assert.Equal(source, ex.SourceName);
        Assert.Equal(region, ex.Region);
        Assert.Same(cause, ex.InnerException);
    }
}
