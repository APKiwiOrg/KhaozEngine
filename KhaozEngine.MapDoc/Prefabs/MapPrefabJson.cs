using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>One closed, bounded canonical codec for payloads, root persistence and identity.</summary>
internal static class MapPrefabJson
{
    internal static readonly MapPrefabExpansionLimits Limits = new();
    static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            foreach (JsonPropertyInfo property in info.Properties)
            {
                if (property.Name is "parentSpace" or "retired")
                {
                    property.IsRequired = false;
                    if (property.Name == "parentSpace") property.ShouldSerialize = (_, value) => value is not null;
                }
                if (info.Type == typeof(MapPlacement)) property.ShouldSerialize = (_, value) => value is not null;
                if (property.Name is "attachments" or "roofs" or "storeys" or "materializedAttachments")
                    property.ShouldSerialize = (_, value) => value is IEnumerable items && items.Cast<object>().Any();
            }
        });
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            TypeInfoResolver = resolver,
            MaxDepth = 64,
        };
        MapNativeJson.Configure(options);
        options.Converters.Add(new MapRationalJsonConverter());
        options.Converters.Add(new MapPatchKeyJsonConverter());
        options.Converters.Add(new ExactEnumConverter());
        options.Converters.Add(new ChildConverter());
        options.Converters.Add(new OverrideConverter());
        options.Converters.Add(new PatchConverter());
        options.Converters.Add(new RecordConverter());
        options.Converters.Add(new MediumConverter());
        options.Converters.Add(new WaterConverter());
        return options;
    }

    internal static T Read<T>(JsonElement value)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(value.GetRawText()) > Limits.MaxPayloadBytes) throw new MapDocumentException("Prefab payload byte limit exceeded.");
            Preflight(value);
            return value.Deserialize<T>(Options) ?? throw new MapDocumentException("Prefab value must not be null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException)
        {
            throw new MapDocumentException("Invalid prefab value: " + ex.Message, ex);
        }
    }

    internal static T Copy<T>(T value)
    {
        using JsonDocument json = JsonDocument.Parse(Bytes(value));
        return Read<T>(json.RootElement);
    }

    internal static string Digest<T>(string domain, T value) =>
        MapWaterJson.Hash(domain, w => Write(w, value));

    internal static byte[] Bytes<T>(T value)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream)) Write(w, value);
        if (stream.Length > Limits.MaxPayloadBytes) throw new MapDocumentException("Prefab payload byte limit exceeded.");
        return stream.ToArray();
    }

    internal static void Write<T>(Utf8JsonWriter w, T value)
    {
        // A child is always written through its closed converter, whatever its runtime type.
        if (value is MapPrefabChild child)
        {
            JsonSerializer.Serialize(w, child, typeof(MapPrefabChild), Options);
            return;
        }
        object? normalized = value switch
        {
            MapPrefabSet set => new MapPrefabSet(set.PayloadVersion, set.ExpansionVersion, set.Instances, set.Transfers)
            {
                Instances = set.Instances.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i with
                {
                    Bindings = new(i.Bindings.Entries.OrderBy(b => b.Child.Kind.ToString(), StringComparer.Ordinal).ThenBy(b => b.Child.Key, StringComparer.Ordinal).ToArray()),
                    Overrides = i.Overrides.OrderBy(o => o.Child.Kind.ToString(), StringComparer.Ordinal).ThenBy(o => o.Child.Key, StringComparer.Ordinal).ToArray(),
                }).ToArray(),
                Transfers = set.Transfers.OrderBy(t => t.InstanceId, StringComparer.Ordinal).ThenBy(t => t.Binding.Child.Kind.ToString(), StringComparer.Ordinal)
                    .ThenBy(t => t.Binding.Child.Key, StringComparer.Ordinal).ToArray(),
                Attachments = set.Attachments.OrderBy(a => a.PlacementId, StringComparer.Ordinal).ThenBy(a => a.Kind.ToString(), StringComparer.Ordinal)
                    .ThenBy(a => a.ResourceId, StringComparer.Ordinal).Select(a => a with
                    { Children = a.Children.OrderBy(c => c.LocalChild.Kind.ToString(), StringComparer.Ordinal).ThenBy(c => c.LocalChild.Key, StringComparer.Ordinal).ToArray() }).ToArray(),
            },
            MapPrefabAsset asset => asset with
            {
                Children = asset.Children.OrderBy(c => c.Key.Kind.ToString(), StringComparer.Ordinal).ThenBy(c => c.Key.Key, StringComparer.Ordinal).ToArray(),
                Roofs = Ordered(asset.Roofs),
                MaterializedAttachments = asset.MaterializedAttachments.OrderBy(r => r.Placement.Id, StringComparer.Ordinal)
                    .ThenBy(r => r.Kind.ToString(), StringComparer.Ordinal).ThenBy(r => r.ResourceId, StringComparer.Ordinal)
                    .Select(r => r with
                    {
                        Children = r.Children.OrderBy(c => c.Child.Kind.ToString(), StringComparer.Ordinal).ThenBy(c => c.Child.Id, StringComparer.Ordinal).ToArray(),
                    }).ToArray(),
            },
            MapBuildingIntent intent => new MapBuildingIntent(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, intent.Lights, intent.Markers)
            {
                RoofLinks = intent.RoofLinks.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => r with { Rooms = Ordered(r.Rooms), Groups = Ordered(r.Groups) }).ToArray(),
                RoofGroups = intent.RoofGroups.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => r with { Rooms = Ordered(r.Rooms) }).ToArray(),
                Lights = intent.Lights.OrderBy(l => l.Id, StringComparer.Ordinal).ToArray(),
                Markers = intent.Markers.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray(),
                Roofs = Ordered(intent.Roofs),
                Storeys = intent.Storeys.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(),
            },
            IReadOnlyList<MapStandaloneResourceTransform> poses => poses.OrderBy(p => p.Kind.ToString(), StringComparer.Ordinal).ThenBy(p => p.OwnerId, StringComparer.Ordinal).ToArray(),
            _ => value,
        };
        JsonSerializer.Serialize(w, normalized, Options);
    }

    static MapPrefabReference[] Ordered(IReadOnlyList<MapPrefabReference> references) => references
        .OrderBy(r => r.Scope.ToString(), StringComparer.Ordinal).ThenBy(r => r.Kind.ToString(), StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();

    // Check finite ceilings and duplicate names before creating payload collections or DTOs.
    internal static void Preflight(JsonElement value)
    {
        long edges = 0, patches = 0, records = 0, geometry = 0;
        var totals = new Dictionary<string, long>(StringComparer.Ordinal);
        Visit(value, "");
        void Visit(JsonElement node, string name, string parent = "")
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                if (node.TryGetProperty("width", out var width) && node.TryGetProperty("depth", out var depth))
                {
                    long x = width.GetInt32(), z = depth.GetInt32();
                    if (x is < 1 or > 64 || z is < 1 or > 64) throw new MapDocumentException("Invalid prefab patch dimensions.");
                    geometry += (x + 1) * (z + 1) + x * z;
                    if (geometry > Limits.MaxGeometryElements) throw new MapDocumentException("Prefab geometry limit exceeded.");
                }
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty p in node.EnumerateObject())
                {
                    if (!names.Add(p.Name)) throw new MapDocumentException("Duplicate prefab member '" + p.Name + "'.");
                    Visit(p.Value, p.Name, name);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                int count = node.GetArrayLength();
                int max = name switch
                {
                    "instances" => Limits.MaxInstances,
                    "children" => Limits.MaxChildren,
                    "references" => Limits.MaxReferenceEdges,
                    "patches" => Limits.MaxPatches,
                    "entries" => Limits.MaxBindings,
                    "overrides" => Limits.MaxOverrides,
                    "transfers" or "attachments" or "materializedAttachments" => Limits.MaxTransfers,
                    _ => Limits.MaxGeometryElements,
                };
                if (name is "instances" or "children" or "entries" or "overrides" or "transfers" or "attachments" or "materializedAttachments")
                {
                    totals.TryGetValue(name, out long total);
                    totals[name] = total + count;
                    if (totals[name] > max) throw new MapDocumentException("Prefab aggregate " + name + " limit exceeded.");
                }
                if (count > max) throw new MapDocumentException("Prefab " + name + " limit exceeded.");
                if (name == "references") edges += count;
                if (name == "patches") patches += count;
                if (name == "records") records += count;
                geometry += count;
                if (edges > Limits.MaxReferenceEdges || patches > Limits.MaxPatches || records > Limits.MaxRecords || geometry > Limits.MaxGeometryElements)
                    throw new MapDocumentException("Prefab aggregate collection limit exceeded.");
                foreach (JsonElement item in node.EnumerateArray()) Visit(item, name, parent);
            }
            else if (node.ValueKind == JsonValueKind.Null && !(parent == "supportBinding" && name is ("surfaceId" or "spaceId" or "referenceY" or "searchBelow" or "searchAbove")) && name is not ("numericId" or "parentSpace" or "owner" or "instanceId" or "previous" or "transform" or "assetId" or "tags" or
                "parent" or "aliasOf" or "sourcePatch" or "heightUnits" or "opening" or "surfaceId" or "bandTop" or "paintTargetSurfaceId" or "indoorSpan" or "anchor"))
                throw new MapDocumentException("Prefab member '" + name + "' must not be null.");
        }
    }

    static void Members(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new MapDocumentException("Expected prefab object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in value.EnumerateObject())
            if (!seen.Add(p.Name) || (!required.Contains(p.Name, StringComparer.Ordinal) && !optional.Contains(p.Name, StringComparer.Ordinal)))
                throw new MapDocumentException("Unknown or duplicate prefab member '" + p.Name + "'.");
        foreach (string name in required)
            if (!seen.Contains(name)) throw new MapDocumentException("Missing prefab member '" + name + "'.");
    }

    sealed class ChildConverter : JsonConverter<MapPrefabChild>
    {
        public override MapPrefabChild Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            JsonElement root = json.RootElement;
            string type = root.GetProperty("type").GetString()!;
            string[] payload = type switch { "Surface" => ["surface", "patches"], "Record" => ["value", "anchor"], "Storey" => ["name"], _ => ["value"] };
            Members(root, new[] { "type", "key" }.Concat(payload).ToArray(), "localTransform", "references");
            if (type == "Placement")
            {
                JsonElement value = root.GetProperty("value");
                Members(value, ["id", "kind", "assetId", "x", "z", "yaw", "scale", "tags"], "y", "numericId", "displayName", "supportBinding");
                if (value.TryGetProperty("numericId", out var numericId) && numericId.ValueKind != JsonValueKind.Null)
                    throw new MapDocumentException("Prefab placement numeric IDs belong to instance bindings.");
            }
            var key = MapPrefabJson.Read<MapPrefabChildKey>(root.GetProperty("key"));
            MapPrefabChild child = type switch
            {
                "Placement" => new MapPrefabPlacementChild(key, MapPrefabJson.Read<MapPlacement>(root.GetProperty("value"))),
                "Surface" => new MapPrefabSurfaceChild(key, MapPrefabJson.Read<MapSurfaceRef>(root.GetProperty("surface")), MapPrefabJson.Read<MapSurfacePatch[]>(root.GetProperty("patches"))),
                "Record" => new MapPrefabRecordChild(key, MapPrefabJson.Read<MapTopologyRecord>(root.GetProperty("value")), MapPrefabJson.Read<MapPatchKey>(root.GetProperty("anchor"))),
                "Storey" => new MapPrefabStoreyChild(key, root.GetProperty("name").GetString()!),
                "WaterMedium" => new MapPrefabMediumChild(key, MapPrefabJson.Read<MapWaterMediumDoc>(root.GetProperty("value"))),
                "WaterBody" => new MapPrefabWaterChild(key, MapPrefabJson.Read<MapWaterBodyDoc>(root.GetProperty("value"))),
                "Light" => new MapPrefabLightChild(key, MapPrefabJson.Read<MapLocalLightDoc>(root.GetProperty("value"))),
                "Marker" => new MapPrefabMarkerChild(key, MapPrefabJson.Read<MapLocalMarkerDoc>(root.GetProperty("value"))),
                "RoofLink" => new MapPrefabRoofChild(key, MapPrefabJson.Read<MapRoofLinkDoc>(root.GetProperty("value"))),
                "RoofGroup" => new MapPrefabRoofGroupChild(key, MapPrefabJson.Read<MapRoofGroupDoc>(root.GetProperty("value"))),
                _ => throw new MapDocumentException("Unsupported prefab child type '" + type + "'."),
            };
            return child with
            {
                LocalTransform = root.TryGetProperty("localTransform", out var transform) ? MapPrefabJson.Read<MapTransform>(transform) : child.LocalTransform,
                References = root.TryGetProperty("references", out var references) ? MapPrefabJson.Read<MapPrefabReferenceSlot[]>(references) : child.References,
            };
        }

        public override void Write(Utf8JsonWriter w, MapPrefabChild child, JsonSerializerOptions options)
        {
            w.WriteStartObject();
            w.WriteString("type", child.Key.Kind.ToString());
            Property(w, "key", child.Key);
            switch (child)
            {
                case MapPrefabPlacementChild p: Property(w, "value", p.Value); break;
                case MapPrefabSurfaceChild s:
                    Property(w, "surface", s.Surface); Property(w, "patches", s.Patches.OrderBy(p => p.Key).ToArray()); break;
                case MapPrefabRecordChild r: Property(w, "value", r.Value); Property(w, "anchor", r.Anchor); break;
                case MapPrefabStoreyChild s: w.WriteString("name", s.Name); break;
                case MapPrefabMediumChild m: Property(w, "value", m.Value); break;
                case MapPrefabWaterChild b: Property(w, "value", b.Value); break;
                case MapPrefabLightChild l: Property(w, "value", l.Value); break;
                case MapPrefabMarkerChild m: Property(w, "value", m.Value); break;
                case MapPrefabRoofChild r: Property(w, "value", r.Value with { Rooms = Ordered(r.Value.Rooms), Groups = Ordered(r.Value.Groups) }); break;
                case MapPrefabRoofGroupChild g: Property(w, "value", g.Value with { Rooms = Ordered(g.Value.Rooms) }); break;
                default: throw new MapDocumentException("Unsupported prefab child.");
            }
            Property(w, "localTransform", child.LocalTransform);
            Property(w, "references", child.References.OrderBy(r => r.Path, StringComparer.Ordinal).ToArray());
            w.WriteEndObject();
        }
    }

    static void Property<T>(Utf8JsonWriter w, string name, T value)
    {
        w.WritePropertyName(name);
        JsonSerializer.Serialize(w, value, Options);
    }

    sealed class OverrideConverter : JsonConverter<MapPrefabOverride>
    {
        public override MapPrefabOverride Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            JsonElement root = json.RootElement;
            string type = root.GetProperty("type").GetString()!;
            string[] required = type switch
            {
                "Replace" or "Add" => ["type", "child", "value"],
                "Transform" => ["type", "child", "transform"],
                _ => ["type", "child"],
            };
            Members(root, required, type == "Remove" ? ["previous"] : type == "Placement" ? ["transform", "assetId", "tags"] : []);
            var child = MapPrefabJson.Read<MapPrefabChildKey>(root.GetProperty("child"));
            T? Optional<T>(string name) => root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? MapPrefabJson.Read<T>(v) : default;
            return type switch
            {
                "Remove" => new MapPrefabRemoveOverride(child, Optional<MapPrefabOverride>("previous")),
                "Replace" => new MapPrefabReplaceOverride(child, MapPrefabJson.Read<MapPrefabChild>(root.GetProperty("value"))),
                "Add" => new MapPrefabAddOverride(child, MapPrefabJson.Read<MapPrefabChild>(root.GetProperty("value"))),
                "Transform" => new MapPrefabTransformOverride(child, MapPrefabJson.Read<MapTransform>(root.GetProperty("transform"))),
                "Placement" => new MapPrefabPlacementOverride(child, Optional<MapTransform?>("transform"), Optional<string>("assetId"), Optional<string[]>("tags")),
                _ => throw new MapDocumentException("Unsupported prefab override type."),
            };
        }

        public override void Write(Utf8JsonWriter w, MapPrefabOverride value, JsonSerializerOptions options)
        {
            w.WriteStartObject();
            w.WriteString("type", value switch { MapPrefabRemoveOverride => "Remove", MapPrefabReplaceOverride => "Replace", MapPrefabAddOverride => "Add", MapPrefabTransformOverride => "Transform", MapPrefabPlacementOverride => "Placement", _ => throw new MapDocumentException("Unsupported prefab override.") });
            Property(w, "child", value.Child);
            switch (value)
            {
                case MapPrefabRemoveOverride r when r.Previous is not null: Property(w, "previous", r.Previous); break;
                case MapPrefabReplaceOverride r: Property(w, "value", r.Value); break;
                case MapPrefabAddOverride a: Property(w, "value", a.Value); break;
                case MapPrefabTransformOverride t: Property(w, "transform", t.Transform); break;
                case MapPrefabPlacementOverride p:
                    if (p.Transform is { } transform) Property(w, "transform", transform);
                    if (p.AssetId is not null) w.WriteString("assetId", p.AssetId);
                    if (p.Tags is not null) Property(w, "tags", p.Tags);
                    break;
            }
            w.WriteEndObject();
        }
    }

    sealed class PatchConverter : JsonConverter<MapSurfacePatch>
    {
        public override MapSurfacePatch Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            return MapSurfacePatchCodec.Decode(Encoding.UTF8.GetBytes(json.RootElement.GetRawText()), MapPrefabJson.Read<MapPatchKey>(json.RootElement.GetProperty("key")));
        }
        public override void Write(Utf8JsonWriter w, MapSurfacePatch value, JsonSerializerOptions options) => w.WriteRawValue(MapSurfacePatchCodec.Encode(value));
    }

    sealed class RecordConverter : JsonConverter<MapTopologyRecord>
    {
        static MapSurfacePatch Envelope() => new() { Key = new("prefab-codec", 0, 0), Width = 1, Depth = 1, Heights = new int[4], Cells = new MapSurfaceCell[1], Presence = new ulong[1] };
        public override MapTopologyRecord Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            var patch = Envelope();
            var root = JsonNode.Parse(MapSurfacePatchCodec.Encode(patch))!.AsObject();
            root["records"] = new JsonArray(JsonNode.Parse(json.RootElement.GetRawText()));
            return MapSurfacePatchCodec.Decode(Encoding.UTF8.GetBytes(root.ToJsonString()), patch.Key).Records.Single();
        }
        public override void Write(Utf8JsonWriter w, MapTopologyRecord value, JsonSerializerOptions options)
        {
            var patch = Envelope(); patch.Records.Add(value);
            using JsonDocument json = JsonDocument.Parse(MapSurfacePatchCodec.Encode(patch));
            json.RootElement.GetProperty("records")[0].WriteTo(w);
        }
    }

    sealed class MediumConverter : JsonConverter<MapWaterMediumDoc>
    {
        public override MapWaterMediumDoc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            using JsonDocument set = JsonDocument.Parse("{\"media\":[" + json.RootElement.GetRawText() + "],\"bodies\":[]}");
            return MapWaterJson.Read(set.RootElement).Media.Single();
        }
        public override void Write(Utf8JsonWriter w, MapWaterMediumDoc value, JsonSerializerOptions options) => MapWaterJson.WriteMedium(w, value);
    }
    sealed class WaterConverter : JsonConverter<MapWaterBodyDoc>
    {
        public override MapWaterBodyDoc Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using JsonDocument json = JsonDocument.ParseValue(ref reader);
            using JsonDocument set = JsonDocument.Parse("{\"media\":[],\"bodies\":[" + json.RootElement.GetRawText() + "]}");
            return MapWaterJson.Read(set.RootElement).Bodies.Single();
        }
        public override void Write(Utf8JsonWriter w, MapWaterBodyDoc value, JsonSerializerOptions options) => MapWaterJson.WriteBody(w, value);
    }

    sealed class ExactEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;
        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(EnumConverter<>).MakeGenericType(typeToConvert))!;
    }
    sealed class EnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string? text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            foreach (T value in Enum.GetValues<T>()) if (value.ToString() == text) return value;
            throw new JsonException("Unsupported exact enum name.");
        }
        public override void Write(Utf8JsonWriter w, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value)) throw new JsonException("Unsupported enum value.");
            w.WriteStringValue(value.ToString());
        }
    }
}

public sealed class MapPrefabSetJsonConverter : JsonConverter<MapPrefabSet>
{
    public override MapPrefabSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    { using JsonDocument json = JsonDocument.ParseValue(ref reader); return MapPrefabJson.Read<MapPrefabSet>(json.RootElement); }
    public override void Write(Utf8JsonWriter w, MapPrefabSet value, JsonSerializerOptions options) => MapPrefabJson.Write(w, value);
}
public sealed class MapBuildingIntentJsonConverter : JsonConverter<MapBuildingIntent>
{
    public override MapBuildingIntent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    { using JsonDocument json = JsonDocument.ParseValue(ref reader); return MapPrefabJson.Read<MapBuildingIntent>(json.RootElement); }
    public override void Write(Utf8JsonWriter w, MapBuildingIntent value, JsonSerializerOptions options) => MapPrefabJson.Write(w, value);
}
public sealed class MapStandaloneTransformsJsonConverter : JsonConverter<IReadOnlyList<MapStandaloneResourceTransform>>
{
    public override IReadOnlyList<MapStandaloneResourceTransform> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    { using JsonDocument json = JsonDocument.ParseValue(ref reader); return MapPrefabJson.Read<MapStandaloneResourceTransform[]>(json.RootElement); }
    public override void Write(Utf8JsonWriter w, IReadOnlyList<MapStandaloneResourceTransform> value, JsonSerializerOptions options) => MapPrefabJson.Write(w, value);
}
