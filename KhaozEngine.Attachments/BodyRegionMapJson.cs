using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KhaozEngine.Attachments;

/// <summary>
/// Strict body region map JSON format 1. Reading refuses unknown or repeated members, other versions and parts whose
/// runs do not add up to their declared triangle count, and names the caller's source in every refusal. Writing is
/// canonical: fixed member order, merged runs and no whitespace.
/// </summary>
public static class BodyRegionMapJson
{
    /// <summary>The only region map format version.</summary>
    public const int FormatVersion = 1;

    /// <summary>Read a region map, refusing with <see cref="BodyRegionMapDataException"/>.</summary>
    public static BodyRegionMap Parse(ReadOnlyMemory<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions());
            JsonElement root = document.RootElement;
            Members(root, "$", "formatVersion", "rig", "regions", "parts");
            JsonElement version = Required(root, "formatVersion", "$");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != FormatVersion)
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.FormatVersionUnsupported, "unsupported formatVersion.")
                {
                    JsonPath = "$.formatVersion",
                    FormatVersion = version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int declared) ? declared : null,
                };
            string rig = Text(Required(root, "rig", "$"), "$.rig");

            var regions = new List<string>();
            int regionIndex = 0;
            foreach (JsonElement name in ArrayValue(Required(root, "regions", "$"), "$.regions").EnumerateArray())
                regions.Add(Text(name, $"$.regions[{regionIndex++}]"));

            var parts = new List<RegionRun[]>();
            foreach (JsonElement part in ArrayValue(Required(root, "parts", "$"), "$.parts").EnumerateArray())
                parts.Add(ReadPart(part, parts.Count));
            return new BodyRegionMap(rig, regions, parts);
        }
        catch (BodyRegionMapDataException error)
        {
            throw ((IAttachmentDataException)error).WithSource(sourceName);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new BodyRegionMapDataException(
                error is JsonException ? BodyRegionMapRefusal.JsonMalformed : BodyRegionMapRefusal.JsonValueShapeInvalid,
                $"{sourceName}: {error.Message}", error)
            { SourceName = sourceName };
        }
    }

    /// <summary>Write a region map's canonical UTF-8 bytes, the input of <see cref="BodyRegionMap.Sha256"/>.</summary>
    public static byte[] Write(BodyRegionMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("rig", map.RigIdentity);
            writer.WriteStartArray("regions");
            foreach (string region in map.Regions) writer.WriteStringValue(region);
            writer.WriteEndArray();
            writer.WriteStartArray("parts");
            for (int part = 0; part < map.Parts.Count; part++)
            {
                writer.WriteStartObject();
                writer.WriteNumber("triangles", map.TriangleCount(part));
                writer.WriteStartArray("runs");
                IReadOnlyList<RegionRun> runs = map.Parts[part];
                for (int index = 0; index < runs.Count; index++)
                {
                    RegionRun run = runs[index];
                    writer.WriteStartArray();
                    writer.WriteNumberValue(run.Region);
                    writer.WriteNumberValue(run.TriangleCount);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    static RegionRun[] ReadPart(JsonElement part, int partIndex)
    {
        string jsonPath = $"$.parts[{partIndex}]";
        Members(part, jsonPath, "triangles", "runs");
        int declared = Integer(Required(part, "triangles", jsonPath), jsonPath + ".triangles");
        JsonElement runArray = ArrayValue(Required(part, "runs", jsonPath), jsonPath + ".runs");
        var runs = new RegionRun[runArray.GetArrayLength()];
        long total = 0;
        int runIndex = 0;
        foreach (JsonElement run in runArray.EnumerateArray())
        {
            string runPath = $"{jsonPath}.runs[{runIndex}]";
            if (run.ValueKind != JsonValueKind.Array || run.GetArrayLength() != 2)
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonValueShapeInvalid, $"{runPath}: a run is a [region, triangleCount] pair.")
                { JsonPath = runPath, PartIndex = partIndex, RunIndex = runIndex };
            runs[runIndex] = new RegionRun(Integer(run[0], runPath + "[0]"), Integer(run[1], runPath + "[1]"));
            total += runs[runIndex++].TriangleCount;
        }
        if (total != declared)
            throw new BodyRegionMapDataException(BodyRegionMapRefusal.TriangleCountMismatch, $"{jsonPath}: runs cover {total} of {declared} triangles.")
            { JsonPath = jsonPath, PartIndex = partIndex, DeclaredTriangleCount = declared, ActualTriangleCount = total };
        return runs;
    }

    static void Members(JsonElement value, string jsonPath, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonValueShapeInvalid, $"{jsonPath}: expected an object.") { JsonPath = jsonPath };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            if (!found.Add(member.Name))
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonPropertyDuplicate, $"{jsonPath}: duplicate property '{member.Name}'.")
                { JsonPath = jsonPath, PropertyName = member.Name };
            if (Array.IndexOf(allowed, member.Name) < 0)
                throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonPropertyUnknown, $"{jsonPath}: unknown property '{member.Name}'.")
                { JsonPath = jsonPath, PropertyName = member.Name };
        }
    }

    static JsonElement Required(JsonElement value, string name, string jsonPath) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonPropertyMissing, $"{jsonPath}: missing '{name}'.")
        { JsonPath = jsonPath, PropertyName = name };

    static string Text(JsonElement value, string jsonPath) => value.ValueKind == JsonValueKind.String
        ? value.GetString()!
        : throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonValueShapeInvalid, $"{jsonPath}: expected a string.") { JsonPath = jsonPath };

    static JsonElement ArrayValue(JsonElement value, string jsonPath) => value.ValueKind == JsonValueKind.Array
        ? value
        : throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonValueShapeInvalid, $"{jsonPath}: expected an array.") { JsonPath = jsonPath };

    static int Integer(JsonElement value, string jsonPath) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)
        ? number
        : throw new BodyRegionMapDataException(BodyRegionMapRefusal.JsonValueShapeInvalid, $"{jsonPath}: expected an Int32 integer.") { JsonPath = jsonPath };
}
