using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

// Internal implementation. System.Text.Json instantiates an attribute-named converter through its public parameterless constructor.
internal sealed class MapFoliageSetJsonConverter : JsonConverter<MapFoliageSet>
{
    readonly long _maxUtf8Bytes;

    public MapFoliageSetJsonConverter() : this(MapFoliagePayloadLimits.MaxUtf8Bytes) { }

    /// <summary>A converter refusing sections above <paramref name="maxUtf8Bytes"/>, so the pre-parse probe is
    /// observable at a small size.</summary>
    internal MapFoliageSetJsonConverter(long maxUtf8Bytes) => _maxUtf8Bytes = maxUtf8Bytes;

    // A null section is refused, not read as absent. An absent section is the only spelling of no foliage.
    public override bool HandleNull => true;

    public override MapFoliageSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) throw new MapFoliageJsonException(MapDocumentRefusal.FoliageSectionNull, "foliage: omit the section instead of writing null.") { FieldPath = "$" };
        // Measure the buffered value before parsing it, so an oversized section never reaches a document. A section the
        // reader cannot skip is not fully buffered and cannot be measured, so it refuses instead of escaping the limit.
        Utf8JsonReader probe = reader;
        if (!probe.TrySkip()) throw new MapFoliageJsonException(MapDocumentRefusal.FoliageSectionIncomplete, "foliage: the section must be fully buffered before it is read.") { FieldPath = "$" };
        if (probe.BytesConsumed - reader.TokenStartIndex > _maxUtf8Bytes)
            throw new MapFoliageJsonException(MapDocumentRefusal.FoliageSectionByteLimitExceeded, $"foliage: the section exceeds {_maxUtf8Bytes} UTF-8 bytes.")
            { FieldPath = "$", ActualByteCount = probe.BytesConsumed - reader.TokenStartIndex, Limit = _maxUtf8Bytes };
        using JsonDocument json = JsonDocument.ParseValue(ref reader);
        try { return MapFoliageJson.Read(json.RootElement, _maxUtf8Bytes); }
        catch (MapDocumentException ex) { throw MapFoliageJsonException.FromDocument(ex); }
    }

    public override void Write(Utf8JsonWriter writer, MapFoliageSet value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        MapFoliageJson.Write(writer, value);
    }
}

/// <summary>The closed canonical codec of foliage payload 1. Every member is required except a clearance rule's
/// <c>placementTag</c>. Names compare ordinally, so unknown, case-variant, repeated and null members refuse. Counts,
/// token lengths and the section's UTF-8 size are checked before nested records materialize or density decodes.
/// Density chunks are canonical base64 of exactly 1024 bytes.</summary>
internal static class MapFoliageJson
{
    static readonly string[] SetMembers = ["payloadVersion", "layers"];
    static readonly string[] LayerMembers =
    [
        "payloadVersion", "id", "support", "originX", "originZ", "cellSize", "width", "depth", "rowDirection", "seed", "spacing",
        "scaleMin", "scaleMax", "rootOffset", "models", "allowedVisibleMaterials", "materials", "policy", "excludeIndoors",
        "excludeSolidObjects", "doorClearance", "edgeFade", "compatibilityOrdinals", "clearanceRules", "exceptionMasks",
        "ownerSupports", "chunks",
    ];
    static readonly string[] SurfaceMembers = ["kind", "surfaceId"];
    static readonly string[] SpaceMembers = ["kind", "spaceId", "referenceY", "searchBelow", "searchAbove"];
    static readonly string[] ModelMembers = ["assetId", "weight"];
    static readonly string[] MaterialMembers = ["id", "resourceId", "kind"];
    static readonly string[] PolicyMembers = ["id", "version", "normalBoundaryHeight"];
    static readonly string[] OrdinalMembers = ["surfaceId", "ordinal"];
    static readonly string[] RuleMembers = ["id", "assetId", "kind", "footprint"];
    static readonly string[] RuleOptional = ["placementTag"];
    static readonly string[] MaskMembers = ["id", "owner", "support", "kind", "footprint"];
    static readonly string[] OwnerSupportMembers = ["owner", "support"];
    static readonly string[] ReferenceMembers = ["scope", "kind", "id"];
    static readonly string[] RectMembers = ["minX", "minZ", "maxX", "maxZ"];
    static readonly string[] ChunkMembers = ["x", "z", "density"];

    [ThreadStatic] static long t_payloadWrites;

    /// <summary>Whole inline payload serializations started on the calling thread. Counts actual calls, so a caller can
    /// prove a phase wrote no complete foliage payload.</summary>
    internal static long ThreadPayloadWrites => t_payloadWrites;

    internal static MapFoliageSet Read(JsonElement value) => Read(value, MapFoliagePayloadLimits.MaxUtf8Bytes);

    /// <summary>Reads under an explicit section size ceiling. The payload ceiling is <see cref="MapFoliagePayloadLimits.MaxUtf8Bytes"/>.</summary>
    internal static MapFoliageSet Read(JsonElement value, long maxUtf8Bytes) => ReadCore(value, maxUtf8Bytes, stored: false);

    internal static MapFoliageSet ReadStoredMetadata(JsonElement value) => ReadCore(value, MapFoliagePayloadLimits.MaxUtf8Bytes, stored: true);

    static MapFoliageSet ReadCore(JsonElement value, long maxUtf8Bytes, bool stored)
    {
        var at = new MapFoliageJsonContext("foliage", "$");
        if (value.ValueKind != JsonValueKind.Object) throw at.Refuse(MapDocumentRefusal.SchemaObjectRequired, "foliage must be an object.");
        if (JsonMarshal.GetRawUtf8Value(value).Length > maxUtf8Bytes)
            throw Fail($"foliage: the section exceeds {maxUtf8Bytes} UTF-8 bytes.");
        Closed(value, at, stored ? ["payloadVersion", "layers", "packs"] : SetMembers);
        RequireVersion(value, at);
        JsonElement layers = RequireArray(value, "layers", at, MapFoliagePayloadLimits.MaxLayers);

        // Every count is charged before any layer materializes, so an oversized payload allocates nothing nested.
        long documentChunks = 0;
        int index = 0;
        foreach (JsonElement layer in layers.EnumerateArray())
        {
            var label = new MapFoliageJsonContext($"foliage layer [{index}]", $"$.layers[{index}]", Index: index);
            index++;
            if (layer.ValueKind != JsonValueKind.Object) throw label.Refuse(MapDocumentRefusal.SchemaObjectRequired, label + " must be an object.");
            Closed(layer, label, LayerMembers);
            RequireVersion(layer, label);
            RequireArray(layer, "models", label, MapFoliagePayloadLimits.MaxModels);
            RequireArray(layer, "allowedVisibleMaterials", label, MapFoliagePayloadLimits.MaxMaterials);
            RequireArray(layer, "materials", label, MapFoliagePayloadLimits.MaxMaterials);
            RequireArray(layer, "compatibilityOrdinals", label, int.MaxValue);
            RequireArray(layer, "clearanceRules", label, MapFoliagePayloadLimits.MaxClearanceRules);
            RequireArray(layer, "exceptionMasks", label, MapFoliagePayloadLimits.MaxExceptionMasks);
            RequireArray(layer, "ownerSupports", label, MapFoliagePayloadLimits.MaxOwnerSupports);
            documentChunks += RequireArray(layer, "chunks", label, MapFoliagePayloadLimits.MaxChunksPerLayer).GetArrayLength();
            if (documentChunks > MapFoliagePayloadLimits.MaxDocumentChunks)
                throw Fail($"foliage: density exceeds {MapFoliagePayloadLimits.MaxDensityBytes} decoded bytes per document.");
        }
        return new MapFoliageSet(1, layers.EnumerateArray().Select((l, i) => ReadLayer(l, new($"foliage layer [{i}]", $"$.layers[{i}]", Index: i), !stored)).ToArray());
    }

    static MapFoliageLayer ReadLayer(JsonElement e, MapFoliageJsonContext label, bool readChunks)
    {
        string id = Text(e, "id", label);
        label = label with { Label = $"foliage layer '{id}'", LayerId = id };
        return new MapFoliageLayer(1, id, ReadBinding(e.GetProperty("support"), label.Property("support")),
            Finite(e, "originX", label), Finite(e, "originZ", label), Finite(e, "cellSize", label),
            Int(e, "width", label), Int(e, "depth", label), RowDirection(e, label), Int(e, "seed", label),
            Finite(e, "spacing", label), Finite(e, "scaleMin", label), Finite(e, "scaleMax", label), Finite(e, "rootOffset", label),
            Items(e, "models", label, (m, at) => { Closed(m, at, ModelMembers); return new MapFoliageModel(Text(m, "assetId", at), Finite(m, "weight", at)); }),
            e.GetProperty("allowedVisibleMaterials").EnumerateArray().Select((m, i) => MaterialId(m, label.Item("allowedVisibleMaterials", i) with { Label = label + " allowedVisibleMaterials" })).ToArray(),
            Items(e, "materials", label, (m, at) =>
            {
                Closed(m, at, MaterialMembers);
                return new MapFoliageMaterial(MaterialId(m.GetProperty("id"), at.Property("id")), Text(m, "resourceId", at),
                    Name<MapFoliageMaterialKind>(m, "kind", at));
            }),
            ReadPolicy(RequireObject(e, "policy", label), label.Property("policy")),
            Bool(e, "excludeIndoors", label), Bool(e, "excludeSolidObjects", label),
            Finite(e, "doorClearance", label), Finite(e, "edgeFade", label),
            Items(e, "compatibilityOrdinals", label, (o, at) =>
            {
                Closed(o, at, OrdinalMembers);
                return new MapFoliageCompatibilityOrdinal(Text(o, "surfaceId", at), Int(o, "ordinal", at));
            }),
            Items(e, "clearanceRules", label, (r, at) =>
            {
                Closed(r, at, RuleMembers, RuleOptional);
                return new MapFoliageClearanceRule(Text(r, "id", at), Text(r, "assetId", at),
                    r.TryGetProperty("placementTag", out _) ? Text(r, "placementTag", at) : null,
                    Name<MapFoliageClearanceKind>(r, "kind", at), ReadRect(RequireObject(r, "footprint", at), at.Property("footprint")));
            }),
            Items(e, "exceptionMasks", label, (m, at) =>
            {
                Closed(m, at, MaskMembers);
                return new MapFoliageMask(Text(m, "id", at), ReadReference(RequireObject(m, "owner", at), at.Property("owner")),
                    ReadBinding(m.GetProperty("support"), at.Property("support")), Name<MapFoliageClearanceKind>(m, "kind", at),
                    ReadRect(RequireObject(m, "footprint", at), at.Property("footprint")));
            }),
            Items(e, "ownerSupports", label, (o, at) =>
            {
                Closed(o, at, OwnerSupportMembers);
                return new MapFoliageOwnerSupport(ReadReference(RequireObject(o, "owner", at), at.Property("owner")),
                    ReadBinding(o.GetProperty("support"), at.Property("support")));
            }),
            readChunks ? Items(e, "chunks", label, (c, at) => ReadChunk(c, at)) : []);
    }

    /// <summary>The closed foliage support binding: a Surface binding carries only its surface ID, and a Space binding
    /// carries its space ID, reference height and both search extents.</summary>
    internal static MapSupportBinding ReadBinding(JsonElement e, string label) => ReadBinding(e, new MapFoliageJsonContext(label));

    static MapSupportBinding ReadBinding(JsonElement e, MapFoliageJsonContext label)
    {
        if (e.ValueKind != JsonValueKind.Object) throw label.Refuse(MapDocumentRefusal.SchemaObjectRequired, label + " must be an object.");
        if (!e.TryGetProperty("kind", out JsonElement kind))
            throw label.Property("kind").Refuse(MapDocumentRefusal.SchemaValueRequired, label + ": missing required property 'kind'.");
        switch (kind.ValueKind == JsonValueKind.String ? kind.GetString() : null)
        {
            case "Surface":
                Closed(e, label, SurfaceMembers);
                return new(MapSupportBindingKind.Surface, Text(e, "surfaceId", label), null, null, null, null);
            case "Space":
                Closed(e, label, SpaceMembers);
                return new(MapSupportBindingKind.Space, null, Text(e, "spaceId", label), Finite(e, "referenceY", label),
                    Finite(e, "searchBelow", label), Finite(e, "searchAbove", label));
            default: throw Fail(label + ": 'kind' must be Surface or Space.");
        }
    }

    internal static void WriteBinding(Utf8JsonWriter w, MapSupportBinding value, string label)
    {
        if (!MapSupportBindingRules.TryWrite(w, value)) throw Fail(label + " carries exactly the members of its kind.");
    }

    internal static void Write(Utf8JsonWriter w, MapFoliageSet set)
    {
        t_payloadWrites++;
        if (MapFoliageStorageWork.Current is { } work) work.InlineWrites++;
        MapFoliageStorage.RequireComplete(set);
        w.WriteStartObject();
        w.WriteNumber("payloadVersion", set.PayloadVersion);
        w.WriteStartArray("layers");
        foreach (MapFoliageLayer layer in set.Layers) WriteLayer(w, layer);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    internal static void WriteLayer(Utf8JsonWriter w, MapFoliageLayer layer,
        Action<Utf8JsonWriter, MapFoliageLayer>? writeChunks = null)
    {
        string label = $"foliage layer '{layer.Id}'";
        w.WriteStartObject();
        w.WriteNumber("payloadVersion", layer.PayloadVersion);
        w.WriteString("id", layer.Id);
        w.WritePropertyName("support");
        WriteBinding(w, layer.Support, label + " support");
        w.WriteNumber("originX", layer.OriginX);
        w.WriteNumber("originZ", layer.OriginZ);
        w.WriteNumber("cellSize", layer.CellSize);
        w.WriteNumber("width", layer.Width);
        w.WriteNumber("depth", layer.Depth);
        w.WriteString("rowDirection", layer.RowDirection.ToString());
        w.WriteNumber("seed", layer.Seed);
        w.WriteNumber("spacing", layer.Spacing);
        w.WriteNumber("scaleMin", layer.ScaleMin);
        w.WriteNumber("scaleMax", layer.ScaleMax);
        w.WriteNumber("rootOffset", layer.RootOffset);
        w.WriteStartArray("models");
        foreach (MapFoliageModel model in layer.Models)
        {
            w.WriteStartObject();
            w.WriteString("assetId", model.AssetId);
            w.WriteNumber("weight", model.Weight);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("allowedVisibleMaterials");
        foreach (ushort id in layer.AllowedVisibleMaterials) w.WriteNumberValue(id);
        w.WriteEndArray();
        w.WriteStartArray("materials");
        foreach (MapFoliageMaterial material in layer.Materials)
        {
            w.WriteStartObject();
            w.WriteNumber("id", material.Id);
            w.WriteString("resourceId", material.ResourceId);
            w.WriteString("kind", material.Kind.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("policy");
        w.WriteString("id", layer.Policy.Id);
        w.WriteNumber("version", layer.Policy.Version);
        w.WriteNumber("normalBoundaryHeight", layer.Policy.NormalBoundaryHeight);
        w.WriteEndObject();
        w.WriteBoolean("excludeIndoors", layer.ExcludeIndoors);
        w.WriteBoolean("excludeSolidObjects", layer.ExcludeSolidObjects);
        w.WriteNumber("doorClearance", layer.DoorClearance);
        w.WriteNumber("edgeFade", layer.EdgeFade);
        w.WriteStartArray("compatibilityOrdinals");
        foreach (MapFoliageCompatibilityOrdinal row in layer.CompatibilityOrdinals)
        {
            w.WriteStartObject();
            w.WriteString("surfaceId", row.SurfaceId);
            w.WriteNumber("ordinal", row.Ordinal);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("clearanceRules");
        foreach (MapFoliageClearanceRule rule in layer.ClearanceRules)
        {
            w.WriteStartObject();
            w.WriteString("id", rule.Id);
            w.WriteString("assetId", rule.AssetId);
            if (rule.PlacementTag is { } tag) w.WriteString("placementTag", tag);
            w.WriteString("kind", rule.Kind.ToString());
            WriteRect(w, rule.Footprint);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("exceptionMasks");
        foreach (MapFoliageMask mask in layer.ExceptionMasks)
        {
            w.WriteStartObject();
            w.WriteString("id", mask.Id);
            WriteReference(w, mask.Owner);
            w.WritePropertyName("support");
            WriteBinding(w, mask.Support, $"{label} mask '{mask.Id}' support");
            w.WriteString("kind", mask.Kind.ToString());
            WriteRect(w, mask.Footprint);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("ownerSupports");
        foreach (MapFoliageOwnerSupport owner in layer.OwnerSupports)
        {
            w.WriteStartObject();
            WriteReference(w, owner.Owner);
            w.WritePropertyName("support");
            WriteBinding(w, owner.Support, $"{label} owner support");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("chunks");
        if (writeChunks is not null) writeChunks(w, layer);
        else foreach (MapFoliageChunk chunk in layer.Chunks)
        {
            w.WriteStartObject();
            w.WriteNumber("x", chunk.Key.X);
            w.WriteNumber("z", chunk.Key.Z);
            w.WriteString("density", Convert.ToBase64String(chunk.Density));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static void WriteReference(Utf8JsonWriter w, MapPrefabReference owner)
    {
        w.WriteStartObject("owner");
        w.WriteString("scope", owner.Scope.ToString());
        w.WriteString("kind", owner.Kind.ToString());
        w.WriteString("id", owner.Id);
        w.WriteEndObject();
    }

    static void WriteRect(Utf8JsonWriter w, MapFoliageRect rect)
    {
        w.WriteStartObject("footprint");
        w.WriteNumber("minX", rect.MinX);
        w.WriteNumber("minZ", rect.MinZ);
        w.WriteNumber("maxX", rect.MaxX);
        w.WriteNumber("maxZ", rect.MaxZ);
        w.WriteEndObject();
    }

    static MapFoliagePolicy ReadPolicy(JsonElement e, MapFoliageJsonContext label)
    {
        Closed(e, label, PolicyMembers);
        return new(Text(e, "id", label), Int(e, "version", label), Finite(e, "normalBoundaryHeight", label));
    }

    static MapPrefabReference ReadReference(JsonElement e, MapFoliageJsonContext label)
    {
        Closed(e, label, ReferenceMembers);
        return new(Name<MapPrefabReferenceScope>(e, "scope", label), Name<MapPrefabChildKind>(e, "kind", label), Text(e, "id", label));
    }

    static MapFoliageRect ReadRect(JsonElement e, MapFoliageJsonContext label)
    {
        Closed(e, label, RectMembers);
        return new(Finite(e, "minX", label), Finite(e, "minZ", label), Finite(e, "maxX", label), Finite(e, "maxZ", label));
    }

    static MapFoliageChunk ReadChunk(JsonElement e, MapFoliageJsonContext label)
    {
        Closed(e, label, ChunkMembers);
        var key = new MapFoliageChunkKey(Int(e, "x", label), Int(e, "z", label));
        label = label with { Label = $"{label} ({key.X}, {key.Z})", FoliageChunk = key };
        JsonElement token = e.GetProperty("density");
        if (token.ValueKind != JsonValueKind.String) throw Fail(label + ": density must be a base64 string.");
        // An escaped token may spell each character in six bytes. Anything longer cannot be a legal token, so it refuses
        // before the string is materialized.
        if (JsonMarshal.GetRawUtf8Value(token).Length > 6 * MapFoliagePayloadLimits.MaxTokenChars + 2)
            throw Fail($"{label}: density token exceeds {MapFoliagePayloadLimits.MaxTokenChars} characters.");
        string text = token.GetString()!;
        if (text.Length > MapFoliagePayloadLimits.MaxTokenChars)
            throw Fail($"{label}: density token exceeds {MapFoliagePayloadLimits.MaxTokenChars} characters.");
        Span<byte> bytes = stackalloc byte[MapFoliagePayloadLimits.MaxTokenChars];
        if (!Convert.TryFromBase64String(text, bytes, out int written))
            throw label.Property("density").Refuse(MapDocumentRefusal.SchemaBase64Malformed, label + ": density is not valid base64.");
        if (written != MapFoliageChunk.ByteLength)
            throw label.Property("density").Refuse(MapDocumentRefusal.SchemaArrayLengthMismatch,
                $"{label}: density decodes to {written} bytes, not {MapFoliageChunk.ByteLength}.",
                actualByteCount: written, expectedByteCount: MapFoliageChunk.ByteLength);
        if (!string.Equals(Convert.ToBase64String(bytes[..written]), text, StringComparison.Ordinal))
            throw label.Property("density").Refuse(MapDocumentRefusal.SchemaBase64Malformed, label + ": density is not canonical base64.");
        return new MapFoliageChunk(key, bytes[..written]);
    }

    static T[] Items<T>(JsonElement e, string name, MapFoliageJsonContext label, Func<JsonElement, MapFoliageJsonContext, T> read)
    {
        JsonElement array = e.GetProperty(name);
        var result = new T[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            MapFoliageJsonContext at = label.Item(name, i);
            if (item.ValueKind != JsonValueKind.Object) throw at.Refuse(MapDocumentRefusal.SchemaObjectRequired, at + " must be an object.");
            result[i++] = read(item, at);
        }
        return result;
    }

    // Unknown, repeated and null members refuse, and every required member must be present. Names compare ordinally.
    internal static void Closed(JsonElement e, string label, string[] required, string[]? optional = null) =>
        Closed(e, new MapFoliageJsonContext(label), required, optional);

    static void Closed(JsonElement e, MapFoliageJsonContext label, string[] required, string[]? optional = null)
    {
        if (e.ValueKind != JsonValueKind.Object) throw label.Refuse(MapDocumentRefusal.SchemaObjectRequired, label + " must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in e.EnumerateObject())
        {
            if (!required.Contains(p.Name, StringComparer.Ordinal) && (optional is null || !optional.Contains(p.Name, StringComparer.Ordinal)))
                throw label.Property(p.Name).Refuse(MapDocumentRefusal.SchemaMemberUnknown, $"{label}: unknown property '{p.Name}'.");
            if (!seen.Add(p.Name)) throw label.Property(p.Name).Refuse(MapDocumentRefusal.IdentityDuplicate,
                $"{label}: duplicate property '{p.Name}'.", entityId: p.Name, subjectKind: "JSON member");
            if (p.Value.ValueKind == JsonValueKind.Null) throw label.Property(p.Name).Refuse(MapDocumentRefusal.SchemaValueRequired,
                $"{label}: property '{p.Name}' must not be null.");
        }
        foreach (string name in required)
            if (!seen.Contains(name)) throw label.Property(name).Refuse(MapDocumentRefusal.SchemaValueRequired,
                $"{label}: missing required property '{name}'.");
    }

    static void RequireVersion(JsonElement e, MapFoliageJsonContext label)
    {
        int version = Int(e, "payloadVersion", label);
        if (version != 1) throw label.Property("payloadVersion").Refuse(MapDocumentRefusal.SchemaPayloadVersionUnsupported,
            label + ": unsupported payloadVersion, only payload 1 is supported.", actualVersion: version);
    }

    internal static JsonElement RequireArray(JsonElement e, string name, string label, int max) =>
        RequireArray(e, name, new MapFoliageJsonContext(label), max);

    static JsonElement RequireArray(JsonElement e, string name, MapFoliageJsonContext label, int max)
    {
        JsonElement value = e.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array) throw label.Property(name).Refuse(MapDocumentRefusal.SchemaArrayRequired, $"{label}: '{name}' must be an array.");
        if (value.GetArrayLength() > max) throw Fail($"{label}: '{name}' exceeds {max} entries.");
        return value;
    }

    static JsonElement RequireObject(JsonElement e, string name, MapFoliageJsonContext label)
    {
        JsonElement value = e.GetProperty(name);
        return value.ValueKind == JsonValueKind.Object ? value : throw label.Property(name).Refuse(MapDocumentRefusal.SchemaObjectRequired, $"{label}: '{name}' must be an object.");
    }

    internal static string Text(JsonElement e, string name, string label) => Text(e, name, new MapFoliageJsonContext(label));

    static string Text(JsonElement e, string name, MapFoliageJsonContext label) => e.GetProperty(name) is { ValueKind: JsonValueKind.String } value
        ? value.GetString()! : throw label.Property(name).Refuse(MapDocumentRefusal.SchemaValueRequired, $"{label}: '{name}' must be a string.");

    static float Finite(JsonElement e, string name, MapFoliageJsonContext label)
    {
        JsonElement value = e.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float number) && float.IsFinite(number)) return number;
        throw label.Property(name).Refuse(MapDocumentRefusal.ValueNonfinite, $"{label}: '{name}' must be a finite number.",
            actualNumber: Number(value));
    }

    internal static int Int(JsonElement e, string name, string label) => Int(e, name, new MapFoliageJsonContext(label));

    static int Int(JsonElement e, string name, MapFoliageJsonContext label)
    {
        JsonElement value = e.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number;
        throw label.Property(name).Refuse(MapDocumentRefusal.ValueOutsideRange, $"{label}: '{name}' must be a 32-bit integer.",
            actualNumber: Number(value), actualInteger: Integer(value), minimumInteger: int.MinValue, maximumInteger: int.MaxValue);
    }

    static bool Bool(JsonElement e, string name, MapFoliageJsonContext label) => e.GetProperty(name).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw label.Property(name).Refuse(MapDocumentRefusal.SchemaBooleanRequired, $"{label}: '{name}' must be a boolean."),
    };

    static ushort MaterialId(JsonElement e, MapFoliageJsonContext label)
    {
        if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt16(out ushort id)) return id;
        throw label.Refuse(MapDocumentRefusal.ValueOutsideRange, label + ": material IDs are integers from 0 to 65535.",
            actualNumber: Number(e), actualInteger: Integer(e), minimumInteger: 0, maximumInteger: ushort.MaxValue);
    }

    // TryGetDouble reads an out-of-range literal such as 1e400 as an infinity the input never held, so only a finite
    // value is kept as the refused number.
    static double? Number(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
    static long? Integer(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out long number) ? number : null;

    static MapRowDirection RowDirection(JsonElement e, MapFoliageJsonContext label) => Name<MapRowDirection>(e, "rowDirection", label);

    // Exact declared names only. Numeric spellings and case variants refuse.
    static T Name<T>(JsonElement e, string name, MapFoliageJsonContext label) where T : struct, Enum =>
        e.GetProperty(name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { } text &&
        Enum.GetNames<T>().Contains(text, StringComparer.Ordinal)
            ? Enum.Parse<T>(text) : throw Fail($"{label}: '{name}' must be one of {string.Join(", ", Enum.GetNames<T>())}.");

    static MapDocumentException Fail(string message) => new(message);
}
