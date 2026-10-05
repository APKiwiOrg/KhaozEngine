using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc;

/// <summary>Stores a nonnegative int64 high-water mark as an exact, invariant decimal string.</summary>
public sealed class MapNumericIdJsonConverter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ReadDecimal(ref reader, allowZero: true);

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
        WriteDecimal(writer, value, allowZero: true);

    internal static long ReadDecimal(ref Utf8JsonReader reader, bool allowZero)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Numeric identities must be decimal strings.");
        string text = reader.GetString()!;
        if (text.Length == 0 || text.Length > 19 || (text.Length > 1 && text[0] == '0'))
            throw new JsonException("Numeric identities must use canonical int64 decimal strings.");
        foreach (char digit in text)
            if (digit < '0' || digit > '9')
                throw new JsonException("Numeric identities must contain only ASCII decimal digits.");
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) ||
            (!allowZero && value == 0))
            throw new JsonException("Numeric identity is outside its permitted int64 range.");
        return value;
    }

    internal static void WriteDecimal(Utf8JsonWriter writer, long value, bool allowZero)
    {
        if (value < 0 || (!allowZero && value == 0))
            throw new JsonException("Numeric identity is outside its permitted int64 range.");
        writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>Stores optional positive placement identities as exact decimal strings.</summary>
public sealed class MapNullableNumericIdJsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : MapNumericIdJsonConverter.ReadDecimal(ref reader, allowZero: false);

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is { } id) MapNumericIdJsonConverter.WriteDecimal(writer, id, allowZero: false);
        else writer.WriteNullValue();
    }
}
