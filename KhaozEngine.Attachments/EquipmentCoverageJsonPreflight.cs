using System;
using System.Text.Json;

namespace KhaozEngine.Attachments;

/// <summary>Validates the bounded schema before allocating a DOM or collecting entries.</summary>
internal static class EquipmentCoverageJsonPreflight
{
    enum Kind { Root, Command, Support, Component, Number, String }

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
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.ExpectedObject, "Expected an object.") { Path = path };
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
                    throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.InvalidText, "Malformed UTF-16 member name.", error)
                    { Path = path };
                }
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.UnknownMember, "Unknown coverage member.")
                { Path = Property(path, name), MemberName = name };
            }
            string memberPath = Property(path, member.Name);
            uint mask = 1u << member.Slot;
            if ((seen & mask) != 0)
                throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.DuplicateMember, "Duplicate coverage member.")
                { Path = memberPath, MemberName = member.Name };
            seen |= mask;
            if (!reader.Read()) throw new JsonException("Expected a member value.");
            if (member.Limit > 0)
                Array(ref reader, member, memberPath);
            else if (member.Type is Kind.Number or Kind.String)
            {
                JsonTokenType expected = member.Type == Kind.String ? JsonTokenType.String : JsonTokenType.Number;
                if (reader.TokenType != expected)
                    throw new EquipmentCoverageDataException(member.Name == "formatVersion" ? EquipmentCoverageRefusal.UnsupportedVersion
                        : member.Type == Kind.String ? EquipmentCoverageRefusal.ExpectedString : NumberReason(member.Name), "Invalid member type.")
                    { Path = memberPath, MemberName = member.Name };
            }
            else
                Object(ref reader, member.Type, memberPath);
        }
    }

    static void Array(ref Utf8JsonReader reader, Member member, string path)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.ExpectedArray, "Expected an array.") { Path = path };
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == member.Limit)
                throw new EquipmentCoverageDataException(CapacityReason(member.Name!), "Coverage array exceeds its capacity.")
                { Path = path, Count = (long)count + 1, Limit = member.Limit };
            if (member.Type == Kind.Component)
            {
                if (reader.TokenType != JsonTokenType.Number)
                    throw new EquipmentCoverageDataException(EquipmentCoverageRefusal.NonFiniteNumber, "Expected a finite frame component.")
                    { Path = $"{path}[{count}]" };
            }
            else
                Object(ref reader, member.Type, $"{path}[{count}]");
            count++;
        }
    }

    internal static EquipmentCoverageRefusal CapacityReason(string name) => name switch
    {
        "commands" => EquipmentCoverageRefusal.CommandCapacity,
        "inwardSupport" => EquipmentCoverageRefusal.SupportRangeCapacity,
        "coverageLocal" => EquipmentCoverageRefusal.FrameComponentCapacity,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    static EquipmentCoverageRefusal NumberReason(string name) => name switch
    {
        "id" => EquipmentCoverageRefusal.InvalidCommandId,
        "partId" => EquipmentCoverageRefusal.InvalidPartId,
        "collarWidth" or "minZ" or "maxZ" or "clearance" or "maxWorldRecession" => EquipmentCoverageRefusal.NonFiniteNumber,
        _ => EquipmentCoverageRefusal.InvalidInteger,
    };

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
