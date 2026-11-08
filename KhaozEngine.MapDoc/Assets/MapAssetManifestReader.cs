using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>Strict payload-1 parsing. Its closed options never affect legacy document serialization.</summary>
internal static class MapAssetManifestReader
{
    internal static MapAssetManifestDoc Read(byte[] bytes, string id)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes);
            RejectDuplicateMembers(json.RootElement);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectRequiredConstructorParameters = true
            };
            MapNativeJson.Configure(options);
            options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            var manifest = json.RootElement.Deserialize<MapAssetManifestDoc>(options)
                ?? throw new JsonException("Manifest must be an object.");
            if (manifest.PayloadVersion != 1) throw new JsonException("Unsupported native manifest payload version.");
            if (manifest.Assets is null || manifest.Resources is null) throw new JsonException("Manifest lists must not be null.");
            foreach (JsonElement resource in json.RootElement.GetProperty("resources").EnumerateArray())
            {
                if (resource.ValueKind != JsonValueKind.Object) throw new JsonException("Resource must be an object.");
                JsonElement kind = resource.GetProperty("kind");
                if (kind.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse(kind.GetString(), ignoreCase: false, out MapResourceKind parsed) ||
                    !Enum.IsDefined(parsed) || kind.GetString() != parsed.ToString())
                    throw new JsonException("Resource kind must be a supported exact enum name.");
            }
            for (int i = 0; i < manifest.Assets.Count; i++) ValidateAsset(manifest.Assets[i], $"$.assets[{i}]");
            for (int i = 0; i < manifest.Resources.Count; i++) ValidateResource(manifest.Resources[i], $"$.resources[{i}]");
            return manifest;
        }
        catch (JsonException ex)
        {
            throw MapDocumentJsonErrors.Wrap(ex, null, $"Invalid native manifest '{id}': {ex.Message}", resourceId: id);
        }
    }

    static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException($"Duplicate property '{property.Name}'.");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }

    internal static void ValidateReference(MapAssetRef reference, string fieldPath = "$")
    {
        if (reference is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Native resource reference must not be null.")
        { FieldPath = fieldPath };
        RequireText(reference.Id, "reference.id", Field(fieldPath, "id"), resourceId: reference.Id);
        RequireText(reference.Path, "reference.path", Field(fieldPath, "path"), resourceId: reference.Id);
        if (reference.PayloadVersion != 1)
            throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, $"Unsupported payload version for resource '{reference.Id}'.")
            { ResourceId = reference.Id, FieldPath = Field(fieldPath, "payloadVersion"), ActualVersion = reference.PayloadVersion, SupportedVersions = [1] };
        if (reference.Sha256 is null || reference.Sha256.Length != 64)
            throw new MapDocumentException(MapDocumentRefusal.DigestMalformed, $"Resource '{reference.Id}' needs a lowercase SHA-256 digest.")
            { ResourceId = reference.Id, FieldPath = Field(fieldPath, "sha256") };
        foreach (char c in reference.Sha256)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new MapDocumentException(MapDocumentRefusal.DigestMalformed, $"Resource '{reference.Id}' needs a lowercase SHA-256 digest.")
                { ResourceId = reference.Id, FieldPath = Field(fieldPath, "sha256") };
    }

    internal static void ValidateResource(MapResourceDoc resource, string fieldPath = "$")
    {
        if (resource is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Resource must not be null.")
        { FieldPath = fieldPath };
        ValidateReference(resource.Reference, Field(fieldPath, "reference"));
        if (!Enum.IsDefined(resource.Kind))
            throw new MapDocumentException(MapDocumentRefusal.AssetKindUnsupported, $"Unsupported resource kind '{resource.Kind}' for '{resource.Reference.Id}'.")
            { ResourceId = resource.Reference.Id, ActualResourceKind = resource.Kind, FieldPath = Field(fieldPath, "kind") };
        RequireIds(resource.Dependencies, "resource.dependencies", Field(fieldPath, "dependencies"), resourceId: resource.Reference.Id);
    }

    static void ValidateAsset(MapAssetDoc asset, string fieldPath)
    {
        if (asset is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Asset must not be null.")
        { FieldPath = fieldPath };
        RequireText(asset.Id, "asset.id", Field(fieldPath, "id"), asset.Id);
        RequireText(asset.MeshResourceId, "asset.meshResourceId", Field(fieldPath, "meshResourceId"), asset.Id);
        if (asset.CollisionResourceId is not null) RequireText(asset.CollisionResourceId, "asset.collisionResourceId", Field(fieldPath, "collisionResourceId"), asset.Id);
        if (asset.SelectionResourceId is not null) RequireText(asset.SelectionResourceId, "asset.selectionResourceId", Field(fieldPath, "selectionResourceId"), asset.Id);
        RequireIds(asset.SupportResourceIds, "asset.supportResourceIds", Field(fieldPath, "supportResourceIds"), asset.Id);
        RequireIds(asset.MaterialResourceIds, "asset.materialResourceIds", Field(fieldPath, "materialResourceIds"), asset.Id);
        RequireIds(asset.LodResourceIds, "asset.lodResourceIds", Field(fieldPath, "lodResourceIds"), asset.Id);
        RequireIds(asset.LightResourceIds, "asset.lightResourceIds", Field(fieldPath, "lightResourceIds"), asset.Id);
        RequireText(asset.Source, "asset.source", Field(fieldPath, "source"), asset.Id);
        RequireText(asset.License, "asset.license", Field(fieldPath, "license"), asset.Id);
        if (!float.IsFinite(asset.SourceUnitsToMetres) || asset.SourceUnitsToMetres <= 0)
            throw new MapDocumentException(float.IsFinite(asset.SourceUnitsToMetres) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite,
                $"Asset '{asset.Id}' sourceUnitsToMetres must be finite and positive.")
            {
                AssetId = asset.Id, FieldPath = Field(fieldPath, "sourceUnitsToMetres"),
                ActualNumber = float.IsFinite(asset.SourceUnitsToMetres) ? asset.SourceUnitsToMetres : null,
                MinimumNumber = float.IsFinite(asset.SourceUnitsToMetres) ? 0 : null,
            };
        ValidateBounds(asset.RenderBounds, asset.Id, Field(fieldPath, "renderBounds"));
        if (asset.LodBounds is { } lod) ValidateBounds(lod, asset.Id, Field(fieldPath, "lodBounds"));
        if (asset.LightBounds is { } light) ValidateBounds(light, asset.Id, Field(fieldPath, "lightBounds"));
    }

    static void ValidateBounds(MapLocalBounds bounds, string assetId, string fieldPath)
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        if (!Finite(bounds.Min) || !Finite(bounds.Max) || bounds.Min.X > bounds.Max.X ||
            bounds.Min.Y > bounds.Max.Y || bounds.Min.Z > bounds.Max.Z)
            throw new MapDocumentException(MapDocumentRefusal.BoundsInvalid, "Native local bounds must be finite and ordered on every axis.")
            { AssetId = assetId, FieldPath = fieldPath };
    }

    static void RequireIds(IReadOnlyList<string> ids, string field, string fieldPath, string? assetId = null, string? resourceId = null)
    {
        if (ids is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, $"{field} must not be null.")
        { AssetId = assetId, ResourceId = resourceId, FieldPath = fieldPath };
        for (int i = 0; i < ids.Count; i++) RequireText(ids[i], field, $"{fieldPath}[{i}]", assetId, resourceId);
    }

    static void RequireText(string text, string field, string fieldPath, string? assetId = null, string? resourceId = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, $"{field} must not be empty.")
        { AssetId = assetId, ResourceId = resourceId, FieldPath = fieldPath };
    }

    static string Field(string path, string name) => MapDocumentJsonPath.AppendProperty(path, name);

    internal static void ValidateAssetResources(MapAssetDoc asset, IReadOnlyDictionary<string, MapResourceDoc> resources)
    {
        foreach (var (id, kind) in ResourceReferences(asset))
            if (!resources.TryGetValue(id, out MapResourceDoc? resource) || resource.Kind != kind)
                throw new MapDocumentException(resource is null ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.ReferenceKindMismatch, $"Asset '{asset.Id}' requires {kind} resource '{id}'.")
                { AssetId = asset.Id, ResourceId = id, ExpectedResourceKind = kind, ActualResourceKind = resource?.Kind };
    }

    internal static IEnumerable<(string Id, MapResourceKind Kind)> ResourceReferences(MapAssetDoc asset) =>
        ResourceReferences(asset.MeshResourceId, asset.CollisionResourceId, asset.SelectionResourceId,
            asset.SupportResourceIds, asset.MaterialResourceIds, asset.LodResourceIds, asset.LightResourceIds);

    internal static IEnumerable<(string Id, MapResourceKind Kind)> ResourceReferences(MapResolvedAsset asset) =>
        ResourceReferences(asset.MeshResourceId, asset.CollisionResourceId, asset.SelectionResourceId,
            asset.SupportResourceIds, asset.MaterialResourceIds, asset.LodResourceIds, asset.LightResourceIds);

    static IEnumerable<(string Id, MapResourceKind Kind)> ResourceReferences(string mesh, string? collision, string? selection,
        IReadOnlyList<string> support, IReadOnlyList<string> materials, IReadOnlyList<string> lods, IReadOnlyList<string> lights)
    {
        yield return (mesh, MapResourceKind.Mesh);
        if (collision is not null) yield return (collision, MapResourceKind.Collider);
        if (selection is not null) yield return (selection, MapResourceKind.Selection);
        foreach (string id in support) yield return (id, MapResourceKind.Surface);
        foreach (string id in materials) yield return (id, MapResourceKind.Material);
        foreach (string id in lods) yield return (id, MapResourceKind.Lod);
        foreach (string id in lights) yield return (id, MapResourceKind.Light);
    }
}
