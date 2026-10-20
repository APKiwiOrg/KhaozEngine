using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Editing;

public abstract record MapPrefabEdit(string Label);
public sealed record MapPlacePrefab(string InstanceId, string ResourceId, MapTransform Transform,
    Surfaces.MapRecordRef? ParentSpace = null, bool NumericLeaves = true) : MapPrefabEdit("Place prefab")
{
    /// <summary>Keyed overrides the new instance starts with, such as a marker replace override that names a repeated
    /// instance's marker. Request data only. They are detached, planned and validated with the placement, and persist
    /// as the instance's Overrides.</summary>
    public IReadOnlyList<MapPrefabOverride> InitialOverrides { get; init; } = [];
}
public sealed record MapMovePrefab(string InstanceId, MapTransform Transform) : MapPrefabEdit("Move prefab");
public sealed record MapSetPrefabOverride(string InstanceId, MapPrefabOverride Override) : MapPrefabEdit("Set prefab override");
public sealed record MapRestorePrefabChild(string InstanceId, MapPrefabChildKey Child) : MapPrefabEdit("Restore prefab child");
public sealed record MapRemovePrefab(string InstanceId) : MapPrefabEdit("Remove prefab");
public sealed record MapUnpackPrefab(string InstanceId) : MapPrefabEdit("Unpack prefab");
public sealed record MapUpdatePrefabAsset(string ResourceId, string OldSha256, string NewResourceId,
    string NewSha256) : MapPrefabEdit("Update prefab asset");
public sealed record MapPrefabEditResult(MapDocument After, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects)
{
    /// <summary>The finalization this result carries, null before finalization or for documents without foliage. Never
    /// serialized.</summary>
    internal MapDocumentFinalizationStamp? Stamp { get; init; }

    /// <summary>Expansions the preparer already built for its before and after documents. Never serialized.</summary>
    internal MapEditExpansions? Expansions { get; init; }
}

/// <summary>Detached document preparation. Accepting the candidate and publishing its reservations belongs to the caller.
/// Every result is finalized by <see cref="MapDocumentEditFinalizer"/>, so it carries its dependent foliage rows.</summary>
public static class MapPrefabEdits
{
    public static MapPrefabEditResult Prepare(MapDocument document, MapAssetClosure assets, MapPrefabEdit edit) =>
        MapDocumentEditFinalizer.Complete(document, PrepareRaw(document, assets, edit), assets, assets);

    /// <summary>The validated candidate before finalization, for callers that compose it with more changes and
    /// finalize the whole candidate once.</summary>
    internal static MapPrefabEditResult PrepareRaw(MapDocument document, MapAssetClosure assets, MapPrefabEdit edit,
        Action<MapDocument>? admitProposal = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(edit);
        MapDocumentEditFinalizer.Preflight(document, assets);
        var before = MapPrefabEditPreparation.Begin(document, assets);
        var after = MapPrefabEditPreparation.Copy(document);
        var set = after.Prefabs ?? new(1, 1, [], []);
        string id = edit switch
        {
            MapPlacePrefab p => p.InstanceId,
            MapMovePrefab p => p.InstanceId,
            MapSetPrefabOverride p => p.InstanceId,
            MapRestorePrefabChild p => p.InstanceId,
            MapRemovePrefab p => p.InstanceId,
            MapUnpackPrefab p => p.InstanceId,
            _ => throw new MapDocumentException("Asset updates require PrepareAssetUpdate and both captured closures."),
        };
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        MapPrefabInstance? instance = set.Instances.SingleOrDefault(i => i.Id == id);
        var oldBindings = instance?.Bindings.Entries ?? [];
        if (edit is MapPlacePrefab place)
        {
            if (instance is not null) throw new MapDocumentException($"Prefab instance '{id}' is already reserved.");
            var initial = InitialOverrides(place);
            var resource = assets.GetResource(place.ResourceId);
            if (resource.Kind != MapResourceKind.Prefab) throw new MapDocumentException("Placement requires a Prefab resource.");
            // The instance starts with its overrides, so bindings, names and every other rule see them before publication.
            instance = new(id, place.ResourceId, resource.Reference.Sha256, place.Transform, place.ParentSpace, new([]), initial);
            Set(after, instance);
            Plan(after, assets, instance, before, place.NumericLeaves);
        }
        else
        {
            if (instance is null || instance.Removed) throw new MapDocumentException($"No live prefab instance '{id}'.");
            switch (edit)
            {
                case MapMovePrefab move: Set(after, instance with { Transform = move.Transform }); break;
                case MapSetPrefabOverride change:
                    ArgumentNullException.ThrowIfNull(change.Override);
                    MapPrefabOverride? previous = instance.Overrides.SingleOrDefault(o => o.Child == change.Override.Child);
                    MapPrefabOverride value = change.Override;
                    if (value is MapPrefabRemoveOverride removed)
                    {
                        if (previous is MapPrefabRemoveOverride) throw new MapDocumentException("Child is already removed.");
                        value = removed with { Previous = removed.Previous ?? previous };
                    }
                    Set(after, instance with { Overrides = instance.Overrides.Where(o => o.Child != value.Child).Append(value).ToArray() });
                    Plan(after, assets, after.Prefabs!.Instances.Single(i => i.Id == id), before);
                    break;
                case MapRestorePrefabChild restore:
                    var dormant = instance.Overrides.SingleOrDefault(o => o.Child == restore.Child) as MapPrefabRemoveOverride
                        ?? throw new MapDocumentException("Restore requires a removed child override.");
                    Set(after, instance with
                    {
                        Overrides = instance.Overrides.Where(o => o.Child != restore.Child)
                            .Concat(dormant.Previous is { } kept ? new[] { kept } : []).ToArray(),
                    });
                    Plan(after, assets, after.Prefabs!.Instances.Single(i => i.Id == id), before);
                    break;
                case MapRemovePrefab:
                    Set(after, instance with { Removed = true, Bindings = new(instance.Bindings.Entries.Select(b => b with { Removed = true }).ToArray()) });
                    break;
                case MapUnpackPrefab: MapPrefabEditPreparation.Unpack(after, assets, instance, before); break;
                default: throw new MapDocumentException("Unknown prefab edit.");
            }
        }
        var bindings = oldBindings.Concat(after.Prefabs!.Instances.Single(i => i.Id == id).Bindings.Entries)
            .Concat(after.Prefabs.Transfers.Where(t => t.InstanceId == id).Select(t => t.Binding)).ToArray();
        admitProposal?.Invoke(after);
        return MapPrefabEditPreparation.Finish(after, assets, before, new[] { id }, bindings,
            unpack: edit is MapUnpackPrefab);
    }

    /// <summary>Reads old and new definitions only from the supplied matching closures and prepares one complete switch.</summary>
    public static MapPrefabEditResult PrepareAssetUpdate(MapDocument document, MapAssetClosure beforeAssets,
        MapAssetClosure afterAssets, MapUpdatePrefabAsset edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(beforeAssets);
        ArgumentNullException.ThrowIfNull(afterAssets);
        ArgumentNullException.ThrowIfNull(edit);
        MapDocumentEditFinalizer.Preflight(document, beforeAssets);
        var before = MapPrefabEditPreparation.Begin(document, beforeAssets);
        var oldResource = beforeAssets.GetResource(edit.ResourceId);
        var nextResource = afterAssets.GetResource(edit.NewResourceId);
        if (oldResource.Kind != MapResourceKind.Prefab || nextResource.Kind != MapResourceKind.Prefab ||
            oldResource.Reference.Sha256 != edit.OldSha256 || nextResource.Reference.Sha256 != edit.NewSha256)
            throw new MapDocumentException("Asset update does not match its old and new verified prefab pins.");
        // Capture both definitions before making the candidate. A removed source remains a tombstone, not an instance update.
        _ = beforeAssets.BorrowPrefab(edit.ResourceId);
        _ = afterAssets.BorrowPrefab(edit.NewResourceId);
        var after = MapPrefabEditPreparation.Copy(document);
        after.NativeAssets = afterAssets.Roots.ToList();
        var instances = (after.Prefabs?.Instances ?? []).Where(i => !i.Removed && i.ResourceId == edit.ResourceId).ToArray();
        var bindings = new List<MapPrefabBinding>();
        foreach (var instance in instances)
        {
            if (instance.ResourceSha256 != edit.OldSha256) throw new MapDocumentException("A referring instance has a different old pin.");
            bindings.AddRange(instance.Bindings.Entries);
            var updated = instance with { ResourceId = edit.NewResourceId, ResourceSha256 = edit.NewSha256 };
            Set(after, updated);
            Plan(after, afterAssets, updated, before);
            bindings.AddRange(after.Prefabs!.Instances.Single(i => i.Id == updated.Id).Bindings.Entries);
        }
        return MapDocumentEditFinalizer.Complete(document, MapPrefabEditPreparation.Finish(after, afterAssets, before,
            instances.Select(i => i.Id).ToArray(), bindings, nativeAssets: true), beforeAssets, afterAssets);
    }

    // Duplicate keys refuse before any binding or numeric ID is allocated. The closed override codec detaches the request.
    static IReadOnlyList<MapPrefabOverride> InitialOverrides(MapPlacePrefab place)
    {
        var values = place.InitialOverrides ?? throw new MapDocumentException("Initial overrides must not be null.");
        if (values.Count == 0) return [];
        var keys = new HashSet<MapPrefabChildKey>();
        foreach (MapPrefabOverride value in values)
        {
            if (value is null) throw new MapDocumentException("Initial overrides must not contain null.");
            if (!keys.Add(value.Child)) throw new MapDocumentException($"Duplicate initial override for {value.Child.Kind} '{value.Child.Key}'.");
        }
        return MapPrefabJson.ReadOverrides(Encoding.UTF8.GetString(MapPrefabJson.Bytes(values.ToArray())));
    }

    internal static void Set(MapDocument document, MapPrefabInstance instance)
    {
        var set = document.Prefabs ?? new(1, 1, [], []);
        document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion,
            set.Instances.Where(i => i.Id != instance.Id).Append(instance).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray(), set.Transfers)
        { Attachments = set.Attachments };
    }

    static void Plan(MapDocument document, MapAssetClosure assets, MapPrefabInstance instance, MapEffectiveResources before, bool numeric = true)
    {
        _ = MapPrefabPreflight.Count(document, assets, new());
        var asset = assets.BorrowPrefab(instance.ResourceId);
        _ = MapPrefabMaterializationOverrides.Reconcile(asset, instance.Overrides);
        var children = MapPrefabChildSet.Compute(asset, assets, instance.Overrides);
        if (children.NeedsParent)
        {
            var parent = instance.ParentSpace ?? throw new MapDocumentException($"Prefab instance '{instance.Id}' requires ParentSpace.");
            if (!before.Surfaces.TryGetRecord(parent, out var record) || record is not MapSpaceDoc)
                throw new MapDocumentException("ParentSpace must name an authored space at its declared anchor.");
        }
        var plan = MapPrefabBindingPlanner.Plan(document, asset, assets, instance.Id, instance.Bindings, numeric);
        Set(document, instance with { Bindings = plan.Bindings });
        document.NumericIdHighWaterMark = plan.HighWaterMark;
    }
}
