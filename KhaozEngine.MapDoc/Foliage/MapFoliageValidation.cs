using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Foliage payload 1 rules. <see cref="Validate(MapFoliageSet)"/> checks a set on its own, the same rules the
/// closed codec and schema state. <see cref="ValidateBound(MapDocument, MapAssetClosure)"/> adds metadata and resource
/// roles against a verified closure without expanding prefabs. Effective owners and support surfaces are checked after expansion.</summary>
public static class MapFoliageValidation
{
    internal const string LegacyTilePolicy = "legacy-tile";
    internal const int LegacyTilePolicyVersion = 1;

    static readonly MapPrefabChildKind[] OwnerKinds =
        [MapPrefabChildKind.Placement, MapPrefabChildKind.Surface, MapPrefabChildKind.Record, MapPrefabChildKind.Marker];

    public static IReadOnlyList<string> Validate(MapFoliageSet set) => Validate(set, null);

    /// <summary>Returns every set and layer refusal as an owned read-only snapshot, in validation order.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateFindings(MapFoliageSet set)
    {
        var findings = new List<MapDocumentFinding>();
        Validate(set, findings);
        return Array.AsReadOnly(findings.ToArray());
    }

    internal static IReadOnlyList<string> Validate(MapFoliageSet set, List<MapDocumentFinding>? findings)
    {
        ArgumentNullException.ThrowIfNull(set);
        var errors = new List<string>();
        if (set.PayloadVersion != 1) Add(new(MapDocumentRefusal.SchemaPayloadVersionUnsupported,
            "foliage: unsupported payloadVersion, only payload 1 is supported.")
        { FieldPath = "PayloadVersion", ActualVersion = set.PayloadVersion, SupportedVersions = [1] });
        if (set.Layers.Count > MapFoliagePayloadLimits.MaxLayers)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, $"foliage: more than {MapFoliagePayloadLimits.MaxLayers} layers.")
            { FieldPath = "Layers", Budget = "layers", ActualCount = set.Layers.Count, Limit = MapFoliagePayloadLimits.MaxLayers });
        long documentChunks = set.Layers.Sum(l => (long)l.ChunkDigests.Count);
        if (documentChunks > MapFoliagePayloadLimits.MaxDocumentChunks)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, $"foliage: density exceeds {MapFoliagePayloadLimits.MaxDensityBytes} decoded bytes per document.")
            {
                FieldPath = "Layers", Budget = "decoded density bytes per document", ActualCount = documentChunks,
                ActualByteCount = documentChunks * MapFoliageChunk.ByteLength, Limit = MapFoliagePayloadLimits.MaxDensityBytes,
            });
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (MapFoliageLayer layer in set.Layers)
        {
            if (string.IsNullOrEmpty(layer.Id)) Add(new(MapDocumentRefusal.SchemaValueRequired, "foliage: every layer needs a non-empty id.")
            { LayerId = layer.Id, FieldPath = $"Layers[{index}].Id", Index = index });
            else if (!ids.Add(layer.Id)) Add(new(MapDocumentRefusal.IdentityDuplicate, $"foliage: duplicate layer id '{layer.Id}'.")
            { LayerId = layer.Id, FieldPath = $"Layers[{index}].Id", Index = index, SubjectKind = "foliage layer" });
            ValidateLayer(layer, $"foliage layer '{layer.Id}'", errors, findings);
            index++;
        }
        return errors;

        void Add(MapDocumentFinding finding)
        {
            errors.Add(finding.Message);
            findings?.Add(finding);
        }
    }

    /// <summary>Checks the set, then every role a verified closure and the document's declared surfaces can answer: model
    /// and clearance assets exist, material resources are Material resources, and Surface supports and compatibility
    /// rows name a declared support floor or a bound prefab surface. Never expands prefabs.</summary>
    public static IReadOnlyList<string> ValidateBound(MapDocument document, MapAssetClosure assets) => ValidateBound(document, assets, null);

    /// <summary>Returns every set, layer and bound-resource refusal as an owned read-only snapshot.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateBoundFindings(MapDocument document, MapAssetClosure assets)
    {
        var findings = new List<MapDocumentFinding>();
        ValidateBound(document, assets, findings);
        return Array.AsReadOnly(findings.ToArray());
    }

    internal static IReadOnlyList<string> ValidateBound(MapDocument document, MapAssetClosure assets, List<MapDocumentFinding>? findings)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        if (document.Foliage is not { } set) return Array.Empty<string>();
        var errors = new List<string>(Validate(set, findings));
        var assetIds = new HashSet<string>(assets.Assets.Select(a => a.Id), StringComparer.Ordinal);
        var declared = (document.Surfaces?.Refs ?? []).Where(s => s?.Id is not null).GroupBy(s => s.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Role, StringComparer.Ordinal);
        // Bound prefab surfaces are known by their persisted world IDs. Their roles are checked after expansion.
        var bindings = (document.Prefabs?.Instances.Where(i => !i.Removed).SelectMany(i => i.Bindings.Entries) ?? [])
            .Concat(document.Prefabs?.Transfers.Where(t => !t.Retired).Select(t => t.Binding) ?? []);
        var bound = bindings.Where(b => !b.Removed && b.Child.Kind == MapPrefabChildKind.Surface).Select(b => b.WorldId)
            .Concat((document.Prefabs?.Attachments.Where(a => !a.Retired).SelectMany(a => a.Children) ?? [])
                .Where(c => c.LocalChild.Kind == MapPrefabChildKind.Surface).Select(c => c.WorldId)).ToHashSet(StringComparer.Ordinal);
        foreach (MapFoliageLayer layer in set.Layers)
        {
            string label = $"foliage layer '{layer.Id}'";
            for (int i = 0; i < layer.Models.Count; i++)
            {
                MapFoliageModel model = layer.Models[i];
                if (!assetIds.Contains(model.AssetId))
                {
                    var finding = new MapDocumentFinding(MapDocumentRefusal.ReferenceMissing,
                        $"{label}: model asset '{model.AssetId}' is not in the verified closure.")
                    { LayerId = layer.Id, FieldPath = $"Models[{i}].AssetId", Index = i, AssetId = model.AssetId };
                    errors.Add(finding.Message);
                    findings?.Add(finding);
                }
            }
            for (int i = 0; i < layer.Materials.Count; i++)
            {
                MapFoliageMaterial material = layer.Materials[i];
                if (!assets.TryGetResource(material.ResourceId, out MapResolvedResource? resource) || resource!.Kind != MapResourceKind.Material)
                    AddFinding(new(resource is null ? MapDocumentRefusal.ReferenceMissing : MapDocumentRefusal.ReferenceKindMismatch,
                        $"{label}: material {material.Id} resource '{material.ResourceId}' is not a Material resource.")
                    { LayerId = layer.Id, FieldPath = $"Materials[{i}].ResourceId", Index = i, ResourceId = material.ResourceId,
                        ActualInteger = material.Id, ExpectedResourceKind = MapResourceKind.Material, ActualResourceKind = resource?.Kind }, errors, findings);
            }
            for (int i = 0; i < layer.ClearanceRules.Count; i++)
            {
                MapFoliageClearanceRule rule = layer.ClearanceRules[i];
                if (!assetIds.Contains(rule.AssetId)) AddFinding(new(MapDocumentRefusal.ReferenceMissing,
                    $"{label} rule '{rule.Id}': asset '{rule.AssetId}' is not in the verified closure.")
                { LayerId = layer.Id, FieldPath = $"ClearanceRules[{i}].AssetId", Index = i, AssetId = rule.AssetId, EntityId = rule.Id }, errors, findings);
            }
            if (layer.Support is { Kind: MapSupportBindingKind.Surface, SurfaceId: { Length: > 0 } support })
                RequireSupportSurface(support, declared, bound, label + " support", errors, findings, layer.Id, "Support.SurfaceId");
            for (int i = 0; i < layer.OwnerSupports.Count; i++)
            {
                MapSupportBinding binding = layer.OwnerSupports[i].Support;
                if (binding is { Kind: MapSupportBindingKind.Surface, SurfaceId: { Length: > 0 } target })
                    RequireSupportSurface(target, declared, bound, label + " dependent support", errors, findings,
                        layer.Id, $"OwnerSupports[{i}].Support.SurfaceId", i);
            }
            for (int i = 0; i < layer.ExceptionMasks.Count; i++)
            {
                MapSupportBinding binding = layer.ExceptionMasks[i].Support;
                if (binding is { Kind: MapSupportBindingKind.Surface, SurfaceId: { Length: > 0 } target })
                    RequireSupportSurface(target, declared, bound, label + " dependent support", errors, findings,
                        layer.Id, $"ExceptionMasks[{i}].Support.SurfaceId", i);
            }
            // Empty IDs are already reported by the set rules.
            for (int i = 0; i < layer.CompatibilityOrdinals.Count; i++)
            {
                MapFoliageCompatibilityOrdinal row = layer.CompatibilityOrdinals[i];
                if (!string.IsNullOrEmpty(row.SurfaceId))
                    RequireSupportSurface(row.SurfaceId, declared, bound, label + " compatibility ordinal", errors, findings,
                        layer.Id, $"CompatibilityOrdinals[{i}].SurfaceId", i);
            }
        }
        return errors;
    }

    /// <summary>The document-level gate run by <see cref="MapDocumentValidator"/>: foliage needs a resolver-2 document, then
    /// the set rules.</summary>
    internal static void ValidateDocument(MapDocument doc, List<string> errors, List<MapDocumentFinding>? findings = null)
    {
        if (doc.Foliage is not { } set) return;
        if (MapFoliageStorageWork.Current is { } work) work.DocumentGates++;
        if (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } || doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
        {
            var finding = MapDocumentFinding.ResolverContract(doc,
                "foliage: requires resolver identity (1, 2) and supportRecipe AuthoredBindingsV2. Adopt authored support " +
                "with MapResolverAdoption.ConvertToAuthoredSupport before adding foliage.",
                doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } ? "resolverIdentity" : "supportRecipe", "foliage", 2, MapSupportRecipe.AuthoredBindingsV2);
            errors.Add(finding.Message);
            findings?.Add(finding);
        }
        errors.AddRange(Validate(set, findings));
    }

    static void RequireSupportSurface(string id, Dictionary<string, MapSurfaceRole> declared, HashSet<string> bound, string label,
        List<string> errors, List<MapDocumentFinding>? findings, string layerId, string field, int? index = null)
    {
        if (declared.TryGetValue(id, out MapSurfaceRole role))
        {
            if (role != MapSurfaceRole.SupportFloor) AddFinding(new(MapDocumentRefusal.ReferenceKindMismatch,
                $"{label}: surface '{id}' is not a support floor.")
            { LayerId = layerId, FieldPath = field, Index = index, SurfaceId = id, EnumType = typeof(MapSurfaceRole), ActualInteger = (long)role }, errors, findings);
        }
        else if (!bound.Contains(id)) AddFinding(new(MapDocumentRefusal.ReferenceMissing, $"{label}: surface '{id}' is not a declared or bound surface.")
        { LayerId = layerId, FieldPath = field, Index = index, SurfaceId = id }, errors, findings);
    }

    static void AddFinding(MapDocumentFinding finding, List<string> errors, List<MapDocumentFinding>? findings)
    {
        errors.Add(finding.Message);
        findings?.Add(finding);
    }

    static void ValidateLayer(MapFoliageLayer layer, string label, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (layer.PayloadVersion != 1) Add(new(MapDocumentRefusal.SchemaPayloadVersionUnsupported,
            label + ": unsupported payloadVersion, only payload 1 is supported.")
        { FieldPath = "PayloadVersion", ActualVersion = layer.PayloadVersion, SupportedVersions = [1] });
        Binding(layer.Support, label + " support", errors, findings, layer.Id, "Support");
        if (!float.IsFinite(layer.OriginX) || !float.IsFinite(layer.OriginZ)) Add(new(MapDocumentRefusal.ValueNonfinite,
            label + ": origin must be finite.")
        { FieldPath = !float.IsFinite(layer.OriginX) ? "OriginX" : "OriginZ", ActualNumber = !float.IsFinite(layer.OriginX) ? layer.OriginX : layer.OriginZ });
        if (!Positive(layer.CellSize)) Add(new(!float.IsFinite(layer.CellSize) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
            label + ": cellSize must be finite and positive.")
        { FieldPath = "CellSize", ActualNumber = layer.CellSize, MinimumNumber = 0 });
        if (layer.Width is < 1 or > MapFoliagePayloadLimits.MaxExtent || layer.Depth is < 1 or > MapFoliagePayloadLimits.MaxExtent)
            Add(new(MapDocumentRefusal.ValueOutsideRange, $"{label}: width and depth must be from 1 to {MapFoliagePayloadLimits.MaxExtent}.")
            { FieldPath = layer.Width is < 1 or > MapFoliagePayloadLimits.MaxExtent ? "Width" : "Depth",
                ActualInteger = layer.Width is < 1 or > MapFoliagePayloadLimits.MaxExtent ? layer.Width : layer.Depth,
                MinimumInteger = 1, MaximumInteger = MapFoliagePayloadLimits.MaxExtent });
        if (layer.RowDirection != MapRowDirection.PositiveZ) Add(new(MapDocumentRefusal.SchemaEnumUnsupported,
            label + ": density rows must advance positive world Z.")
        { FieldPath = "RowDirection", EnumType = typeof(MapRowDirection), ActualInteger = (long)layer.RowDirection });
        if (!Positive(layer.Spacing)) Add(new(!float.IsFinite(layer.Spacing) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
            label + ": spacing must be finite and positive.")
        { FieldPath = "Spacing", ActualNumber = layer.Spacing, MinimumNumber = 0 });
        if (!Positive(layer.ScaleMin) || !float.IsFinite(layer.ScaleMax) || layer.ScaleMax < layer.ScaleMin)
            Add(new(!float.IsFinite(layer.ScaleMin) ? MapDocumentRefusal.ValueNonfinite : layer.ScaleMin <= 0 ? MapDocumentRefusal.ValueOutsideRange :
                !float.IsFinite(layer.ScaleMax) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.BoundsInvalid,
                label + ": scale range must be finite, positive and ordered.")
            { FieldPath = !Positive(layer.ScaleMin) ? "ScaleMin" : "ScaleMax", ActualNumber = !Positive(layer.ScaleMin) ? layer.ScaleMin : layer.ScaleMax,
                MinimumNumber = !Positive(layer.ScaleMin) ? 0 : layer.ScaleMin, MaximumNumber = layer.ScaleMax });
        if (!float.IsFinite(layer.RootOffset)) Add(new(MapDocumentRefusal.ValueNonfinite, label + ": rootOffset must be finite.")
        { FieldPath = "RootOffset", ActualNumber = layer.RootOffset });
        if (!float.IsFinite(layer.DoorClearance) || layer.DoorClearance < 0)
            Add(new(!float.IsFinite(layer.DoorClearance) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                label + ": doorClearance must be finite and nonnegative.")
            { FieldPath = "DoorClearance", ActualNumber = layer.DoorClearance, MinimumNumber = 0 });
        if (!float.IsFinite(layer.EdgeFade) || layer.EdgeFade < 0)
            Add(new(!float.IsFinite(layer.EdgeFade) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                label + ": edgeFade must be finite and nonnegative.")
            { FieldPath = "EdgeFade", ActualNumber = layer.EdgeFade, MinimumNumber = 0 });
        if (layer.Policy.Id != LegacyTilePolicy || layer.Policy.Version != LegacyTilePolicyVersion)
            Add(new(layer.Policy.Id != LegacyTilePolicy
                    ? string.IsNullOrEmpty(layer.Policy.Id) ? MapDocumentRefusal.SchemaValueRequired : MapDocumentRefusal.FoliagePolicyUnsupported
                    : MapDocumentRefusal.SchemaPayloadVersionUnsupported,
                $"{label}: only policy {LegacyTilePolicy} version {LegacyTilePolicyVersion} is supported.")
            { FieldPath = layer.Policy.Id != LegacyTilePolicy ? "Policy.Id" : "Policy.Version",
                PolicyId = layer.Policy.Id, ExpectedPolicyId = LegacyTilePolicy,
                ActualVersion = layer.Policy.Id == LegacyTilePolicy ? layer.Policy.Version : null,
                SupportedVersions = layer.Policy.Id == LegacyTilePolicy ? [LegacyTilePolicyVersion] : null });
        if (!float.IsFinite(layer.Policy.NormalBoundaryHeight)) Add(new(MapDocumentRefusal.ValueNonfinite, label + ": normalBoundaryHeight must be finite.")
        { FieldPath = "Policy.NormalBoundaryHeight", ActualNumber = layer.Policy.NormalBoundaryHeight });
        Models(layer, label, errors, findings);
        Materials(layer, label, errors, findings);
        Ordinals(layer, label, errors, findings);
        Clearance(layer, label, errors, findings);
        Chunks(layer, label, errors, findings);

        void Add(MapDocumentFinding finding) => AddFinding(finding with { LayerId = layer.Id }, errors, findings);
    }

    static void Models(MapFoliageLayer layer, string label, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (layer.Models.Count is 0 or > MapFoliagePayloadLimits.MaxModels)
            Add(new(layer.Models.Count == 0 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.WorkCapacityExceeded,
                $"{label}: models must hold from 1 to {MapFoliagePayloadLimits.MaxModels} entries.")
            { FieldPath = "Models", ActualCount = layer.Models.Count, MinimumInteger = 1, MaximumInteger = MapFoliagePayloadLimits.MaxModels,
                Budget = "models", Limit = MapFoliagePayloadLimits.MaxModels });
        var ids = new HashSet<string>(StringComparer.Ordinal);
        float total = 0;
        for (int i = 0; i < layer.Models.Count; i++)
        {
            MapFoliageModel model = layer.Models[i];
            if (string.IsNullOrEmpty(model.AssetId)) Add(new(MapDocumentRefusal.SchemaValueRequired, label + ": every model needs a non-empty assetId.")
            { FieldPath = $"Models[{i}].AssetId", Index = i, AssetId = model.AssetId });
            else if (!ids.Add(model.AssetId)) Add(new(MapDocumentRefusal.IdentityDuplicate, $"{label}: model '{model.AssetId}' is listed twice.")
            { FieldPath = $"Models[{i}].AssetId", Index = i, AssetId = model.AssetId, SubjectKind = "foliage model" });
            if (!Positive(model.Weight)) Add(new(!float.IsFinite(model.Weight) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange,
                $"{label}: model '{model.AssetId}' must have a finite positive weight.")
            { FieldPath = $"Models[{i}].Weight", Index = i, AssetId = model.AssetId, ActualNumber = model.Weight, MinimumNumber = 0 });
            total += model.Weight;
        }
        if (!float.IsFinite(total)) Add(new(MapDocumentRefusal.WorkCapacityExceeded, label + ": model weights exceed the supported total.")
        { FieldPath = "Models.Weight", Budget = "model weights", ActualCount = layer.Models.Count, ActualNumber = total, MaximumNumber = float.MaxValue });

        void Add(MapDocumentFinding finding) => AddFinding(finding with { LayerId = layer.Id }, errors, findings);
    }

    static void Materials(MapFoliageLayer layer, string label, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (layer.Materials.Count > MapFoliagePayloadLimits.MaxMaterials || layer.AllowedVisibleMaterials.Count > MapFoliagePayloadLimits.MaxMaterials)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, $"{label}: more than {MapFoliagePayloadLimits.MaxMaterials} materials.")
            { FieldPath = layer.Materials.Count > MapFoliagePayloadLimits.MaxMaterials ? "Materials" : "AllowedVisibleMaterials",
                Budget = layer.Materials.Count > MapFoliagePayloadLimits.MaxMaterials ? "materials" : "allowed visible materials",
                ActualCount = layer.Materials.Count > MapFoliagePayloadLimits.MaxMaterials ? layer.Materials.Count : layer.AllowedVisibleMaterials.Count,
                Limit = MapFoliagePayloadLimits.MaxMaterials });
        var declared = new HashSet<ushort>();
        for (int i = 0; i < layer.Materials.Count; i++)
        {
            MapFoliageMaterial material = layer.Materials[i];
            if (material.Id == 0) Add(new(MapDocumentRefusal.ValueOutsideRange, label + ": material id 0 is void and cannot be declared.")
            { FieldPath = $"Materials[{i}].Id", Index = i, ActualInteger = material.Id, ResourceId = material.ResourceId,
                MinimumInteger = 1, MaximumInteger = ushort.MaxValue });
            else if (!declared.Add(material.Id)) Add(new(MapDocumentRefusal.IdentityDuplicate, $"{label}: material {material.Id} is declared twice.")
            { FieldPath = $"Materials[{i}].Id", Index = i, ActualInteger = material.Id, ResourceId = material.ResourceId, SubjectKind = "foliage material" });
            if (string.IsNullOrEmpty(material.ResourceId)) Add(new(MapDocumentRefusal.SchemaValueRequired, $"{label}: material {material.Id} needs a resourceId.")
            { FieldPath = $"Materials[{i}].ResourceId", Index = i, ActualInteger = material.Id, ResourceId = material.ResourceId });
            if (!Enum.IsDefined(material.Kind)) Add(new(MapDocumentRefusal.SchemaEnumUnsupported,
                $"{label}: material {material.Id} has an unsupported kind.")
            { FieldPath = $"Materials[{i}].Kind", Index = i, NumericId = material.Id, ResourceId = material.ResourceId,
                EnumType = typeof(MapFoliageMaterialKind), ActualInteger = (long)material.Kind });
        }
        // A sorted distinct nonzero set, each a declared material.
        for (int i = 0; i < layer.AllowedVisibleMaterials.Count; i++)
        {
            ushort id = layer.AllowedVisibleMaterials[i];
            if (i > 0 && id <= layer.AllowedVisibleMaterials[i - 1])
                Add(new(MapDocumentRefusal.OrderInvalid, label + ": allowedVisibleMaterials must be sorted ascending without repeats.")
                { FieldPath = $"AllowedVisibleMaterials[{i}]", Index = i, ActualInteger = id, PreviousInteger = layer.AllowedVisibleMaterials[i - 1] });
            if (!declared.Contains(id)) Add(new(MapDocumentRefusal.ReferenceMissing, $"{label}: allowed visible material {id} is not a declared material.")
            { FieldPath = $"AllowedVisibleMaterials[{i}]", Index = i, ActualInteger = id, SubjectKind = "visible material" });
        }

        void Add(MapDocumentFinding finding) => AddFinding(finding with { LayerId = layer.Id }, errors, findings);
    }

    static void Ordinals(MapFoliageLayer layer, string label, List<string> errors, List<MapDocumentFinding>? findings)
    {
        var surfaces = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < layer.CompatibilityOrdinals.Count; i++)
        {
            MapFoliageCompatibilityOrdinal row = layer.CompatibilityOrdinals[i];
            if (string.IsNullOrEmpty(row.SurfaceId)) Add(new(MapDocumentRefusal.SchemaValueRequired, label + ": every compatibility ordinal needs a surfaceId.")
            { FieldPath = $"CompatibilityOrdinals[{i}].SurfaceId", Index = i, SurfaceId = row.SurfaceId });
            else if (!surfaces.Add(row.SurfaceId)) Add(new(MapDocumentRefusal.IdentityDuplicate,
                $"{label}: compatibility ordinal surface '{row.SurfaceId}' is listed twice.")
            { FieldPath = $"CompatibilityOrdinals[{i}].SurfaceId", Index = i, SurfaceId = row.SurfaceId, SubjectKind = "compatibility ordinal" });
            if (row.Ordinal < 0) Add(new(MapDocumentRefusal.ValueOutsideRange, $"{label}: compatibility ordinal of '{row.SurfaceId}' must be nonnegative.")
            { FieldPath = $"CompatibilityOrdinals[{i}].Ordinal", Index = i, SurfaceId = row.SurfaceId, ActualInteger = row.Ordinal, MinimumInteger = 0 });
        }

        void Add(MapDocumentFinding finding) => AddFinding(finding with { LayerId = layer.Id }, errors, findings);
    }

    static void Clearance(MapFoliageLayer layer, string label, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (layer.ClearanceRules.Count > MapFoliagePayloadLimits.MaxClearanceRules)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, $"{label}: more than {MapFoliagePayloadLimits.MaxClearanceRules} clearance rules.")
            { FieldPath = "ClearanceRules", Budget = "clearance rules", ActualCount = layer.ClearanceRules.Count, Limit = MapFoliagePayloadLimits.MaxClearanceRules });
        if (layer.ExceptionMasks.Count > MapFoliagePayloadLimits.MaxExceptionMasks)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, $"{label}: more than {MapFoliagePayloadLimits.MaxExceptionMasks} exception masks.")
            { FieldPath = "ExceptionMasks", Budget = "exception masks", ActualCount = layer.ExceptionMasks.Count, Limit = MapFoliagePayloadLimits.MaxExceptionMasks });
        if (layer.OwnerSupports.Count > MapFoliagePayloadLimits.MaxOwnerSupports)
            Add(new(MapDocumentRefusal.WorkCapacityExceeded, $"{label}: more than {MapFoliagePayloadLimits.MaxOwnerSupports} owner supports.")
            { FieldPath = "OwnerSupports", Budget = "owner supports", ActualCount = layer.OwnerSupports.Count, Limit = MapFoliagePayloadLimits.MaxOwnerSupports });
        var rules = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < layer.ClearanceRules.Count; i++)
        {
            MapFoliageClearanceRule rule = layer.ClearanceRules[i];
            string at = $"{label} rule '{rule.Id}'";
            if (string.IsNullOrEmpty(rule.Id)) Add(new(MapDocumentRefusal.SchemaValueRequired, label + ": every clearance rule needs a non-empty id.")
            { FieldPath = $"ClearanceRules[{i}].Id", Index = i, EntityId = rule.Id });
            else if (!rules.Add(rule.Id)) Add(new(MapDocumentRefusal.IdentityDuplicate, at + " is listed twice.")
            { FieldPath = $"ClearanceRules[{i}].Id", Index = i, EntityId = rule.Id, SubjectKind = "clearance rule" });
            if (string.IsNullOrEmpty(rule.AssetId)) Add(new(MapDocumentRefusal.SchemaValueRequired, at + ": assetId must be non-empty.")
            { FieldPath = $"ClearanceRules[{i}].AssetId", Index = i, EntityId = rule.Id, AssetId = rule.AssetId });
            if (rule.PlacementTag is { Length: 0 }) Add(new(MapDocumentRefusal.SchemaValueRequired, at + ": placementTag must be omitted or non-empty.")
            { FieldPath = $"ClearanceRules[{i}].PlacementTag", Index = i, EntityId = rule.Id, ActualText = rule.PlacementTag });
            Kind(rule.Kind, at, errors, findings, layer.Id, $"ClearanceRules[{i}].Kind", i, rule.Id);
            Rect(rule.Footprint, at, errors, findings, layer.Id, $"ClearanceRules[{i}].Footprint", i, rule.Id);
        }
        var masks = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < layer.ExceptionMasks.Count; i++)
        {
            MapFoliageMask mask = layer.ExceptionMasks[i];
            string at = $"{label} mask '{mask.Id}'";
            if (string.IsNullOrEmpty(mask.Id)) Add(new(MapDocumentRefusal.SchemaValueRequired, label + ": every exception mask needs a non-empty id.")
            { FieldPath = $"ExceptionMasks[{i}].Id", Index = i, EntityId = mask.Id });
            else if (!masks.Add(mask.Id)) Add(new(MapDocumentRefusal.IdentityDuplicate, at + " is listed twice.")
            { FieldPath = $"ExceptionMasks[{i}].Id", Index = i, EntityId = mask.Id, SubjectKind = "exception mask" });
            Owner(mask.Owner, at, errors, findings, layer.Id, $"ExceptionMasks[{i}].Owner", i);
            Binding(mask.Support, at + " support", errors, findings, layer.Id, $"ExceptionMasks[{i}].Support", i, mask.Id);
            Kind(mask.Kind, at, errors, findings, layer.Id, $"ExceptionMasks[{i}].Kind", i, mask.Id);
            Rect(mask.Footprint, at, errors, findings, layer.Id, $"ExceptionMasks[{i}].Footprint", i, mask.Id);
        }
        var owners = new HashSet<MapPrefabReference>();
        for (int i = 0; i < layer.OwnerSupports.Count; i++)
        {
            MapFoliageOwnerSupport owner = layer.OwnerSupports[i];
            string at = $"{label} owner support '{owner.Owner?.Id}'";
            Owner(owner.Owner, at, errors, findings, layer.Id, $"OwnerSupports[{i}].Owner", i);
            if (owner.Owner is not null && !owners.Add(owner.Owner)) Add(new(MapDocumentRefusal.IdentityDuplicate, at + " is listed twice.")
            { FieldPath = $"OwnerSupports[{i}].Owner", Index = i, EntityId = owner.Owner.Id,
                ChildKind = owner.Owner.Kind, ReferenceScope = owner.Owner.Scope, SubjectKind = "owner support" });
            Binding(owner.Support, at + " support", errors, findings, layer.Id, $"OwnerSupports[{i}].Support", i, owner.Owner?.Id);
        }

        void Add(MapDocumentFinding finding) => AddFinding(finding with { LayerId = layer.Id }, errors, findings);
    }

    // Keys inside the chunk grid of the domain, unique, with zero padding outside [0, Width) by [0, Depth).
    static void Chunks(MapFoliageLayer layer, string label, List<string> errors, List<MapDocumentFinding>? findings)
    {
        if (layer.ChunkDigests.Count > MapFoliagePayloadLimits.MaxChunksPerLayer)
            AddFinding(new(MapDocumentRefusal.WorkCapacityExceeded, $"{label}: more than {MapFoliagePayloadLimits.MaxChunksPerLayer} chunks.")
            { LayerId = layer.Id, FieldPath = "ChunkDigests", Budget = "chunks per layer", ActualCount = layer.ChunkDigests.Count,
                Limit = MapFoliagePayloadLimits.MaxChunksPerLayer }, errors, findings);
        if (layer.Width is < 1 or > MapFoliagePayloadLimits.MaxExtent || layer.Depth is < 1 or > MapFoliagePayloadLimits.MaxExtent) return;
        int columns = (layer.Width + MapFoliageChunk.Size - 1) / MapFoliageChunk.Size;
        int rows = (layer.Depth + MapFoliageChunk.Size - 1) / MapFoliageChunk.Size;
        for (int i = 0; i < layer.ChunkDigests.Count; i++)
        {
            MapFoliageChunkDigest advertised = layer.ChunkDigests[i];
            if (advertised.Key.X < 0 || advertised.Key.X >= columns || advertised.Key.Z < 0 || advertised.Key.Z >= rows)
            {
                bool x = advertised.Key.X < 0 || advertised.Key.X >= columns;
                AddFinding(new(MapDocumentRefusal.FoliageChunkOutOfDomain, $"{label} advertised chunk {advertised.Key} lies outside the density domain.")
                { LayerId = layer.Id, FieldPath = $"ChunkDigests[{i}].Key.{(x ? "X" : "Z")}", Index = i, FoliageChunk = advertised.Key,
                    Width = layer.Width, Depth = layer.Depth, ActualInteger = x ? advertised.Key.X : advertised.Key.Z,
                    MinimumInteger = 0, MaximumInteger = (x ? columns : rows) - 1 }, errors, findings);
            }
        }
        var keys = new HashSet<MapFoliageChunkKey>();
        for (int i = 0; i < layer.Chunks.Count; i++)
        {
            MapFoliageChunk chunk = layer.Chunks[i];
            MapFoliageChunkKey key = chunk.Key;
            string at = $"{label} chunk ({key.X}, {key.Z})";
            if (!keys.Add(key)) AddFinding(new(MapDocumentRefusal.IdentityDuplicate, at + " is listed twice.")
            { LayerId = layer.Id, FieldPath = $"Chunks[{i}].Key", Index = i, FoliageChunk = key, SubjectKind = "resident foliage chunk" }, errors, findings);
            ValidateChunk(layer, chunk, errors, findings);
        }
    }

    internal static void ValidateChunk(MapFoliageLayer layer, MapFoliageChunk chunk, List<string> errors, List<MapDocumentFinding>? findings = null)
    {
        MapFoliageChunkKey key = chunk.Key;
        string at = $"foliage layer '{layer.Id}' chunk ({key.X}, {key.Z})";
        int columns = (layer.Width + MapFoliageChunk.Size - 1) / MapFoliageChunk.Size;
        int rows = (layer.Depth + MapFoliageChunk.Size - 1) / MapFoliageChunk.Size;
        if (key.X < 0 || key.X >= columns || key.Z < 0 || key.Z >= rows)
        {
            bool x = key.X < 0 || key.X >= columns;
            AddFinding(new(MapDocumentRefusal.FoliageChunkOutOfDomain, at + " lies outside the density domain.")
            { LayerId = layer.Id, FieldPath = "Chunk.Key." + (x ? "X" : "Z"), FoliageChunk = key,
                Width = layer.Width, Depth = layer.Depth, ActualInteger = x ? key.X : key.Z,
                MinimumInteger = 0, MaximumInteger = (x ? columns : rows) - 1 }, errors, findings);
            return;
        }
        int usedX = Math.Min(MapFoliageChunk.Size, layer.Width - key.X * MapFoliageChunk.Size);
        int usedZ = Math.Min(MapFoliageChunk.Size, layer.Depth - key.Z * MapFoliageChunk.Size);
        if (usedX == MapFoliageChunk.Size && usedZ == MapFoliageChunk.Size) return;
        ReadOnlySpan<byte> density = chunk.Density;
        for (int i = 0; i < density.Length; i++)
            if (density[i] != 0 && (i % MapFoliageChunk.Size >= usedX || i / MapFoliageChunk.Size >= usedZ))
            {
                AddFinding(new(MapDocumentRefusal.FoliagePaddingNonzero, at + ": padding outside the density domain must be zero.")
                { LayerId = layer.Id, FieldPath = "Chunk.Density", FoliageChunk = key, Index = i,
                    CellX = i % MapFoliageChunk.Size, CellZ = i / MapFoliageChunk.Size, ActualInteger = density[i],
                    Width = layer.Width, Depth = layer.Depth }, errors, findings);
                break;
            }
    }

    // The marker's closed binding rule, so blank IDs refuse here exactly as they do for markers.
    static void Binding(MapSupportBinding? binding, string label, List<string> errors, List<MapDocumentFinding>? findings,
        string layerId, string field, int? index = null, string? entityId = null)
    {
        if (binding is null) AddFinding(new(MapDocumentRefusal.SchemaValueRequired, label + ": a support binding is required.")
        { LayerId = layerId, FieldPath = field, Index = index, EntityId = entityId }, errors, findings);
        else
        {
            var local = findings is null ? null : new List<MapDocumentFinding>();
            MapSupportBindingRules.Validate(binding, label + ": ", errors, local);
            if (local is not null)
                foreach (MapDocumentFinding finding in local)
                    findings!.Add(finding with
                    {
                        LayerId = layerId, FieldPath = field + "." + finding.FieldPath!["$.supportBinding.".Length..],
                        Index = index, EntityId = entityId,
                    });
        }
    }

    static void Owner(MapPrefabReference? owner, string label, List<string> errors, List<MapDocumentFinding>? findings,
        string layerId, string field, int index)
    {
        if (owner is null || owner.Scope != MapPrefabReferenceScope.World || !OwnerKinds.Contains(owner.Kind) || string.IsNullOrEmpty(owner.Id))
            AddFinding(new(owner is null ? MapDocumentRefusal.SchemaValueRequired :
                owner.Scope != MapPrefabReferenceScope.World ? MapDocumentRefusal.AuthoredOwnershipMismatch :
                !OwnerKinds.Contains(owner.Kind) ? MapDocumentRefusal.AuthoredReferenceKindInvalid : MapDocumentRefusal.SchemaValueRequired,
                label + ": owner must be a World Placement, Surface, Record or Marker reference with an id.")
            { LayerId = layerId, FieldPath = field, Index = index, EntityId = owner?.Id, ChildKind = owner?.Kind,
                ReferenceScope = owner?.Scope, ExpectedReferenceScope = MapPrefabReferenceScope.World, SubjectKind = "foliage owner" }, errors, findings);
    }

    static void Kind(MapFoliageClearanceKind kind, string label, List<string> errors, List<MapDocumentFinding>? findings,
        string layerId, string field, int index, string entityId)
    {
        if (!Enum.IsDefined(kind)) AddFinding(new(MapDocumentRefusal.SchemaEnumUnsupported, label + ": unsupported clearance kind.")
        { LayerId = layerId, FieldPath = field, Index = index, EntityId = entityId,
            EnumType = typeof(MapFoliageClearanceKind), ActualInteger = (long)kind }, errors, findings);
    }

    static void Rect(MapFoliageRect rect, string label, List<string> errors, List<MapDocumentFinding>? findings,
        string layerId, string field, int index, string entityId)
    {
        if (!float.IsFinite(rect.MinX) || !float.IsFinite(rect.MinZ) || !float.IsFinite(rect.MaxX) || !float.IsFinite(rect.MaxZ) ||
            !(rect.MaxX > rect.MinX) || !(rect.MaxZ > rect.MinZ))
        {
            string component = !float.IsFinite(rect.MinX) ? "MinX" : !float.IsFinite(rect.MinZ) ? "MinZ" :
                !float.IsFinite(rect.MaxX) ? "MaxX" : !float.IsFinite(rect.MaxZ) ? "MaxZ" :
                !(rect.MaxX > rect.MinX) ? "MaxX" : "MaxZ";
            float actual = component switch { "MinX" => rect.MinX, "MinZ" => rect.MinZ, "MaxX" => rect.MaxX, _ => rect.MaxZ };
            bool finite = float.IsFinite(actual);
            AddFinding(new(finite ? MapDocumentRefusal.BoundsInvalid : MapDocumentRefusal.ValueNonfinite,
                label + ": footprint must be finite with MaxX > MinX and MaxZ > MinZ.")
            { LayerId = layerId, FieldPath = field + "." + component, Index = index, EntityId = entityId, ActualNumber = actual,
                MinimumNumber = finite ? component == "MaxX" ? rect.MinX : rect.MinZ : null,
                MaximumNumber = finite ? actual : null }, errors, findings);
        }
    }

    static bool Positive(float value) => float.IsFinite(value) && value > 0;
}
