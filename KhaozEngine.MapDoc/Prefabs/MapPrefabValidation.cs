using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Structural prefab validity and closure checks. Validation never allocates world identities.</summary>
public static class MapPrefabValidation
{
    public static IReadOnlyList<string> Validate(MapDocument document, MapAssetClosure? assets) => Validate(document, assets, null);

    /// <summary>Returns the ordered immutable findings behind the existing prose validation API.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateFindings(MapDocument document, MapAssetClosure? assets)
    {
        var findings = new List<MapDocumentFinding>();
        _ = Validate(document, assets, findings);
        return findings.AsReadOnly();
    }

    internal static IReadOnlyList<string> Validate(MapDocument document, MapAssetClosure? assets, List<MapDocumentFinding>? findings)
        => Validate(document, assets, findings, out _);

    static IReadOnlyList<string> Validate(MapDocument document, MapAssetClosure? assets, List<MapDocumentFinding>? findings,
        out Exception? cause)
    {
        ArgumentNullException.ThrowIfNull(document);
        cause = null;
        var errors = new List<string>();
        try { ValidateDocument(document, assets); }
        catch (MapDocumentException ex)
        {
            cause = ex;
            errors.Add(ex.Message);
            if (ex.Findings is { Count: > 0 } nested) findings?.AddRange(nested);
            else if (MapDocumentFinding.FromException(ex) is { } finding) findings?.Add(finding);
            else findings?.Add(new(MapDocumentRefusal.PrefabStateUnreadable, ex.Message) { DocumentId = document.Id });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException or JsonException)
        {
            cause = ex;
            string message = "Invalid prefab state: " + ex.Message;
            errors.Add(message);
            var known = MapDocumentJsonErrors.TryWrapKnown(ex, null, message, out _);
            if (known?.Findings is { Count: > 0 } nested) findings?.AddRange(nested);
            else findings?.Add(known is not null ? MapDocumentFinding.FromException(known)!
                : new(MapDocumentRefusal.PrefabStateUnreadable, message) { DocumentId = document.Id });
        }
        return errors;
    }

    internal static void RequireDocument(MapDocument document, MapAssetClosure assets)
    {
        var findings = new List<MapDocumentFinding>();
        var errors = Validate(document, assets, findings, out Exception? cause);
        if (errors.Count != 0) throw MapDocumentRefusalFactory.FromFinding(findings[0], string.Join("\n", errors), cause, findings);
    }

    static void ValidateDocument(MapDocument doc, MapAssetClosure? assets)
    {
        MapPrismCoverage.RequireStandaloneStoreys(doc);
        bool present = doc.Prefabs is not null || doc.BuildingIntent is not null || doc.NativeResourceTransforms is not null;
        if (present && (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } || doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2))
        {
            var identity = doc.ResolverIdentity;
            var reason = identity is null ? MapDocumentRefusal.SchemaValueRequired
                : identity.PayloadVersion != 1 ? MapDocumentRefusal.SchemaPayloadVersionUnsupported : MapDocumentRefusal.ResolverContractMismatch;
            throw new MapDocumentException(reason, "Prefab sections require resolver identity (1, 2) and AuthoredBindingsV2.")
            {
                PayloadVersion = identity?.PayloadVersion, ResolverVersion = identity?.ResolverVersion, SupportRecipe = doc.SupportRecipe,
                ExpectedResolverVersion = 2, ExpectedSupportRecipe = MapSupportRecipe.AuthoredBindingsV2, SubjectKind = "prefab",
                FieldPath = identity is null || identity.ResolverVersion != 2 && identity.PayloadVersion == 1 ? "resolverIdentity"
                    : identity.PayloadVersion != 1 ? "resolverIdentity.payloadVersion" : "supportRecipe",
                ActualVersion = identity is { PayloadVersion: not 1 } ? identity.PayloadVersion : null,
                SupportedVersions = identity is { PayloadVersion: not 1 } ? [1] : null,
            };
        }
        if (doc.Prefabs is { } set)
        {
            if (set.PayloadVersion != 1 || set.ExpansionVersion != 1)
                throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "Unsupported prefab set or expansion version.")
                {
                    FieldPath = set.PayloadVersion != 1 ? "$.prefabs.payloadVersion" : "$.prefabs.expansionVersion",
                    ActualVersion = set.PayloadVersion != 1 ? set.PayloadVersion : set.ExpansionVersion, SupportedVersions = [1],
                };
            int instances = 0, transfers = 0, attachments = 0;
            if (set.Instances is null || (instances = set.Instances.Count) > MapPrefabJson.Limits.MaxInstances ||
                set.Transfers is null || (transfers = set.Transfers.Count) > MapPrefabJson.Limits.MaxTransfers ||
                set.Attachments is null || (attachments = set.Attachments.Count) > MapPrefabJson.Limits.MaxTransfers)
            {
                (string field, long? count, long limit) = set.Instances is null || instances > MapPrefabJson.Limits.MaxInstances
                    ? ("instances", set.Instances is null ? (long?)null : instances, MapPrefabJson.Limits.MaxInstances)
                    : set.Transfers is null || transfers > MapPrefabJson.Limits.MaxTransfers
                        ? ("transfers", set.Transfers is null ? (long?)null : transfers, MapPrefabJson.Limits.MaxTransfers)
                        : ("attachments", set.Attachments is null ? (long?)null : attachments, MapPrefabJson.Limits.MaxTransfers);
                throw MapPrefabCapacity.CollectionRefusal(field, count, limit,
                    "Prefab root collection limit exceeded or collection missing.", documentId: doc.Id);
            }
            Unique(set.Instances, i => i.Id, "prefab instance");
            foreach (MapPrefabInstance instance in set.Instances)
            {
                Text(instance.ResourceId); Digest(instance.ResourceSha256); Pose(instance.Transform);
                Unique(instance.Bindings.Entries, b => b.Child, "binding");
                foreach (MapPrefabBinding binding in instance.Bindings.Entries) Binding(binding);
                Unique(instance.Overrides, o => o.Child, "override");
                foreach (MapPrefabOverride value in instance.Overrides) Override(value);
                if (instance.Unpacked && !instance.Removed) throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "An unpacked source must remain a removed instance.")
                    { InstanceId = instance.Id, FieldPath = "$.removed", SubjectKind = "unpacked source" };
                // Unpacked inventory is historical. Ordinary deleted instances retain their existing reservation semantics.
                if (instance.Removed)
                {
                    if (instance.Unpacked) RequireRemovedBindings(instance, set.Transfers);
                    else if (set.Transfers.Any(t => t.InstanceId == instance.Id)) throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "A source with transfers must be marked unpacked.")
                        { InstanceId = instance.Id, FieldPath = "$.unpacked", SubjectKind = "transfer source" };
                    continue;
                }
                if (assets is null) continue;
                MapResolvedResource resource = assets.GetResource(instance.ResourceId);
                if (resource.Kind != MapResourceKind.Prefab || resource.Reference.Sha256 != instance.ResourceSha256)
                    throw new MapDocumentException(resource.Kind != MapResourceKind.Prefab ? MapDocumentRefusal.ReferenceKindMismatch : MapDocumentRefusal.PrefabAssetPinMismatch,
                        "Instance '" + instance.Id + "' does not match its verified Prefab resource.")
                    {
                        InstanceId = instance.Id, ResourceId = instance.ResourceId, ExpectedHash = instance.ResourceSha256, ActualHash = resource.Reference.Sha256,
                        ExpectedResourceKind = MapResourceKind.Prefab, ActualResourceKind = resource.Kind,
                    };
                MapPrefabAsset asset = assets.GetPrefab(instance.ResourceId);
                RequireAsset(asset, assets);
                MapPrefabLocalMaterializations.Validate(asset, assets);
                var keys = asset.Children.Select(c => c.Key).ToHashSet();
                foreach (MapPrefabAddOverride add in Intents(instance.Overrides).OfType<MapPrefabAddOverride>())
                    if (!keys.Add(add.Child)) throw new MapDocumentException(MapDocumentRefusal.PrefabAddedChildExists, "Add override duplicates a declared child.")
                        { InstanceId = instance.Id, Child = add.Child };
                foreach (MapPrefabOverride value in instance.Overrides)
                    if (!keys.Contains(value.Child)) throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Override names an undeclared child.")
                        { InstanceId = instance.Id, Child = value.Child, SubjectKind = "override target" };
                var reconciled = MapPrefabMaterializationOverrides.Reconcile(asset, instance.Overrides);
                var childSet = MapPrefabChildSet.Compute(asset, assets, instance.Overrides);
                RequireBindings(instance, childSet);
                var effective = reconciled.EffectiveAsset.Children.ToDictionary(c => c.Key);
                foreach (MapPrefabOverride retained in Intents(instance.Overrides))
                {
                    if (retained is MapPrefabReplaceOverride replacement) effective[retained.Child] = replacement.Value;
                    if (retained is MapPrefabAddOverride added) effective[retained.Child] = added.Value;
                    if (retained is MapPrefabPlacementOverride placement && effective.TryGetValue(retained.Child, out var target) && target is MapPrefabPlacementChild owner)
                        effective[retained.Child] = MapPrefabMaterializationOverrides.Placement(owner, placement.Transform, placement.AssetId, placement.Tags);
                }
                bool needsParent = effective.Values.Any(c => c is MapPrefabRecordChild { Value: MapSpaceDoc } or MapPrefabWaterChild ||
                    c.References.Any(r => r.Target.Id == "$parent"));
                if (needsParent && instance.ParentSpace is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Prefab rooms and $parent references require ParentSpace.")
                    { InstanceId = instance.Id, FieldPath = "$.parentSpace" };
                if (instance.ParentSpace is { } parent) { Text(parent.Id); Text(parent.Anchor.SurfaceId); }
                RequireAsset(reconciled.LiveAsset, assets);
                MapPrefabLocalMaterializations.Validate(reconciled.LiveAsset, assets);
                // Rows pin the original captured definitions, so the effective view is checked without them.
                RequireAsset(asset with { Children = effective.Values.ToArray(), MaterializedAttachments = [] }, assets, asset.MaterializedAttachments);
                RequireLeafResources(effective.Values, assets, null);
                foreach (MapPrefabPlacementOverride placement in Intents(instance.Overrides).OfType<MapPrefabPlacementOverride>())
                    if (placement.AssetId is { } assetId) _ = assets.GetAsset(assetId);
            }
            Unique(set.Transfers, t => (t.InstanceId, t.Binding.Child), "transfer");
            foreach (MapPrefabBindingTransfer transfer in set.Transfers) { Text(transfer.InstanceId); Binding(transfer.Binding); }
            Unique(set.Attachments, a => (a.PlacementId, a.Kind, a.ResourceId), "attachment transfer");
            foreach (MapPrefabAttachmentTransfer attachment in set.Attachments)
            {
                Text(attachment.PlacementId); Text(attachment.ResourceId); Digest(attachment.ResourceSha256); Defined(attachment.Kind);
                Unique(attachment.Children, c => c.LocalChild, "attached child");
                foreach (MapPrefabAttachedChild child in attachment.Children)
                {
                    Key(child.LocalChild); Text(child.WorldId); Pose(child.OwnerLocalPose, "$.ownerLocalPose"); Digest(child.SourceDigest);
                    if (child.Anchor is { } anchor) Text(anchor.SurfaceId);
                    if (attachment.Kind == MapPrefabAttachmentKind.Support && child.LocalChild.Kind is not (MapPrefabChildKind.Surface or MapPrefabChildKind.Record) ||
                        attachment.Kind == MapPrefabAttachmentKind.Light && child.LocalChild.Kind != MapPrefabChildKind.Light)
                        throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Attached child kind does not match its attachment.")
                        {
                            AttachmentKind = attachment.Kind, Child = child.LocalChild, ChildKind = child.LocalChild.Kind,
                            PlacementId = attachment.PlacementId, ResourceId = attachment.ResourceId, WorldId = child.WorldId,
                        };
                }
            }
            // Run the same finite preflight over in-memory state before publication.
            using var json = JsonDocument.Parse(MapPrefabJson.Bytes(set));
            MapPrefabJson.Preflight(json.RootElement);
        }
        if (doc.BuildingIntent is { } intent)
        {
            if (intent.PayloadVersion != 1) throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "Unsupported building intent payload version.")
                { FieldPath = "$.buildingIntent.payloadVersion", ActualVersion = intent.PayloadVersion, SupportedVersions = [1] };
            Unique(intent.Lights, l => l.Id, "light"); foreach (var l in intent.Lights) Light(l);
            Unique(intent.Markers, m => m.Id, "marker"); foreach (var m in intent.Markers) Marker(m);
            Unique(intent.RoofLinks, r => r.Id, "roof link"); foreach (var r in intent.RoofLinks) Roof(r);
            Unique(intent.RoofGroups, g => g.Id, "roof group"); foreach (var g in intent.RoofGroups) Group(g);
            Unique(intent.Roofs, r => (r.Scope, r.Kind, r.Id), "roof inventory");
            foreach (var roof in intent.Roofs) RoofReference(roof);
            Unique(intent.Storeys, s => s.Id, "storey");
            foreach (var s in intent.Storeys)
            {
                if (s.PayloadVersion != 1) throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "Unsupported storey payload version.")
                    { FieldPath = "$.payloadVersion", ActualVersion = s.PayloadVersion, SupportedVersions = [1], EntityId = s.Id };
                Text(s.Name);
                if (s.InstanceId is { } owner && (doc.Prefabs is null || !doc.Prefabs.Instances.Any(i => i.Id == owner)))
                    throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Storey names an unknown instance.")
                        { InstanceId = owner, EntityId = s.Id, SubjectKind = "storey owner" };
            }
            using var json = JsonDocument.Parse(MapPrefabJson.Bytes(intent));
            MapPrefabJson.Preflight(json.RootElement);
        }
        if (doc.NativeResourceTransforms is { } poses)
        {
            Unique(poses, p => (p.Kind, p.OwnerId), "standalone resource transform");
            foreach (var p in poses) { Defined(p.Kind); Text(p.OwnerId); Pose(p.Transform, "$.transform"); Digest(p.SourceDigest); }
            using var json = JsonDocument.Parse(MapPrefabJson.Bytes(poses));
            MapPrefabJson.Preflight(json.RootElement);
        }
        // R6-P3. Closure-bound validation of a complete document, loaded or stored, certifies effective marker IDs and
        // names from metadata alone. A partial window cannot certify the whole world.
        if (assets is not null && doc.Tiles is not { IsPartial: true }) MapMarkerExpansion.RequireUnique(doc, assets);
    }

    internal static void RequireAsset(MapPrefabAsset asset) => RequireAsset(asset, null);

    internal static void RequireAsset(MapPrefabAsset asset, MapAssetClosure? assets,
        IReadOnlyList<MapPrefabLocalMaterialization>? suppressed = null)
    {
        if (asset.PayloadVersion != 1) throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "Unsupported prefab payload version.")
            { FieldPath = "$.payloadVersion", ActualVersion = asset.PayloadVersion, SupportedVersions = [1], AssetId = asset.Id };
        Text(asset.Id, "$.id");
        int count = 0;
        if (asset.Children is null || (count = asset.Children.Count) > MapPrefabJson.Limits.MaxChildren)
            throw MapPrefabCapacity.CollectionRefusal("children", asset.Children is null ? null : count, MapPrefabJson.Limits.MaxChildren,
                "Prefab child limit exceeded or collection missing.", assetId: asset.Id);
        Unique(asset.Children, c => c.Key, "prefab child");
        var children = asset.Children.ToDictionary(c => c.Key);
        var targets = new ChildTargets(children, assets, suppressed ?? asset.MaterializedAttachments);
        foreach (MapPrefabChild child in asset.Children)
        {
            Child(child);
            foreach (MapPrefabReferenceSlot slot in child.References ?? []) if (slot?.Target is { } target) Reference(target);
            var (_, occurrences) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
            MapPrefabReferences.RequireSlots(child.References!, occurrences, targets,
                child is MapPrefabRecordChild ? child.LocalTransform : null, $"Prefab {child.Key.Kind} '{child.Key.Key}'", child.Key);
            if (child is MapPrefabRecordChild && child.References!.Single(s => s.Path == "/anchor/surfaceId").Target.Scope != MapPrefabReferenceScope.Local)
                throw new MapDocumentException(MapDocumentRefusal.PrefabLocalAnchorInvalid, $"Prefab record '{child.Key.Key}' must be anchored on a declared local surface.")
                    { Child = child.Key, RecordId = child.Key.Key, FieldPath = "$.anchor.surfaceId" };
            // Annotation references carry their scope directly and take no slots.
            foreach (MapPrefabReference reference in MapPrefabReferences.Annotations(child))
                if (reference.Scope == MapPrefabReferenceScope.Local && !targets.Has(reference.Kind, reference.Id))
                    throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"Prefab {child.Key.Kind} '{child.Key.Key}' names undeclared local {reference.Kind} '{reference.Id}'.")
                        { Child = child.Key, ChildKind = reference.Kind, EntityId = reference.Id, SubjectKind = "local annotation target" };
        }
        Unique(asset.Roofs, r => (r.Scope, r.Kind, r.Id), "roof inventory");
        foreach (MapPrefabReference roof in asset.Roofs)
        {
            RoofReference(roof);
            if (roof.Scope == MapPrefabReferenceScope.Local && !children.ContainsKey(new(roof.Kind, roof.Id)))
                throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Roof inventory names an undeclared placement.")
                    { EntityId = roof.Id, ChildKind = roof.Kind, SubjectKind = "roof placement" };
        }
        MapPrefabLocalMaterializations.RequireStructure(asset);
        using var json = JsonDocument.Parse(MapPrefabJson.Bytes(asset));
        MapPrefabJson.Preflight(json.RootElement);
    }

    internal static void RequireLeafResources(IEnumerable<MapPrefabChild> children, MapAssetClosure assets, IReadOnlyList<string>? dependencies)
    {
        foreach (MapPrefabChild child in children)
        {
            if (child is MapPrefabPlacementChild placement)
            {
                MapResolvedAsset asset = assets.GetAsset(placement.Value.AssetId!);
                // Every leaf resource kind was checked before the verified closure was published.
                foreach (var (id, _) in MapAssetManifestReader.ResourceReferences(asset)) RequireDependency(id);
            }
            if (child is MapPrefabWaterChild water)
            {
                string id = water.Value.RenderMaterial;
                MapResolvedResource resource = assets.GetResource(id);
                if (resource.Kind != MapResourceKind.Material)
                    throw new MapDocumentException(MapDocumentRefusal.ReferenceKindMismatch, "Prefab leaf requires Material resource '" + id + "'.")
                        { ResourceId = id, ExpectedResourceKind = MapResourceKind.Material, ActualResourceKind = resource.Kind, Child = child.Key };
                RequireDependency(id);
            }
        }
        void RequireDependency(string id)
        {
            if (dependencies is not null && !dependencies.Contains(id, StringComparer.Ordinal))
                throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Prefab payload resource edge '" + id + "' is not a declared dependency.")
                    { ResourceId = id, SubjectKind = "prefab dependency" };
        }
    }

    static void Child(MapPrefabChild child)
    {
        Key(child.Key, "$.key"); Pose(child.LocalTransform, "$.localTransform", child.Key);
        (MapPrefabChildKind Kind, string Id) identity = child switch
        {
            MapPrefabPlacementChild p => (MapPrefabChildKind.Placement, p.Value.Id),
            MapPrefabSurfaceChild s => (MapPrefabChildKind.Surface, s.Surface.Id),
            MapPrefabRecordChild r => (MapPrefabChildKind.Record, r.Value.Id),
            MapPrefabStoreyChild => (MapPrefabChildKind.Storey, child.Key.Key),
            MapPrefabMediumChild m => (MapPrefabChildKind.WaterMedium, m.Value.Key),
            MapPrefabWaterChild w => (MapPrefabChildKind.WaterBody, w.Value.Id),
            MapPrefabLightChild l => (MapPrefabChildKind.Light, l.Value.Id),
            MapPrefabMarkerChild m => (MapPrefabChildKind.Marker, m.Value.Id),
            MapPrefabRoofChild r => (MapPrefabChildKind.RoofLink, r.Value.Id),
            MapPrefabRoofGroupChild g => (MapPrefabChildKind.RoofGroup, g.Value.Id),
            _ => throw new MapDocumentException(MapDocumentRefusal.OperationKindUnsupported, "Unsupported prefab child.")
                { RecordType = child.GetType(), Child = child.Key },
        };
        if (identity.Kind != child.Key.Kind || identity.Id != child.Key.Key)
            throw new MapDocumentException(MapDocumentRefusal.PrefabPayloadIdMismatch, "Prefab child key does not match its payload ID/kind.")
                { Child = child.Key, ChildKind = identity.Kind, EntityId = identity.Id, SubjectKind = identity.Kind.ToString(), FieldPath = "$.key" };
        switch (child)
        {
            case MapPrefabPlacementChild p:
                if (p.Value.NumericId is not null) throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "Prefab placement numeric IDs belong to instance bindings.")
                    { Child = child.Key, NumericId = p.Value.NumericId, FieldPath = "$.value.numericId" };
                if (p.LocalTransform != new MapTransform(Vector3.Zero, 0, 1))
                    throw new MapDocumentException(MapDocumentRefusal.PrefabPlacementTransformDuplicated, "Placement children must use only their placement transform.")
                        { Child = child.Key, FieldPath = "$.localTransform" };
                Pose(new MapTransform(new(p.Value.X, p.Value.Y ?? 0, p.Value.Z), p.Value.Yaw, p.Value.Scale));
                Text(p.Value.AssetId!); Text(p.Value.Kind);
                if (p.Value.Tags is null || p.Value.Tags.Any(t => t is null))
                    throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Placement tags must not be null.")
                        { Child = child.Key, FieldPath = "$.value.tags" };
                break;
            case MapPrefabSurfaceChild s:
                Unique(s.Patches, p => p.Key, "surface patch");
                foreach (MapSurfacePatch patch in s.Patches)
                {
                    if (patch.Key.SurfaceId != s.Surface.Id || patch.Records.Count != 0)
                        throw new MapDocumentException(patch.Key.SurfaceId != s.Surface.Id ? MapDocumentRefusal.AuthoredOwnershipMismatch : MapDocumentRefusal.SchemaArrayLengthMismatch,
                            "Surface children must contain only their geometry and no embedded records.")
                        {
                            Child = child.Key, SurfaceId = s.Surface.Id, Patch = patch.Key,
                            FieldPath = patch.Key.SurfaceId != s.Surface.Id ? "$.key.surfaceId" : "$.records",
                            ActualCount = patch.Key.SurfaceId != s.Surface.Id ? null : patch.Records.Count,
                            ExpectedCount = patch.Key.SurfaceId != s.Surface.Id ? null : 0,
                        };
                    _ = MapSurfacePatchCodec.Encode(patch);
                }
                break;
            case MapPrefabRecordChild { Value: MapPrismDomain prism }:
                if (prism.InstanceId != "") throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "Prefab-local prism ownership is assigned by expansion.")
                    { Child = child.Key, RecordId = prism.Id, InstanceId = prism.InstanceId, FieldPath = "$.value.instanceId" };
                break;
            case MapPrefabStoreyChild s: Text(s.Name); break;
            case MapPrefabLightChild l: Light(l.Value); break;
            case MapPrefabMarkerChild m: Marker(m.Value); break;
            case MapPrefabRoofChild r: Roof(r.Value); break;
            case MapPrefabRoofGroupChild g: Group(g.Value); break;
        }
    }

    // Every key in the effective child set has a binding, live keys are not Removed and every other binding is.
    // A removed instance keeps its reservations without a live child set. On that tombstone a key may instead be
    // covered by a transfer from this source instance, which then owns the identity alone.
    static void RequireRemovedBindings(MapPrefabInstance instance, IReadOnlyList<MapPrefabBindingTransfer> transfers)
    {
        var inventory = instance.Bindings.Entries.ToDictionary(b => b.Child);
        var owned = transfers.Where(t => t.InstanceId == instance.Id).ToDictionary(t => t.Binding.Child);
        foreach (var binding in inventory.Values)
        {
            if (binding.Removed)
            {
                if (owned.ContainsKey(binding.Child)) throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "Dormant source bindings must remain reserved without a transfer.")
                    { InstanceId = instance.Id, Child = binding.Child, WorldId = binding.WorldId, ActualCount = 1, ExpectedCount = 0, SubjectKind = "dormant source binding" };
            }
            else if (!owned.TryGetValue(binding.Child, out var transfer) || transfer.Binding != binding)
                throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "The source inventory requires exactly one matching transfer for every transferred child.")
                    { InstanceId = instance.Id, Child = binding.Child, WorldId = binding.WorldId, ActualCount = owned.ContainsKey(binding.Child) ? 1 : 0, ExpectedCount = 1, SubjectKind = "transferred source binding" };
        }
        foreach (var transfer in owned.Values)
            if (!inventory.ContainsKey(transfer.Binding.Child)) throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, "A transfer must belong to its retained source inventory.")
                { InstanceId = instance.Id, Child = transfer.Binding.Child, WorldId = transfer.Binding.WorldId, SubjectKind = "retained source inventory" };
    }

    static void RequireBindings(MapPrefabInstance instance, MapPrefabChildSet children)
    {
        var bindings = instance.Bindings.Entries.ToDictionary(b => b.Child);
        foreach (MapPrefabChildKey key in children.Known.Concat(children.Live.Keys))
            if (!bindings.ContainsKey(key)) throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Prefab bindings must completely describe every child.")
                { InstanceId = instance.Id, Child = key, SubjectKind = "prefab binding" };
        foreach (MapPrefabBinding binding in instance.Bindings.Entries)
            if (binding.Removed == children.Live.ContainsKey(binding.Child))
                throw new MapDocumentException(MapDocumentRefusal.AuthoredOwnershipMismatch, $"Binding for {binding.Child.Kind} '{binding.Child.Key}' must be Removed exactly when its child is absent.")
                    { InstanceId = instance.Id, Child = binding.Child, WorldId = binding.WorldId, SubjectKind = "prefab binding liveness" };
    }

    sealed class ChildTargets : IMapPrefabTargets
    {
        readonly IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChild> _explicit;
        readonly Dictionary<MapPrefabChildKey, MapPrefabChild> _implicit = new();
        readonly IReadOnlyList<MapPrefabLocalMaterialization> _suppressed;
        readonly bool _closureFree;
        internal ChildTargets(IReadOnlyDictionary<MapPrefabChildKey, MapPrefabChild> children, MapAssetClosure? assets,
            IReadOnlyList<MapPrefabLocalMaterialization> suppressed)
        {
            _explicit = children; _suppressed = suppressed; _closureFree = assets is null;
            if (assets is null) return;
            var owned = children.Values.ToArray();
            foreach (var child in MapEffectiveSupport.Enumerate(assets, owned, suppressed).Concat(MapPrefabAttachedLights.Enumerate(assets, owned, suppressed)))
                if (children.ContainsKey(child.Key) || !_implicit.TryAdd(child.Key, child))
                    throw new MapDocumentException(MapDocumentRefusal.IdentityDuplicate, "Implicit child collides with an explicit or implicit child.")
                        { Child = child.Key, ChildKind = child.Key.Kind, EntityId = child.Key.Key, SubjectKind = "implicit child" };
        }
        bool Parsed(MapPrefabChildKind kind, string id, out MapPrefabPlacementChild? owner) =>
            MapPrefabReferences.ImplicitOwner(kind, id, _explicit.Values, _suppressed, out owner);
        public bool Deferred(MapPrefabChildKind kind, string id) => _closureFree && !_explicit.ContainsKey(new(kind, id)) && Parsed(kind, id, out _);
        public bool Has(MapPrefabChildKind kind, string id) => _explicit.ContainsKey(new(kind, id)) || _implicit.ContainsKey(new(kind, id)) || Deferred(kind, id);
        public bool HasPatch(MapPatchKey key)
        {
            if (Deferred(MapPrefabChildKind.Surface, key.SurfaceId)) return true;
            return Child(MapPrefabChildKind.Surface, key.SurfaceId) is MapPrefabSurfaceChild surface && surface.Patches.Any(p => p.Key == key);
        }
        MapPrefabChild? Child(MapPrefabChildKind kind, string id) => _explicit.TryGetValue(new(kind, id), out var child) ? child
            : _implicit.TryGetValue(new(kind, id), out child) ? child : null;
        public MapPatchKey? Anchor(string recordId) => Child(MapPrefabChildKind.Record, recordId) is MapPrefabRecordChild record ? record.Anchor : null;
        public MapTransform? Pose(MapPrefabChildKind kind, string id) => _explicit.TryGetValue(new(kind, id), out var child) ? child.LocalTransform
            : Parsed(kind, id, out var owner) ? MapPrefabMaterializationOverrides.Pose(owner!.Value) : null;
    }

    static IEnumerable<MapPrefabOverride> Intents(IReadOnlyList<MapPrefabOverride> overrides)
    {
        foreach (MapPrefabOverride value in overrides)
        {
            yield return value;
            if (value is MapPrefabRemoveOverride { Previous: { } previous }) yield return previous;
        }
    }

    static void Override(MapPrefabOverride value)
    {
        Key(value.Child);
        switch (value)
        {
            case MapPrefabRemoveOverride r when r.Previous is MapPrefabRemoveOverride: Fail(MapDocumentRefusal.PrefabPreviousOverrideNestedRemove, "Previous must not be a remove override.", new() { Child = r.Child, FieldPath = "$.previous" }); break;
            case MapPrefabRemoveOverride r when r.Previous is not null:
                if (r.Previous.Child != r.Child) Fail(MapDocumentRefusal.AuthoredValueMismatch, "Previous override must retain the same child key.", new() { Child = r.Previous.Child, ExpectedChild = r.Child, FieldPath = "$.previous.child" });
                Override(r.Previous); break;
            case MapPrefabReplaceOverride r: Child(r.Value); if (r.Child != r.Value.Key) Fail(MapDocumentRefusal.AuthoredValueMismatch, "Replacement key differs.", new() { Child = r.Value.Key, ExpectedChild = r.Child, FieldPath = "$.value.key" }); break;
            case MapPrefabAddOverride a: Child(a.Value); if (a.Child != a.Value.Key) Fail(MapDocumentRefusal.AuthoredValueMismatch, "Added key differs.", new() { Child = a.Value.Key, ExpectedChild = a.Child, FieldPath = "$.value.key" }); break;
            case MapPrefabTransformOverride t: Pose(t.Transform); break;
            case MapPrefabPlacementOverride p:
                if (p.Child.Kind != MapPrefabChildKind.Placement) Fail(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Placement override requires a Placement child.", new() { Child = p.Child, ChildKind = p.Child.Kind, ExpectedChildKind = MapPrefabChildKind.Placement, FieldPath = "$.child.kind", SubjectKind = "placement override" });
                if (p.Transform is { } transform) Pose(transform);
                if (p.AssetId is { } assetId) Text(assetId);
                if (p.Tags is { } tags && tags.Any(t => t is null)) Fail(MapDocumentRefusal.SchemaValueRequired, "Override tags must not contain null.", new() { Child = p.Child, FieldPath = "$.tags" });
                break;
        }
    }

    internal static void RequireEffectiveAnnotations(MapBuildingIntent intent, IReadOnlyList<MapResolvedPlacement> placements, MapSurfaceSet surfaces)
    {
        if (intent.Roofs.Count == 0 && intent.RoofLinks.Count == 0 && intent.RoofGroups.Count == 0 && intent.Lights.Count == 0 && intent.Markers.Count == 0) return;
        Unique(intent.Lights, l => l.Id, "effective light");
        Unique(intent.Markers, m => m.Id, "effective marker");
        Unique(intent.Markers, m => m.Name, "effective marker name");
        Unique(intent.RoofLinks, r => r.Id, "effective roof link");
        Unique(intent.RoofGroups, g => g.Id, "effective roof group");
        var owners = placements.Select(p => p.PlacementId).ToHashSet(StringComparer.Ordinal);
        var rooms = surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapSpaceDoc>()
            .Where(r => r.Kind == MapSpaceKind.Room).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var roofs = intent.Roofs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var groups = intent.RoofGroups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        void Require(MapPrefabReference reference, MapPrefabChildKind kind, HashSet<string> ids, string what)
        {
            if (reference.Scope != MapPrefabReferenceScope.World || reference.Kind != kind || !ids.Contains(reference.Id))
                Fail(reference.Scope != MapPrefabReferenceScope.World ? MapDocumentRefusal.AuthoredValueMismatch
                    : reference.Kind != kind ? MapDocumentRefusal.AuthoredReferenceKindInvalid : MapDocumentRefusal.ReferenceMissing,
                    $"Unknown effective {what} '{reference.Id}'.", new()
                    {
                        EntityId = reference.Id, ReferenceScope = reference.Scope, ExpectedReferenceScope = MapPrefabReferenceScope.World,
                        ChildKind = reference.Kind, ExpectedChildKind = kind, SubjectKind = what,
                    });
        }
        foreach (var roof in intent.Roofs) Require(roof, MapPrefabChildKind.Placement, owners, "roof placement");
        foreach (var group in intent.RoofGroups)
            foreach (var room in group.Rooms) Require(room, MapPrefabChildKind.Record, rooms, "roof room");
        foreach (var link in intent.RoofLinks)
        {
            Require(link.Roof, MapPrefabChildKind.Placement, roofs, "inventoried roof");
            foreach (var room in link.Rooms) Require(room, MapPrefabChildKind.Record, rooms, "roof room");
            foreach (var group in link.Groups) Require(group, MapPrefabChildKind.RoofGroup, groups, "roof group");
        }
        foreach (var light in intent.Lights)
        {
            Light(light);
            if (light.Owner is { } owner) Require(owner, MapPrefabChildKind.Placement, owners, "light owner");
        }
        foreach (var marker in intent.Markers) Marker(marker);
    }

    static void Binding(MapPrefabBinding binding)
    {
        Key(binding.Child); Text(binding.WorldId);
        if (binding.NumericId is { } numeric && (numeric <= 0 || binding.Child.Kind != MapPrefabChildKind.Placement)) Fail(numeric <= 0 ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.AuthoredReferenceKindInvalid,
                "Only placement bindings may have positive numeric IDs.", new()
                {
                    Child = binding.Child, ChildKind = binding.Child.Kind, WorldId = binding.WorldId, NumericId = numeric,
                    MinimumInteger = numeric <= 0 ? 1 : null, ExpectedChildKind = numeric <= 0 ? null : MapPrefabChildKind.Placement,
                    FieldPath = numeric <= 0 ? "$.numericId" : "$.child.kind", SubjectKind = "prefab binding",
                });
    }
    internal static void RequireLight(MapLocalLightDoc light) => Light(light);
    static void Light(MapLocalLightDoc l)
    {
        if (l.PayloadVersion != 1) throw new MapDocumentException(MapDocumentRefusal.SchemaPayloadVersionUnsupported, "Unsupported local light payload version.")
            { FieldPath = "$.payloadVersion", ActualVersion = l.PayloadVersion, SupportedVersions = [1], EntityId = l.Id };
        Text(l.Id, "$.id"); Vector(l.Position, "$.position"); Vector(l.Colour, "$.colour");
        if (!float.IsFinite(l.Intensity) || l.Intensity < 0 || !float.IsFinite(l.Radius) || l.Radius <= 0)
        {
            bool intensity = !float.IsFinite(l.Intensity) || l.Intensity < 0;
            float value = intensity ? l.Intensity : l.Radius;
            throw new MapDocumentException(!float.IsFinite(value) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange, "Invalid local light intensity/radius.")
                { EntityId = l.Id, FieldPath = intensity ? "$.intensity" : "$.radius", ActualNumber = value, MinimumNumber = float.IsFinite(value) ? 0 : null };
        }
        if (l.Colour.X < 0 || l.Colour.Y < 0 || l.Colour.Z < 0)
            throw new MapDocumentException(MapDocumentRefusal.ValueOutsideRange, "Light colour must be nonnegative.")
            {
                EntityId = l.Id, FieldPath = l.Colour.X < 0 ? "$.colour.x" : l.Colour.Y < 0 ? "$.colour.y" : "$.colour.z",
                ActualNumber = l.Colour.X < 0 ? l.Colour.X : l.Colour.Y < 0 ? l.Colour.Y : l.Colour.Z, MinimumNumber = 0,
            };
        if (l.Owner is { } owner)
        {
            Reference(owner, "$.owner");
            if (owner.Kind != MapPrefabChildKind.Placement)
                throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Light owner requires a Placement reference.")
                    { FieldPath = "$.owner.kind", EntityId = owner.Id, ChildKind = owner.Kind, SubjectKind = "light owner" };
        }
    }
    static void Marker(MapLocalMarkerDoc m) => MapMarkerValidation.Require(m);
    static void Roof(MapRoofLinkDoc roof)
    {
        Text(roof.Id); RoofReference(roof.Roof);
        foreach (var room in roof.Rooms)
        {
            Reference(room);
            if (room.Kind != MapPrefabChildKind.Record)
                throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Roof room requires a Record reference.")
                    { EntityId = room.Id, ChildKind = room.Kind, SubjectKind = "roof room", FieldPath = "$.rooms" };
        }
        foreach (var group in roof.Groups)
        {
            Reference(group);
            if (group.Kind != MapPrefabChildKind.RoofGroup)
                throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Roof group requires a RoofGroup reference.")
                    { EntityId = group.Id, ChildKind = group.Kind, SubjectKind = "roof group", FieldPath = "$.groups" };
        }
        if (!float.IsFinite(roof.AboveY)) throw new MapDocumentException(MapDocumentRefusal.ValueNonfinite, "Roof link height must be finite.")
            { EntityId = roof.Id, ActualNumber = roof.AboveY, FieldPath = "$.aboveY" };
    }
    static void Group(MapRoofGroupDoc group)
    {
        Text(group.Id);
        foreach (var room in group.Rooms)
        {
            Reference(room);
            if (room.Kind != MapPrefabChildKind.Record)
                throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Roof group room requires a Record reference.")
                    { EntityId = room.Id, ChildKind = room.Kind, SubjectKind = "roof group room", FieldPath = "$.rooms" };
        }
    }
    static void RoofReference(MapPrefabReference roof)
    {
        Reference(roof);
        if (roof.Kind != MapPrefabChildKind.Placement)
            throw new MapDocumentException(MapDocumentRefusal.AuthoredReferenceKindInvalid, "Roof inventory requires Placement references.")
                { EntityId = roof.Id, ChildKind = roof.Kind, SubjectKind = "roof placement" };
    }
    static void Reference(MapPrefabReference reference, string? path = null)
    {
        Defined(reference.Scope, path is null ? null : path + ".scope", reference.Id, reference.Kind);
        Defined(reference.Kind, path is null ? null : path + ".kind", reference.Id, reference.Kind);
        Text(reference.Id, path is null ? null : path + ".id", reference.Kind);
    }
    static void Key(MapPrefabChildKey key, string? path = null)
    {
        Defined(key.Kind, path is null ? null : path + ".kind", key.Key, key.Kind);
        Text(key.Key, path is null ? null : path + ".key", key.Kind);
    }
    static void Defined<T>(T value, string? path = null, string? id = null, MapPrefabChildKind? kind = null) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new MapDocumentException(MapDocumentRefusal.SchemaEnumUnsupported, "Unsupported enum value.")
            { EnumType = typeof(T), ActualInteger = Convert.ToInt64(value), FieldPath = path, EntityId = id, ChildKind = kind };
    }
    static void Text(string value, string? path = null, MapPrefabChildKind? kind = null, string? subjectKind = null, int? index = null)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "A nonempty prefab identifier/name is required.")
            { FieldPath = path, EntityId = value, ChildKind = kind, SubjectKind = subjectKind, Index = index };
    }
    static void Digest(string value)
    {
        if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new MapDocumentException(MapDocumentRefusal.DigestMalformed, "Expected lowercase SHA-256 digest.") { ActualHash = value };
    }
    static void Vector(Vector3 value, string? path = null)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new MapDocumentException(MapDocumentRefusal.ValueNonfinite, "Expected finite XYZ.")
            {
                FieldPath = path is null ? null : path + (!float.IsFinite(value.X) ? ".x" : !float.IsFinite(value.Y) ? ".y" : ".z"),
                ActualNumber = !float.IsFinite(value.X) ? value.X : !float.IsFinite(value.Y) ? value.Y : value.Z,
            };
    }
    static void Pose(MapTransform value, string? path = null, MapPrefabChildKey? child = null)
    {
        Vector(value.Position, path is null ? null : path + ".position");
        if (!float.IsFinite(value.YawRadians) || !float.IsFinite(value.Scale) || value.Scale <= 0)
        {
            bool yaw = !float.IsFinite(value.YawRadians);
            float number = yaw ? value.YawRadians : value.Scale;
            throw new MapDocumentException(!float.IsFinite(number) ? MapDocumentRefusal.ValueNonfinite : MapDocumentRefusal.ValueOutsideRange, "Expected finite yaw and positive uniform scale.")
            {
                Child = child, FieldPath = path is null ? null : path + (yaw ? ".yawRadians" : ".scale"),
                ActualNumber = number, MinimumNumber = float.IsFinite(number) ? 0 : null,
            };
        }
    }
    static void Pose(MapEffectiveTransform value, string? path = null)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z) || !double.IsFinite(value.YawRadians) || !double.IsFinite(value.Scale) || value.Scale <= 0)
        {
            (string Field, double Number) failed = !double.IsFinite(value.X) ? (".x", value.X) : !double.IsFinite(value.Y) ? (".y", value.Y)
                : !double.IsFinite(value.Z) ? (".z", value.Z) : !double.IsFinite(value.YawRadians) ? (".yawRadians", value.YawRadians) : (".scale", value.Scale);
            throw new MapDocumentException(double.IsFinite(failed.Number) ? MapDocumentRefusal.ValueOutsideRange : MapDocumentRefusal.ValueNonfinite, "Expected finite precise pose and positive uniform scale.")
                { FieldPath = path is null ? null : path + failed.Field, ActualNumber = failed.Number, MinimumNumber = double.IsFinite(failed.Number) ? 0 : null };
        }
    }
    static void Unique<T, TKey>(IReadOnlyList<T> values, Func<T, TKey> key, string what) where TKey : notnull
    {
        if (values is null) Fail(MapDocumentRefusal.SchemaValueRequired, what + " collection must not be null.", new() { SubjectKind = what });
        var seen = new HashSet<TKey>();
        int index = 0;
        foreach (T value in values!)
        {
            if (value is null) Fail(MapDocumentRefusal.SchemaValueRequired, what + " must not be null.", new() { SubjectKind = what, Index = index });
            TKey id = key(value); if (id is string text) Text(text, subjectKind: what, index: index);
            if (!seen.Add(id)) Fail(MapDocumentRefusal.IdentityDuplicate, "Duplicate " + what + " '" + id + "'.", MapPrefabValidationSubject.FromKey(id, what, index));
            index++;
        }
    }
    [DoesNotReturn]
    static void Fail(MapDocumentRefusal reason, string message, MapPrefabValidationSubject subject) =>
        throw new MapDocumentException(reason, message)
        {
            EntityId = subject.EntityId, InstanceId = subject.InstanceId, PlacementId = subject.PlacementId, ResourceId = subject.ResourceId,
            WorldId = subject.WorldId, SurfaceId = subject.SurfaceId, Patch = subject.Patch,
            Child = subject.Child, ExpectedChild = subject.ExpectedChild, ChildKind = subject.ChildKind, ExpectedChildKind = subject.ExpectedChildKind,
            AttachmentKind = subject.AttachmentKind, ReferenceScope = subject.ReferenceScope, ExpectedReferenceScope = subject.ExpectedReferenceScope,
            NumericId = subject.NumericId, MinimumInteger = subject.MinimumInteger, FieldPath = subject.FieldPath, SubjectKind = subject.SubjectKind, Index = subject.Index,
        };

}
