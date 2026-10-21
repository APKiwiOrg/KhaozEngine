using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Reusable roof intent over one immutable acquisition. The roof list is a live read-only view.
/// Membership retains the released point-query budgets, status and witness. Detached policy results copy the list.</summary>
public sealed class MapRoofEvaluationBuffer
{
    readonly record struct Link(int Roof, HashSet<string> Rooms, MapExactValue AboveY);

    readonly MapSpaceMembership _membership;
    readonly MapReadWitness _witness;
    readonly MapRoofIntent[] _visible, _concealed, _rows;
    readonly Link[] _links;
    readonly bool[] _hidden;
    MapFramePoint _observer;
    MapMembershipResult? _resolved;

    public MapRoofEvaluationBuffer(MapScopedScene scene)
        : this(Intent(scene), scene.SurfaceView) { }

    internal MapRoofEvaluationBuffer(MapBuildingIntent intent, MapScopedSurfaces view)
    {
        var records = MapSpaceGeometry.Records(view).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        _membership = new(view, () => records);
        _witness = view.ReadWitness;
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

    public MapMembershipStatus Status { get; private set; } = MapMembershipStatus.Outside;
    public string? SpaceId { get; private set; }
    public IReadOnlyList<MapRoofIntent> Roofs { get; }

    public void Evaluate(MapFramePoint observer, MapRoofMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_resolved is null || observer != _observer)
        {
            _resolved = _membership.Query(observer);
            _observer = observer;
        }
        Status = _resolved.Status;
        SpaceId = _resolved.SpaceId;
        Array.Clear(_hidden);
        if (Status == MapMembershipStatus.Resolved && SpaceId is { } room)
            foreach (var link in _links)
                if (_resolved.ExactPoint.Y.CompareTo(link.AboveY) < 0 && link.Rooms.Contains(room))
                    _hidden[link.Roof] = true;
        for (int i = 0; i < _rows.Length; i++)
        {
            bool visible = mode != MapRoofMode.AlwaysHidden && (mode == MapRoofMode.AlwaysVisible || !_hidden[i]);
            _rows[i] = visible ? _visible[i] : _concealed[i];
        }
    }

    internal MapRoofIntentResult Detach() => new(Status, SpaceId,
        Array.AsReadOnly((MapRoofIntent[])_rows.Clone()), _witness);

    static MapBuildingIntent Intent(MapScopedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return scene.BuildingIntent;
    }
}
