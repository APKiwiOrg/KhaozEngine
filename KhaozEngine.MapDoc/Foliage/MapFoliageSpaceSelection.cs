using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>A Space binding's support decision at one candidate.</summary>
internal readonly record struct MapFoliageSpaceOutcome(MapFoliageStatus Status, MapFoliageSupportGrid? Grid, MapFoliageSupportCell? Cell,
    long CellX, long CellZ, string? OwnerId, string? SpaceId);

/// <summary>Decides a Space binding per candidate point from prepared data, with no support query. It follows the released
/// selection kernel's Space rule over the chunk's prepared faces: every support floor's cell at the exact point and the
/// lowest-keyed face holding it, that face's exact height against ReferenceY minus SearchBelow and plus SearchAbove and
/// an upward normal, room eligibility from the exact prism footprint and its interval, footprint eligibility from the
/// footprint cells and the spaces reachable at the point, then the highest eligible face, with equal-height independent
/// owners ambiguous. Offset lattices therefore resolve per point and never by floor order. No eligible support inside the
/// bound space refuses as MissingSupport, outside its domain there is no sample.</summary>
internal sealed class MapFoliageSpaceSelection
{
    readonly MapScopedSurfaces _view;
    readonly MapPreparedSupport _support;
    readonly IReadOnlyList<MapFoliageSupportGrid> _grids;
    readonly string _owner;
    readonly bool _room;
    readonly MapExactValue _low, _high;
    readonly Room[] _rooms;

    sealed record Room(MapWaterDomain Domain, MapExactValue Lower, MapExactValue Upper, string SpaceId, string Owner, int Depth, MapSpaceDoc Parent);

    MapFoliageSpaceSelection(MapScopedSurfaces view, MapPreparedSupport support, IReadOnlyList<MapFoliageSupportGrid> grids,
        MapSpaceDoc space, MapSupportBinding binding)
    {
        _view = view;
        _support = support;
        _grids = grids;
        _owner = MapSpaceGeometry.Owner(view, space);
        _room = space.Kind == MapSpaceKind.Room;
        MapExactValue y = MapExactValue.FromSingle(binding.ReferenceY!.Value);
        _low = y.Subtract(MapExactValue.FromSingle(binding.SearchBelow!.Value));
        _high = y.Add(MapExactValue.FromSingle(binding.SearchAbove!.Value));
        _rooms = support.Records.OfType<MapPrismDomain>().Select(prism =>
        {
            MapSpaceDoc room = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Space);
            (MapExactValue lower, MapExactValue upper) = MapPrismGeometry.Heights(view, prism);
            return new Room(MapPrismGeometry.Compile(view, prism), lower, upper, room.AliasOf is { } alias ? alias.Id : room.Id,
                MapSpaceGeometry.Owner(view, room), MapSpaceGeometry.Ancestors(view, room).Count,
                MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Parent));
        }).ToArray();
    }

    /// <summary>The selection for <paramref name="binding"/>, or null when its space is not in this chunk's acquisition, so
    /// the chunk holds none of its domain.</summary>
    internal static MapFoliageSpaceSelection? Create(MapScopedSurfaces view, MapPreparedSupport support,
        IReadOnlyList<MapFoliageSupportGrid> grids, MapSupportBinding binding) =>
        support.Records.OfType<MapSpaceDoc>().FirstOrDefault(s => s.Id == binding.SpaceId) is { } space
            ? new(view, support, grids, space, binding) : null;

    internal MapFoliageSpaceOutcome Select(MapExactXz point)
    {
        var intersections = new List<(MapFoliageSupportGrid Grid, MapFoliageSupportCell Cell, long X, long Z, MapCompiledFace Face, MapExactValue Height)>();
        foreach (MapFoliageSupportGrid grid in _grids)
            foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(_view, grid.Surface.Id, point))
            {
                if (!grid.Cells.TryGetValue((x, z), out MapFoliageSupportCell? cell) || cell.Fallback) continue;
                if (cell.Faces.Support(cell.SlotCell, point) is not { } hit) continue;
                if (intersections.Count >= _view.Scope.Limits.MaxSupportIntersections) return Refused(MapFoliageStatus.CapacityExceeded);
                intersections.Add((grid, cell, x, z, hit.Face, hit.Height));
            }
        MapSpaceFootprint[] footprints = _support.Footprints.Where(f => MapSpaceGeometry.FootprintCell(_view, f, point) is not null).ToArray();
        Dictionary<string, MapRecordRef?> reachable = MapSupportSelection.Reachable(_view, _support.Membership, _owner, footprints,
            _support.Records, point);
        var eligible = new List<(MapSupportSelection.Candidate Candidate, int Index)>();
        for (int i = 0; i < intersections.Count; i++)
        {
            var (_, _, _, _, face, height) = intersections[i];
            bool band = height.CompareTo(_low) >= 0 && height.CompareTo(_high) <= 0 && face.Normal.Y > 0;
            if (_room && OwnerPart(point, height))
            {
                if (Occupant(point, height)?.Owner == _owner && band) eligible.Add((new(face, height, _owner, null), i));
                continue;
            }
            foreach (MapSpaceFootprint footprint in footprints)
            {
                if (footprint.Lower.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1) ||
                    footprint.Lower.SurfaceId != face.Key.OwnerId) continue;
                string owner = SpaceOwner(footprint);
                if (!reachable.TryGetValue(owner, out MapRecordRef? via) || !band) continue;
                eligible.Add((new(face, height, owner, via), i));
                break;
            }
        }
        eligible.Sort((a, b) =>
        {
            int order = b.Candidate.Height.CompareTo(a.Candidate.Height);
            return order != 0 ? order : a.Candidate.Face.Key.CompareTo(b.Candidate.Face.Key);
        });
        if (eligible.Count != 0)
        {
            var highest = eligible.TakeWhile(c => c.Candidate.Height == eligible[0].Candidate.Height).ToArray();
            if (!MapSupportSelection.OneSeamOwner(_view, highest.Select(c => c.Candidate).ToArray(), _support.Seams, point))
                return Refused(MapFoliageStatus.Ambiguous);
            var selected = intersections[highest[0].Index];
            return new(MapFoliageStatus.Complete, selected.Grid, selected.Cell, selected.X, selected.Z, selected.Face.Key.OwnerId,
                highest[0].Candidate.SpaceId);
        }
        if (LegacyFallback(point, footprints) is { } fallback) return fallback;
        bool inside = _room ? _rooms.Any(r => r.Owner == _owner && r.Domain.Contains(point) && Covered(r, point))
            : footprints.Any(f => SpaceOwner(f) == _owner);
        return inside ? Refused(MapFoliageStatus.MissingSupport) : new(MapFoliageStatus.Complete, null, null, 0, 0, null, null);
    }

    // A logical fallback cell of a legacy exterior lower bound of the bound space supports at its legacy height.
    MapFoliageSpaceOutcome? LegacyFallback(MapExactXz point, MapSpaceFootprint[] footprints)
    {
        foreach (MapSpaceFootprint footprint in footprints.Where(f => f.Lower.Kind == MapBoundKind.LegacyExteriorV1 && SpaceOwner(f) == _owner))
            foreach (MapFoliageSupportGrid grid in _grids.Where(g => g.Surface.Id == footprint.Lower.SurfaceId))
                foreach (var (x, z) in MapEffectiveGeometry.CellCandidates(_view, grid.Surface.Id, point))
                {
                    if (!grid.Cells.TryGetValue((x, z), out MapFoliageSupportCell? cell) || !cell.Fallback) continue;
                    MapExactValue height = MapEffectiveGeometry.LegacyHeight(_view, cell.Faces.Validated, cell.SlotCell, point);
                    if (height.CompareTo(_low) < 0 || height.CompareTo(_high) > 0) continue;
                    string? owner = MapFoliageSupportIdentity.From(new MapSupportResult(MapSupportStatus.LegacyFallback, null, height.ToSingle(), null,
                        new MapLegacyCellTag(cell.Key, cell.SlotCell, MapFoliageValidation.LegacyTilePolicy), _owner, null, null, _view.ReadWitness));
                    return new(MapFoliageStatus.Complete, grid, cell, x, z, owner, _owner);
                }
        return null;
    }

    // The bound room's own occupied part holds the height: its prism footprint holds the point, the height lies in its
    // interval and its parent covers the point.
    bool OwnerPart(MapExactXz point, MapExactValue height) => _rooms.Any(r => r.Owner == _owner && Holds(r, point, height));

    // The innermost room holding the point at the height, as membership resolves nested prisms.
    Room? Occupant(MapExactXz point, MapExactValue height)
    {
        Room? best = null;
        foreach (Room room in _rooms)
            if (Holds(room, point, height) && (best is null || room.Depth > best.Depth)) best = room;
        return best;
    }

    bool Holds(Room room, MapExactXz point, MapExactValue height) => height.CompareTo(room.Lower) >= 0 && height.CompareTo(room.Upper) < 0 &&
        room.Domain.Contains(point) && Covered(room, point);

    // A room occupies only where its parent does: inside a parent room's prism, or on a footprint of its parent space.
    bool Covered(Room room, MapExactXz point)
    {
        string parent = MapSpaceGeometry.Owner(_view, room.Parent);
        if (room.Parent.Kind == MapSpaceKind.Room)
            return _rooms.Any(r => r.Owner == parent && r.Domain.Contains(point) && Covered(r, point));
        return _support.Footprints.Any(f => SpaceOwner(f) == parent && MapSpaceGeometry.FootprintCell(_view, f, point) is not null);
    }

    string SpaceOwner(MapSpaceFootprint footprint) => MapSpaceGeometry.Owner(_view, MapSpaceGeometry.Record<MapSpaceDoc>(_view, footprint.Space));

    static MapFoliageSpaceOutcome Refused(MapFoliageStatus status) => new(status, null, null, 0, 0, null, null);
}
