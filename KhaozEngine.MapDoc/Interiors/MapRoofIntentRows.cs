using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Immutable visible/hidden rows and reusable per-roof membership flags for both policy adapters.</summary>
internal sealed class MapRoofIntentRows
{
    readonly record struct Link(int Roof, HashSet<string> Rooms, MapExactValue AboveY);
    readonly MapRoofIntent[] _visible, _concealed, _rows;
    readonly Link[] _links;
    readonly bool[] _hidden;
    internal MapRoofIntentRows(MapBuildingIntent intent)
    {
        _visible = intent.Roofs.Select(r => new MapRoofIntent(r.Id, true, true, true, true)).ToArray();
        _concealed = intent.Roofs.Select(r => new MapRoofIntent(r.Id, false, true, false, true)).ToArray();
        _rows = (MapRoofIntent[])_visible.Clone();
        Roofs = Array.AsReadOnly(_rows);
        // One reusable membership flag per prepared roof bounds scratch by the captured inventory.
        _hidden = new bool[_rows.Length];
        var indices = intent.Roofs.Select((roof, index) => (roof.Id, index)).ToDictionary(r => r.Id, r => r.index, StringComparer.Ordinal);
        var groups = intent.RoofGroups.ToDictionary(g => g.Id, g => g.Rooms.Select(r => r.Id).ToArray(), StringComparer.Ordinal);
        _links = intent.RoofLinks.Select(link => new Link(indices[link.Roof.Id],
            link.Rooms.Select(r => r.Id).Concat(link.Groups.SelectMany(g => groups[g.Id])).ToHashSet(StringComparer.Ordinal),
            MapExactValue.FromSingle(link.AboveY))).ToArray();
    }
    internal IReadOnlyList<MapRoofIntent> Roofs { get; }
    internal void Evaluate(MapMembershipObservation membership, MapRoofMode mode)
    {
        Array.Clear(_hidden);
        if (membership.Status == MapMembershipStatus.Resolved && membership.SpaceId is { } room)
            foreach (var link in _links)
                if (membership.ExactPoint.Y.CompareTo(link.AboveY) < 0 && link.Rooms.Contains(room)) _hidden[link.Roof] = true;
        for (int i = 0; i < _rows.Length; i++)
        {
            bool visible = mode != MapRoofMode.AlwaysHidden && (mode == MapRoofMode.AlwaysVisible || !_hidden[i]);
            _rows[i] = visible ? _visible[i] : _concealed[i];
        }
    }
    internal IReadOnlyList<MapRoofIntent> Detach() => Array.AsReadOnly((MapRoofIntent[])_rows.Clone());
}
