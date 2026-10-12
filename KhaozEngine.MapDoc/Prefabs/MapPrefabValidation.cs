using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Structural prefab validity and closure checks. Validation never allocates world identities.</summary>
public static class MapPrefabValidation
{
    public static IReadOnlyList<string> Validate(MapDocument document, MapAssetClosure? assets)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        try { ValidateDocument(document, assets); }
        catch (MapDocumentException ex) { errors.Add(ex.Message); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException or JsonException)
        { errors.Add("Invalid prefab state: " + ex.Message); }
        return errors;
    }

    internal static void RequireDocument(MapDocument document, MapAssetClosure assets)
    {
        var errors = Validate(document, assets);
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
    }

    static void ValidateDocument(MapDocument doc, MapAssetClosure? assets)
    {
        bool present = doc.Prefabs is not null || doc.BuildingIntent is not null || doc.NativeResourceTransforms is not null;
        if (present && (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } || doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2))
            Fail("Prefab sections require resolver identity (1, 2) and AuthoredBindingsV2.");
        if (doc.Prefabs is { } set)
        {
            if (set.PayloadVersion != 1 || set.ExpansionVersion != 1) Fail("Unsupported prefab set or expansion version.");
            if (set.Instances is null || set.Instances.Count > MapPrefabJson.Limits.MaxInstances ||
                set.Transfers is null || set.Transfers.Count > MapPrefabJson.Limits.MaxTransfers ||
                set.Attachments is null || set.Attachments.Count > MapPrefabJson.Limits.MaxTransfers)
                Fail("Prefab root collection limit exceeded or collection missing.");
            Unique(set.Instances, i => i.Id, "prefab instance");
            foreach (MapPrefabInstance instance in set.Instances)
            {
                Text(instance.ResourceId); Digest(instance.ResourceSha256); Pose(instance.Transform);
                Unique(instance.Bindings.Entries, b => b.Child, "binding");
                foreach (MapPrefabBinding binding in instance.Bindings.Entries) Binding(binding);
                Unique(instance.Overrides, o => o.Child, "override");
                foreach (MapPrefabOverride value in instance.Overrides) Override(value);
                if (assets is null) continue;
                MapResolvedResource resource = assets.GetResource(instance.ResourceId);
                if (resource.Kind != MapResourceKind.Prefab || resource.Reference.Sha256 != instance.ResourceSha256)
                    Fail("Instance '" + instance.Id + "' does not match its verified Prefab resource.");
                MapPrefabAsset asset = assets.GetPrefab(instance.ResourceId);
                RequireAsset(asset, assets);
                MapPrefabLocalMaterializations.Validate(asset, assets);
                var keys = asset.Children.Select(c => c.Key).ToHashSet();
                foreach (MapPrefabAddOverride add in Intents(instance.Overrides).OfType<MapPrefabAddOverride>())
                    if (!keys.Add(add.Child)) Fail("Add override duplicates a declared child.");
                foreach (MapPrefabOverride value in instance.Overrides)
                    if (!keys.Contains(value.Child)) Fail("Override names an undeclared child.");
                RequireBindings(instance, MapPrefabChildSet.Compute(asset, assets, instance.Overrides), set.Transfers);
                var reconciled = MapPrefabMaterializationOverrides.Reconcile(asset, instance.Overrides);
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
                if (needsParent && instance.ParentSpace is null) Fail("Prefab rooms and $parent references require ParentSpace.");
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
                    Key(child.LocalChild); Text(child.WorldId); Pose(child.OwnerLocalPose); Digest(child.SourceDigest);
                    if (child.Anchor is { } anchor) Text(anchor.SurfaceId);
                    if (attachment.Kind == MapPrefabAttachmentKind.Support && child.LocalChild.Kind is not (MapPrefabChildKind.Surface or MapPrefabChildKind.Record) ||
                        attachment.Kind == MapPrefabAttachmentKind.Light && child.LocalChild.Kind != MapPrefabChildKind.Light)
                        Fail("Attached child kind does not match its attachment.");
                }
            }
            // Run the same finite preflight over in-memory state before publication.
            using var json = JsonDocument.Parse(MapPrefabJson.Bytes(set));
            MapPrefabJson.Preflight(json.RootElement);
        }
        if (doc.BuildingIntent is { } intent)
        {
            if (intent.PayloadVersion != 1) Fail("Unsupported building intent payload version.");
            Unique(intent.Lights, l => l.Id, "light"); foreach (var l in intent.Lights) Light(l);
            Unique(intent.Markers, m => m.Id, "marker"); foreach (var m in intent.Markers) Marker(m);
            Unique(intent.RoofLinks, r => r.Id, "roof link"); foreach (var r in intent.RoofLinks) Roof(r);
            Unique(intent.RoofGroups, g => g.Id, "roof group"); foreach (var g in intent.RoofGroups) Group(g);
            foreach (var roof in intent.Roofs) RoofReference(roof);
            Unique(intent.Storeys, s => s.Id, "storey");
            foreach (var s in intent.Storeys)
            {
                if (s.PayloadVersion != 1) Fail("Unsupported storey payload version.");
                Text(s.Name);
                if (s.InstanceId is { } owner && (doc.Prefabs is null || !doc.Prefabs.Instances.Any(i => i.Id == owner))) Fail("Storey names an unknown instance.");
            }
            using var json = JsonDocument.Parse(MapPrefabJson.Bytes(intent));
            MapPrefabJson.Preflight(json.RootElement);
        }
        if (doc.NativeResourceTransforms is { } poses)
        {
            Unique(poses, p => (p.Kind, p.OwnerId), "standalone resource transform");
            foreach (var p in poses) { Defined(p.Kind); Text(p.OwnerId); Pose(p.Transform); Digest(p.SourceDigest); }
            using var json = JsonDocument.Parse(MapPrefabJson.Bytes(poses));
            MapPrefabJson.Preflight(json.RootElement);
        }
    }

    internal static void RequireAsset(MapPrefabAsset asset) => RequireAsset(asset, null);

    internal static void RequireAsset(MapPrefabAsset asset, MapAssetClosure? assets,
        IReadOnlyList<MapPrefabLocalMaterialization>? suppressed = null)
    {
        if (asset.PayloadVersion != 1) Fail("Unsupported prefab payload version.");
        Text(asset.Id);
        if (asset.Children is null || asset.Children.Count > MapPrefabJson.Limits.MaxChildren) Fail("Prefab child limit exceeded or collection missing.");
        Unique(asset.Children, c => c.Key, "prefab child");
        var children = asset.Children.ToDictionary(c => c.Key);
        var targets = new ChildTargets(children, assets, suppressed ?? asset.MaterializedAttachments);
        foreach (MapPrefabChild child in asset.Children)
        {
            Child(child);
            foreach (MapPrefabReferenceSlot slot in child.References ?? []) if (slot?.Target is { } target) Reference(target);
            var (_, occurrences) = MapPrefabReferenceWalker.Child(child, MapPrefabReferenceMap.Identity);
            MapPrefabReferences.RequireSlots(child.References!, occurrences, targets,
                child is MapPrefabRecordChild ? child.LocalTransform : null, $"Prefab {child.Key.Kind} '{child.Key.Key}'");
            if (child is MapPrefabRecordChild && child.References!.Single(s => s.Path == "/anchor/surfaceId").Target.Scope != MapPrefabReferenceScope.Local)
                Fail($"Prefab record '{child.Key.Key}' must be anchored on a declared local surface.");
            // Annotation references carry their scope directly and take no slots.
            foreach (MapPrefabReference reference in MapPrefabReferences.Annotations(child))
                if (reference.Scope == MapPrefabReferenceScope.Local && !targets.Has(reference.Kind, reference.Id))
                    Fail($"Prefab {child.Key.Kind} '{child.Key.Key}' names undeclared local {reference.Kind} '{reference.Id}'.");
        }
        foreach (MapPrefabReference roof in asset.Roofs)
        {
            RoofReference(roof);
            if (roof.Scope == MapPrefabReferenceScope.Local && !children.ContainsKey(new(roof.Kind, roof.Id))) Fail("Roof inventory names an undeclared placement.");
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
                if (assets.GetResource(id).Kind != MapResourceKind.Material) Fail("Prefab leaf requires Material resource '" + id + "'.");
                RequireDependency(id);
            }
        }
        void RequireDependency(string id)
        {
            if (dependencies is not null && !dependencies.Contains(id, StringComparer.Ordinal)) Fail("Prefab payload resource edge '" + id + "' is not a declared dependency.");
        }
    }

    static void Child(MapPrefabChild child)
    {
        Key(child.Key); Pose(child.LocalTransform);
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
            _ => throw new MapDocumentException("Unsupported prefab child."),
        };
        if (identity.Kind != child.Key.Kind || identity.Id != child.Key.Key) Fail("Prefab child key does not match its payload ID/kind.");
        switch (child)
        {
            case MapPrefabPlacementChild p:
                if (p.Value.NumericId is not null) Fail("Prefab placement numeric IDs belong to instance bindings.");
                if (p.LocalTransform != new MapTransform(Vector3.Zero, 0, 1)) Fail("Placement children must use only their placement transform.");
                Pose(new MapTransform(new(p.Value.X, p.Value.Y ?? 0, p.Value.Z), p.Value.Yaw, p.Value.Scale));
                Text(p.Value.AssetId!); Text(p.Value.Kind);
                if (p.Value.Tags is null || p.Value.Tags.Any(t => t is null)) Fail("Placement tags must not be null.");
                break;
            case MapPrefabSurfaceChild s:
                Unique(s.Patches, p => p.Key, "surface patch");
                foreach (MapSurfacePatch patch in s.Patches)
                {
                    if (patch.Key.SurfaceId != s.Surface.Id || patch.Records.Count != 0) Fail("Surface children must contain only their geometry and no embedded records.");
                    _ = MapSurfacePatchCodec.Encode(patch);
                }
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
    static void RequireBindings(MapPrefabInstance instance, MapPrefabChildSet children, IReadOnlyList<MapPrefabBindingTransfer> transfers)
    {
        var bindings = instance.Bindings.Entries.ToDictionary(b => b.Child);
        var transferred = instance.Removed
            ? transfers.Where(t => t.InstanceId == instance.Id).Select(t => t.Binding.Child).ToHashSet()
            : new HashSet<MapPrefabChildKey>();
        foreach (MapPrefabChildKey key in children.Known.Concat(children.Live.Keys))
            if (!bindings.ContainsKey(key) && !transferred.Contains(key)) Fail("Prefab bindings must completely describe every child.");
        if (instance.Removed) return;
        foreach (MapPrefabBinding binding in instance.Bindings.Entries)
            if (binding.Removed == children.Live.ContainsKey(binding.Child))
                Fail($"Binding for {binding.Child.Kind} '{binding.Child.Key}' must be Removed exactly when its child is absent.");
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
                if (children.ContainsKey(child.Key) || !_implicit.TryAdd(child.Key, child)) Fail("Implicit child collides with an explicit or implicit child.");
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
            case MapPrefabRemoveOverride r when r.Previous is MapPrefabRemoveOverride: Fail("Previous must not be a remove override."); break;
            case MapPrefabRemoveOverride r when r.Previous is not null:
                if (r.Previous.Child != r.Child) Fail("Previous override must retain the same child key.");
                Override(r.Previous); break;
            case MapPrefabReplaceOverride r: Child(r.Value); if (r.Child != r.Value.Key) Fail("Replacement key differs."); break;
            case MapPrefabAddOverride a: Child(a.Value); if (a.Child != a.Value.Key) Fail("Added key differs."); break;
            case MapPrefabTransformOverride t: Pose(t.Transform); break;
            case MapPrefabPlacementOverride p:
                if (p.Child.Kind != MapPrefabChildKind.Placement) Fail("Placement override requires a Placement child.");
                if (p.Transform is { } transform) Pose(transform);
                if (p.AssetId is { } assetId) Text(assetId);
                if (p.Tags is { } tags && tags.Any(t => t is null)) Fail("Override tags must not contain null.");
                break;
        }
    }

    static void Binding(MapPrefabBinding binding)
    {
        Key(binding.Child); Text(binding.WorldId);
        if (binding.NumericId is { } numeric && (numeric <= 0 || binding.Child.Kind != MapPrefabChildKind.Placement)) Fail("Only placement bindings may have positive numeric IDs.");
    }
    internal static void RequireLight(MapLocalLightDoc light) => Light(light);
    static void Light(MapLocalLightDoc l)
    {
        if (l.PayloadVersion != 1) Fail("Unsupported local light payload version.");
        Text(l.Id); Vector(l.Position); Vector(l.Colour);
        if (!float.IsFinite(l.Intensity) || l.Intensity < 0 || !float.IsFinite(l.Radius) || l.Radius <= 0) Fail("Invalid local light intensity/radius.");
        if (l.Owner is { } owner) Reference(owner);
    }
    static void Marker(MapLocalMarkerDoc m)
    {
        if (m.PayloadVersion != 1) Fail("Unsupported local marker payload version.");
        Text(m.Id); Text(m.Name); Text(m.Role); Vector(m.Position);
        if (!float.IsFinite(m.YawRadians) || m.Tags is null || m.Tags.Any(t => t is null)) Fail("Invalid marker yaw/tags.");
    }
    static void Roof(MapRoofLinkDoc roof)
    {
        Text(roof.Id); RoofReference(roof.Roof);
        foreach (var room in roof.Rooms) { Reference(room); if (room.Kind != MapPrefabChildKind.Record) Fail("Roof room requires a Record reference."); }
        foreach (var group in roof.Groups) { Reference(group); if (group.Kind != MapPrefabChildKind.RoofGroup) Fail("Roof group requires a RoofGroup reference."); }
        if (!float.IsFinite(roof.AboveY)) Fail("Roof link height must be finite.");
    }
    static void Group(MapRoofGroupDoc group)
    {
        Text(group.Id); foreach (var room in group.Rooms) { Reference(room); if (room.Kind != MapPrefabChildKind.Record) Fail("Roof group room requires a Record reference."); }
    }
    static void RoofReference(MapPrefabReference roof) { Reference(roof); if (roof.Kind != MapPrefabChildKind.Placement) Fail("Roof inventory requires Placement references."); }
    static void Reference(MapPrefabReference reference) { Defined(reference.Scope); Defined(reference.Kind); Text(reference.Id); }
    static void Key(MapPrefabChildKey key) { Defined(key.Kind); Text(key.Key); }
    static void Defined<T>(T value) where T : struct, Enum { if (!Enum.IsDefined(value)) Fail("Unsupported enum value."); }
    static void Text(string value) { if (string.IsNullOrWhiteSpace(value)) Fail("A nonempty prefab identifier/name is required."); }
    static void Digest(string value) { if (value is not { Length: 64 } || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) Fail("Expected lowercase SHA-256 digest."); }
    static void Vector(Vector3 value) { if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) Fail("Expected finite XYZ."); }
    static void Pose(MapTransform value)
    {
        Vector(value.Position); if (!float.IsFinite(value.YawRadians) || !float.IsFinite(value.Scale) || value.Scale <= 0) Fail("Expected finite yaw and positive uniform scale.");
    }
    static void Pose(MapEffectiveTransform value)
    {
        if (!double.IsFinite(value.X) || !double.IsFinite(value.Y) || !double.IsFinite(value.Z) || !double.IsFinite(value.YawRadians) || !double.IsFinite(value.Scale) || value.Scale <= 0)
            Fail("Expected finite precise pose and positive uniform scale.");
    }
    static void Unique<T, TKey>(IReadOnlyList<T> values, Func<T, TKey> key, string what) where TKey : notnull
    {
        if (values is null) Fail(what + " collection must not be null.");
        var seen = new HashSet<TKey>();
        foreach (T value in values!)
        {
            if (value is null) Fail(what + " must not be null.");
            TKey id = key(value); if (id is string text) Text(text); if (!seen.Add(id)) Fail("Duplicate " + what + " '" + id + "'.");
        }
    }
    [DoesNotReturn]
    static void Fail(string message) => throw new MapDocumentException(message);
}
