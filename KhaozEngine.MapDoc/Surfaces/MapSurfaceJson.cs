using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>Paint metadata validity shared by canonical identity and region compilation.</summary>
internal static class MapSurfaceJson
{
    internal static void RequirePaintPriority(MapSurfaceRef surface)
    {
        if (surface.Role != MapSurfaceRole.PaintOverride && surface.PaintPriority != 0)
            throw new MapDocumentException(MapDocumentRefusal.SurfacePaintPriorityMisplaced, "paintPriority is valid only for PaintOverride surfaces.")
            { SurfaceId = surface.Id, FieldPath = nameof(surface.PaintPriority), ActualInteger = surface.PaintPriority };
    }
}

/// <summary>Constructs exact authored units instead of accepting a readonly struct's invalid default.</summary>
internal sealed class MapRationalJsonConverter : JsonConverter<MapRational>
{
    public override MapRational Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaObjectRequired, "A lattice unit must be an object.")
        { FieldPath = "$" };
        int? numerator = null, denominator = null;
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out int part))
                throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaValueRequired, "Lattice unit parts must be int32 numbers.")
                { FieldPath = MapDocumentJsonPath.AppendProperty("$", property.Name) };
            if (string.Equals(property.Name, "numerator", StringComparison.OrdinalIgnoreCase))
            {
                if (numerator is not null) throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaMemberDuplicate, "Duplicate lattice unit numerator.")
                { FieldPath = MapDocumentJsonPath.AppendProperty("$", property.Name) };
                numerator = part;
            }
            else if (string.Equals(property.Name, "denominator", StringComparison.OrdinalIgnoreCase))
            {
                if (denominator is not null) throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaMemberDuplicate, "Duplicate lattice unit denominator.")
                { FieldPath = MapDocumentJsonPath.AppendProperty("$", property.Name) };
                denominator = part;
            }
            else throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaMemberUnknown, "Unknown lattice unit part.")
            { FieldPath = MapDocumentJsonPath.AppendProperty("$", property.Name) };
        }
        if (numerator is null || denominator is null) throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaMemberMissing, "Missing lattice unit part.")
        { FieldPath = numerator is null ? "$.numerator" : "$.denominator" };
        try { return new MapRational(numerator.Value, denominator.Value); }
        catch (MapDocumentException ex) { throw MapSurfaceJsonException.FromDocument(ex); }
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
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw Refusal(MapDocumentRefusal.SchemaObjectRequired, "A patch key must be an object.", "$");
        string? surfaceId = null;
        long? slotX = null, slotZ = null;
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "surfaceId", StringComparison.OrdinalIgnoreCase))
            {
                if (surfaceId is not null || property.Value.ValueKind != JsonValueKind.String)
                    throw Refusal(surfaceId is not null ? MapDocumentRefusal.SchemaMemberDuplicate : MapDocumentRefusal.SchemaValueRequired,
                        "Invalid or duplicate patch surfaceId.", MapDocumentJsonPath.AppendProperty("$", property.Name), surfaceId);
                surfaceId = property.Value.GetString();
            }
            else if (string.Equals(property.Name, "slotX", StringComparison.OrdinalIgnoreCase))
            {
                if (slotX is not null) throw Refusal(MapDocumentRefusal.SchemaMemberDuplicate, "Duplicate patch slotX.", MapDocumentJsonPath.AppendProperty("$", property.Name), surfaceId);
                slotX = ReadSlot(property.Value, MapDocumentJsonPath.AppendProperty("$", property.Name), surfaceId);
            }
            else if (string.Equals(property.Name, "slotZ", StringComparison.OrdinalIgnoreCase))
            {
                if (slotZ is not null) throw Refusal(MapDocumentRefusal.SchemaMemberDuplicate, "Duplicate patch slotZ.", MapDocumentJsonPath.AppendProperty("$", property.Name), surfaceId);
                slotZ = ReadSlot(property.Value, MapDocumentJsonPath.AppendProperty("$", property.Name), surfaceId);
            }
            else throw Refusal(MapDocumentRefusal.SchemaMemberUnknown, "Unknown patch key member.", MapDocumentJsonPath.AppendProperty("$", property.Name), surfaceId);
        }
        if (string.IsNullOrWhiteSpace(surfaceId) || slotX is null || slotZ is null)
            throw Refusal(MapDocumentRefusal.SchemaValueRequired, "Missing patch key member.",
                string.IsNullOrWhiteSpace(surfaceId) ? "$.surfaceId" : slotX is null ? "$.slotX" : "$.slotZ", surfaceId);
        return new(surfaceId, slotX.Value, slotZ.Value);
    }

    static long ReadSlot(JsonElement value, string fieldPath, string? surfaceId)
    {
        if (value.ValueKind != JsonValueKind.String) throw Refusal(MapDocumentRefusal.SchemaValueRequired, "Patch slots must be int64 decimal strings.", fieldPath, surfaceId);
        string text = value.GetString()!;
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long slot) ||
            !string.Equals(text, slot.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            throw new MapSurfaceJsonException(MapDocumentRefusal.SchemaInt64Malformed, "Patch slots must be canonical int64 decimal strings.")
            { FieldPath = fieldPath, SurfaceId = surfaceId, ActualText = text };
        return slot;
    }

    public override void Write(Utf8JsonWriter writer, MapPatchKey value, JsonSerializerOptions options)
    {
        if (string.IsNullOrWhiteSpace(value.SurfaceId)) throw Refusal(MapDocumentRefusal.SchemaValueRequired, "A patch surfaceId is required.", "$.surfaceId", value.SurfaceId);
        writer.WriteStartObject();
        writer.WriteString("surfaceId", value.SurfaceId);
        writer.WriteString("slotX", value.SlotX.ToString(CultureInfo.InvariantCulture));
        writer.WriteString("slotZ", value.SlotZ.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    static MapSurfaceJsonException Refusal(MapDocumentRefusal reason, string message, string fieldPath, string? surfaceId = null) =>
        new(reason, message) { FieldPath = fieldPath, SurfaceId = surfaceId };
}
