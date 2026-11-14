using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Finds a required member missing from a prefab codec value before deserialization, so the refusal is
/// <see cref="MapDocumentRefusal.SchemaMemberMissing"/> at a <c>FieldPath</c> rooted at the document and naming the
/// member. A value embedded in a larger file, such as a map's <c>prefabs</c> block or a support file's
/// <c>surfaces</c>, is read at its member path. Without this pass a member missing inside a value a converter reads
/// separately, such as a child's surface or an override's child, surfaced as the serializer's bare
/// <see cref="JsonException"/>, read as <see cref="MapDocumentRefusal.SchemaMalformed"/> at a path relative to that
/// nested value without the member name. Required members come from the codec's resolved metadata, which is what the
/// serializer enforces, and from <see cref="MapPrefabPayloadShapes"/>, the tables the child and override converters
/// read. A value a dedicated codec reads (topology records, patches, water, marker support bindings, patch keys, lattice
/// units, vectors and enums) keeps that codec's own refusal.</summary>
internal static class MapPrefabMissingMembers
{
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
        if (!TypeOf(node, path, out string? type)) return;
        Members(node, path, [MapPrefabPayloadShapes.Key, .. MapPrefabPayloadShapes.ChildPayload(type)], required: true, options);
        Members(node, path, MapPrefabPayloadShapes.ChildOptional, required: false, options);
        if (type == "Placement" && node.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.Object)
            foreach (string name in MapPrefabPayloadShapes.PlacementRequired)
                if (!value.TryGetProperty(name, out _)) throw Missing(MapDocumentJsonPath.AppendProperty(path, "value"), name);
    }

    // Only the members this override type admits are walked. A foreign member is left to the converter, which refuses
    // it as unknown.
    static void Override(JsonElement node, string path, JsonSerializerOptions options)
    {
        if (!TypeOf(node, path, out string? type)) return;
        MapPrefabPayloadShapes.OverrideShape shape = MapPrefabPayloadShapes.Override(type);
        foreach (string name in shape.RequiredNames)
            if (!node.TryGetProperty(name, out _)) throw Missing(path, name);
        Members(node, path, shape.Admitted.ToArray(), required: false, options);
    }

    static bool TypeOf(JsonElement node, string path, [NotNullWhen(true)] out string? type)
    {
        type = null;
        if (node.ValueKind != JsonValueKind.Object) return false;
        if (!node.TryGetProperty(MapPrefabPayloadShapes.TypeName.Name, out JsonElement kind)) throw Missing(path, MapPrefabPayloadShapes.TypeName.Name);
        if (kind.ValueKind != JsonValueKind.String) return false;
        type = kind.GetString()!;
        return true;
    }

    static void Members(JsonElement node, string path, MapPrefabPayloadShapes.Member[] members, bool required, JsonSerializerOptions options)
    {
        foreach (MapPrefabPayloadShapes.Member member in members)
        {
            if (node.TryGetProperty(member.Name, out JsonElement value)) Visit(value, member.Type, MapDocumentJsonPath.AppendProperty(path, member.Name), options);
            else if (required) throw Missing(path, member.Name);
        }
    }

    static MapDocumentException Missing(string path, string name) =>
        new(MapDocumentRefusal.SchemaMemberMissing, "Missing prefab member '" + name + "'.")
        { FieldPath = MapDocumentJsonPath.AppendProperty(path, name) };
}
