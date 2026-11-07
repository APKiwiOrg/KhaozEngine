using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Render3D;

/// <summary>
/// Strict host coverage JSON format 1. Reading refuses unknown or duplicate members, other versions, nonfinite numbers
/// and over-limit arrays before allocating for them, and reports the caller's asset identity in every refusal. Writing is
/// canonical: fixed member order, sorted entries, normalized negative zero and no whitespace. The semantic digest hashes
/// a domain prefix and the canonical bytes, so it never equals the exact byte hash of a fetched file.
/// </summary>
public static class HostCoverageMapJson
{
    public const int FormatVersion = 1;

    static readonly byte[] SemanticDomain = Encoding.UTF8.GetBytes("KhaozEngine.HostCoverageMap/1\n");

    public static HostCoverageMap Read(ReadOnlySpan<byte> json, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        try
        {
            HostCoverageJsonPreflight.Validate(json);
            var reader = new Utf8JsonReader(json);
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            JsonElement root = document.RootElement;
            Members(root, "$", "formatVersion", "hostSha256", "topology", "regions", "refinement", "authoring");
            JsonElement version = Required(root, "formatVersion", "$");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number) || number != FormatVersion)
                throw new HostCoverageDataException(HostCoverageRefusal.UnsupportedVersion, "Unsupported formatVersion.")
                { Path = "$.formatVersion", FormatVersion = version.TryGetInt32(out int actual) ? actual : null };
            string hostSha256 = Text(root, "hostSha256", "$");

            JsonElement topologyArray = BoundedArray(root, "topology", "$", HostCoverageMap.MaxSurfaceVertices);
            var topology = new CoverageTopologyEntry[topologyArray.GetArrayLength()];
            int index = 0;
            foreach (JsonElement item in topologyArray.EnumerateArray())
            {
                string context = $"$.topology[{index}]";
                Members(item, context, "mesh", "primitive", "vertex", "surfaceId");
                topology[index++] = new(Integer(item, "mesh", context), Integer(item, "primitive", context),
                    Integer(item, "vertex", context), Integer(item, "surfaceId", context));
            }

            JsonElement regionArray = BoundedArray(root, "regions", "$", HostCoverageMap.MaxRegions);
            var regions = new HostCoverageRegion[regionArray.GetArrayLength()];
            index = 0;
            foreach (JsonElement item in regionArray.EnumerateArray())
                regions[index] = ReadRegion(item, $"$.regions[{index++}]");

            JsonElement refinementArray = BoundedArray(root, "refinement", "$", HostCoverageMap.MaxTriangles);
            var refinement = new HostCoverageRefinement[refinementArray.GetArrayLength()];
            index = 0;
            foreach (JsonElement item in refinementArray.EnumerateArray())
            {
                string context = $"$.refinement[{index}]";
                Members(item, context, "mesh", "primitive", "firstTriangle", "count", "level");
                refinement[index++] = new(Range(item, context), Integer(item, "level", context));
            }

            JsonElement authoring = Required(root, "authoring", "$");
            Members(authoring, "$.authoring", "exporter", "revision");
            HostCoverageAuthoring labels = new(Text(authoring, "exporter", "$.authoring"), Text(authoring, "revision", "$.authoring"));
            try { return new HostCoverageMap(hostSha256, topology, regions, refinement, labels); }
            catch (HostCoverageDataException error) when (error.Reason is HostCoverageRefusal.UnknownSurface or HostCoverageRefusal.PositiveStrengthOverlap)
            {
                // Region constructors canonicalize strengths. Diagnostics name the source JSON position.
                throw error.WithContext(path: SourceStrengthPath(regionArray, error));
            }
        }
        catch (HostCoverageDataException error)
        {
            throw error.WithContext(identity: identity);
        }
        catch (JsonException error)
        {
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidJson, $"{identity}: invalid coverage JSON.", error)
            { Identity = identity, Path = error.Path ?? "$" };
        }
    }

    public static byte[] Write(HostCoverageMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("hostSha256", map.HostSha256);
            writer.WriteStartArray("topology");
            foreach (CoverageTopologyEntry entry in map.Topology)
            {
                writer.WriteStartObject();
                writer.WriteNumber("mesh", entry.Mesh);
                writer.WriteNumber("primitive", entry.Primitive);
                writer.WriteNumber("vertex", entry.Vertex);
                writer.WriteNumber("surfaceId", entry.SurfaceId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("regions");
            foreach (HostCoverageRegion region in map.Regions)
            {
                writer.WriteStartObject();
                writer.WriteString("key", region.Key);
                writer.WriteStartArray("triangles");
                foreach (HostCoverageRegionTriangles entry in region.Triangles)
                {
                    writer.WriteStartObject();
                    WriteRange(writer, entry.Triangles);
                    writer.WriteString("role", entry.Role == CoverageTriangleRole.Core ? "core" : "collar");
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("strengths");
                foreach (CoverageSurfaceStrength entry in region.Strengths)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("surfaceId", entry.SurfaceId);
                    writer.WriteNumber("strength", entry.Strength == 0f ? 0f : entry.Strength);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("refinement");
            foreach (HostCoverageRefinement entry in map.Refinement)
            {
                writer.WriteStartObject();
                WriteRange(writer, entry.Triangles);
                writer.WriteNumber("level", entry.Level);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("authoring");
            writer.WriteString("exporter", map.Authoring.Exporter);
            writer.WriteString("revision", map.Authoring.Revision);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Lowercase SHA-256 of the host coverage domain prefix followed by the canonical bytes. Names the
    /// coverage identity independent of whitespace, member order and entry order in the source file.</summary>
    public static string SemanticSha256(HostCoverageMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.SemanticSha256;
    }

    internal static string ComputeSemanticSha256(HostCoverageMap map)
    {
        byte[] canonical = Write(map);
        byte[] prefixed = new byte[SemanticDomain.Length + canonical.Length];
        SemanticDomain.CopyTo(prefixed, 0);
        canonical.CopyTo(prefixed, SemanticDomain.Length);
        return Convert.ToHexStringLower(SHA256.HashData(prefixed));
    }

    static string SourceStrengthPath(JsonElement regions, HostCoverageDataException error)
    {
        int regionIndex = 0;
        foreach (JsonElement region in regions.EnumerateArray())
        {
            if (region.GetProperty("key").GetString() == error.RegionKey)
            {
                int strengthIndex = 0;
                foreach (JsonElement strength in region.GetProperty("strengths").EnumerateArray())
                {
                    if (strength.GetProperty("surfaceId").GetInt32() == error.SurfaceId)
                        return $"$.regions[{regionIndex}].strengths[{strengthIndex}].surfaceId";
                    strengthIndex++;
                }
            }
            regionIndex++;
        }
        return error.Path;
    }

    static HostCoverageRegion ReadRegion(JsonElement item, string context)
    {
        Members(item, context, "key", "triangles", "strengths");
        string key = Text(item, "key", context);

        JsonElement triangleArray = BoundedArray(item, "triangles", context, HostCoverageMap.MaxTriangles);
        var triangles = new HostCoverageRegionTriangles[triangleArray.GetArrayLength()];
        int index = 0;
        foreach (JsonElement entry in triangleArray.EnumerateArray())
        {
            string entryContext = $"{context}.triangles[{index}]";
            Members(entry, entryContext, "mesh", "primitive", "firstTriangle", "count", "role");
            CoverageTriangleRole role = Text(entry, "role", entryContext) switch
            {
                "core" => CoverageTriangleRole.Core,
                "collar" => CoverageTriangleRole.Collar,
                _ => throw new HostCoverageDataException(HostCoverageRefusal.InvalidRole, "Expected core or collar role.") { Path = entryContext + ".role", RegionKey = key },
            };
            triangles[index++] = new(Range(entry, entryContext), role);
        }
        JsonElement strengthArray = BoundedArray(item, "strengths", context, HostCoverageMap.MaxSurfaceVertices);
        var strengths = new CoverageSurfaceStrength[strengthArray.GetArrayLength()];
        index = 0;
        foreach (JsonElement entry in strengthArray.EnumerateArray())
        {
            string entryContext = $"{context}.strengths[{index}]";
            Members(entry, entryContext, "surfaceId", "strength");
            strengths[index++] = new(Integer(entry, "surfaceId", entryContext), Single(entry, "strength", entryContext));
        }
        try { return new HostCoverageRegion(key, triangles, strengths); }
        catch (HostCoverageDataException error) { throw error.WithContext(prefix: context); }
    }

    static CoverageTriangleRange Range(JsonElement value, string context) => new(Integer(value, "mesh", context),
        Integer(value, "primitive", context), Integer(value, "firstTriangle", context), Integer(value, "count", context));

    static void WriteRange(Utf8JsonWriter writer, CoverageTriangleRange range)
    {
        writer.WriteNumber("mesh", range.Mesh);
        writer.WriteNumber("primitive", range.Primitive);
        writer.WriteNumber("firstTriangle", range.FirstTriangle);
        writer.WriteNumber("count", range.Count);
    }

    static void Members(JsonElement value, string context, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new HostCoverageDataException(HostCoverageRefusal.ExpectedObject, "Expected an object.") { Path = context };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            if (!found.Add(member.Name))
                throw new HostCoverageDataException(HostCoverageRefusal.DuplicateMember, "Duplicate coverage member.")
                { Path = HostCoverageJsonPreflight.Property(context, member.Name), MemberName = member.Name };
            if (Array.IndexOf(allowed, member.Name) < 0)
                throw new HostCoverageDataException(HostCoverageRefusal.UnknownMember, "Unknown coverage member.")
                { Path = HostCoverageJsonPreflight.Property(context, member.Name), MemberName = member.Name };
        }
    }

    static JsonElement Required(JsonElement value, string name, string context) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new HostCoverageDataException(HostCoverageRefusal.MissingMember, "Missing coverage member.")
        { Path = HostCoverageJsonPreflight.Property(context, name), MemberName = name };

    static JsonElement BoundedArray(JsonElement value, string name, string context, int limit)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Array)
            throw new HostCoverageDataException(HostCoverageRefusal.ExpectedArray, "Expected an array.") { Path = context + "." + name };
        int count = member.GetArrayLength();
        if (count > limit)
            throw new HostCoverageDataException(HostCoverageJsonPreflight.CapacityReason(name), "Coverage array exceeds its capacity.")
            { Path = context + "." + name, Count = count, Limit = limit };
        return member;
    }

    static string Text(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        string path = context + "." + name;
        if (member.ValueKind != JsonValueKind.String)
            throw new HostCoverageDataException(HostCoverageRefusal.ExpectedString, "Expected a string.") { Path = path };
        try { return member.GetString()!; }
        catch (InvalidOperationException error)
        {
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidText, "Malformed UTF-16 JSON text.", error) { Path = path };
        }
    }

    static int Integer(JsonElement value, string name, string context)
    {
        JsonElement member = Required(value, name, context);
        if (member.ValueKind != JsonValueKind.Number || !member.TryGetInt32(out int result))
            throw new HostCoverageDataException(HostCoverageRefusal.InvalidInteger, "Expected a 32-bit integer.") { Path = context + "." + name };
        return result;
    }

    static float Single(JsonElement value, string name, string context) => Finite(Required(value, name, context), name, context);

    static float Finite(JsonElement value, string name, string context)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float result) || !float.IsFinite(result))
            throw new HostCoverageDataException(HostCoverageRefusal.NonFiniteNumber, "Expected a finite float.") { Path = context + "." + name };
        return result;
    }
}
