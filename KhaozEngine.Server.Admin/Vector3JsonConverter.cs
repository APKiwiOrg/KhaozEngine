using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.Server.Admin;

/// <summary>
/// Serializes a <see cref="Vector3"/> as <c>{"x":..,"y":..,"z":..}</c>. System.Text.Json does NOT serialize
/// fields by default, and <see cref="Vector3"/>'s X/Y/Z are fields, so without this an admin DTO carrying a
/// Vector3 (e.g. <c>OnlinePlayer.Position</c>) emits an empty <c>{}</c> and every consumer reads a zero position.
/// Registered scoped on the admin endpoint's <see cref="JsonSerializerOptions"/> so it does not change how any
/// other type serializes (a blanket <c>IncludeFields</c> would).
/// </summary>
internal sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        float x = 0f, y = 0f, z = 0f;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected object for Vector3.");
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return new Vector3(x, y, z);
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string? name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "x": case "X": x = reader.GetSingle(); break;
                case "y": case "Y": y = reader.GetSingle(); break;
                case "z": case "Z": z = reader.GetSingle(); break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Unterminated Vector3 object.");
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteNumber("z", value.Z);
        writer.WriteEndObject();
    }
}
