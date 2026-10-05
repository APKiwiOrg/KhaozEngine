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
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A native vector must be an object.");
        int seen = 0;
        Vector3 result = default;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected a vector component.");
            int bit = reader.GetString() switch { "x" => 1, "y" => 2, "z" when dimensions == 3 => 4, _ => 0 };
            if (bit == 0 || (seen & bit) != 0) throw new JsonException("Unknown or duplicate native vector component.");
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float value) || !float.IsFinite(value))
                throw new JsonException("Native vector components must be finite numbers.");
            seen |= bit;
            if (bit == 1) result.X = value;
            else if (bit == 2) result.Y = value;
            else result.Z = value;
        }
        if (reader.TokenType != JsonTokenType.EndObject || seen != (dimensions == 3 ? 7 : 3))
            throw new JsonException("Missing native vector component.");
        return result;
    }

    internal static void Write(Utf8JsonWriter writer, Vector3 value, int dimensions)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new JsonException("Native vector components must be finite numbers.");
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        if (dimensions == 3) writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}
