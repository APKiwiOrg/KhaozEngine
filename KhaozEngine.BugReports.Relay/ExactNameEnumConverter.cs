using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.BugReports.Relay;

/// <summary>
/// Writes a relay enum as its camelCase name and reads back only that exact name. Integers, other casings and flag
/// lists are refused, so a relay document has one spelling per value.
/// </summary>
internal sealed class ExactNameEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(typeToConvert))!;

    sealed class Converter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        readonly Dictionary<TEnum, string> names = new();
        readonly Dictionary<string, TEnum> values = new(StringComparer.Ordinal);

        public Converter()
        {
            foreach (TEnum value in Enum.GetValues<TEnum>())
            {
                string name = JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
                names[value] = name;
                values[name] = value;
            }
        }

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && values.TryGetValue(reader.GetString()!, out TEnum value))
                return value;
            throw new JsonException($"Expected one of the exact {typeof(TEnum).Name} names.");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!names.TryGetValue(value, out string? name))
                throw new JsonException($"{typeof(TEnum).Name} has no name for {value}.");
            writer.WriteStringValue(name);
        }
    }
}
