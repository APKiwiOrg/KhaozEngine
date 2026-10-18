using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Foliage payload 1 rules. <see cref="Validate"/> checks a set on its own, the same rules the closed codec and
/// the schema state. <see cref="ValidateBound"/> adds metadata and resource roles against a verified closure without
/// expanding prefabs. Effective owners and effective support surfaces are checked after expansion.</summary>
public static class MapFoliageValidation
{
    internal const string LegacyTilePolicy = "legacy-tile";
    internal const int LegacyTilePolicyVersion = 1;

    static readonly MapPrefabChildKind[] OwnerKinds =
        [MapPrefabChildKind.Placement, MapPrefabChildKind.Surface, MapPrefabChildKind.Record, MapPrefabChildKind.Marker];

    public static IReadOnlyList<string> Validate(MapFoliageSet set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var errors = new List<string>();
        if (set.PayloadVersion != 1) errors.Add("foliage: unsupported payloadVersion, only payload 1 is supported.");
        if (set.Layers.Count > MapFoliagePayloadLimits.MaxLayers)
            errors.Add($"foliage: more than {MapFoliagePayloadLimits.MaxLayers} layers.");
        if (set.Layers.Sum(l => (long)l.Chunks.Count) > MapFoliagePayloadLimits.MaxDocumentChunks)
            errors.Add($"foliage: density exceeds {MapFoliagePayloadLimits.MaxDensityBytes} decoded bytes per document.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set.Layers)
        {
            if (string.IsNullOrEmpty(layer.Id)) errors.Add("foliage: every layer needs a non-empty id.");
            else if (!ids.Add(layer.Id)) errors.Add($"foliage: duplicate layer id '{layer.Id}'.");
            ValidateLayer(layer, $"foliage layer '{layer.Id}'", errors);
        }
        return errors;
    }

    /// <summary>Checks the set, then every role a verified closure and the document's declared surfaces can answer: model
    /// and clearance assets exist, material resources are Material resources, and Surface supports and compatibility
    /// rows name a declared support floor or a bound prefab surface. Never expands prefabs.</summary>
    public static IReadOnlyList<string> ValidateBound(MapDocument document, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        if (document.Foliage is not { } set) return Array.Empty<string>();
        var errors = new List<string>(Validate(set));
        var assetIds = new HashSet<string>(assets.Assets.Select(a => a.Id), StringComparer.Ordinal);
        var declared = (document.Surfaces?.Refs ?? []).Where(s => s?.Id is not null).GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Role, StringComparer.Ordinal);
        // Bound prefab surfaces are known by their persisted world IDs. Their roles are checked after expansion.
        var bound = new HashSet<string>(document.Prefabs?.Instances.Where(i => !i.Removed)
            .SelectMany(i => i.Bindings.Entries)
            .Where(b => !b.Removed && b.Child.Kind == MapPrefabChildKind.Surface).Select(b => b.WorldId) ?? Enumerable.Empty<string>(),
            StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set.Layers)
        {
            string label = $"foliage layer '{layer.Id}'";
            foreach (MapFoliageModel model in layer.Models)
                if (!assetIds.Contains(model.AssetId)) errors.Add($"{label}: model asset '{model.AssetId}' is not in the verified closure.");
            foreach (MapFoliageMaterial material in layer.Materials)
                if (!assets.TryGetResource(material.ResourceId, out MapResolvedResource? resource) || resource!.Kind != MapResourceKind.Material)
                    errors.Add($"{label}: material {material.Id} resource '{material.ResourceId}' is not a Material resource.");
            foreach (MapFoliageClearanceRule rule in layer.ClearanceRules)
                if (!assetIds.Contains(rule.AssetId)) errors.Add($"{label} rule '{rule.Id}': asset '{rule.AssetId}' is not in the verified closure.");
            if (layer.Support is { Kind: MapSupportBindingKind.Surface, SurfaceId: { Length: > 0 } support })
                RequireSupportSurface(support, declared, bound, label + " support", errors);
            // Empty IDs are already reported by the set rules.
            foreach (MapFoliageCompatibilityOrdinal row in layer.CompatibilityOrdinals.Where(r => !string.IsNullOrEmpty(r.SurfaceId)))
                RequireSupportSurface(row.SurfaceId, declared, bound, label + " compatibility ordinal", errors);
        }
        return errors;
    }

    /// <summary>The document-level gate run by <see cref="MapDocumentValidator"/>: foliage needs a resolver-2 document, then
    /// the set rules.</summary>
    internal static void ValidateDocument(MapDocument doc, List<string> errors)
    {
        if (doc.Foliage is not { } set) return;
        if (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } || doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
            errors.Add("foliage: requires resolver identity (1, 2) and supportRecipe AuthoredBindingsV2. Adopt authored support " +
                "with MapResolverAdoption.ConvertToAuthoredSupport before adding foliage.");
        errors.AddRange(Validate(set));
    }

    static void RequireSupportSurface(string id, Dictionary<string, MapSurfaceRole> declared, HashSet<string> bound, string label,
        List<string> errors)
    {
        if (declared.TryGetValue(id, out MapSurfaceRole role))
        {
            if (role != MapSurfaceRole.SupportFloor) errors.Add($"{label}: surface '{id}' is not a support floor.");
        }
        else if (!bound.Contains(id)) errors.Add($"{label}: surface '{id}' is not a declared or bound surface.");
    }

    static void ValidateLayer(MapFoliageLayer layer, string label, List<string> errors)
    {
        if (layer.PayloadVersion != 1) errors.Add(label + ": unsupported payloadVersion, only payload 1 is supported.");
        Binding(layer.Support, label + " support", errors);
        if (!float.IsFinite(layer.OriginX) || !float.IsFinite(layer.OriginZ)) errors.Add(label + ": origin must be finite.");
        if (!Positive(layer.CellSize)) errors.Add(label + ": cellSize must be finite and positive.");
        if (layer.Width is < 1 or > MapFoliagePayloadLimits.MaxExtent || layer.Depth is < 1 or > MapFoliagePayloadLimits.MaxExtent)
            errors.Add($"{label}: width and depth must be from 1 to {MapFoliagePayloadLimits.MaxExtent}.");
        if (layer.RowDirection != MapRowDirection.PositiveZ) errors.Add(label + ": density rows must advance positive world Z.");
        if (!Positive(layer.Spacing)) errors.Add(label + ": spacing must be finite and positive.");
        if (!Positive(layer.ScaleMin) || !float.IsFinite(layer.ScaleMax) || layer.ScaleMax < layer.ScaleMin)
            errors.Add(label + ": scale range must be finite, positive and ordered.");
        if (!float.IsFinite(layer.RootOffset)) errors.Add(label + ": rootOffset must be finite.");
        if (!float.IsFinite(layer.DoorClearance) || layer.DoorClearance < 0) errors.Add(label + ": doorClearance must be finite and nonnegative.");
        if (!float.IsFinite(layer.EdgeFade) || layer.EdgeFade < 0) errors.Add(label + ": edgeFade must be finite and nonnegative.");
        if (layer.Policy.Id != LegacyTilePolicy || layer.Policy.Version != LegacyTilePolicyVersion)
            errors.Add($"{label}: only policy {LegacyTilePolicy} version {LegacyTilePolicyVersion} is supported.");
        if (!float.IsFinite(layer.Policy.NormalBoundaryHeight)) errors.Add(label + ": normalBoundaryHeight must be finite.");
        Models(layer, label, errors);
        Materials(layer, label, errors);
        Ordinals(layer, label, errors);
        Clearance(layer, label, errors);
        Chunks(layer, label, errors);
    }

    static void Models(MapFoliageLayer layer, string label, List<string> errors)
    {
        if (layer.Models.Count is 0 or > MapFoliagePayloadLimits.MaxModels)
            errors.Add($"{label}: models must hold from 1 to {MapFoliagePayloadLimits.MaxModels} entries.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        float total = 0;
        foreach (MapFoliageModel model in layer.Models)
        {
            if (string.IsNullOrEmpty(model.AssetId)) errors.Add(label + ": every model needs a non-empty assetId.");
            else if (!ids.Add(model.AssetId)) errors.Add($"{label}: model '{model.AssetId}' is listed twice.");
            if (!Positive(model.Weight)) errors.Add($"{label}: model '{model.AssetId}' must have a finite positive weight.");
            total += model.Weight;
        }
        if (!float.IsFinite(total)) errors.Add(label + ": model weights exceed the supported total.");
    }

    static void Materials(MapFoliageLayer layer, string label, List<string> errors)
    {
        if (layer.Materials.Count > MapFoliagePayloadLimits.MaxMaterials || layer.AllowedVisibleMaterials.Count > MapFoliagePayloadLimits.MaxMaterials)
            errors.Add($"{label}: more than {MapFoliagePayloadLimits.MaxMaterials} materials.");
        var declared = new HashSet<ushort>();
        foreach (MapFoliageMaterial material in layer.Materials)
        {
            if (material.Id == 0) errors.Add(label + ": material id 0 is void and cannot be declared.");
            else if (!declared.Add(material.Id)) errors.Add($"{label}: material {material.Id} is declared twice.");
            if (string.IsNullOrEmpty(material.ResourceId)) errors.Add($"{label}: material {material.Id} needs a resourceId.");
            if (!Enum.IsDefined(material.Kind)) errors.Add($"{label}: material {material.Id} has an unsupported kind.");
        }
        // A sorted distinct nonzero set, each a declared material.
        for (int i = 0; i < layer.AllowedVisibleMaterials.Count; i++)
        {
            ushort id = layer.AllowedVisibleMaterials[i];
            if (i > 0 && id <= layer.AllowedVisibleMaterials[i - 1])
                errors.Add(label + ": allowedVisibleMaterials must be sorted ascending without repeats.");
            if (!declared.Contains(id)) errors.Add($"{label}: allowed visible material {id} is not a declared material.");
        }
    }

    static void Ordinals(MapFoliageLayer layer, string label, List<string> errors)
    {
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapFoliageCompatibilityOrdinal row in layer.CompatibilityOrdinals)
        {
            if (string.IsNullOrEmpty(row.SurfaceId)) errors.Add(label + ": every compatibility ordinal needs a surfaceId.");
            else if (!surfaces.Add(row.SurfaceId)) errors.Add($"{label}: compatibility ordinal surface '{row.SurfaceId}' is listed twice.");
            if (row.Ordinal < 0) errors.Add($"{label}: compatibility ordinal of '{row.SurfaceId}' must be nonnegative.");
        }
    }

    static void Clearance(MapFoliageLayer layer, string label, List<string> errors)
    {
        if (layer.ClearanceRules.Count > MapFoliagePayloadLimits.MaxClearanceRules)
            errors.Add($"{label}: more than {MapFoliagePayloadLimits.MaxClearanceRules} clearance rules.");
        if (layer.ExceptionMasks.Count > MapFoliagePayloadLimits.MaxExceptionMasks)
            errors.Add($"{label}: more than {MapFoliagePayloadLimits.MaxExceptionMasks} exception masks.");
        if (layer.OwnerSupports.Count > MapFoliagePayloadLimits.MaxOwnerSupports)
            errors.Add($"{label}: more than {MapFoliagePayloadLimits.MaxOwnerSupports} owner supports.");
        var rules = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapFoliageClearanceRule rule in layer.ClearanceRules)
        {
            string at = $"{label} rule '{rule.Id}'";
            if (string.IsNullOrEmpty(rule.Id)) errors.Add(label + ": every clearance rule needs a non-empty id.");
            else if (!rules.Add(rule.Id)) errors.Add(at + " is listed twice.");
            if (string.IsNullOrEmpty(rule.AssetId)) errors.Add(at + ": assetId must be non-empty.");
            if (rule.PlacementTag is { Length: 0 }) errors.Add(at + ": placementTag must be omitted or non-empty.");
            Kind(rule.Kind, at, errors);
            Rect(rule.Footprint, at, errors);
        }
        var masks = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapFoliageMask mask in layer.ExceptionMasks)
        {
            string at = $"{label} mask '{mask.Id}'";
            if (string.IsNullOrEmpty(mask.Id)) errors.Add(label + ": every exception mask needs a non-empty id.");
            else if (!masks.Add(mask.Id)) errors.Add(at + " is listed twice.");
            Owner(mask.Owner, at, errors);
            Binding(mask.Support, at + " support", errors);
            Kind(mask.Kind, at, errors);
            Rect(mask.Footprint, at, errors);
        }
        var owners = new HashSet<MapPrefabReference>();
        foreach (MapFoliageOwnerSupport owner in layer.OwnerSupports)
        {
            string at = $"{label} owner support '{owner.Owner?.Id}'";
            Owner(owner.Owner, at, errors);
            if (owner.Owner is not null && !owners.Add(owner.Owner)) errors.Add(at + " is listed twice.");
            Binding(owner.Support, at + " support", errors);
        }
    }

    // Keys inside the chunk grid of the domain, unique, with zero padding outside [0, Width) by [0, Depth).
    static void Chunks(MapFoliageLayer layer, string label, List<string> errors)
    {
        if (layer.Chunks.Count > MapFoliagePayloadLimits.MaxChunksPerLayer)
            errors.Add($"{label}: more than {MapFoliagePayloadLimits.MaxChunksPerLayer} chunks.");
        if (layer.Width is < 1 or > MapFoliagePayloadLimits.MaxExtent || layer.Depth is < 1 or > MapFoliagePayloadLimits.MaxExtent) return;
        int columns = (layer.Width + MapFoliageChunk.Size - 1) / MapFoliageChunk.Size;
        int rows = (layer.Depth + MapFoliageChunk.Size - 1) / MapFoliageChunk.Size;
        var keys = new HashSet<MapFoliageChunkKey>();
        foreach (MapFoliageChunk chunk in layer.Chunks)
        {
            MapFoliageChunkKey key = chunk.Key;
            string at = $"{label} chunk ({key.X}, {key.Z})";
            if (!keys.Add(key)) errors.Add(at + " is listed twice.");
            if (key.X < 0 || key.X >= columns || key.Z < 0 || key.Z >= rows)
            {
                errors.Add(at + " lies outside the density domain.");
                continue;
            }
            int usedX = Math.Min(MapFoliageChunk.Size, layer.Width - key.X * MapFoliageChunk.Size);
            int usedZ = Math.Min(MapFoliageChunk.Size, layer.Depth - key.Z * MapFoliageChunk.Size);
            if (usedX == MapFoliageChunk.Size && usedZ == MapFoliageChunk.Size) continue;
            ReadOnlySpan<byte> density = chunk.Density;
            for (int i = 0; i < density.Length; i++)
                if (density[i] != 0 && (i % MapFoliageChunk.Size >= usedX || i / MapFoliageChunk.Size >= usedZ))
                {
                    errors.Add(at + ": padding outside the density domain must be zero.");
                    break;
                }
        }
    }

    // The marker's closed binding rule, so blank IDs refuse here exactly as they do for markers.
    static void Binding(MapSupportBinding? binding, string label, List<string> errors)
    {
        if (binding is null) errors.Add(label + ": a support binding is required.");
        else MapSupportBindingRules.Validate(binding, label + ": ", errors);
    }

    static void Owner(MapPrefabReference? owner, string label, List<string> errors)
    {
        if (owner is null || owner.Scope != MapPrefabReferenceScope.World || !OwnerKinds.Contains(owner.Kind) || string.IsNullOrEmpty(owner.Id))
            errors.Add(label + ": owner must be a World Placement, Surface, Record or Marker reference with an id.");
    }

    static void Kind(MapFoliageClearanceKind kind, string label, List<string> errors)
    {
        if (!Enum.IsDefined(kind)) errors.Add(label + ": unsupported clearance kind.");
    }

    static void Rect(MapFoliageRect rect, string label, List<string> errors)
    {
        if (!float.IsFinite(rect.MinX) || !float.IsFinite(rect.MinZ) || !float.IsFinite(rect.MaxX) || !float.IsFinite(rect.MaxZ) ||
            !(rect.MaxX > rect.MinX) || !(rect.MaxZ > rect.MinZ))
            errors.Add(label + ": footprint must be finite with MaxX > MinX and MaxZ > MinZ.");
    }

    static bool Positive(float value) => float.IsFinite(value) && value > 0;
}
