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
            throw Malformed("Numeric identities must be decimal strings.", null, allowZero);
        string text = reader.GetString()!;
        if (text.Length == 0 || text.Length > 19 || (text.Length > 1 && text[0] == '0'))
            throw Malformed("Numeric identities must use canonical int64 decimal strings.", text, allowZero);
        foreach (char digit in text)
            if (digit < '0' || digit > '9')
                throw Malformed("Numeric identities must contain only ASCII decimal digits.", text, allowZero);
        bool parsed = long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value);
        if (!parsed || (!allowZero && value == 0))
            throw new MapNumericIdJsonException(MapDocumentRefusal.ValueOutsideRange, "Numeric identity is outside its permitted int64 range.")
            {
                FieldPath = "$",
                ActualText = text,
                ActualInteger = parsed ? value : null,
                MinimumInteger = allowZero ? 0 : 1,
                MaximumInteger = long.MaxValue
            };
        return value;
    }

    static MapNumericIdJsonException Malformed(string message, string? text, bool allowZero) =>
        new(MapDocumentRefusal.SchemaInt64Malformed, message)
        { FieldPath = "$", ActualText = text, MinimumInteger = allowZero ? 0 : 1, MaximumInteger = long.MaxValue };

    internal static void WriteDecimal(Utf8JsonWriter writer, long value, bool allowZero)
    {
        if (value < 0 || (!allowZero && value == 0))
            throw new MapNumericIdJsonException(MapDocumentRefusal.ValueOutsideRange, "Numeric identity is outside its permitted int64 range.")
            { FieldPath = "$", ActualInteger = value, MinimumInteger = allowZero ? 0 : 1, MaximumInteger = long.MaxValue };
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
