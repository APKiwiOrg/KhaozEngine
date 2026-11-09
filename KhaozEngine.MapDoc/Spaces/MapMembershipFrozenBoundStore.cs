using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Private semantic bound rows. Geometry sharing never shares a view's statuses or failure details.</summary>
internal sealed class MapMembershipFrozenBoundStore
{
    internal readonly record struct Row(int Cell, int Faces, int Status, int Opening, int Compatibility, int Detail, int Flags, int ResultCell);
    internal readonly MapMembershipPreparedGeometry Geometry;
    internal readonly MapMembershipArenaPages<Row> Rows;
    readonly MapMembershipArenaPages<MapRecordRef> _openings;
    readonly MapMembershipArenaPages<MapLegacyCellTag> _compatibility;
    readonly MapMembershipArenaPages<string> _details;
    readonly MapMembershipArenaPages<MapCellDemand> _cells;
    long _ownedDetailBytes;
    bool _frozen;
    MapMembershipFrozenBoundStore(MapMembershipPreparedGeometry geometry)
    {
        Geometry = geometry;
        Rows = MapMembershipArenaPages<Row>.Create(128);
        _openings = MapMembershipArenaPages<MapRecordRef>.Create(64);
        _compatibility = MapMembershipArenaPages<MapLegacyCellTag>.Create(64);
        _details = MapMembershipArenaPages<string>.Create(64);
        _cells = MapMembershipArenaPages<MapCellDemand>.Create(64);
    }
    internal static MapMembershipFrozenBoundStore Create(MapMembershipPreparedGeometry geometry)
    { MapMembershipBuild.Inventory(80); return new(geometry); }
    internal long InventoryBytes => checked(80 + Rows.InventoryBytes + _openings.InventoryBytes +
        _compatibility.InventoryBytes + _details.InventoryBytes + _cells.InventoryBytes + _ownedDetailBytes);
    internal MapMembershipBoundPlan Add(MapSpacePointBounds.Bound result, MapMembershipPreparedGeometry.Cell? cell,
        int faces, bool validatesPatch = false)
    {
        int opening = result.Opening is { } o ? _openings.Append(o) : -1;
        int compatibility = result.Compatibility is { } c ? _compatibility.Append(c) : -1;
        int detail = -1;
        if (result.Detail is { } text)
        {
            if (!ReferenceEquals(string.IsInterned(text), text))
            {
                long bytes = checked((22L + 2L * text.Length + 7) & ~7L);
                MapMembershipBuild.Inventory(bytes); _ownedDetailBytes += bytes;
            }
            detail = _details.Append(text);
        }
        int original = result.Cell is { } demand ? cell is { } target && target.Demand == demand ? -2 : _cells.Append(demand) : -1;
        int index = Rows.Append(new(cell?.Id ?? -1, faces, (int)result.Status, opening, compatibility, detail,
            validatesPatch ? 1 : 0, original));
        return new(this, index);
    }
    internal MapRecordRef? Opening(int index) => index < 0 ? null : _openings[index];
    internal MapLegacyCellTag? Compatibility(int index) => index < 0 ? null : _compatibility[index];
    internal string? Detail(int index) => index < 0 ? null : _details[index];
    internal MapCellDemand? ResultCell(Row row) => row.ResultCell == -2 ? Geometry.Cells[row.Cell].Demand
        : row.ResultCell < 0 ? null : _cells[row.ResultCell];
    internal void Freeze()
    {
        if (_frozen) return; _frozen = true;
        Rows.Freeze(); _openings.Freeze(); _compatibility.Freeze(); _details.Freeze(); _cells.Freeze();
        if (MapMembershipBuild.Current is { } build)
        {
            build.Counts.FrozenBoundRows += Rows.Count;
            build.Counts.FrozenBoundCapacityBytes += InventoryBytes;
            build.Counts.SideTableRows += _openings.Count + _compatibility.Count + _details.Count + _cells.Count;
            build.Counts.SideTableBytes += _openings.InventoryBytes + _compatibility.InventoryBytes +
                _details.InventoryBytes + _cells.InventoryBytes + _ownedDetailBytes;
        }
    }
}

internal readonly record struct MapMembershipBoundResult(MapMembershipFrozenBoundStore Store, int Index)
{
    MapMembershipFrozenBoundStore.Row Row => Store.Rows[Index];
    internal MapMembershipStatus Status => (MapMembershipStatus)Row.Status;
    internal MapRecordRef? Opening => Store.Opening(Row.Opening);
    internal MapLegacyCellTag? Compatibility => Store.Compatibility(Row.Compatibility);
    internal string? Detail => Store.Detail(Row.Detail);
    internal MapCellDemand? Cell => Store.ResultCell(Row);
}

internal readonly record struct MapMembershipBoundPlan(MapMembershipFrozenBoundStore Store, int Index)
{
    MapMembershipFrozenBoundStore.Row Row => Store.Rows[Index];
    internal MapMembershipBoundResult Result => new(Store, Index);
    internal MapMembershipPreparedGeometry.Cell? Cell => Row.Cell < 0 ? null : Store.Geometry.Cells[Row.Cell];
    internal int Faces => Row.Faces;
    internal bool ValidatesPatch => (Row.Flags & 1) != 0;
    internal MapExactValue? Height(MapExactXz point, MapMembershipPreparedGeometry geometry, MapMembershipExact exact)
    {
        if (Cell is not { } cell) return null;
        return Result.Compatibility is not null ? cell.LegacyHeight(point, exact)
            : MapMembershipPreparedGeometry.Height(geometry.FaceSets[Faces], point, exact);
    }
}
