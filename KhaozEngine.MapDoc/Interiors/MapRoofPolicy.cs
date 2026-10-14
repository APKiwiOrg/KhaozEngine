using System;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Evaluates explicit concealment links against acquired room membership. Groups expand only their
/// attached link, without portal traversal, ancestor matching or changes to statics.</summary>
public static class MapRoofPolicy
{
    public static MapRoofIntentResult Evaluate(MapEffectiveResources resources, MapScopedSurfaces view,
        MapFramePoint observer, MapRoofMode mode)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(view);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        resources.RequireView(view);
        var membership = new MapSpaceMembership(view).Query(observer);
        var intent = resources.BuildingIntent;
        var groups = intent.RoofGroups.ToDictionary(g => g.Id, g => g.Rooms.Select(r => r.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var hidden = intent.RoofLinks.Where(link => membership.Status == MapMembershipStatus.Resolved &&
                membership.SpaceId is { } room && membership.ExactPoint.Y.CompareTo(MapExactValue.FromSingle(link.AboveY)) < 0 &&
                (link.Rooms.Any(r => r.Id == room) || link.Groups.Any(g => groups[g.Id].Contains(room))))
            .Select(link => link.Roof.Id).ToHashSet(StringComparer.Ordinal);
        var roofs = intent.Roofs.Select(roof =>
        {
            bool visible = mode != MapRoofMode.AlwaysHidden && (mode == MapRoofMode.AlwaysVisible || !hidden.Contains(roof.Id));
            return new MapRoofIntent(roof.Id, visible, true, visible, true);
        }).ToArray();
        return new(membership.Status, membership.SpaceId, Array.AsReadOnly(roofs), membership.Witness);
    }
}
