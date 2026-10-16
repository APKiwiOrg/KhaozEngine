using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>The one marker diff shared by every native transaction. A changed point, yaw, enabled state, binding or
/// height provenance is spatial and reports both point boxes with Residency. A create or delete reports its one box.
/// Other changes are metadata and report only the ID. Every name change, including a create or delete, is a rename
/// row. Markers never add Physics, Nav or Unbounded.</summary>
public static class MapMarkerEffects
{
    public static MapNativeEditEffects Describe(IReadOnlyList<MapEffectiveMarker> before, IReadOnlyList<MapEffectiveMarker> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var old = ById(before);
        var current = ById(after);
        var ids = new List<string>();
        var names = new List<MapMarkerNameChange>();
        MapBox3? oldBox = null, newBox = null;
        foreach (string id in old.Keys.Union(current.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            old.TryGetValue(id, out MapEffectiveMarker? a);
            current.TryGetValue(id, out MapEffectiveMarker? b);
            bool spatial = a is null || b is null || a.Position != b.Position || a.YawRadians != b.YawRadians ||
                a.Enabled != b.Enabled || !Equals(a.HeightProvenance, b.HeightProvenance);
            if (!spatial && !Metadata(a!, b!)) continue;
            ids.Add(id);
            if (!string.Equals(a?.Name, b?.Name, StringComparison.Ordinal)) names.Add(new(id, a?.Name, b?.Name));
            if (!spatial) continue;
            if (a is not null) oldBox = Union(oldBox, MapEffectiveAnnotations.Bounds(a));
            if (b is not null) newBox = Union(newBox, MapEffectiveAnnotations.Bounds(b));
        }
        var invalidates = oldBox is null && newBox is null ? MapNativeInvalidation.None : MapNativeInvalidation.Residency;
        return new(oldBox, newBox, [], [], [], [], invalidates)
        {
            MarkerIds = Array.AsReadOnly(ids.ToArray()),
            MarkerNameChanges = Array.AsReadOnly(names.ToArray()),
        };
    }

    static bool Metadata(MapEffectiveMarker a, MapEffectiveMarker b) => a.PayloadVersion != b.PayloadVersion ||
        !string.Equals(a.Name, b.Name, StringComparison.Ordinal) || !string.Equals(a.Role, b.Role, StringComparison.Ordinal) ||
        !a.Tags.SequenceEqual(b.Tags, StringComparer.Ordinal) || !string.Equals(a.ArchetypeId, b.ArchetypeId, StringComparison.Ordinal) ||
        !string.Equals(a.InstanceId, b.InstanceId, StringComparison.Ordinal);

    static Dictionary<string, MapEffectiveMarker> ById(IReadOnlyList<MapEffectiveMarker> markers)
    {
        var result = new Dictionary<string, MapEffectiveMarker>(StringComparer.Ordinal);
        foreach (MapEffectiveMarker marker in markers)
            if (!result.TryAdd(marker.Id, marker)) throw new MapDocumentException($"Effective marker ID '{marker.Id}' is not unique.");
        return result;
    }

    static MapBox3 Union(MapBox3? a, MapBox3 b) => a is { } box ? box.Union(b) : b;
}
