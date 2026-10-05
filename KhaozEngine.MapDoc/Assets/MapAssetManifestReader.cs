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
            foreach (MapAssetDoc asset in manifest.Assets) ValidateAsset(asset);
            foreach (MapResourceDoc resource in manifest.Resources) ValidateResource(resource);
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new MapDocumentException($"Invalid native manifest '{id}': {ex.Message}", ex);
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

    internal static void ValidateReference(MapAssetRef reference)
    {
        if (reference is null) throw new MapDocumentException("Native resource reference must not be null.");
        RequireText(reference.Id, "reference.id");
        RequireText(reference.Path, "reference.path");
        if (reference.PayloadVersion != 1)
            throw new MapDocumentException($"Unsupported payload version for resource '{reference.Id}'.");
        if (reference.Sha256 is null || reference.Sha256.Length != 64)
            throw new MapDocumentException($"Resource '{reference.Id}' needs a lowercase SHA-256 digest.");
        foreach (char c in reference.Sha256)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new MapDocumentException($"Resource '{reference.Id}' needs a lowercase SHA-256 digest.");
    }

    internal static void ValidateResource(MapResourceDoc resource)
    {
        if (resource is null) throw new MapDocumentException("Resource must not be null.");
        ValidateReference(resource.Reference);
        if (!Enum.IsDefined(resource.Kind) || resource.Kind == MapResourceKind.Prefab)
            throw new MapDocumentException($"Unsupported resource kind '{resource.Kind}' for '{resource.Reference.Id}'.");
        RequireIds(resource.Dependencies, "resource.dependencies");
    }

    static void ValidateAsset(MapAssetDoc asset)
    {
        if (asset is null) throw new MapDocumentException("Asset must not be null.");
        RequireText(asset.Id, "asset.id");
        RequireText(asset.MeshResourceId, "asset.meshResourceId");
        if (asset.CollisionResourceId is not null) RequireText(asset.CollisionResourceId, "asset.collisionResourceId");
        if (asset.SelectionResourceId is not null) RequireText(asset.SelectionResourceId, "asset.selectionResourceId");
        RequireIds(asset.SupportResourceIds, "asset.supportResourceIds");
        RequireIds(asset.MaterialResourceIds, "asset.materialResourceIds");
        RequireIds(asset.LodResourceIds, "asset.lodResourceIds");
        RequireIds(asset.LightResourceIds, "asset.lightResourceIds");
        RequireText(asset.Source, "asset.source");
        RequireText(asset.License, "asset.license");
        if (!float.IsFinite(asset.SourceUnitsToMetres) || asset.SourceUnitsToMetres <= 0)
            throw new MapDocumentException($"Asset '{asset.Id}' sourceUnitsToMetres must be finite and positive.");
        ValidateBounds(asset.RenderBounds);
        if (asset.LodBounds is { } lod) ValidateBounds(lod);
        if (asset.LightBounds is { } light) ValidateBounds(light);
    }

    static void ValidateBounds(MapLocalBounds bounds)
    {
        static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        if (!Finite(bounds.Min) || !Finite(bounds.Max) || bounds.Min.X > bounds.Max.X ||
            bounds.Min.Y > bounds.Max.Y || bounds.Min.Z > bounds.Max.Z)
            throw new MapDocumentException("Native local bounds must be finite and ordered on every axis.");
    }

    static void RequireIds(IReadOnlyList<string> ids, string field)
    {
        if (ids is null) throw new MapDocumentException($"{field} must not be null.");
        foreach (string id in ids) RequireText(id, field);
    }

    static void RequireText(string text, string field)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new MapDocumentException($"{field} must not be empty.");
    }

    internal static void ValidateAssetResources(MapAssetDoc asset, IReadOnlyDictionary<string, MapResourceDoc> resources)
    {
        foreach (var (id, kind) in ResourceReferences(asset))
            if (!resources.TryGetValue(id, out MapResourceDoc? resource) || resource.Kind != kind)
                throw new MapDocumentException($"Asset '{asset.Id}' requires {kind} resource '{id}'.");
    }

    internal static IEnumerable<(string Id, MapResourceKind Kind)> ResourceReferences(MapAssetDoc asset)
    {
        yield return (asset.MeshResourceId, MapResourceKind.Mesh);
        if (asset.CollisionResourceId is { } collision) yield return (collision, MapResourceKind.Collider);
        if (asset.SelectionResourceId is { } selection) yield return (selection, MapResourceKind.Selection);
        foreach (string id in asset.SupportResourceIds) yield return (id, MapResourceKind.Surface);
        foreach (string id in asset.MaterialResourceIds) yield return (id, MapResourceKind.Material);
        foreach (string id in asset.LodResourceIds) yield return (id, MapResourceKind.Lod);
        foreach (string id in asset.LightResourceIds) yield return (id, MapResourceKind.Light);
    }
}
