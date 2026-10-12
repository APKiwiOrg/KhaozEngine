using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A leaf support resource, payload 1: surfaces with their patches and embedded records, an optional space,
/// and one explicit scoped slot per embedded reference. Pointers are rooted at the resource, with surfaces in ID
/// order and patches in key order.</summary>
public sealed record MapSupportResource(int PayloadVersion, MapSurfaceSet Surfaces, MapRecordRef? Space,
    IReadOnlyList<MapPrefabReferenceSlot> References);

/// <summary>The closed, bounded support resource codec.</summary>
public static class MapSupportResourceFile
{
    static readonly string[] Required = ["payloadVersion", "surfaces", "patches", "references"];

    public static MapSupportResource Read(ReadOnlyMemory<byte> bytes, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        if (bytes.Length > MapPrefabJson.Limits.MaxPayloadBytes) throw new MapDocumentException("Support resource byte limit exceeded.");
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) Fail(resourceId, "expected an object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
                if (!seen.Add(property.Name) || !Required.Contains(property.Name, StringComparer.Ordinal) && property.Name != "space")
                    Fail(resourceId, "unknown or duplicate member '" + property.Name + "'");
            foreach (string name in Required)
                if (!seen.Contains(name)) Fail(resourceId, "missing member '" + name + "'");
            MapPrefabJson.Preflight(root);
            JsonElement payload = root.GetProperty("payloadVersion");
            int version = payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out int number) ? number : 0;
            var surfaces = new MapSurfaceSet();
            surfaces.Refs.AddRange(MapPrefabJson.Read<MapSurfaceRef[]>(root.GetProperty("surfaces")));
            foreach (MapSurfacePatch patch in MapPrefabJson.Read<MapSurfacePatch[]>(root.GetProperty("patches")))
                if (!surfaces.Patches.TryAdd(patch.Key, patch)) Fail(resourceId, "duplicate patch");
            MapRecordRef? space = root.TryGetProperty("space", out JsonElement value) ? MapPrefabJson.Read<MapRecordRef>(value) : null;
            var resource = new MapSupportResource(version, surfaces, space, MapPrefabJson.Read<MapPrefabReferenceSlot[]>(root.GetProperty("references")));
            Require(resource);
            return resource;
        }
        catch (JsonException ex) { throw new MapDocumentException("Invalid support resource '" + resourceId + "'.", ex); }
    }

    public static byte[] Write(MapSupportResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Require(resource);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("payloadVersion", resource.PayloadVersion);
            w.WritePropertyName("surfaces");
            MapPrefabJson.Write(w, resource.Surfaces.Refs.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray());
            w.WritePropertyName("patches");
            MapPrefabJson.Write(w, resource.Surfaces.Patches.Values.OrderBy(p => p.Key).ToArray());
            if (resource.Space is { } space) { w.WritePropertyName("space"); MapPrefabJson.Write(w, space); }
            w.WritePropertyName("references");
            MapPrefabJson.Write(w, resource.References.OrderBy(s => s.Path, StringComparer.Ordinal).ToArray());
            w.WriteEndObject();
        }
        if (stream.Length > MapPrefabJson.Limits.MaxPayloadBytes) throw new MapDocumentException("Support resource byte limit exceeded.");
        return stream.ToArray();
    }

    /// <summary>Payload, geometry and scope checks. Local references name this resource's own surfaces and records.</summary>
    internal static void Require(MapSupportResource resource)
    {
        if (resource.PayloadVersion != 1) throw new MapDocumentException("Unsupported support resource payload version.");
        if (resource.Surfaces is null || resource.References is null) throw new MapDocumentException("Support resource collections must not be null.");
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapSurfaceRef surface in resource.Surfaces.Refs)
            if (surface is null || string.IsNullOrWhiteSpace(surface.Id) || !surfaces.Add(surface.Id))
                throw new MapDocumentException("Support resource surfaces need distinct nonempty IDs.");
        var anchors = new Dictionary<string, MapPatchKey>(StringComparer.Ordinal);
        foreach (var (key, patch) in resource.Surfaces.Patches)
        {
            if (patch is null || patch.Key != key || !surfaces.Contains(key.SurfaceId))
                throw new MapDocumentException("Support resource patches must belong to a declared surface.");
            _ = MapSurfacePatchCodec.Encode(patch);
            foreach (MapTopologyRecord record in patch.Records)
                if (!anchors.TryAdd(record.Id, key)) throw new MapDocumentException($"Duplicate support resource record '{record.Id}'.");
        }
        var (_, occurrences) = MapPrefabReferenceWalker.Resource(resource, MapPrefabReferenceMap.Identity);
        MapPrefabReferences.RequireSlots(resource.References, occurrences, new Targets(surfaces, anchors, resource.Surfaces), null, "support resource");
    }

    sealed class Targets(HashSet<string> surfaces, Dictionary<string, MapPatchKey> anchors, MapSurfaceSet set) : IMapPrefabTargets
    {
        public bool Has(MapPrefabChildKind kind, string id) => kind switch
        {
            MapPrefabChildKind.Surface => surfaces.Contains(id),
            MapPrefabChildKind.Record => anchors.ContainsKey(id),
            _ => false,
        };
        public bool HasPatch(MapPatchKey key) => set.Patches.ContainsKey(key);
        public MapPatchKey? Anchor(string recordId) => anchors.TryGetValue(recordId, out MapPatchKey key) ? key : null;
        public MapTransform? Pose(MapPrefabChildKind kind, string id) => null;
    }

    [DoesNotReturn]
    static void Fail(string resourceId, string message) => throw new MapDocumentException($"Invalid support resource '{resourceId}': {message}.");
}
