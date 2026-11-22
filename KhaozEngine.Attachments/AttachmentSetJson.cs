using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.Attachments;

public static class AttachmentSetJson
{
    public const int RigidOnlyFormatVersion = 1;

    /// <summary>The format of a set with skinned parts and no layer. Kept at 2 because a public constant is compiled into its callers.</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>The format of a worn set with a layer, covered regions and a region tag on every part.</summary>
    public const int RegionsFormatVersion = 3;

    /// <summary>The format of a worn set with skinned part motion or colliders.</summary>
    public const int MotionFormatVersion = 4;

    /// <summary>The format of a worn set with region standoff or skinned part lift.</summary>
    public const int LiftFormatVersion = 5;

    public static AttachmentSetDefinition Parse(ReadOnlyMemory<byte> utf8, string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions());
            JsonElement root = document.RootElement;
            Members(root, "root", "$", "formatVersion", "layer", "covers", "colliders", "standoff", "parts");
            JsonElement version = Required(root, "formatVersion", "root", "$");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int number)
                || number is not (RigidOnlyFormatVersion or CurrentFormatVersion or RegionsFormatVersion or MotionFormatVersion or LiftFormatVersion))
                throw new AttachmentSetDataException(AttachmentSetRefusal.FormatVersionUnsupported, "root: unsupported formatVersion.")
                {
                    JsonPath = "$.formatVersion",
                    FormatVersion = version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int declaredVersion) ? declaredVersion : null,
                };
            bool liftFormat = number == LiftFormatVersion;
            bool motionFormat = number >= MotionFormatVersion;
            bool tagged = number >= RegionsFormatVersion;
            if (!tagged) Members(root, "root", "$", "formatVersion", "parts");
            else if (!motionFormat) Members(root, "root", "$", "formatVersion", "layer", "covers", "parts");
            else if (!liftFormat) Members(root, "root", "$", "formatVersion", "layer", "covers", "colliders", "parts");
            // A missing layer in format 4 or 5 has a specific refusal when an addition requires it.
            AttachmentLayer? layer = tagged && (!motionFormat || root.TryGetProperty("layer", out _)) ? ReadLayer(root) : null;
            string[]? covers = tagged ? ReadCovers(root) : null;
            ColliderDefinition[] colliders = motionFormat ? AttachmentSetMotionJson.ReadColliders(root) : [];
            RegionStandoff[] standoff = liftFormat ? AttachmentSetLiftJson.ReadStandoff(root) : [];
            JsonElement array = Required(root, "parts", "root", "$");
            if (array.ValueKind != JsonValueKind.Array)
                throw new AttachmentSetDataException(AttachmentSetRefusal.PartsNotArray, "root: parts must be an array.") { JsonPath = "$.parts" };
            var parts = new List<AttachmentPartDefinition>();
            int index = 0;
            foreach (JsonElement item in array.EnumerateArray())
            {
                int partIndex = index++;
                string jsonPath = $"$.parts[{partIndex}]";
                string context = $"parts[{partIndex}]";
                bool rigid = number == RigidOnlyFormatVersion;
                if (rigid)
                    Members(item, context, jsonPath, "id", "mesh", "joint", "local");
                else
                {
                    if (liftFormat) Members(item, context, jsonPath, "id", "kind", "mesh", "joint", "local", "region", "regions", "motion", "lift");
                    else if (motionFormat) Members(item, context, jsonPath, "id", "kind", "mesh", "joint", "local", "region", "regions", "motion");
                    else if (tagged) Members(item, context, jsonPath, "id", "kind", "mesh", "joint", "local", "region", "regions");
                    else Members(item, context, jsonPath, "id", "kind", "mesh", "joint", "local");
                    string kind = Text(item, "kind", context, jsonPath);
                    rigid = kind switch
                    {
                        "rigid" => true,
                        "skinned" => false,
                        _ => throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPartKindUnsupported, $"{context}: unsupported attachment kind.")
                        { JsonPath = jsonPath + ".kind", Kind = kind },
                    };
                    if (!rigid)
                    {
                        if (liftFormat) Members(item, context, jsonPath, "id", "kind", "mesh", "regions", "motion", "lift");
                        else if (motionFormat) Members(item, context, jsonPath, "id", "kind", "mesh", "regions", "motion");
                        else Members(item, context, jsonPath, tagged ? ["id", "kind", "mesh", "regions"] : ["id", "kind", "mesh"]);
                    }
                    else if (tagged) Members(item, context, jsonPath, "id", "kind", "mesh", "joint", "local", "region");
                }
                JsonElement idValue = Required(item, "id", context, jsonPath);
                if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetUInt32(out uint id))
                    throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPartIdInvalid, $"{context}: id must be a positive uint.") { JsonPath = jsonPath + ".id" };
                context += $" part {id}";
                JsonElement mesh = Required(item, "mesh", context, jsonPath);
                Members(mesh, context + " mesh", jsonPath + ".mesh", "key", "sha256");
                string key = Text(mesh, "key", context + " mesh", jsonPath + ".mesh");
                string hash = Text(mesh, "sha256", context + " mesh", jsonPath + ".mesh");
                if (!rigid)
                {
                    parts.Add(new SkinnedAttachmentPartDefinition(id, new(key, hash))
                    {
                        Regions = tagged ? ReadRuns(item, id, jsonPath) : [],
                        Motion = motionFormat ? AttachmentSetMotionJson.ReadMotion(item, id, partIndex) : null,
                        Lift = liftFormat ? AttachmentSetLiftJson.ReadLift(item, id, partIndex) : null,
                    });
                    continue;
                }
                string joint = Text(item, "joint", context, jsonPath);
                JsonElement local = Required(item, "local", context, jsonPath);
                if (local.ValueKind != JsonValueKind.Array || local.GetArrayLength() != 16)
                    throw new AttachmentSetDataException(AttachmentSetRefusal.LocalTransformLengthMismatch, $"{context}: local must contain 16 row-major numbers.")
                    {
                        JsonPath = jsonPath + ".local",
                        ActualCount = local.ValueKind == JsonValueKind.Array ? local.GetArrayLength() : null,
                        ExpectedCount = 16,
                    };
                float[] values = new float[16];
                int component = 0;
                foreach (JsonElement value in local.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out float parsed) || !float.IsFinite(parsed))
                        throw new AttachmentSetDataException(AttachmentSetRefusal.LocalTransformComponentInvalid, $"{context}: local[{component}] must be a finite float.")
                        { JsonPath = jsonPath + $".local[{component}]", ComponentIndex = component };
                    values[component++] = parsed;
                }
                var matrix = new Matrix4x4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
                    values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
                string? region = null;
                if (tagged)
                {
                    RequireRegionTag(item, "region", id, jsonPath);
                    region = Text(item, "region", context, jsonPath);
                }
                parts.Add(new RigidAttachmentPartDefinition(id, new(key, hash), joint, matrix) { Region = region });
            }
            if (motionFormat && layer is null)
            {
                if (liftFormat) AttachmentSetLiftJson.RequireLayerForAdditions(parts, standoff);
                AttachmentSetMotionJson.RequireLayerForAdditions(parts, colliders);
                layer = ReadLayer(root);
            }
            return new AttachmentSetDefinition(parts, layer, covers, colliders, standoff);
        }
        catch (MalformedDataException error)
        {
            if (error is IAttachmentDataException sourced) throw sourced.WithSource(sourceName);
            throw;
        }
        catch (AttachmentSetArgumentException error)
        {
            throw ((IAttachmentDataException)error).WithSource(sourceName);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new AttachmentSetDataException(
                error is JsonException ? AttachmentSetRefusal.JsonMalformed : AttachmentSetRefusal.JsonValueShapeInvalid,
                $"{sourceName}: {error.Message}", error)
            { SourceName = sourceName };
        }
    }

    public static byte[] Write(AttachmentSetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // The lowest format that expresses the definition, so a set without a layer keeps its format 1 or 2 bytes and hash.
        bool tagged = definition.Layer is not null;
        bool hasLiftAdditions = tagged && AttachmentSetLiftJson.HasAdditions(definition);
        bool hasMotionAdditions = tagged && !hasLiftAdditions && AttachmentSetMotionJson.HasAdditions(definition);
        bool rigidOnly = !tagged && definition.Parts.All(part => part is RigidAttachmentPartDefinition);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            int format = hasLiftAdditions ? LiftFormatVersion : hasMotionAdditions ? MotionFormatVersion : tagged ? RegionsFormatVersion
                : rigidOnly ? RigidOnlyFormatVersion : CurrentFormatVersion;
            writer.WriteNumber("formatVersion", format);
            if (definition.Layer is { } layer)
            {
                writer.WriteString("layer", LayerNames[(int)layer]);
                writer.WriteStartArray("covers");
                foreach (string region in definition.Covers) writer.WriteStringValue(region);
                writer.WriteEndArray();
            }
            AttachmentSetMotionJson.WriteColliders(writer, definition.Colliders);
            AttachmentSetLiftJson.WriteStandoff(writer, definition.Standoff);
            writer.WriteStartArray("parts");
            foreach (AttachmentPartDefinition part in definition.Parts)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", part.Id);
                if (!rigidOnly)
                    writer.WriteString("kind", part is RigidAttachmentPartDefinition ? "rigid" : "skinned");
                writer.WriteStartObject("mesh");
                writer.WriteString("key", part.Mesh.Key);
                writer.WriteString("sha256", part.Mesh.Sha256);
                writer.WriteEndObject();
                if (part is RigidAttachmentPartDefinition rigid)
                {
                    writer.WriteString("joint", rigid.Joint);
                    writer.WriteStartArray("local");
                    foreach (float value in AttachmentSetDefinition.Values(rigid.Local))
                        writer.WriteNumberValue(value == 0 ? 0f : value);
                    writer.WriteEndArray();
                    if (tagged) writer.WriteString("region", rigid.Region);
                }
                else if (tagged && part is SkinnedAttachmentPartDefinition skinned)
                {
                    writer.WriteStartArray("regions");
                    foreach (NamedRegionRun run in skinned.Regions)
                    {
                        writer.WriteStartArray();
                        writer.WriteStringValue(run.Region);
                        writer.WriteNumberValue(run.TriangleCount);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    AttachmentSetMotionJson.WriteMotion(writer, skinned.Motion);
                    AttachmentSetLiftJson.WriteLift(writer, skinned.Lift);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    // Indexed by AttachmentLayer value.
    static readonly string[] LayerNames = ["body", "underclothes", "garment", "armour", "outerwear"];

    static AttachmentLayer ReadLayer(JsonElement root)
    {
        if (!root.TryGetProperty("layer", out _))
            throw new AttachmentSetDataException(AttachmentSetRefusal.LayerMissing, "root: a region-tagged set requires a layer.") { JsonPath = "$", PropertyName = "layer" };
        string name = Text(root, "layer", "root", "$");
        int index = Array.IndexOf(LayerNames, name);
        return index >= 0
            ? (AttachmentLayer)index
            : throw new AttachmentSetDataException(AttachmentSetRefusal.LayerUnknown, $"root: layer '{name}' is not a defined layer.") { JsonPath = "$.layer", LayerName = name };
    }

    static string[] ReadCovers(JsonElement root)
    {
        JsonElement covers = Required(root, "covers", "root", "$");
        if (covers.ValueKind != JsonValueKind.Array)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonValueShapeInvalid, "root: covers must be an array of region names.") { JsonPath = "$.covers" };
        var names = new string[covers.GetArrayLength()];
        int index = 0;
        foreach (JsonElement name in covers.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.String)
                throw new AttachmentSetDataException(AttachmentSetRefusal.JsonValueShapeInvalid, $"covers[{index}]: a covered region is a string.")
                { JsonPath = $"$.covers[{index}]", CoverIndex = index };
            names[index++] = name.GetString()!;
        }
        return names;
    }

    static NamedRegionRun[] ReadRuns(JsonElement item, uint partId, string jsonPath)
    {
        RequireRegionTag(item, "regions", partId, jsonPath);
        JsonElement array = item.GetProperty("regions");
        if (array.ValueKind != JsonValueKind.Array)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonValueShapeInvalid, $"part {partId}: regions must be an array of runs.")
            { JsonPath = jsonPath + ".regions", PartId = partId };
        var runs = new NamedRegionRun[array.GetArrayLength()];
        int runIndex = 0;
        foreach (JsonElement run in array.EnumerateArray())
        {
            string runPath = $"{jsonPath}.regions[{runIndex}]";
            if (run.ValueKind != JsonValueKind.Array || run.GetArrayLength() != 2 || run[0].ValueKind != JsonValueKind.String
                || run[1].ValueKind != JsonValueKind.Number || !run[1].TryGetInt32(out int triangles))
                throw new AttachmentSetDataException(AttachmentSetRefusal.JsonValueShapeInvalid, $"part {partId} run {runIndex}: a run is a [region, triangleCount] pair.")
                { JsonPath = runPath, PartId = partId, RunIndex = runIndex };
            runs[runIndex++] = new NamedRegionRun(run[0].GetString()!, triangles);
        }
        return runs;
    }

    static void RequireRegionTag(JsonElement item, string name, uint partId, string jsonPath)
    {
        if (!item.TryGetProperty(name, out _))
            throw new AttachmentSetDataException(AttachmentSetRefusal.PartRegionMissing, $"part {partId}: a region-tagged set requires '{name}' on every part.")
            { JsonPath = jsonPath, PropertyName = name, PartId = partId };
    }

    static void Members(JsonElement value, string context, string jsonPath, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonObjectRequired, $"{context}: expected an object.") { JsonPath = jsonPath };
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in value.EnumerateObject())
        {
            if (!found.Add(member.Name))
                throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPropertyDuplicate, $"{context}: duplicate property '{member.Name}'.")
                { JsonPath = jsonPath, PropertyName = member.Name };
            if (Array.IndexOf(allowed, member.Name) < 0)
                throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPropertyUnknown, $"{context}: unknown property '{member.Name}'.")
                { JsonPath = jsonPath, PropertyName = member.Name };
        }
    }

    static JsonElement Required(JsonElement value, string name, string context, string jsonPath) => value.TryGetProperty(name, out JsonElement result)
        ? result : throw new AttachmentSetDataException(AttachmentSetRefusal.JsonPropertyMissing, $"{context}: missing '{name}'.")
        { JsonPath = jsonPath, PropertyName = name };

    static string Text(JsonElement value, string name, string context, string jsonPath)
    {
        JsonElement member = Required(value, name, context, jsonPath);
        if (member.ValueKind != JsonValueKind.String)
            throw new AttachmentSetDataException(AttachmentSetRefusal.JsonStringRequired, $"{context}: '{name}' must be a string.")
            { JsonPath = AttachmentJsonPath.Property(jsonPath, name), PropertyName = name };
        return member.GetString()!;
    }

    internal static AttachmentSetRefusal StrictReason(StrictAttachmentJson.Fault fault) => fault switch
    {
        StrictAttachmentJson.Fault.UnknownProperty => AttachmentSetRefusal.JsonPropertyUnknown,
        StrictAttachmentJson.Fault.DuplicateProperty => AttachmentSetRefusal.JsonPropertyDuplicate,
        StrictAttachmentJson.Fault.MissingProperty => AttachmentSetRefusal.JsonPropertyMissing,
        StrictAttachmentJson.Fault.ValueShape => AttachmentSetRefusal.JsonValueShapeInvalid,
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };
}
