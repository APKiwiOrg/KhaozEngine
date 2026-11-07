using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SurfaceCodecAdmissionRefusalTests
{
    [Fact]
    public void Encode_forwards_the_first_local_rule_and_the_complete_ordered_findings()
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 1));
        patch.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 64));
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, ex.Reason);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal("EdgeSubdivisions[0].Segments", ex.FieldPath);
        Assert.Equal(0, ex.Index);
        Assert.Equal(0, ex.CellX);
        Assert.Equal(0, ex.CellZ);
        Assert.Equal(MapCellEdge.West, ex.Edge);
        Assert.Equal(1L, ex.ActualInteger);
        Assert.Equal(2L, ex.MinimumInteger);
        Assert.Equal(64L, ex.MaximumInteger);
        Assert.NotNull(ex.Findings);
        Assert.Equal(new[] { MapDocumentRefusal.ValueOutsideRange, MapDocumentRefusal.IdentityDuplicate }, ex.Findings.Select(f => f.Reason));
        Assert.Equal(2, patch.EdgeSubdivisions.Count);
        patch.EdgeSubdivisions.Clear();
        Assert.Equal(2, ex.Findings.Count);
        Assert.Empty(RoundTrip(patch).EdgeSubdivisions);
    }

    [Theory]
    [InlineData("null", MapDocumentRefusal.SchemaValueRequired)]
    [InlineData("duplicate", MapDocumentRefusal.IdentityDuplicate)]
    [InlineData("type", MapDocumentRefusal.OperationKindUnsupported)]
    public void Encode_record_admission_identifies_the_refused_item(string fault, MapDocumentRefusal reason)
    {
        var patch = SurfacePatchFixtures.Row(1);
        MapHorizontalOpening valid = new("opening", patch.Key, new[] { 0 });
        patch.Records.Add(fault == "null" ? null! : fault == "type" ? new FutureRecord("opening") : valid);
        if (fault == "duplicate") patch.Records.Add(valid);
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        Assert.Equal(reason, ex.Reason);
        if (fault == "null") Assert.Equal("records[0]", ex.FieldPath);
        else Assert.Equal("opening", ex.RecordId);
        if (fault == "duplicate") Assert.Equal(1, ex.Index);
        if (fault == "type") Assert.Equal(typeof(FutureRecord), ex.RecordType);
        Assert.Equal(fault == "duplicate" ? 2 : 1, patch.Records.Count);
        patch.Records.Clear();
        patch.Records.Add(valid);
        Assert.Equal("opening", Assert.Single(RoundTrip(patch).Records).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Record_text_identifies_the_required_field(bool decode)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Records.Add(new MapHorizontalOpening("opening", patch.Key, new[] { 0 }));
        JsonObject json = Payload(patch);
        MapDocumentException ex;
        if (decode)
        {
            json["records"]![0]!["id"] = " ";
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records[0] = patch.Records[0] with { Id = " " };
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(MapDocumentRefusal.SchemaValueRequired, ex.Reason);
        Assert.Equal("id", ex.FieldPath);
        Assert.Equal(" ", ex.ActualText);
        json["records"]![0]!["id"] = "opening";
        patch.Records[0] = patch.Records[0] with { Id = "opening" };
        Assert.Equal("opening", Assert.Single(decode ? Decode(json, patch.Key).Records : RoundTrip(patch).Records).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Decode_record_type_distinguishes_missing_from_unsupported(bool missing)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Records.Add(new MapHorizontalOpening("opening", patch.Key, new[] { 0 }));
        JsonObject json = Payload(patch);
        json["records"]![0]!["type"] = missing ? null : "future";
        var ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        Assert.Equal(missing ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.OperationKindUnsupported, ex.Reason);
        Assert.Equal("type", ex.FieldPath);
        if (!missing)
        {
            Assert.Equal("future", ex.ActualText);
            Assert.Equal("opening", ex.RecordId);
        }
        json["records"]![0]!["type"] = "opening";
        Assert.Equal("opening", Assert.Single(Decode(json, patch.Key).Records).Id);
    }

    [Theory]
    [InlineData(false, 1, 2)]
    [InlineData(false, 4098, 4097)]
    [InlineData(true, 1, 2)]
    public void Chain_cardinality_identifies_its_inclusive_domain(bool decode, int refused, int accepted)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Records.Add(Chain(accepted));
        JsonObject json = Payload(patch);
        MapDocumentException ex;
        if (decode)
        {
            json["records"]![0]!["vertices"]!.AsArray().RemoveAt(1);
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records[0] = Chain(refused);
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, ex.Reason);
        Assert.Equal("vertices", ex.FieldPath);
        Assert.Equal("chain", ex.RecordId);
        Assert.Equal((long)refused, ex.ActualCount);
        Assert.Equal(2L, ex.MinimumInteger);
        Assert.Equal(4097L, ex.MaximumInteger);
        patch.Records[0] = Chain(accepted);
        Assert.Equal(accepted, Assert.IsType<MapBoundaryChain>(Assert.Single(RoundTrip(patch).Records)).Vertices.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prism_version_retains_the_submitted_and_supported_versions(bool decode)
    {
        var patch = SurfacePatchFixtures.Row(1);
        var prism = new MapPrismDomain("prism", 1, new("room", patch.Key), new("outside", patch.Key), "", "storey",
            new(MapLatticeFrame.ImportedMetreCentimetre, new[] { new MapWaterRect(0, 0, 1, 1) }, Array.Empty<MapWaterRing>()), 0, 1);
        patch.Records.Add(prism);
        JsonObject json = Payload(patch);
        MapDocumentException ex;
        if (decode)
        {
            json["records"]![0]!["payloadVersion"] = 2;
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records[0] = prism with { PayloadVersion = 2 };
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(MapDocumentRefusal.SchemaPayloadVersionUnsupported, ex.Reason);
        Assert.Equal("prism", ex.RecordId);
        Assert.Equal("payloadVersion", ex.FieldPath);
        Assert.Equal(2, ex.ActualVersion);
        Assert.Equal(new[] { 1 }, ex.SupportedVersions);
        patch.Records[0] = prism;
        Assert.Equal(1, Assert.IsType<MapPrismDomain>(Assert.Single(RoundTrip(patch).Records)).PayloadVersion);
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(false, 4097, 4096)]
    [InlineData(true, 0, 1)]
    public void Slot_cardinality_admits_the_exact_endpoints(bool decode, int refused, int accepted)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Records.Add(new MapHorizontalOpening("opening", patch.Key, Enumerable.Range(0, accepted).ToArray()));
        JsonObject json = Payload(patch);
        MapDocumentException ex;
        if (decode)
        {
            json["records"]![0]!["slotCells"] = new JsonArray();
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records[0] = new MapHorizontalOpening("opening", patch.Key, Enumerable.Range(0, refused).ToArray());
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(MapDocumentRefusal.ValueOutsideRange, ex.Reason);
        Assert.Equal("slotCells", ex.FieldPath);
        Assert.Equal((long)refused, ex.ActualCount);
        Assert.Equal(1L, ex.MinimumInteger);
        Assert.Equal(4096L, ex.MaximumInteger);
        patch.Records[0] = new MapHorizontalOpening("opening", patch.Key, Enumerable.Range(0, accepted).ToArray());
        Assert.Equal(accepted, Assert.IsType<MapHorizontalOpening>(Assert.Single(RoundTrip(patch).Records)).SlotCells.Count);
    }

    [Theory]
    [InlineData(false, -1, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(true, 4096, MapDocumentRefusal.ValueOutsideRange)]
    [InlineData(false, 0, MapDocumentRefusal.OrderInvalid)]
    [InlineData(true, 0, MapDocumentRefusal.OrderInvalid)]
    [InlineData(false, 1, MapDocumentRefusal.OrderInvalid)]
    [InlineData(true, 1, MapDocumentRefusal.OrderInvalid)]
    public void Slot_values_distinguish_domain_from_strict_order(bool decode, int value, MapDocumentRefusal reason)
    {
        var patch = SurfacePatchFixtures.Row(1);
        int[] refused = reason == MapDocumentRefusal.OrderInvalid ? new[] { 1, value } : new[] { value };
        int[] accepted = reason == MapDocumentRefusal.OrderInvalid ? new[] { 0, 1 } : new[] { value < 0 ? 0 : 4095 };
        patch.Records.Add(new MapHorizontalOpening("opening", patch.Key, accepted));
        JsonObject json = Payload(patch);
        MapDocumentException ex;
        if (decode)
        {
            json["records"]![0]!["slotCells"] = new JsonArray(refused.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records[0] = new MapHorizontalOpening("opening", patch.Key, refused);
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(reason, ex.Reason);
        Assert.Equal("slotCells", ex.FieldPath);
        Assert.Equal((long)value, ex.ActualInteger);
        Assert.Equal(reason == MapDocumentRefusal.OrderInvalid ? 1 : 0, ex.Index);
        if (reason == MapDocumentRefusal.OrderInvalid) Assert.Equal(1L, ex.PreviousInteger);
        else
        {
            Assert.Equal(0L, ex.MinimumInteger);
            Assert.Equal(4095L, ex.MaximumInteger);
        }
        patch.Records[0] = new MapHorizontalOpening("opening", patch.Key, accepted);
        Assert.Equal(accepted, Assert.IsType<MapHorizontalOpening>(Assert.Single(RoundTrip(patch).Records)).SlotCells);
    }

    [Theory]
    [InlineData(false, "chain", typeof(MapChainKind), 1)]
    [InlineData(true, "chain", typeof(MapChainKind), 1)]
    [InlineData(false, "strip", typeof(MapStripFacing), 2)]
    [InlineData(true, "strip", typeof(MapStripFacing), 2)]
    [InlineData(false, "space", typeof(MapSpaceKind), 2)]
    [InlineData(true, "space", typeof(MapSpaceKind), 2)]
    [InlineData(false, "side", typeof(MapSide), 1)]
    [InlineData(true, "side", typeof(MapSide), 1)]
    [InlineData(false, "bound", typeof(MapBoundKind), 4)]
    [InlineData(true, "bound", typeof(MapBoundKind), 4)]
    public void Topology_enum_retains_its_type_field_and_numeric_value(bool decode, string kind, Type enumType, int accepted)
    {
        var patch = SurfacePatchFixtures.Row(1);
        patch.Records.Add(EnumRecord(kind, accepted, patch.Key));
        JsonObject json = Payload(patch);
        string field = kind == "strip" ? "facing" : kind == "side" ? "side" : "kind";
        MapDocumentException ex;
        if (decode)
        {
            JsonNode record = json["records"]![0]!;
            JsonNode target = kind == "side" ? record["walls"]![0]! : kind == "bound" ? record["lower"]! : record;
            target[field] = 255;
            ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        }
        else
        {
            patch.Records[0] = EnumRecord(kind, 255, patch.Key);
            ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.Encode(patch));
        }
        Assert.Equal(MapDocumentRefusal.SchemaEnumUnsupported, ex.Reason);
        Assert.Equal(enumType, ex.EnumType);
        Assert.Equal(field, ex.FieldPath);
        Assert.Equal(255L, ex.ActualInteger);
        patch.Records[0] = EnumRecord(kind, accepted, patch.Key);
        Assert.Equal("record", Assert.Single(RoundTrip(patch).Records).Id);
    }

    [Fact]
    public void Decode_rectangle_admission_preserves_patch_and_dimensions()
    {
        var patch = SurfacePatchFixtures.Row(1);
        JsonObject json = Payload(patch);
        json["cellMinX"] = 64;
        var ex = Assert.Throws<MapDocumentException>(() => Decode(json, patch.Key));
        Assert.Equal(MapDocumentRefusal.BoundsInvalid, ex.Reason);
        Assert.Equal(patch.Key, ex.Patch);
        Assert.Equal("rectangle", ex.FieldPath);
        Assert.Equal(1, ex.Width);
        Assert.Equal(1, ex.Depth);
        json["cellMinX"] = 63;
        Assert.Equal(63, Decode(json, patch.Key).CellMinX);
    }

    [Fact]
    public void Trailing_value_guard_identifies_its_payload_without_changing_parser_admission()
    {
        var key = new MapPatchKey("r", 0, 0);
        // The JSON reader currently rejects a second root before returning true. Inject that exact guard value.
        var ex = Assert.Throws<MapDocumentException>(() => MapSurfacePatchCodec.RequireNoTrailingValueForTests(true, key));
        Assert.Equal(MapDocumentRefusal.SurfacePatchTrailingBytes, ex.Reason);
        Assert.Equal(key, ex.Patch);
        MapSurfacePatchCodec.RequireNoTrailingValueForTests(false, key);
        Assert.Equal(key, RoundTrip(SurfacePatchFixtures.Row(1)).Key);
    }

    static MapBoundaryChain Chain(int count) => new("chain", MapChainKind.Authored, null,
        Enumerable.Range(0, count).Select(x => new MapChainVertex(new("r", MapLatticeAddress.Corner(x, 0)), 0)).ToArray());
    static MapTopologyRecord EnumRecord(string kind, int value, MapPatchKey key) => kind switch
    {
        "chain" => Chain(2) with { Id = "record", Kind = (MapChainKind)value },
        "strip" => new MapWallStrip("record", new("lower", key), new("upper", key), (MapStripFacing)value, 1),
        "space" => new MapSpaceDoc("record", (MapSpaceKind)value, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()),
        "side" => new MapSpaceDoc("record", MapSpaceKind.Cave, null, null, Array.Empty<string>(),
            new[] { new MapBoundaryRef(new("wall", key), (MapSide)value) }, Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()),
        _ => new MapSpaceFootprint("record", new("room", key), key, new[] { 0 },
            new((MapBoundKind)value, "r", null), new(MapBoundKind.OpenTop, null, null)),
    };
    static JsonObject Payload(MapSurfacePatch patch) => JsonNode.Parse(MapSurfacePatchCodec.Encode(patch))!.AsObject();
    static MapSurfacePatch Decode(JsonObject json, MapPatchKey key) => MapSurfacePatchCodec.Decode(Encoding.UTF8.GetBytes(json.ToJsonString()), key);
    static MapSurfacePatch RoundTrip(MapSurfacePatch patch) => MapSurfacePatchCodec.Decode(MapSurfacePatchCodec.Encode(patch), patch.Key);
    sealed record FutureRecord(string Id) : MapTopologyRecord(Id);
}
