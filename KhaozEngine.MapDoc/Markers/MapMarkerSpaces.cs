using System;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>Occupied space by point membership at a marker's resolved exact position.</summary>
public static class MapMarkerSpaces
{
    /// <summary>The space and parent chain holding <paramref name="marker"/> in <paramref name="view"/>. A point inside no
    /// authored space is Outside with no parents. Missing geometry, ambiguity, capacity and nonrepresentability keep
    /// their own statuses and are never reported as Outside.</summary>
    public static MapMembershipResult Query(MapScopedSurfaces view, MapEffectiveMarker marker)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(marker);
        return new MapSpaceMembership(view).QueryExact(marker.Position);
    }
}
