using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Finds a required member missing from a prefab codec value before deserialization, so the refusal is
/// <see cref="MapDocumentRefusal.SchemaMemberMissing"/> at a <c>FieldPath</c> rooted at the document and naming the
/// member. A map document's prefab blocks are read at their member path, such as <c>$.prefabs</c>. Without this pass a
/// member missing inside a value a converter reads separately, such as a child's surface or an override's child,
/// surfaced as the serializer's bare <see cref="JsonException"/>, read as <see cref="MapDocumentRefusal.SchemaMalformed"/>
/// at a path relative to that nested value without the member name. Required members come from the codec's resolved
/// metadata, which is what the serializer enforces, and from the closed member tables the child and override converters
/// read. A value a dedicated codec reads (topology records, patches, water, marker support bindings, patch keys, lattice
/// units, vectors and enums) keeps that codec's own refusal.</summary>
internal static class MapPrefabMissingMembers
{
    /// <summary>A payload member of a child or override and the type its converter reads it as.</summary>
    internal readonly record struct Member(string Name, Type Type);

    /// <summary>The members a placement child's value must carry. Its DTO admits more, so the converter checks these.</summary>
    internal static readonly string[] PlacementRequired = ["id", "kind", "assetId", "x", "z", "yaw", "scale", "tags"];

    static readonly Member[] ChildCommon = [new("type", typeof(string)), new("key", typeof(MapPrefabChildKey))];
    static readonly Member[] ChildOptional = [new("localTransform", typeof(MapTransform)), new("references", typeof(MapPrefabReferenceSlot[]))];
    static readonly Member[] UnknownChildPayload = [new("value", typeof(object))];
    static readonly Dictionary<string, Member[]> ChildPayloads = new(StringComparer.Ordinal)
    {
        ["Placement"] = [new("value", typeof(MapPlacement))],
        ["Surface"] = [new("surface", typeof(MapSurfaceRef)), new("patches", typeof(MapSurfacePatch[]))],
        ["Record"] = [new("value", typeof(MapTopologyRecord)), new("anchor", typeof(MapPatchKey))],
        ["Storey"] = [new("name", typeof(string))],
        ["WaterMedium"] = [new("value", typeof(MapWaterMediumDoc))],
        ["WaterBody"] = [new("value", typeof(MapWaterBodyDoc))],
        ["Light"] = [new("value", typeof(MapLocalLightDoc))],
        ["Marker"] = [new("value", typeof(MapLocalMarkerDoc))],
        ["RoofLink"] = [new("value", typeof(MapRoofLinkDoc))],
        ["RoofGroup"] = [new("value", typeof(MapRoofGroupDoc))],
    };

    // An override member keeps one type whatever the override type, so one table serves every override.
    static readonly Dictionary<string, Type> OverrideMembers = new(StringComparer.Ordinal)
    {
        ["type"] = typeof(string),
        ["child"] = typeof(MapPrefabChildKey),
        ["value"] = typeof(MapPrefabChild),
        ["transform"] = typeof(MapTransform),
        ["previous"] = typeof(MapPrefabOverride),
        ["assetId"] = typeof(string),
        ["tags"] = typeof(string[]),
    };

    /// <summary>The payload member names a child of this type requires. An unsupported type requires a value and is
    /// then refused by kind.</summary>
    internal static string[] ChildPayloadNames(string type) =>
        Array.ConvertAll(ChildPayloads.GetValueOrDefault(type, UnknownChildPayload), m => m.Name);

    /// <summary>The members an override of this type requires.</summary>
    internal static string[] OverrideRequired(string type) => type switch
    {
        "Replace" or "Add" => ["type", "child", "value"],
        "Transform" => ["type", "child", "transform"],
        _ => ["type", "child"],
    };

    /// <summary>Refuses the first required member missing from <paramref name="value"/> read as <paramref name="type"/>,
    /// where <paramref name="fieldPath"/> is the value's path in its document. A value of the wrong kind or an explicit
    /// null is left to the reader that refuses it today.</summary>
    internal static void Require(JsonElement value, Type type, JsonSerializerOptions options, string fieldPath) =>
        Visit(value, type, fieldPath, options);

    static void Visit(JsonElement node, Type type, string path, JsonSerializerOptions options)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(MapPrefabChild)) { Child(node, path, options); return; }
        if (type == typeof(MapPrefabOverride)) { Override(node, path, options); return; }
        JsonTypeInfo info = options.GetTypeInfo(type);
        if (info.Kind == JsonTypeInfoKind.Object && node.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonPropertyInfo property in info.Properties)
            {
                if (!node.TryGetProperty(property.Name, out JsonElement member))
                {
                    if (property.IsRequired) throw Missing(path, property.Name);
                }
                // A member with its own converter is read by that converter, which owns its refusals.
                else if (property.CustomConverter is null) Visit(member, property.PropertyType, MapDocumentJsonPath.AppendProperty(path, property.Name), options);
            }
        }
        else if (info.Kind == JsonTypeInfoKind.Enumerable && node.ValueKind == JsonValueKind.Array && info.ElementType is { } element)
        {
            int index = 0;
            foreach (JsonElement item in node.EnumerateArray()) Visit(item, element, $"{path}[{index++}]", options);
        }
        else if (info.Kind == JsonTypeInfoKind.Dictionary && node.ValueKind == JsonValueKind.Object && info.ElementType is { } entry)
            foreach (JsonProperty item in node.EnumerateObject()) Visit(item.Value, entry, MapDocumentJsonPath.AppendProperty(path, item.Name), options);
    }

    static void Child(JsonElement node, string path, JsonSerializerOptions options)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (!node.TryGetProperty("type", out JsonElement kind)) throw Missing(path, "type");
        if (kind.ValueKind != JsonValueKind.String) return;
        string type = kind.GetString()!;
        Members(node, path, ChildCommon, required: true, options);
        Members(node, path, ChildPayloads.GetValueOrDefault(type, UnknownChildPayload), required: true, options);
        Members(node, path, ChildOptional, required: false, options);
        if (type == "Placement" && node.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.Object)
            foreach (string name in PlacementRequired)
                if (!value.TryGetProperty(name, out _)) throw Missing(MapDocumentJsonPath.AppendProperty(path, "value"), name);
    }

    static void Override(JsonElement node, string path, JsonSerializerOptions options)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        if (!node.TryGetProperty("type", out JsonElement kind)) throw Missing(path, "type");
        if (kind.ValueKind != JsonValueKind.String) return;
        foreach (string name in OverrideRequired(kind.GetString()!))
            if (!node.TryGetProperty(name, out _)) throw Missing(path, name);
        foreach (JsonProperty member in node.EnumerateObject())
            if (OverrideMembers.TryGetValue(member.Name, out Type? type))
                Visit(member.Value, type, MapDocumentJsonPath.AppendProperty(path, member.Name), options);
    }

    static void Members(JsonElement node, string path, Member[] members, bool required, JsonSerializerOptions options)
    {
        foreach (Member member in members)
        {
            if (node.TryGetProperty(member.Name, out JsonElement value)) Visit(value, member.Type, MapDocumentJsonPath.AppendProperty(path, member.Name), options);
            else if (required) throw Missing(path, member.Name);
        }
    }

    static MapDocumentException Missing(string path, string name) =>
        new(MapDocumentRefusal.SchemaMemberMissing, "Missing prefab member '" + name + "'.")
        { FieldPath = MapDocumentJsonPath.AppendProperty(path, name) };
}
