using System;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>The authoritative visitor for typed authored-placement references.</summary>
public static class NativePlacementReferences
{
    /// <summary>Remaps typed references only, including foliage mask and owner correspondence owners. Format 4 has none.
    /// Tags, resource IDs, asset IDs and Kind are not placement references.</summary>
    public static void Remap(MapDocument document, string oldId, string newId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newId);
        var remapped = MapPrefabReferences.RemapWorld(document, MapPrefabChildKind.Placement, oldId, newId);
        document.Prefabs = remapped.Prefabs;
        document.BuildingIntent = remapped.BuildingIntent;
        document.NativeResourceTransforms = remapped.NativeResourceTransforms;
        // Foliage owners are typed World references, remapped by the same authoritative walker.
        document.Foliage = remapped.Foliage;
        if (document.Prefabs is { } set)
            document.Prefabs = new(set.PayloadVersion, set.ExpansionVersion,
                set.Instances.Select(i => !i.Unpacked ? i : i with
                { Bindings = new(i.Bindings.Entries.Select(b => b.Child.Kind == MapPrefabChildKind.Placement && b.WorldId == oldId ? b with { WorldId = newId } : b).ToArray()) }).ToArray(), set.Transfers)
            { Attachments = set.Attachments };
        MapPrefabAttachmentDescriptors.Refresh(document);
    }
}
