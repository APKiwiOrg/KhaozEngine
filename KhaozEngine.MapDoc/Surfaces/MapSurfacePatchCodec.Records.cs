using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Surfaces;

public static partial class MapSurfacePatchCodec
{
    static void WriteRecords(Utf8JsonWriter writer, IReadOnlyList<MapTopologyRecord> records)
    {
        // Every supported record needs more than 24 encoded bytes, even with minimal empty collections.
        if (records.Count > MaxEncodedBytes / 24) throw Limit();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            MapTopologyRecord record = records[i];
            if (record is null)
                throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "invalid record") { FieldPath = $"records[{i}]", Index = i };
            RequireText(record.Id, "id");
            if (!ids.Add(record.Id))
                throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, "duplicate record id") { RecordId = record.Id, FieldPath = "id", Index = i };
        }
        writer.WriteStartArray("records");
        foreach (MapTopologyRecord record in records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            WriteRecord(writer, record);
            FlushBudget(writer);
        }
        writer.WriteEndArray();
    }

    static void WriteRecord(Utf8JsonWriter w, MapTopologyRecord record)
    {
        string type = record switch
        {
            MapSurfaceSeam => "seam",
            MapBoundaryChain => "chain",
            MapWallStrip => "strip",
            MapCavePortal => "portal",
            MapHorizontalOpening => "opening",
            MapVerticalLink => "link",
            MapSpaceDoc => "space",
            MapSpaceFootprint => "footprint",
            MapPrismDomain => "prism",
            _ => throw new MapDocumentException(MapDocumentRefusal.OperationKindUnsupported, "unknown topology record type")
            { RecordId = record.Id, RecordType = record.GetType(), FieldPath = "type" },
        };
        w.WriteStartObject(); w.WriteString("type", type); Text(w, "id", record.Id);
        switch (record)
        {
            case MapSurfaceSeam seam:
                WireCount(seam.Pairs.Count, 64);
                w.WritePropertyName("first"); WriteEdge(w, seam.First);
                w.WritePropertyName("second"); WriteEdge(w, seam.Second);
                WriteArray(w, "pairs", seam.Pairs.OrderBy(p => p.First.SurfaceId, StringComparer.Ordinal)
                    .ThenBy(p => p.First.Address).ThenBy(p => p.Second.SurfaceId, StringComparer.Ordinal).ThenBy(p => p.Second.Address),
                    static (writer, pair) =>
                    {
                        writer.WriteStartObject(); writer.WritePropertyName("first"); WriteVertex(writer, pair.First);
                        writer.WritePropertyName("second"); WriteVertex(writer, pair.Second); writer.WriteEndObject();
                    });
                break;
            case MapBoundaryChain chain:
                if (chain.Vertices.Count is < 2 or > 4097)
                    throw new MapDocumentException(MapDocumentRefusal.ValueOutsideRange, "chain requires 2 to 4097 vertices")
                    { RecordId = chain.Id, FieldPath = "vertices", ActualCount = chain.Vertices.Count, MinimumInteger = 2, MaximumInteger = 4097 };
                EnumValue(chain.Kind, "kind"); w.WriteNumber("kind", (byte)chain.Kind);
                w.WritePropertyName("sourcePatch");
                if (chain.SourcePatch is { } source) WriteKey(w, source); else w.WriteNullValue();
                WriteArray(w, "vertices", chain.Vertices, static (writer, vertex) =>
                {
                    writer.WriteStartObject(); writer.WritePropertyName("vertex"); WriteVertex(writer, vertex.Vertex);
                    if (vertex.HeightUnits is { } height) writer.WriteNumber("heightUnits", height); else writer.WriteNull("heightUnits");
                    writer.WriteEndObject();
                });
                break;
            case MapWallStrip strip:
                RefProperty(w, "lowerChain", strip.LowerChain); RefProperty(w, "upperChain", strip.UpperChain);
                EnumValue(strip.Facing, "facing"); w.WriteNumber("facing", (byte)strip.Facing); w.WriteNumber("materialId", strip.MaterialId);
                break;
            case MapCavePortal portal:
                WireCount(portal.Interval.Count, 40);
                RefProperty(w, "fromSpace", portal.FromSpace); RefProperty(w, "toSpace", portal.ToSpace);
                WriteArray(w, "interval", portal.Interval, WriteVertex);
                RefProperty(w, "bandBottom", portal.BandBottom); RefProperty(w, "bandTop", portal.BandTop);
                break;
            case MapHorizontalOpening opening:
                w.WritePropertyName("patch"); WriteKey(w, opening.Patch); Slots(w, opening.SlotCells);
                break;
            case MapVerticalLink link:
                RefProperty(w, "upperSpace", link.UpperSpace); RefProperty(w, "lowerSpace", link.LowerSpace);
                Refs(w, "openings", link.Openings); Refs(w, "portals", link.Portals); Refs(w, "geometryOwners", link.GeometryOwners);
                break;
            case MapSpaceDoc space:
                WireCount(space.DomainTags.Count, 3);
                EnumValue(space.Kind, "kind"); w.WriteNumber("kind", (byte)space.Kind);
                RefProperty(w, "parent", space.Parent); RefProperty(w, "aliasOf", space.AliasOf);
                WriteArray(w, "domainTags", space.DomainTags.OrderBy(t => t, StringComparer.Ordinal), static (writer, tag) =>
                {
                    RequireText(tag, "domainTags"); writer.WriteStringValue(tag);
                });
                Boundaries(w, "walls", space.Walls); Boundaries(w, "portals", space.Portals); Refs(w, "links", space.Links);
                break;
            case MapPrismDomain prism:
                if (prism.PayloadVersion != 1)
                    throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "unsupported prism payload version")
                    { RecordId = prism.Id, FieldPath = "payloadVersion", ActualVersion = prism.PayloadVersion, SupportedVersions = new[] { 1 } };
                w.WriteNumber("payloadVersion", prism.PayloadVersion);
                RefProperty(w, "space", prism.Space); RefProperty(w, "parent", prism.Parent);
                w.WriteString("instanceId", prism.InstanceId); Text(w, "storeyId", prism.StoreyId);
                w.WritePropertyName("footprint");
                MapWaterJson.WriteDomain(w, prism.Footprint, $"prism '{prism.Id}' footprint");
                w.WriteNumber("lowerY", prism.LowerY); w.WriteNumber("upperY", prism.UpperY);
                break;
            case MapSpaceFootprint footprint:
                RefProperty(w, "space", footprint.Space); w.WritePropertyName("lattice"); WriteKey(w, footprint.Lattice);
                Slots(w, footprint.SlotCells); w.WritePropertyName("lower"); WriteBound(w, footprint.Lower);
                w.WritePropertyName("upper"); WriteBound(w, footprint.Upper);
                break;
        }
        w.WriteEndObject();
    }

    static MapTopologyRecord ReadRecord(JsonElement r)
    {
        string type = r.GetProperty("type").GetString()
            ?? throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "record type is required") { FieldPath = "type" };
        string id = r.GetProperty("id").GetString()!; RequireText(id, "id");
        switch (type)
        {
            case "seam":
                Members(r, "type", "id", "first", "second", "pairs");
                return new MapSurfaceSeam(id, ReadEdge(r.GetProperty("first")), ReadEdge(r.GetProperty("second")),
                    ReadList(r.GetProperty("pairs"), static pair =>
                    {
                        Members(pair, "first", "second");
                        return (ReadVertex(pair.GetProperty("first")), ReadVertex(pair.GetProperty("second")));
                    }));
            case "chain":
                Members(r, "type", "id", "kind", "sourcePatch", "vertices");
                JsonElement vertices = Array(r.GetProperty("vertices"), 4097, "vertices");
                if (vertices.GetArrayLength() < 2)
                    throw new MapDocumentException(MapDocumentRefusal.ValueOutsideRange, "chain requires 2 to 4097 vertices")
                    { RecordId = id, FieldPath = "vertices", ActualCount = vertices.GetArrayLength(), MinimumInteger = 2, MaximumInteger = 4097 };
                JsonElement source = r.GetProperty("sourcePatch");
                return new MapBoundaryChain(id, ReadEnum<MapChainKind>(r.GetProperty("kind"), "kind"),
                    source.ValueKind == JsonValueKind.Null ? null : ReadKey(source), ReadList(vertices, static v =>
                    {
                        Members(v, "vertex", "heightUnits"); JsonElement height = v.GetProperty("heightUnits");
                        return new MapChainVertex(ReadVertex(v.GetProperty("vertex")), height.ValueKind == JsonValueKind.Null ? null : height.GetInt32());
                    }));
            case "strip":
                Members(r, "type", "id", "lowerChain", "upperChain", "facing", "materialId");
                return new MapWallStrip(id, ReadRef(r.GetProperty("lowerChain")), ReadRef(r.GetProperty("upperChain")),
                    ReadEnum<MapStripFacing>(r.GetProperty("facing"), "facing"), r.GetProperty("materialId").GetUInt16());
            case "portal":
                Members(r, "type", "id", "fromSpace", "toSpace", "interval", "bandBottom", "bandTop");
                return new MapCavePortal(id, ReadRef(r.GetProperty("fromSpace")), ReadRef(r.GetProperty("toSpace")),
                    ReadList(r.GetProperty("interval"), ReadVertex), ReadRef(r.GetProperty("bandBottom")), OptionalRef(r.GetProperty("bandTop")));
            case "opening":
                Members(r, "type", "id", "patch", "slotCells");
                return new MapHorizontalOpening(id, ReadKey(r.GetProperty("patch")), ReadSlots(r.GetProperty("slotCells")));
            case "link":
                Members(r, "type", "id", "upperSpace", "lowerSpace", "openings", "portals", "geometryOwners");
                return new MapVerticalLink(id, ReadRef(r.GetProperty("upperSpace")), ReadRef(r.GetProperty("lowerSpace")),
                    ReadList(r.GetProperty("openings"), ReadRef), ReadList(r.GetProperty("portals"), ReadRef),
                    ReadList(r.GetProperty("geometryOwners"), ReadRef));
            case "space":
                Members(r, "type", "id", "kind", "parent", "aliasOf", "domainTags", "walls", "portals", "links");
                return new MapSpaceDoc(id, ReadEnum<MapSpaceKind>(r.GetProperty("kind"), "kind"), OptionalRef(r.GetProperty("parent")),
                    OptionalRef(r.GetProperty("aliasOf")), ReadList(r.GetProperty("domainTags"), static tag =>
                    {
                        string text = tag.GetString()!; RequireText(text, "domainTags"); return text;
                    }), ReadList(r.GetProperty("walls"), ReadBoundary), ReadList(r.GetProperty("portals"), ReadBoundary),
                    ReadList(r.GetProperty("links"), ReadRef));
            case "prism":
                Members(r, "type", "id", "payloadVersion", "space", "parent", "instanceId", "storeyId", "footprint", "lowerY", "upperY");
                if (r.GetProperty("payloadVersion").GetInt32() != 1)
                    throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "unsupported prism payload version")
                    { RecordId = id, FieldPath = "payloadVersion", ActualVersion = r.GetProperty("payloadVersion").GetInt32(), SupportedVersions = new[] { 1 } };
                return new MapPrismDomain(id, 1, ReadRef(r.GetProperty("space")), ReadRef(r.GetProperty("parent")),
                    r.GetProperty("instanceId").GetString()!, r.GetProperty("storeyId").GetString()!,
                    MapWaterJson.ReadDomain(r.GetProperty("footprint")), r.GetProperty("lowerY").GetSingle(), r.GetProperty("upperY").GetSingle());
            case "footprint":
                Members(r, "type", "id", "space", "lattice", "slotCells", "lower", "upper");
                return new MapSpaceFootprint(id, ReadRef(r.GetProperty("space")), ReadKey(r.GetProperty("lattice")),
                    ReadSlots(r.GetProperty("slotCells")), ReadBound(r.GetProperty("lower")), ReadBound(r.GetProperty("upper")));
            default: throw new MapDocumentException(MapDocumentRefusal.OperationKindUnsupported, "unknown topology record type")
                { RecordId = id, FieldPath = "type", ActualText = type };
        }
    }

    static void WriteVertex(Utf8JsonWriter w, MapLatticeVertex vertex)
    {
        vertex.Address.RequireValid(); w.WriteStartObject(); Text(w, "surfaceId", vertex.SurfaceId);
        w.WriteStartObject("address"); w.WriteString("x", Decimal(vertex.Address.X)); w.WriteString("z", Decimal(vertex.Address.Z));
        w.WriteNumber("d", vertex.Address.Denominator); w.WriteEndObject(); w.WriteEndObject();
    }
    static MapLatticeVertex ReadVertex(JsonElement v)
    {
        Members(v, "surfaceId", "address"); string id = v.GetProperty("surfaceId").GetString()!; RequireText(id, "surfaceId");
        JsonElement a = v.GetProperty("address"); Members(a, "x", "z", "d");
        return new(id, MapLatticeAddress.Create(Long(a.GetProperty("x")), Long(a.GetProperty("z")), a.GetProperty("d").GetInt32()));
    }
    static void WriteEdge(Utf8JsonWriter w, MapSurfaceEdgeRef edge)
    {
        w.WriteStartObject(); w.WritePropertyName("patch"); WriteKey(w, edge.Patch);
        w.WritePropertyName("from"); WriteVertex(w, edge.From); w.WritePropertyName("to"); WriteVertex(w, edge.To); w.WriteEndObject();
    }
    static MapSurfaceEdgeRef ReadEdge(JsonElement e)
    {
        Members(e, "patch", "from", "to");
        return new(ReadKey(e.GetProperty("patch")), ReadVertex(e.GetProperty("from")), ReadVertex(e.GetProperty("to")));
    }
    static void RefProperty(Utf8JsonWriter w, string name, MapRecordRef? reference)
    {
        w.WritePropertyName(name); if (reference is null) w.WriteNullValue(); else WriteRef(w, reference);
    }
    static void WriteRef(Utf8JsonWriter w, MapRecordRef reference)
    {
        w.WriteStartObject(); Text(w, "id", reference.Id); w.WritePropertyName("anchor"); WriteKey(w, reference.Anchor); w.WriteEndObject();
    }
    static MapRecordRef ReadRef(JsonElement r)
    {
        Members(r, "id", "anchor"); string id = r.GetProperty("id").GetString()!; RequireText(id, "id");
        return new(id, ReadKey(r.GetProperty("anchor")));
    }
    static MapRecordRef? OptionalRef(JsonElement r) => r.ValueKind == JsonValueKind.Null ? null : ReadRef(r);
    static IEnumerable<MapRecordRef> OrderedRefs(IReadOnlyList<MapRecordRef> refs)
    {
        WireCount(refs.Count, 32);
        return refs.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Anchor);
    }
    static void Refs(Utf8JsonWriter w, string name, IReadOnlyList<MapRecordRef> refs) => WriteArray(w, name, OrderedRefs(refs), WriteRef);
    static void Boundaries(Utf8JsonWriter w, string name, IReadOnlyList<MapBoundaryRef> boundaries)
    {
        WireCount(boundaries.Count, 40);
        WriteArray(w, name, boundaries.OrderBy(b => b.Record.Id, StringComparer.Ordinal).ThenBy(b => b.Record.Anchor).ThenBy(b => b.Side),
            static (writer, b) =>
            {
                EnumValue(b.Side, "side"); writer.WriteStartObject(); RefProperty(writer, "record", b.Record);
                writer.WriteNumber("side", (byte)b.Side); writer.WriteEndObject();
            });
    }
    static MapBoundaryRef ReadBoundary(JsonElement b)
    {
        Members(b, "record", "side"); return new(ReadRef(b.GetProperty("record")), ReadEnum<MapSide>(b.GetProperty("side"), "side"));
    }
    static void Slots(Utf8JsonWriter w, IReadOnlyList<int> slots)
    {
        ValidateSlots(slots); WriteArray(w, "slotCells", slots, static (writer, cell) => writer.WriteNumberValue(cell));
    }
    static int[] ReadSlots(JsonElement cells)
    {
        int[] slots = ReadList(Array(cells, 4096, "slotCells"), static c => c.GetInt32()); ValidateSlots(slots); return slots;
    }
    static void ValidateSlots(IReadOnlyList<int> slots)
    {
        if (slots.Count is < 1 or > 4096)
            throw new MapDocumentException(MapDocumentRefusal.ValueOutsideRange, "invalid slotCells length")
            { FieldPath = "slotCells", ActualCount = slots.Count, MinimumInteger = 1, MaximumInteger = 4096 };
        int previous = -1;
        int index = 0;
        foreach (int cell in slots)
        {
            if (cell is < 0 or >= 4096 || cell <= previous)
                throw new MapDocumentException(cell is < 0 or >= 4096 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.OrderInvalid,
                    "invalid or duplicate slotCells order")
                {
                    FieldPath = "slotCells", Index = index, ActualInteger = cell, PreviousInteger = index > 0 ? previous : null,
                    MinimumInteger = cell is < 0 or >= 4096 ? 0 : null, MaximumInteger = cell is < 0 or >= 4096 ? 4095 : null,
                };
            previous = cell;
            index++;
        }
    }
    static void WriteBound(Utf8JsonWriter w, MapBoundRef bound)
    {
        EnumValue(bound.Kind, "kind"); w.WriteStartObject(); w.WriteNumber("kind", (byte)bound.Kind);
        if (bound.SurfaceId is null) w.WriteNull("surfaceId"); else Text(w, "surfaceId", bound.SurfaceId);
        RefProperty(w, "opening", bound.Opening); w.WriteEndObject();
    }
    static MapBoundRef ReadBound(JsonElement b)
    {
        Members(b, "kind", "surfaceId", "opening");
        return new(ReadEnum<MapBoundKind>(b.GetProperty("kind"), "kind"), b.GetProperty("surfaceId").GetString(), OptionalRef(b.GetProperty("opening")));
    }
    static T ReadEnum<T>(JsonElement value, string fieldPath) where T : struct, Enum
    {
        T result = (T)Enum.ToObject(typeof(T), value.GetByte()); EnumValue(result, fieldPath); return result;
    }
    static void EnumValue<T>(T value, string fieldPath) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new MapDocumentException(MapDocumentRefusal.SchemaEnumUnsupported, "invalid topology enum value")
            { EnumType = typeof(T), ActualInteger = Convert.ToInt64(value), FieldPath = fieldPath };
    }
    static void Text(Utf8JsonWriter w, string name, string value) { RequireText(value, name); w.WriteString(name, value); }
    static void RequireText(string value, string fieldPath)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "record text is required") { FieldPath = fieldPath, ActualText = value };
        if (value.Length > MaxEncodedBytes) throw Limit();
    }
    static T[] ReadList<T>(JsonElement array, Func<JsonElement, T> read)
    {
        _ = Array(array, MaxEncodedBytes / 2, "record list");
        var result = new T[array.GetArrayLength()];
        int i = 0; foreach (JsonElement item in array.EnumerateArray()) result[i++] = read(item);
        return result;
    }
    static void WriteArray<T>(Utf8JsonWriter w, string name, IEnumerable<T> values, Action<Utf8JsonWriter, T> write)
    {
        w.WriteStartArray(name); int count = 0;
        foreach (T value in values)
        {
            write(w, value);
            if (++count % 64 == 0) FlushBudget(w);
        }
        w.WriteEndArray(); FlushBudget(w);
    }
    // Conservative minimum wire bytes per valid item, excluding array separators and parent fields.
    // These checks precede LINQ buffering and do not restrict geometry that fits the payload budget.
    static void WireCount(int count, int minimumItemBytes)
    {
        if (count < 0 || checked((long)count * minimumItemBytes) > MaxEncodedBytes) throw Limit();
    }
    static void FlushBudget(Utf8JsonWriter w)
    {
        if (w.BytesCommitted + w.BytesPending > MaxEncodedBytes) throw Limit();
        w.Flush();
    }
}
