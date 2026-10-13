using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;

namespace KhaozEngine.MapEdit;

public sealed record MapPrefabAssetInfo(string ResourceId, string Sha256, int PayloadVersion, int Children);
public sealed record MapPrefabPublishResult(MapAssetRef Resource, MapPrefabMutationResult? Mutation);

/// <summary>The shared author-time prefab workflow. Local edits remain detached until an immutable save is accepted.</summary>
public sealed class PrefabService(MapEditSession session)
{
    readonly object _lock = new();
    MapPrefabLocalSession? _local;
    MapAssetClosure? _published;
    internal MapPrefabLocalSession? LocalSession => _local;
    public IReadOnlyList<MapPrefabAssetInfo> List() => session.WithNativeAssets(c =>
        (IReadOnlyList<MapPrefabAssetInfo>)c.Assets.CapturedResources.Where(r => r.Kind == MapResourceKind.Prefab)
            .Select(r => { var a = c.Assets.GetPrefab(r.Reference.Id); return new MapPrefabAssetInfo(r.Reference.Id, r.Reference.Sha256, a.PayloadVersion, a.Children.Count); })
            .OrderBy(a => a.ResourceId, StringComparer.Ordinal).ToArray());
    public MapPrefabAsset Open(string resourceId, MapRecordRef? parentSpace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        lock (_lock)
        {
            return session.WithNativeAssets(c =>
            {
                var assets = c.Assets.TryGetResource(resourceId, out _) ? c.Assets : _published?.TryGetResource(resourceId, out _) == true ? _published : c.Assets;
                var asset = assets.GetPrefab(resourceId);
                bool needsParent = MapPrefabChildSet.Compute(asset, assets, []).NeedsParent;
                if (needsParent && parentSpace is null)
                    parentSpace = (c.Document.Prefabs?.Instances ?? []).Where(i => i.ResourceId == resourceId && i.ParentSpace is not null)
                        .OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.ParentSpace).FirstOrDefault();
                if (needsParent && parentSpace is null) throw new MapDocumentException("This prefab requires an explicit authored parent space.");
                var local = new MapPrefabLocalSession(asset, assets, c.Document, parentSpace);
                _local?.Cancel();
                _local = local;
                return local.Export();
            });
        }
    }
    public MapPrefabAsset Edit(MapPrefabChild child, bool remove = false)
    {
        lock (_lock) { RequireLocal().Edit(child, remove); return RequireLocal().Export(); }
    }
    internal MapPrefabAsset Export() { lock (_lock) return RequireLocal().Export(); }
    public void Cancel() { lock (_lock) { _local?.Cancel(); _local = null; } }
    public MapPrefabPublishResult Save(string resourceId, string relativeDirectory, bool updateOpenMap = true)
    {
        lock (_lock)
        {
            var publisher = new PrefabAssetPublisher(session);
            var result = publisher.Publish(RequireLocal().Export(), resourceId, relativeDirectory, updateOpenMap);
            _published = publisher.PublishedAssets;
            return result;
        }
    }
    public MapPrefabAsset Extract(IReadOnlyList<MapPrefabReference> selected, MapTransform origin, string assetId)
    {
        lock (_lock)
        {
            return session.WithNativeAssets(c =>
            {
                var asset = MapPrefabExtraction.Extract(c.Document, c.Assets, selected, origin, assetId);
                var local = new MapPrefabLocalSession(asset, c.Assets, c.Document);
                _local?.Cancel(); _local = local;
                return local.Export();
            });
        }
    }
    MapPrefabLocalSession RequireLocal() => _local ?? throw new MapDocumentException("Open or extract a prefab asset before editing or saving.");
}
