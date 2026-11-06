using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>A leaf support resource, payload 1: surfaces with their patches and embedded records, an optional space,
/// and one explicit scoped slot per embedded reference. Pointers are rooted at the resource, with surfaces in ID
/// order and patches in key order. The encoded payload begins with the discriminant
/// <c>"resourceSchema":"kemap/support-resource"</c>.</summary>
public sealed record MapSupportResource(int PayloadVersion, MapSurfaceSet Surfaces, MapRecordRef? Space,
    IReadOnlyList<MapPrefabReferenceSlot> References);

/// <summary>The closed, bounded support resource codec.</summary>
public static class MapSupportResourceFile
{
    static readonly string[] Required = [MapTypedResourceSchema.Property, "payloadVersion", "surfaces", "patches", "references"];

    public static MapSupportResource Read(ReadOnlyMemory<byte> bytes, string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        RequirePayloadSize(bytes.Length, resourceId);
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) Fail(MapDocumentRefusal.SchemaObjectRequired, resourceId, "expected an object", "$");
            MapTypedResourceSchema.Require(root, MapTypedResourceSchema.Support, "Support resource", resourceId);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                bool duplicate = seen.Contains(property.Name);
                if (!seen.Add(property.Name) || !Required.Contains(property.Name, StringComparer.Ordinal) && property.Name != "space")
                    Fail(duplicate ? MapDocumentRefusal.SchemaMemberDuplicate : MapDocumentRefusal.SchemaMemberUnknown,
                        resourceId, "unknown or duplicate member '" + property.Name + "'", MapDocumentJsonPath.AppendProperty("$", property.Name));
            }
            foreach (string name in Required)
                if (!seen.Contains(name)) Fail(MapDocumentRefusal.SchemaMemberMissing, resourceId, "missing member '" + name + "'", MapDocumentJsonPath.AppendProperty("$", name));
            MapPrefabJson.Preflight(root);
            JsonElement payload = root.GetProperty("payloadVersion");
            bool versionIsInteger = payload.ValueKind == JsonValueKind.Number && payload.TryGetInt32(out _);
            int version = versionIsInteger ? payload.GetInt32() : 0;
            var surfaces = new MapSurfaceSet();
            surfaces.Refs.AddRange(MapPrefabJson.Read<MapSurfaceRef[]>(root.GetProperty("surfaces")));
            foreach (MapSurfacePatch patch in MapPrefabJson.Read<MapSurfacePatch[]>(root.GetProperty("patches")))
                if (!surfaces.Patches.TryAdd(patch.Key, patch)) Fail(MapDocumentRefusal.IdentityDuplicate, resourceId, "duplicate patch", "$.patches", patch.Key);
            MapRecordRef? space = root.TryGetProperty("space", out JsonElement value) ? MapPrefabJson.Read<MapRecordRef>(value) : null;
            var resource = new MapSupportResource(version, surfaces, space, MapPrefabJson.Read<MapPrefabReferenceSlot[]>(root.GetProperty("references")));
            Require(resource, resourceId, versionIsInteger);
            return resource;
        }
        catch (JsonException ex) { throw MapDocumentJsonErrors.Wrap(ex, null, "Invalid support resource '" + resourceId + "'.", resourceId: resourceId); }
        catch (MapDocumentException ex) when (ex.ResourceId is null)
        { throw ex.WithSourceName(null, ex.Message, resourceId: resourceId); }
    }

    public static byte[] Write(MapSupportResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        Require(resource);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString(MapTypedResourceSchema.Property, MapTypedResourceSchema.Support);
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
        RequirePayloadSize(stream.Length);
        return stream.ToArray();
    }

    // The same value-taking guard admits both actual byte counts and exact-cap fault tests without
    // allocating a 256 MiB payload. It does not bypass serialization or change the read/write boundary.
    internal static void RequirePayloadSize(long byteCount, string? resourceId = null)
    {
        long limit = MapPrefabJson.Limits.MaxPayloadBytes;
        if (byteCount > limit)
            throw new MapDocumentException(MapDocumentRefusal.WorkCapacityExceeded, "Support resource byte limit exceeded.")
            { ResourceId = resourceId, Budget = "MaxPayloadBytes", ActualByteCount = byteCount, Limit = limit };
    }

    /// <summary>Payload, geometry and scope checks. Local references name this resource's own surfaces and records.</summary>
    internal static void Require(MapSupportResource resource, string? resourceId = null, bool versionIsInteger = true)
    {
        if (resource.PayloadVersion != 1) throw new MapDocumentException(versionIsInteger ? MapDocumentRefusal.SchemaPayloadVersionUnsupported : MapDocumentRefusal.SchemaValueRequired, "Unsupported support resource payload version.")
        { ResourceId = resourceId, FieldPath = "$.payloadVersion", ActualVersion = versionIsInteger ? resource.PayloadVersion : null, SupportedVersions = [1] };
        if (resource.Surfaces is null || resource.References is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Support resource collections must not be null.")
        { ResourceId = resourceId, FieldPath = resource.Surfaces is null ? "$.surfaces" : "$.references" };
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        int index = -1;
        foreach (MapSurfaceRef surface in resource.Surfaces.Refs)
        {
            index++;
            if (surface is null || string.IsNullOrWhiteSpace(surface.Id) || !surfaces.Add(surface.Id))
                throw new MapDocumentException(surface is null || string.IsNullOrWhiteSpace(surface.Id)
                    ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.IdentityDuplicate,
                    "Support resource surfaces need distinct nonempty IDs.")
                { ResourceId = resourceId, SurfaceId = surface?.Id, Index = index, FieldPath = $"$.surfaces[{index}]" + (surface is null ? "" : ".id") };
            if (surface.Role != MapSurfaceRole.SupportFloor || surface.PresencePolicy != MapPresencePolicy.Native)
                throw new MapDocumentException(!Enum.IsDefined(surface.Role) || surface.Role == MapSurfaceRole.SupportFloor && !Enum.IsDefined(surface.PresencePolicy)
                    ? MapDocumentRefusal.SchemaEnumUnsupported : MapDocumentRefusal.SurfaceSupportRoleInvalid,
                    "Support resources declare native support floors only, without analytic terrain.")
                {
                    ResourceId = resourceId, SurfaceId = surface.Id, Index = index,
                    FieldPath = $"$.surfaces[{index}]." + (surface.Role != MapSurfaceRole.SupportFloor ? "role" : "presencePolicy"),
                    EnumType = surface.Role != MapSurfaceRole.SupportFloor ? typeof(MapSurfaceRole) : typeof(MapPresencePolicy),
                    ActualInteger = surface.Role != MapSurfaceRole.SupportFloor ? (byte)surface.Role : (byte)surface.PresencePolicy,
                };
            _ = surface.Frame.Corner(0, 0, 0);
        }
        var anchors = new Dictionary<string, MapPatchKey>(StringComparer.Ordinal);
        foreach (var (key, patch) in resource.Surfaces.Patches)
        {
            if (patch is null || patch.Key != key || !surfaces.Contains(key.SurfaceId))
                throw new MapDocumentException(patch is null ? MapDocumentRefusal.SchemaValueRequired :
                    patch.Key != key ? MapDocumentRefusal.AuthoredValueMismatch : MapDocumentRefusal.ReferenceMissing,
                    "Support resource patches must belong to a declared surface.")
                { ResourceId = resourceId, Patch = patch?.Key, ExpectedPatch = key, SurfaceId = key.SurfaceId, FieldPath = "$.patches" };
            _ = MapSurfacePatchCodec.Encode(patch);
            var surface = resource.Surfaces.Refs.Single(s => s.Id == key.SurfaceId);
            if (MapSurfaceCompiler.Compile(surface, patch).LegacyFallbackCells.Count != 0)
                // Native support admission above excludes the legacy-only fallback branch.
                throw new InvalidOperationException("Support resources cannot declare analytic fallback cells.");
            foreach (MapTopologyRecord record in patch.Records)
                if (!anchors.TryAdd(record.Id, key)) throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, $"Duplicate support resource record '{record.Id}'.")
                { ResourceId = resourceId, RecordId = record.Id, EntityId = record.Id, Patch = key, SubjectKind = "support resource record" };
        }
        var (_, occurrences) = MapPrefabReferenceWalker.Resource(resource, MapPrefabReferenceMap.Identity);
        MapPrefabReferences.RequireSlots(resource.References, occurrences, new Targets(surfaces, anchors, resource.Surfaces), null, "support resource", resourceId: resourceId);
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
    static void Fail(MapDocumentRefusal reason, string resourceId, string message, string fieldPath, MapPatchKey? patch = null) =>
        throw new MapDocumentException(reason, $"Invalid support resource '{resourceId}': {message}.")
        { ResourceId = resourceId, FieldPath = fieldPath, Patch = patch, SubjectKind = patch is null ? null : "support resource patch" };
}
