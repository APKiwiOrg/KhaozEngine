using System;
using System.Linq;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Owned intent rows and reference containers, including the nested arrays copied by capture.</summary>
internal static class MapBuildingIntentPreparation
{
    internal static long Bytes(MapBuildingIntent intent) => checked(128L +
        IntPtr.Size * (long)(intent.RoofLinks.Count + intent.RoofGroups.Count + intent.Roofs.Count +
            intent.Storeys.Count + intent.Lights.Count + intent.Markers.Count) +
        intent.RoofLinks.Sum(l => 96L + (IntPtr.Size + 32L) * (l.Rooms.Count + l.Groups.Count)) +
        intent.RoofGroups.Sum(g => 32L + (IntPtr.Size + 32L) * g.Rooms.Count) +
        32L * (intent.Roofs.Count + intent.Storeys.Count) +
        intent.Lights.Sum(l => 128L + (l.Owner is null ? 0 : 32) + (l.ShadowExclusion is null ? 0 : 64)) +
        intent.Markers.Sum(m => 128L + IntPtr.Size * (long)m.Tags.Count + (m.SupportBinding is null ? 0 : 64)));

    internal static MapBuildingIntent Clone(MapBuildingIntent intent)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        long bytes = Bytes(intent);
        // Sorting and final arrays coexist. Strings and other immutable scalar values remain shared.
        MapSceneAdmission.Current?.ReserveBytes(checked(2 * bytes));
        MapPrefabReference[] References(System.Collections.Generic.IReadOnlyList<MapPrefabReference> values) =>
            values.OrderBy(r => r.Scope.ToString(), StringComparer.Ordinal).ThenBy(r => r.Kind.ToString(), StringComparer.Ordinal)
                .ThenBy(r => r.Id, StringComparer.Ordinal).Select(r => r with { }).ToArray();
        var result = new MapBuildingIntent(intent.PayloadVersion,
            intent.RoofLinks.OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => l with
            { Roof = l.Roof with { }, Rooms = References(l.Rooms), Groups = References(l.Groups) }).ToArray(),
            intent.RoofGroups.OrderBy(g => g.Id, StringComparer.Ordinal).Select(g => g with { Rooms = References(g.Rooms) }).ToArray(),
            intent.Lights.OrderBy(l => l.Id, StringComparer.Ordinal).Select(l => l with
            { Owner = l.Owner is null ? null : l.Owner with { }, ShadowExclusion = l.ShadowExclusion is null ? null : l.ShadowExclusion with { } }).ToArray(),
            intent.Markers.OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m with
            { Tags = m.Tags.ToArray(), SupportBinding = m.SupportBinding is null ? null : m.SupportBinding with { } }).ToArray())
        {
            Roofs = References(intent.Roofs),
            Storeys = intent.Storeys.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => s with { }).ToArray(),
        };
        preparation?.Complete(bytes);
        return result;
    }
}
