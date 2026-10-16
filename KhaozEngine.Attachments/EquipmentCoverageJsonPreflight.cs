using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

/// <summary>Checks the bounded schema without copying input, allocating a DOM or collecting model entries.</summary>
internal static class EquipmentCoverageJsonPreflight
{
    enum Kind { Root, Command, Support, Component, Number, String }

    readonly record struct Member(int Slot, string? Name, Kind Type, int Limit = 0);

    internal static void Validate(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read()) throw new JsonException("Expected an equipment coverage object.");
        Object(ref reader, Kind.Root);
        if (reader.Read()) throw new JsonException("Unexpected data after the equipment coverage object.");
    }

    static void Object(ref Utf8JsonReader reader, Kind kind, int index = -1, int parent = -1)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new InvalidDataException($"{Context(kind, index, parent)}: expected an object.");
        uint seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Member member = Find(ref reader, kind);
            if (member.Name is null)
                throw new InvalidDataException($"{Context(kind, index, parent)}: unknown property '{DiagnosticName(ref reader)}'.");
            uint mask = 1u << member.Slot;
            if ((seen & mask) != 0)
                throw new InvalidDataException($"{Context(kind, index, parent)}: duplicate property '{member.Name}'.");
            seen |= mask;
            if (!reader.Read()) throw new JsonException("Expected a property value.");
            if (member.Limit > 0)
                Array(ref reader, member, kind, index, parent);
            else
            {
                JsonTokenType expected = member.Type == Kind.String ? JsonTokenType.String : JsonTokenType.Number;
                if (reader.TokenType != expected)
                {
                    if (member.Name == "formatVersion") throw new InvalidDataException("root: unsupported formatVersion.");
                    string type = member.Type == Kind.String ? "string" : "number";
                    throw new InvalidDataException($"{Context(kind, index, parent)}: '{member.Name}' must be a {type}.");
                }
            }
        }
    }

    static void Array(ref Utf8JsonReader reader, Member member, Kind container, int index, int parent)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw ArrayError(member, container, index, parent, false);
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == member.Limit)
                throw ArrayError(member, container, index, parent, true);
            if (member.Type == Kind.Component)
            {
                if (reader.TokenType != JsonTokenType.Number)
                    throw new InvalidDataException($"{Context(container, index, parent)}: 'coverageLocal[{count}]' must be a finite float.");
            }
            else
                Object(ref reader, member.Type, count, index);
            count++;
        }
    }

    static InvalidDataException ArrayError(Member member, Kind container, int index, int parent, bool overLimit)
    {
        string context = Context(container, index, parent);
        if (member.Type == Kind.Component)
            return new InvalidDataException($"{context}: coverageLocal must contain 16 row-major numbers.");
        return new InvalidDataException(overLimit
            ? $"{context}: '{member.Name}' exceeds the limit of {member.Limit} entries."
            : $"{context}: '{member.Name}' must be an array.");
    }

    static Member Find(ref Utf8JsonReader reader, Kind kind)
    {
        // Known ASCII names need at most six bytes per character when escaped.
        if (reader.ValueSpan.Length > 6 * "maxWorldRecession".Length) return default;
        switch (kind)
        {
            case Kind.Root:
                if (reader.ValueTextEquals("formatVersion"u8)) return new(0, "formatVersion", Kind.Number);
                if (reader.ValueTextEquals("attachmentSha256"u8)) return new(1, "attachmentSha256", Kind.String);
                if (reader.ValueTextEquals("hostMapSha256"u8)) return new(2, "hostMapSha256", Kind.String);
                if (reader.ValueTextEquals("commands"u8)) return new(3, "commands", Kind.Command, EquipmentCoverageDefinition.MaxCommands);
                break;
            case Kind.Command:
                if (reader.ValueTextEquals("id"u8)) return new(0, "id", Kind.Number);
                if (reader.ValueTextEquals("partId"u8)) return new(1, "partId", Kind.Number);
                if (reader.ValueTextEquals("hostRegion"u8)) return new(2, "hostRegion", Kind.String);
                if (reader.ValueTextEquals("coverageLocal"u8)) return new(3, "coverageLocal", Kind.Component, 16);
                if (reader.ValueTextEquals("inwardSupport"u8)) return new(4, "inwardSupport", Kind.Support, EquipmentCoverageCommand.MaxSupportTriangles);
                if (reader.ValueTextEquals("collarWidth"u8)) return new(5, "collarWidth", Kind.Number);
                if (reader.ValueTextEquals("minZ"u8)) return new(6, "minZ", Kind.Number);
                if (reader.ValueTextEquals("maxZ"u8)) return new(7, "maxZ", Kind.Number);
                if (reader.ValueTextEquals("clearance"u8)) return new(8, "clearance", Kind.Number);
                if (reader.ValueTextEquals("maxWorldRecession"u8)) return new(9, "maxWorldRecession", Kind.Number);
                break;
            case Kind.Support:
                if (reader.ValueTextEquals("mesh"u8)) return new(0, "mesh", Kind.Number);
                if (reader.ValueTextEquals("primitive"u8)) return new(1, "primitive", Kind.Number);
                if (reader.ValueTextEquals("firstTriangle"u8)) return new(2, "firstTriangle", Kind.Number);
                if (reader.ValueTextEquals("count"u8)) return new(3, "count", Kind.Number);
                break;
        }
        return default;
    }

    // A refusal never decodes an unbounded unknown name just to include it in a diagnostic.
    static string DiagnosticName(ref Utf8JsonReader reader) => reader.ValueSpan.Length <= 64 ? reader.GetString()! : "<omitted>";

    static string Context(Kind kind, int index, int parent) => kind switch
    {
        Kind.Root => "root",
        Kind.Command => $"commands[{index}]",
        _ => $"commands[{parent}] inwardSupport[{index}]",
    };
}
