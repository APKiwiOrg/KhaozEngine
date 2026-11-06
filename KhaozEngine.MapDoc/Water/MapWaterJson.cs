using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Reads and writes the root <c>water</c> section through <see cref="MapWaterJson"/>, the one canonical
/// codec the monolithic file, the tiled manifest and every identity share.</summary>
public sealed class MapWaterSetJsonConverter : JsonConverter<MapWaterSet>
{
    public override MapWaterSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        try { return MapWaterJson.Read(json.RootElement); }
        catch (Exception ex) when (ex is MapDocumentException or InvalidOperationException or FormatException)
        {
            if (ex is MapDocumentException known) throw MapWaterJsonException.FromDocument(known, "water: " + ex.Message);
            throw new MapWaterJsonException(MapDocumentRefusal.SchemaMalformed, "water: " + ex.Message, ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, MapWaterSet value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        MapWaterJson.Write(writer, value);
    }
}

/// <summary>The canonical water codec: media by key and bodies by id, both ordinal, each record's members in a fixed
/// order, and rects, rings and covers in stored order. Every member is closed and required except a body's
/// <c>displayName</c>. Lattice coordinates and slots use <see cref="MapSignedDecimal"/> strings.</summary>
internal static class MapWaterJson
{
    internal static void Write(Utf8JsonWriter w, MapWaterSet water)
    {
        // Null entries refuse before ordering reads their keys.
        foreach (MapWaterMediumDoc medium in water.Media) Require(medium, "water medium", "media");
        foreach (MapWaterBodyDoc body in water.Bodies) Require(body, "water body", "bodies");
        w.WriteStartObject();
        w.WriteStartArray("media");
        foreach (MapWaterMediumDoc medium in water.Media.OrderBy(m => m.Key, StringComparer.Ordinal)) WriteMedium(w, medium);
        w.WriteEndArray();
        w.WriteStartArray("bodies");
        foreach (MapWaterBodyDoc body in water.Bodies.OrderBy(b => b.Id, StringComparer.Ordinal)) WriteBody(w, body);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static void WriteMedium(Utf8JsonWriter w, MapWaterMediumDoc medium)
    {
        Require(medium, "water medium", "media");
        w.WriteStartObject();
        w.WriteString("key", medium.Key);
        w.WriteString("kind", medium.Kind.ToString());
        w.WriteNumber("wadeSpeedScale", medium.WadeSpeedScale == 0f ? 0f : medium.WadeSpeedScale);
        w.WriteEndObject();
    }

    internal static void WriteBody(Utf8JsonWriter w, MapWaterBodyDoc body)
    {
        Require(body, "water body", "bodies");
        string label = $"water body '{body.Id}'";
        MapWaterDomainDoc domain = Require(body.Domain, label + " domain", "domain", body.Id);
        w.WriteStartObject();
        w.WriteString("id", body.Id);
        // Negative zero is the same level as zero, so it writes as zero and shares one identity.
        w.WriteNumber("surfaceY", body.SurfaceY == 0f ? 0f : body.SurfaceY);
        w.WriteString("medium", body.Medium);
        w.WriteString("renderMaterial", body.RenderMaterial);
        w.WritePropertyName("domain");
        WriteDomain(w, domain, label);
        w.WriteStartArray("covers");
        foreach (MapRecordRef cover in Require(body.Covers, label + " covers", "covers", body.Id))
        {
            Require(cover, label + " cover", "covers", body.Id);
            w.WriteStartObject();
            w.WriteString("id", cover.Id);
            w.WriteStartObject("anchor");
            w.WriteString("surfaceId", cover.Anchor.SurfaceId);
            w.WriteString("slotX", MapSignedDecimal.Format(cover.Anchor.SlotX));
            w.WriteString("slotZ", MapSignedDecimal.Format(cover.Anchor.SlotZ));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (body.DisplayName is not null) w.WriteString("displayName", body.DisplayName);
        w.WriteEndObject();
    }

    internal static void WriteDomain(Utf8JsonWriter w, MapWaterDomainDoc domain, string label = "water domain")
    {
        Require(domain, label, "domain");
        w.WriteStartObject();
        w.WritePropertyName("frame");
        WriteFrame(w, Require(domain.Frame, label + " frame", "frame"));
        w.WriteStartArray("rects");
        foreach (MapWaterRect rect in Require(domain.Rects, label + " rects", "rects"))
        {
            w.WriteStartObject();
            w.WriteString("minX", MapSignedDecimal.Format(rect.MinX));
            w.WriteString("minZ", MapSignedDecimal.Format(rect.MinZ));
            w.WriteString("maxX", MapSignedDecimal.Format(rect.MaxX));
            w.WriteString("maxZ", MapSignedDecimal.Format(rect.MaxZ));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("rings");
        foreach (MapWaterRing ring in Require(domain.Rings, label + " rings", "rings"))
        {
            w.WriteStartObject();
            w.WriteStartArray("vertices");
            foreach (MapLatticeAddress address in Require(Require(ring, label + " ring", "rings").Vertices, label + " ring vertices", "vertices"))
                WriteAddress(w, address);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static MapWaterSet Read(JsonElement json)
    {
        Members(json, "water", "$", ["media", "bodies"]);
        var water = new MapWaterSet();
        int index = 0;
        foreach (JsonElement medium in Items(json.GetProperty("media"), "water media", "$.media"))
        {
            string path = $"$.media[{index++}]";
            Members(medium, "water medium", path, ["key", "kind", "wadeSpeedScale"]);
            water.Media.Add(new MapWaterMediumDoc(Text(medium.GetProperty("key"), path + ".key"),
                Named<MapWaterMediumKind>(medium.GetProperty("kind"), path + ".kind"), Finite(medium.GetProperty("wadeSpeedScale"), path + ".wadeSpeedScale")));
        }
        index = 0;
        foreach (JsonElement body in Items(json.GetProperty("bodies"), "water bodies", "$.bodies")) water.Bodies.Add(ReadBody(body, $"$.bodies[{index++}]"));
        return water;
    }

    static MapWaterBodyDoc ReadBody(JsonElement body, string path)
    {
        Members(body, "water body", path, ["id", "surfaceY", "medium", "renderMaterial", "domain", "covers"], "displayName");
        MapWaterDomainDoc domain = ReadDomain(body.GetProperty("domain"), path + ".domain");
        MapRecordRef[] covers = Items(body.GetProperty("covers"), "water covers", path + ".covers").Select((cover, index) =>
        {
            string item = $"{path}.covers[{index}]";
            Members(cover, "water cover", item, ["id", "anchor"]);
            JsonElement anchor = cover.GetProperty("anchor");
            Members(anchor, "water cover anchor", item + ".anchor", ["surfaceId", "slotX", "slotZ"]);
            return new MapRecordRef(Text(cover.GetProperty("id"), item + ".id"), new MapPatchKey(Text(anchor.GetProperty("surfaceId"), item + ".anchor.surfaceId"),
                Long(anchor.GetProperty("slotX"), item + ".anchor.slotX"), Long(anchor.GetProperty("slotZ"), item + ".anchor.slotZ")));
        }).ToArray();
        return new MapWaterBodyDoc(Text(body.GetProperty("id"), path + ".id"), Finite(body.GetProperty("surfaceY"), path + ".surfaceY"),
            Text(body.GetProperty("medium"), path + ".medium"), Text(body.GetProperty("renderMaterial"), path + ".renderMaterial"),
            domain, covers,
            body.TryGetProperty("displayName", out JsonElement name) ? Text(name, path + ".displayName") : null);
    }

    internal static MapWaterDomainDoc ReadDomain(JsonElement domain, string path = "$")
    {
        Members(domain, "water domain", path, ["frame", "rects", "rings"]);
        MapWaterRect[] rects = Items(domain.GetProperty("rects"), "water rects", path + ".rects").Select((rect, index) =>
        {
            string item = $"{path}.rects[{index}]";
            Members(rect, "water rect", item, ["minX", "minZ", "maxX", "maxZ"]);
            return new MapWaterRect(Long(rect.GetProperty("minX"), item + ".minX"), Long(rect.GetProperty("minZ"), item + ".minZ"),
                Long(rect.GetProperty("maxX"), item + ".maxX"), Long(rect.GetProperty("maxZ"), item + ".maxZ"));
        }).ToArray();
        MapWaterRing[] rings = Items(domain.GetProperty("rings"), "water rings", path + ".rings").Select((ring, index) =>
        {
            string item = $"{path}.rings[{index}]";
            Members(ring, "water ring", item, ["vertices"]);
            return new MapWaterRing(Items(ring.GetProperty("vertices"), "water ring vertices", item + ".vertices")
                .Select((address, vertex) => ReadAddress(address, $"{item}.vertices[{vertex}]")).ToArray());
        }).ToArray();
        return new(ReadFrame(domain.GetProperty("frame"), path + ".frame"), rects, rings);
    }

    internal static void WriteFrame(Utf8JsonWriter w, MapLatticeFrame frame)
    {
        w.WriteStartObject();
        WriteRational(w, "cellUnitMetres", frame.CellUnitMetres);
        WriteRational(w, "heightUnitMetres", frame.HeightUnitMetres);
        w.WriteString("rowDirection", frame.RowDirection.ToString());
        w.WriteString("datum", frame.Datum.ToString());
        w.WriteEndObject();
    }

    static MapLatticeFrame ReadFrame(JsonElement frame, string path)
    {
        Members(frame, "water frame", path, ["cellUnitMetres", "heightUnitMetres", "rowDirection", "datum"]);
        return new MapLatticeFrame(ReadRational(frame.GetProperty("cellUnitMetres"), path + ".cellUnitMetres"), ReadRational(frame.GetProperty("heightUnitMetres"), path + ".heightUnitMetres"),
            Named<MapRowDirection>(frame.GetProperty("rowDirection"), path + ".rowDirection"), Named<MapHeightDatum>(frame.GetProperty("datum"), path + ".datum"));
    }

    static void WriteRational(Utf8JsonWriter w, string name, MapRational value)
    {
        w.WriteStartObject(name);
        w.WriteNumber("numerator", value.Numerator);
        w.WriteNumber("denominator", value.Denominator);
        w.WriteEndObject();
    }

    static MapRational ReadRational(JsonElement value, string path)
    {
        Members(value, "lattice unit", path, ["numerator", "denominator"]);
        return new MapRational(value.GetProperty("numerator").GetInt32(), value.GetProperty("denominator").GetInt32());
    }

    static void WriteAddress(Utf8JsonWriter w, MapLatticeAddress address)
    {
        address.RequireValid();
        w.WriteStartObject();
        w.WriteString("x", MapSignedDecimal.Format(address.X));
        w.WriteString("z", MapSignedDecimal.Format(address.Z));
        w.WriteNumber("d", address.Denominator);
        w.WriteEndObject();
    }

    static MapLatticeAddress ReadAddress(JsonElement address, string path)
    {
        Members(address, "lattice address", path, ["x", "z", "d"]);
        return MapLatticeAddress.Create(Long(address.GetProperty("x"), path + ".x"), Long(address.GetProperty("z"), path + ".z"),
            address.GetProperty("d").GetInt32());
    }

    /// <summary>SHA-256 hex over a NUL-terminated domain string and the compact canonical bytes.</summary>
    internal static string Hash(string domain, Action<Utf8JsonWriter> body)
    {
        using var sink = new MapCanonical.HashingBufferWriter();
        sink.Append(Encoding.UTF8.GetBytes(domain + "\0"));
        using (var w = new Utf8JsonWriter(sink, new JsonWriterOptions { Indented = false }))
        {
            body(w);
            w.Flush();
        }
        return Convert.ToHexStringLower(sink.GetHashAndReset());
    }

    static void Members(JsonElement value, string what, string path, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new MapDocumentException(MapDocumentRefusal.SchemaObjectRequired, $"{what} must be an object")
        { FieldPath = path };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal))
                throw new MapDocumentException(MapDocumentRefusal.SchemaMemberUnknown, $"{what}: unknown member '{property.Name}'")
                { FieldPath = MapDocumentJsonPath.AppendProperty(path, property.Name) };
            if (!seen.Add(property.Name)) throw new MapDocumentException(MapDocumentRefusal.SchemaMemberDuplicate, $"{what}: duplicate member '{property.Name}'")
            { FieldPath = MapDocumentJsonPath.AppendProperty(path, property.Name) };
        }
        foreach (string name in required)
            if (!seen.Contains(name)) throw new MapDocumentException(MapDocumentRefusal.SchemaMemberMissing, $"{what}: missing member '{name}'")
            { FieldPath = MapDocumentJsonPath.AppendProperty(path, name) };
    }

    static IEnumerable<JsonElement> Items(JsonElement value, string what, string path) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray() : throw new MapDocumentException(MapDocumentRefusal.SchemaArrayRequired, $"{what} must be an array") { FieldPath = path };

    static string Text(JsonElement value, string path) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "expected a string") { FieldPath = path };

    static float Finite(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) && float.IsFinite(result)) return result;
        throw new MapDocumentException(value.ValueKind == JsonValueKind.Number ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.SchemaValueRequired,
            "expected a finite number")
        { FieldPath = path, ActualNumber = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null };
    }

    static long Long(JsonElement value, string path)
    {
        string text = Text(value, path);
        return MapSignedDecimal.TryParse(text, out long result) ? result
            : throw new MapDocumentException(MapDocumentRefusal.SchemaInt64Malformed, "invalid decimal int64 string")
            { FieldPath = path, ActualText = text, MinimumInteger = long.MinValue, MaximumInteger = long.MaxValue };
    }

    static T Named<T>(JsonElement value, string path) where T : struct, Enum
    {
        string text = Text(value, path);
        foreach (T candidate in Enum.GetValues<T>())
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal)) return candidate;
        throw new MapDocumentException(MapDocumentRefusal.SchemaEnumUnsupported, $"unknown {typeof(T).Name} '{text}'")
        { FieldPath = path, ActualText = text, EnumType = typeof(T) };
    }

    static T Require<T>(T? value, string what, string fieldPath, string? entityId = null) where T : class =>
        value ?? throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, $"{what} is required")
        { FieldPath = fieldPath, EntityId = entityId };
}
