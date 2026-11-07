using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>Installs only native vector converters. Numeric placement IDs remain property-scoped.</summary>
public static class MapNativeJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool has2 = false, has3 = false;
        foreach (JsonConverter converter in options.Converters)
        {
            has2 |= converter is MapNativeVector2Converter;
            has3 |= converter is MapNativeVector3Converter;
        }
        if (!has2) options.Converters.Add(new MapNativeVector2Converter());
        if (!has3) options.Converters.Add(new MapNativeVector3Converter());
    }
}

/// <summary>Closed, finite native x/y vectors, independent of serializer field settings.</summary>
public sealed class MapNativeVector2Converter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Vector3 value = MapNativeVectorJson.Read(ref reader, dimensions: 2);
        return new Vector2(value.X, value.Y);
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options) =>
        MapNativeVectorJson.Write(writer, new Vector3(value, 0), dimensions: 2);
}

/// <summary>Closed, finite native x/y/z vectors, independent of serializer field settings.</summary>
public sealed class MapNativeVector3Converter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MapNativeVectorJson.Read(ref reader, dimensions: 3);

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options) =>
        MapNativeVectorJson.Write(writer, value, dimensions: 3);
}

internal static class MapNativeVectorJson
{
    internal static Vector3 Read(ref Utf8JsonReader reader, int dimensions)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new MapNativeVectorJsonException(MapDocumentRefusal.SchemaObjectRequired, "A native vector must be an object.") { FieldPath = "$" };
        int seen = 0;
        Vector3 result = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            // Utf8JsonReader rejects any non-property token here before this internal invariant can fail.
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a vector component.");
            string component = reader.GetString()!;
            int bit = component switch { "x" => 1, "y" => 2, "z" when dimensions == 3 => 4, _ => 0 };
            if (bit == 0 || (seen & bit) != 0) throw new MapNativeVectorJsonException(bit == 0 ? MapDocumentRefusal.SchemaMemberUnknown : MapDocumentRefusal.SchemaMemberDuplicate,
                "Unknown or duplicate native vector component.") { FieldPath = MapDocumentJsonPath.AppendProperty("$", component) };
            float value = default;
            bool parsed = reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out value);
            if (!parsed || !float.IsFinite(value))
                throw new MapNativeVectorJsonException(parsed ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.SchemaValueRequired,
                    "Native vector components must be finite numbers.")
                { FieldPath = MapDocumentJsonPath.AppendProperty("$", component), ActualNumber = parsed ? value : null };
            seen |= bit;
            if (bit == 1) result.X = value;
            else if (bit == 2) result.Y = value;
            else result.Z = value;
        }
        if (reader.TokenType != JsonTokenType.EndObject || seen != (dimensions == 3 ? 7 : 3))
            throw new MapNativeVectorJsonException(MapDocumentRefusal.SchemaMemberMissing, "Missing native vector component.")
            { FieldPath = (seen & 1) == 0 ? "$.x" : (seen & 2) == 0 ? "$.y" : "$.z" };
        return result;
    }

    internal static void Write(Utf8JsonWriter writer, Vector3 value, int dimensions)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new MapNativeVectorJsonException(MapDocumentRefusal.ValueNonfinite, "Native vector components must be finite numbers.")
            { FieldPath = !float.IsFinite(value.X) ? "$.x" : !float.IsFinite(value.Y) ? "$.y" : "$.z",
                ActualNumber = !float.IsFinite(value.X) ? value.X : !float.IsFinite(value.Y) ? value.Y : value.Z };
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        if (dimensions == 3) writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}
