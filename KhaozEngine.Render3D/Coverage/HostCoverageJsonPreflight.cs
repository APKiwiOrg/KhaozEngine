using System;
using System.Text.Json;

namespace KhaozEngine.Render3D;

/// <summary>Validates the bounded schema before allocating a DOM or collecting entries.</summary>
internal static class HostCoverageJsonPreflight
{
    enum Kind { Root, Topology, Region, Triangle, Strength, Refinement, Authoring, Number, String }

    readonly record struct Member(int Slot, string? Name, Kind Type, int Limit = 0);

    internal static void Validate(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read()) throw new JsonException("Expected a coverage object.");
        Object(ref reader, Kind.Root, "$");
        if (reader.Read()) throw new JsonException("Unexpected data after coverage object.");
    }

    static void Object(ref Utf8JsonReader reader, Kind kind, string path)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new HostCoverageDataException(HostCoverageRefusal.ExpectedObject, "Expected an object.") { Path = path };
        uint seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            Member member = Find(ref reader, kind);
            if (member.Name is null)
            {
                // Do not allocate an unbounded unknown name for diagnostics.
                string name;
                try { name = reader.ValueSpan.Length <= 64 ? reader.GetString()! : "<omitted>"; }
                catch (InvalidOperationException error)
                {
                    throw new HostCoverageDataException(HostCoverageRefusal.InvalidText, "Malformed UTF-16 member name.", error)
                    { Path = path };
                }
                throw new HostCoverageDataException(HostCoverageRefusal.UnknownMember, "Unknown coverage member.")
                { Path = Property(path, name), MemberName = name };
            }
            string memberPath = Property(path, member.Name);
            uint mask = 1u << member.Slot;
            if ((seen & mask) != 0)
                throw new HostCoverageDataException(HostCoverageRefusal.DuplicateMember, "Duplicate coverage member.")
                { Path = memberPath, MemberName = member.Name };
            seen |= mask;
            if (!reader.Read()) throw new JsonException("Expected a member value.");
            if (member.Limit > 0)
                Array(ref reader, member, memberPath);
            else if (member.Type is Kind.Number or Kind.String)
            {
                JsonTokenType expected = member.Type == Kind.String ? JsonTokenType.String : JsonTokenType.Number;
                if (reader.TokenType != expected)
                    throw new HostCoverageDataException(member.Name == "formatVersion" ? HostCoverageRefusal.UnsupportedVersion
                        : member.Type == Kind.String ? HostCoverageRefusal.ExpectedString : NumberReason(member.Name), "Invalid member type.")
                    { Path = memberPath, MemberName = member.Name };
            }
            else
                Object(ref reader, member.Type, memberPath);
        }
    }

    static void Array(ref Utf8JsonReader reader, Member member, string path)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new HostCoverageDataException(HostCoverageRefusal.ExpectedArray, "Expected an array.") { Path = path };
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == member.Limit)
                throw new HostCoverageDataException(CapacityReason(member.Name!), "Coverage array exceeds its capacity.")
                { Path = path, Count = (long)count + 1, Limit = member.Limit };
            Object(ref reader, member.Type, $"{path}[{count}]");
            count++;
        }
    }

    internal static HostCoverageRefusal CapacityReason(string name) => name switch
    {
        "topology" => HostCoverageRefusal.TopologyCapacity,
        "regions" => HostCoverageRefusal.RegionCapacity,
        "triangles" => HostCoverageRefusal.RegionTriangleCapacity,
        "strengths" => HostCoverageRefusal.StrengthCapacity,
        "refinement" => HostCoverageRefusal.RefinementRangeCapacity,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    static HostCoverageRefusal NumberReason(string name) => name switch
    {
        "strength" => HostCoverageRefusal.NonFiniteNumber,
        _ => HostCoverageRefusal.InvalidInteger,
    };

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

    internal static string Property(string parent, string name)
    {
        bool simple = name.Length > 0 && name[0] is (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_');
        foreach (char character in name)
            if (character is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            {
                simple = false;
                break;
            }
        return simple ? parent + "." + name : parent + "[\"" + JsonEncodedText.Encode(name) + "\"]";
    }
}
