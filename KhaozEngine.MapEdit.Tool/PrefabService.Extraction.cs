using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEdit;

public sealed partial class PrefabService
{
    static (MapRecordRef? Parent, IReadOnlySet<MapPrefabChildKey> Excluded) ExtractionContext(MapDocument document,
        MapAssetClosure assets, IReadOnlyList<MapPrefabReference> selected)
    {
        var keys = selected.Select(r => new MapPrefabChildKey(r.Kind, r.Id)).ToHashSet();
        var sources = (document.Prefabs?.Instances ?? []).Where(i => i.Bindings.Entries.Any(b => keys.Contains(new(b.Child.Kind, b.WorldId)))).ToArray();
        var rooms = MapPrefabResolver.Expand(document, assets).Surfaces.AllRecords().OfType<MapSpaceDoc>()
            .Where(r => r.Kind == MapSpaceKind.Room && keys.Contains(new(MapPrefabChildKind.Record, r.Id)));
        var parents = rooms.Select(r => r.Parent).Concat(sources.Select(i => i.ParentSpace)).OfType<MapRecordRef>()
            .Where(r => !keys.Contains(new(MapPrefabChildKind.Record, r.Id))).Distinct().ToArray();
        if (parents.Length > 1) throw new MapDocumentException("The extraction selection has ambiguous authored parent spaces.");
        return (parents.SingleOrDefault(), sources.SelectMany(i => i.Bindings.Entries)
            .Select(b => new MapPrefabChildKey(b.Child.Kind, b.WorldId)).Concat(keys).ToHashSet());
    }
}
