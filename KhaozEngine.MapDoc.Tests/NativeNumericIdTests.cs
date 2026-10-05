using System;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeNumericIdTests
{
    [Fact]
    public void NativeIds_LargeDecimal_DeletionAndExhaustion()
    {
        var doc = Load();
        doc.Placements[0].NumericId = 9007199254740993L;
        MapNumericIds.Reserve(doc, new[] { 9007199254740993L });
        string json = MapDocumentFile.SaveText(doc);
        Assert.Contains("\"9007199254740993\"", json);
        var root = JsonNode.Parse(json)!;
        Assert.Equal("9007199254740993", root["numericIdHighWaterMark"]!.GetValue<string>());
        Assert.Equal(9007199254740993L, MapDocumentFile.LoadText(json).Placements[0].NumericId);
        doc.Placements.Clear();
        Assert.Equal(9007199254740994L, MapNumericIds.Allocate(doc));
        doc.NumericIdHighWaterMark = long.MaxValue;
        Assert.Throws<OverflowException>(() => MapNumericIds.Allocate(doc));
        Assert.Equal(long.MaxValue, doc.NumericIdHighWaterMark);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("9223372036854775808")]
    [InlineData("")]
    [InlineData("1\0")]
    [InlineData("١")]
    public void NativeId_InvalidDecimalStringsRefuse(string value)
    {
        var root = Root();
        root["placements"]![0]!["numericId"] = value;
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
        if (value == "0") return;
        root = Root();
        root["numericIdHighWaterMark"] = value;
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
    }

    [Fact]
    public void NativeId_NumericJsonAndFailedReservationDoNotAllocate()
    {
        var doc = Load();
        var root = Root();
        root["placements"]![0]!["numericId"] = 12;
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
        MapNumericIds.Reserve(doc, new[] { 11L });
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc, new[] { 12L, 12L }));
        Assert.Equal(11L, doc.NumericIdHighWaterMark);
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc, new[] { 12L, 0L }));
        Assert.Equal(11L, doc.NumericIdHighWaterMark);
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc, new[] { 12L, -1L }));
        Assert.Equal(11L, doc.NumericIdHighWaterMark);
        Assert.Equal(12L, MapNumericIds.Allocate(doc));
        doc.NumericIdHighWaterMark = 0;
        doc.Placements[0].NumericId = 11;
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc));
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Allocate(doc));
        Assert.Equal(0L, doc.NumericIdHighWaterMark);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void HighWaterMark_RejectsNonStringTokens(string json)
    {
        var root = Root();
        root["numericIdHighWaterMark"] = JsonNode.Parse(json);
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
    }

    [Fact]
    public void Reservation_UsesMaximumRegardlessOfPlacementOrderAndRetainsTombstones()
    {
        var doc = Load();
        doc.Placements[0].NumericId = 40;
        doc.Placements.Add(new MapPlacement { Id = "other", Kind = "tree", NumericId = 3 });
        MapNumericIds.Reserve(doc, new[] { 7L, 2L });
        Assert.Equal(40L, doc.NumericIdHighWaterMark);
        doc.Placements.Reverse();
        MapNumericIds.Reserve(doc, Array.Empty<long>());
        Assert.Equal(41L, MapNumericIds.Allocate(doc));
        doc.Placements.Clear();
        MapNumericIds.Reserve(doc, new[] { 5L });
        Assert.Equal(42L, MapNumericIds.Allocate(doc));
    }

    [Fact]
    public void Reservation_MaximumRoundtripsAndExhaustsWithoutMutation()
    {
        var doc = Load();
        MapNumericIds.Reserve(doc, new[] { long.MaxValue });
        doc.Placements[0].NumericId = long.MaxValue;
        var loaded = MapDocumentFile.LoadText(MapDocumentFile.SaveText(doc));
        Assert.Equal(long.MaxValue, loaded.NumericIdHighWaterMark);
        Assert.Equal(long.MaxValue, loaded.Placements[0].NumericId);
        Assert.Throws<OverflowException>(() => MapNumericIds.Allocate(loaded));
        Assert.Equal(long.MaxValue, loaded.NumericIdHighWaterMark);
    }

    [Fact]
    public void DuplicatePlacements_RefuseLoadSaveAndAllocationWithoutMutation()
    {
        var doc = Load();
        doc.NumericIdHighWaterMark = 7;
        doc.Placements[0].NumericId = 7;
        doc.Placements.Add(new MapPlacement { Id = "other", Kind = "tree", NumericId = 7 });
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.SaveText(doc));
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc, new[] { 20L }));
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Allocate(doc));
        Assert.Equal(7L, doc.NumericIdHighWaterMark);
        var root = Root();
        root["numericIdHighWaterMark"] = "7";
        var placements = root["placements"]!.AsArray();
        placements[0]!["numericId"] = "7";
        var other = placements[0]!.DeepClone();
        other["id"] = "other";
        placements.Add(other);
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
    }

    [Fact]
    public void HighWaterMark_BelowPlacementRefusesLoad_ZeroAndNullPlacementRemainValid()
    {
        var root = Root();
        root["numericIdHighWaterMark"] = "0";
        root["placements"]![0]!["numericId"] = null;
        Assert.Null(MapDocumentFile.LoadText(root.ToJsonString()).Placements[0].NumericId);
        root["placements"]![0]!["numericId"] = "1";
        Assert.Throws<MapDocumentException>(() => MapDocumentFile.LoadText(root.ToJsonString()));
        root["numericIdHighWaterMark"] = "1";
        Assert.Equal(1L, MapDocumentFile.LoadText(root.ToJsonString()).Placements[0].NumericId);
    }

    [Theory]
    [InlineData(-1L, 4L)]
    [InlineData(3L, 0L)]
    [InlineData(3L, -4L)]
    public void Reservation_InvalidDocumentLeavesEveryFieldUnchanged(long mark, long id)
    {
        var doc = Load();
        doc.NumericIdHighWaterMark = mark;
        doc.Placements[0].NumericId = id;
        Assert.Throws<MapDocumentException>(() => MapNumericIds.Reserve(doc, new[] { 20L }));
        Assert.Equal(mark, doc.NumericIdHighWaterMark);
        Assert.Equal(id, doc.Placements[0].NumericId);
        Assert.Equal("old-inn", Assert.Single(doc.Placements).Id);
    }

    [Fact]
    public void DecimalFields_KeepCaseInsensitiveNamesAndOtherNumberTypes()
    {
        var root = Root();
        root.Remove("numericIdHighWaterMark");
        root["NUMERICIDHIGHWATERMARK"] = "12";
        root["placements"]![0]!["NUMERICID"] = "12";
        var doc = MapDocumentFile.LoadText(root.ToJsonString());
        Assert.Equal(12L, doc.Placements[0].NumericId);
        Assert.Equal(12L, doc.NumericIdHighWaterMark);
        var saved = JsonNode.Parse(MapDocumentFile.SaveText(doc))!;
        Assert.Equal(4, saved["formatVersion"]!.GetValue<int>());
        Assert.Equal(7, saved["terrain"]!["seed"]!.GetValue<int>());
    }

    static MapDocument Load() => MapDocumentFile.LoadText(NativeFixtures.AnalyticV3Json());
    static JsonObject Root() => JsonNode.Parse(MapDocumentFile.SaveText(Load()))!.AsObject();
}
