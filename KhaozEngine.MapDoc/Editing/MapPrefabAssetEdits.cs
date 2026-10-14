using System;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>Pure immutable asset child edits. Captured rows are reconciled from the original pins before new bytes exist.</summary>
public static class MapPrefabAssetEdits
{
    public static MapPrefabAsset Replace(MapPrefabAsset asset, MapPrefabChild child, bool remove = false)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(child);
        MapPrefabValidation.RequireAsset(asset);
        bool exists = asset.Children.Any(c => c.Key == child.Key);
        if (remove && !exists) throw new MapDocumentException("Cannot remove an undeclared prefab child.");
        MapPrefabOverride edit = remove ? new MapPrefabRemoveOverride(child.Key) : exists
            ? new MapPrefabReplaceOverride(child.Key, child) : new MapPrefabAddOverride(child.Key, child);
        var reconciled = MapPrefabMaterializationOverrides.Reconcile(asset, new[] { edit });
        // Keeping an empty row is the persisted suppression of the advertised edge.
        var result = reconciled.LiveAsset;
        return MapPrefabFile.Read(MapPrefabFile.Write(result), asset.Id);
    }
}
