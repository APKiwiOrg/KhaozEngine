using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Constructs exact authored units instead of accepting a readonly struct's invalid default.</summary>
internal sealed class MapRationalJsonConverter : JsonConverter<MapRational>
{
    public override MapRational Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A lattice unit must be an object.");
        int? numerator = null, denominator = null;
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int part))
                throw new JsonException("Lattice unit parts must be int32 numbers.");
            if (string.Equals(property.Name, "numerator", StringComparison.OrdinalIgnoreCase))
            {
                if (numerator is not null) throw new JsonException("Duplicate lattice unit numerator.");
                numerator = part;
            }
            else if (string.Equals(property.Name, "denominator", StringComparison.OrdinalIgnoreCase))
            {
                if (denominator is not null) throw new JsonException("Duplicate lattice unit denominator.");
                denominator = part;
            }
            else throw new JsonException("Unknown lattice unit part.");
        }
        if (numerator is null || denominator is null) throw new JsonException("Missing lattice unit part.");
        try { return new MapRational(numerator.Value, denominator.Value); }
        catch (MapDocumentException ex) { throw new JsonException(ex.Message, ex); }
    }

    public override void Write(Utf8JsonWriter writer, MapRational value, JsonSerializerOptions options)
    {
        _ = value.Exact();
        writer.WriteStartObject();
        writer.WriteNumber("numerator", value.Numerator);
        writer.WriteNumber("denominator", value.Denominator);
        writer.WriteEndObject();
    }
}

/// <summary>Shares the patch codec's canonical decimal representation for signed slot coordinates.</summary>
internal sealed class MapPatchKeyJsonConverter : JsonConverter<MapPatchKey>
{
    public override MapPatchKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("A patch key must be an object.");
        string? surfaceId = null;
        long? slotX = null, slotZ = null;
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "surfaceId", StringComparison.OrdinalIgnoreCase))
            {
                if (surfaceId is not null || property.Value.ValueKind != JsonValueKind.String)
                    throw new JsonException("Invalid or duplicate patch surfaceId.");
                surfaceId = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "slotX", StringComparison.OrdinalIgnoreCase))
            {
                if (slotX is not null) throw new JsonException("Duplicate patch slotX.");
                slotX = ReadSlot(property.Value);
            }
            else if (string.Equals(property.Name, "slotZ", StringComparison.OrdinalIgnoreCase))
            {
                if (slotZ is not null) throw new JsonException("Duplicate patch slotZ.");
                slotZ = ReadSlot(property.Value);
            }
            else throw new JsonException("Unknown patch key member.");
        }
        if (string.IsNullOrWhiteSpace(surfaceId) || slotX is null || slotZ is null)
            throw new JsonException("Missing patch key member.");
        return new(surfaceId, slotX.Value, slotZ.Value);
    }

    static long ReadSlot(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Patch slots must be int64 decimal strings.");
        string text = value.GetString()!;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long slot) ||
            !string.Equals(text, slot.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new JsonException("Patch slots must be canonical int64 decimal strings.");
        return slot;
    }

    public override void Write(Utf8JsonWriter writer, MapPatchKey value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.SurfaceId)) throw new JsonException("A patch surfaceId is required.");
        writer.WriteStartObject();
        writer.WriteString("surfaceId", value.SurfaceId);
        writer.WriteString("slotX", value.SlotX.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("slotZ", value.SlotZ.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }
}
