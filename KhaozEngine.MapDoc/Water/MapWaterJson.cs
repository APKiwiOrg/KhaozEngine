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
            throw new JsonException("water: " + ex.Message, ex);
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
        foreach (MapWaterMediumDoc medium in water.Media) Require(medium, "water medium");
        foreach (MapWaterBodyDoc body in water.Bodies) Require(body, "water body");
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
        Require(medium, "water medium");
        w.WriteStartObject();
        w.WriteString("key", medium.Key);
        w.WriteString("kind", medium.Kind.ToString());
        w.WriteNumber("wadeSpeedScale", medium.WadeSpeedScale == 0f ? 0f : medium.WadeSpeedScale);
        w.WriteEndObject();
    }

    internal static void WriteBody(Utf8JsonWriter w, MapWaterBodyDoc body)
    {
        Require(body, "water body");
        string label = $"water body '{body.Id}'";
        MapWaterDomainDoc domain = Require(body.Domain, label + " domain");
        w.WriteStartObject();
        w.WriteString("id", body.Id);
        // Negative zero is the same level as zero, so it writes as zero and shares one identity.
        w.WriteNumber("surfaceY", body.SurfaceY == 0f ? 0f : body.SurfaceY);
        w.WriteString("medium", body.Medium);
        w.WriteString("renderMaterial", body.RenderMaterial);
        w.WritePropertyName("domain");
        WriteDomain(w, domain, label);
        w.WriteStartArray("covers");
        foreach (MapRecordRef cover in Require(body.Covers, label + " covers"))
        {
            Require(cover, label + " cover");
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
        Require(domain, label);
        w.WriteStartObject();
        w.WritePropertyName("frame");
        WriteFrame(w, Require(domain.Frame, label + " frame"));
        w.WriteStartArray("rects");
        foreach (MapWaterRect rect in Require(domain.Rects, label + " rects"))
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
        foreach (MapWaterRing ring in Require(domain.Rings, label + " rings"))
        {
            w.WriteStartObject();
            w.WriteStartArray("vertices");
            foreach (MapLatticeAddress address in Require(Require(ring, label + " ring").Vertices, label + " ring vertices"))
                WriteAddress(w, address);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static MapWaterSet Read(JsonElement json)
    {
        Members(json, "water", ["media", "bodies"]);
        var water = new MapWaterSet();
        foreach (JsonElement medium in Items(json.GetProperty("media"), "water media"))
        {
            Members(medium, "water medium", ["key", "kind", "wadeSpeedScale"]);
            water.Media.Add(new MapWaterMediumDoc(Text(medium.GetProperty("key")),
                Named<MapWaterMediumKind>(medium.GetProperty("kind")), Finite(medium.GetProperty("wadeSpeedScale"))));
        }
        foreach (JsonElement body in Items(json.GetProperty("bodies"), "water bodies")) water.Bodies.Add(ReadBody(body));
        return water;
    }

    static MapWaterBodyDoc ReadBody(JsonElement body)
    {
        Members(body, "water body", ["id", "surfaceY", "medium", "renderMaterial", "domain", "covers"], "displayName");
        MapWaterDomainDoc domain = ReadDomain(body.GetProperty("domain"));
        MapRecordRef[] covers = Items(body.GetProperty("covers"), "water covers").Select(cover =>
        {
            Members(cover, "water cover", ["id", "anchor"]);
            JsonElement anchor = cover.GetProperty("anchor");
            Members(anchor, "water cover anchor", ["surfaceId", "slotX", "slotZ"]);
            return new MapRecordRef(Text(cover.GetProperty("id")), new MapPatchKey(Text(anchor.GetProperty("surfaceId")),
                Long(anchor.GetProperty("slotX")), Long(anchor.GetProperty("slotZ"))));
        }).ToArray();
        return new MapWaterBodyDoc(Text(body.GetProperty("id")), Finite(body.GetProperty("surfaceY")),
            Text(body.GetProperty("medium")), Text(body.GetProperty("renderMaterial")),
            domain, covers,
            body.TryGetProperty("displayName", out JsonElement name) ? Text(name) : null);
    }

    internal static MapWaterDomainDoc ReadDomain(JsonElement domain)
    {
        Members(domain, "water domain", ["frame", "rects", "rings"]);
        MapWaterRect[] rects = Items(domain.GetProperty("rects"), "water rects").Select(rect =>
        {
            Members(rect, "water rect", ["minX", "minZ", "maxX", "maxZ"]);
            return new MapWaterRect(Long(rect.GetProperty("minX")), Long(rect.GetProperty("minZ")),
                Long(rect.GetProperty("maxX")), Long(rect.GetProperty("maxZ")));
        }).ToArray();
        MapWaterRing[] rings = Items(domain.GetProperty("rings"), "water rings").Select(ring =>
        {
            Members(ring, "water ring", ["vertices"]);
            return new MapWaterRing(Items(ring.GetProperty("vertices"), "water ring vertices").Select(ReadAddress).ToArray());
        }).ToArray();
        return new(ReadFrame(domain.GetProperty("frame")), rects, rings);
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

    static MapLatticeFrame ReadFrame(JsonElement frame)
    {
        Members(frame, "water frame", ["cellUnitMetres", "heightUnitMetres", "rowDirection", "datum"]);
        return new MapLatticeFrame(ReadRational(frame.GetProperty("cellUnitMetres")), ReadRational(frame.GetProperty("heightUnitMetres")),
            Named<MapRowDirection>(frame.GetProperty("rowDirection")), Named<MapHeightDatum>(frame.GetProperty("datum")));
    }

    static void WriteRational(Utf8JsonWriter w, string name, MapRational value)
    {
        w.WriteStartObject(name);
        w.WriteNumber("numerator", value.Numerator);
        w.WriteNumber("denominator", value.Denominator);
        w.WriteEndObject();
    }

    static MapRational ReadRational(JsonElement value)
    {
        Members(value, "lattice unit", ["numerator", "denominator"]);
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

    static MapLatticeAddress ReadAddress(JsonElement address)
    {
        Members(address, "lattice address", ["x", "z", "d"]);
        return MapLatticeAddress.Create(Long(address.GetProperty("x")), Long(address.GetProperty("z")),
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

    static void Members(JsonElement value, string what, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new MapDocumentException($"{what} must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal) && !optional.Contains(property.Name, StringComparer.Ordinal))
                throw new MapDocumentException($"{what}: unknown member '{property.Name}'");
            if (!seen.Add(property.Name)) throw new MapDocumentException($"{what}: duplicate member '{property.Name}'");
        }
        foreach (string name in required)
            if (!seen.Contains(name)) throw new MapDocumentException($"{what}: missing member '{name}'");
    }

    static IEnumerable<JsonElement> Items(JsonElement value, string what) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray() : throw new MapDocumentException($"{what} must be an array");

    static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? value.GetString()! : throw new MapDocumentException("expected a string");

    static float Finite(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) && float.IsFinite(result)
            ? result : throw new MapDocumentException("expected a finite number");

    static long Long(JsonElement value) => MapSignedDecimal.TryParse(Text(value), out long result)
        ? result : throw new MapDocumentException("invalid decimal int64 string");

    static T Named<T>(JsonElement value) where T : struct, Enum
    {
        string text = Text(value);
        foreach (T candidate in Enum.GetValues<T>())
            if (string.Equals(candidate.ToString(), text, StringComparison.Ordinal)) return candidate;
        throw new MapDocumentException($"unknown {typeof(T).Name} '{text}'");
    }

    static T Require<T>(T? value, string what) where T : class =>
        value ?? throw new MapDocumentException($"{what} is required");
}
