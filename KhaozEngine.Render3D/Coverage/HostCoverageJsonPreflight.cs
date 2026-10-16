using System;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Render3D;

/// <summary>Checks the bounded schema without copying input, allocating a DOM or collecting model entries.</summary>
internal static class HostCoverageJsonPreflight
{
    enum Kind { Root, Topology, Region, Triangle, Strength, Refinement, Authoring, Number, String }

    readonly record struct Member(int Slot, string? Name, Kind Type, int Limit = 0);

    internal static void Validate(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read()) throw new JsonException("Expected a host coverage object.");
        Object(ref reader, Kind.Root);
        if (reader.Read()) throw new JsonException("Unexpected data after the host coverage object.");
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
            else if (member.Type is Kind.Number or Kind.String)
            {
                JsonTokenType expected = member.Type == Kind.String ? JsonTokenType.String : JsonTokenType.Number;
                if (reader.TokenType != expected)
                {
                    if (member.Name == "formatVersion") throw new InvalidDataException("root: unsupported formatVersion.");
                    string type = member.Type == Kind.String ? "string" : "number";
                    throw new InvalidDataException($"{Context(kind, index, parent)}: '{member.Name}' must be a {type}.");
                }
            }
            else
                Object(ref reader, member.Type);
        }
    }

    static void Array(ref Utf8JsonReader reader, Member member, Kind container, int index, int parent)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException($"{Context(container, index, parent)}: '{member.Name}' must be an array.");
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == member.Limit)
                throw new InvalidDataException($"{Context(container, index, parent)}: '{member.Name}' exceeds the limit of {member.Limit} entries.");
            Object(ref reader, member.Type, count++, index);
        }
    }

    static Member Find(ref Utf8JsonReader reader, Kind kind)
    {
        // Known ASCII names need at most six bytes per character when escaped.
        if (reader.ValueSpan.Length > 6 * "formatVersion".Length) return default;
        switch (kind)
        {
            case Kind.Root:
                if (reader.ValueTextEquals("formatVersion"u8)) return new(0, "formatVersion", Kind.Number);
                if (reader.ValueTextEquals("hostSha256"u8)) return new(1, "hostSha256", Kind.String);
                if (reader.ValueTextEquals("topology"u8)) return new(2, "topology", Kind.Topology, HostCoverageMap.MaxSurfaceVertices);
                if (reader.ValueTextEquals("regions"u8)) return new(3, "regions", Kind.Region, HostCoverageMap.MaxRegions);
                if (reader.ValueTextEquals("refinement"u8)) return new(4, "refinement", Kind.Refinement, HostCoverageMap.MaxTriangles);
                if (reader.ValueTextEquals("authoring"u8)) return new(5, "authoring", Kind.Authoring);
                break;
            case Kind.Region:
                if (reader.ValueTextEquals("key"u8)) return new(0, "key", Kind.String);
                if (reader.ValueTextEquals("triangles"u8)) return new(1, "triangles", Kind.Triangle, HostCoverageMap.MaxTriangles);
                if (reader.ValueTextEquals("strengths"u8)) return new(2, "strengths", Kind.Strength, HostCoverageMap.MaxSurfaceVertices);
                break;
            case Kind.Strength:
                if (reader.ValueTextEquals("surfaceId"u8)) return new(0, "surfaceId", Kind.Number);
                if (reader.ValueTextEquals("strength"u8)) return new(1, "strength", Kind.Number);
                break;
            case Kind.Authoring:
                if (reader.ValueTextEquals("exporter"u8)) return new(0, "exporter", Kind.String);
                if (reader.ValueTextEquals("revision"u8)) return new(1, "revision", Kind.String);
                break;
            default:
                if (reader.ValueTextEquals("mesh"u8)) return new(0, "mesh", Kind.Number);
                if (reader.ValueTextEquals("primitive"u8)) return new(1, "primitive", Kind.Number);
                if (kind == Kind.Topology)
                {
                    if (reader.ValueTextEquals("vertex"u8)) return new(2, "vertex", Kind.Number);
                    if (reader.ValueTextEquals("surfaceId"u8)) return new(3, "surfaceId", Kind.Number);
                }
                else
                {
                    if (reader.ValueTextEquals("firstTriangle"u8)) return new(2, "firstTriangle", Kind.Number);
                    if (reader.ValueTextEquals("count"u8)) return new(3, "count", Kind.Number);
                    if (kind == Kind.Triangle && reader.ValueTextEquals("role"u8)) return new(4, "role", Kind.String);
                    if (kind == Kind.Refinement && reader.ValueTextEquals("level"u8)) return new(4, "level", Kind.Number);
                }
                break;
        }
        return default;
    }

    // A refusal never decodes an unbounded unknown name just to include it in a diagnostic.
    static string DiagnosticName(ref Utf8JsonReader reader) => reader.ValueSpan.Length <= 64 ? reader.GetString()! : "<omitted>";

    static string Context(Kind kind, int index, int parent) => kind switch
    {
        Kind.Root => "root",
        Kind.Topology => $"topology[{index}]",
        Kind.Region => $"regions[{index}]",
        Kind.Triangle => $"regions[{parent}] triangles[{index}]",
        Kind.Strength => $"regions[{parent}] strengths[{index}]",
        Kind.Refinement => $"refinement[{index}]",
        _ => "authoring",
    };
}
