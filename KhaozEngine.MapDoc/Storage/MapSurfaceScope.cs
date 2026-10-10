using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>A frame-local request. Identity includes exact float bits and all provider budgets.</summary>
public sealed record MapSurfaceScope(WorldFrame Frame, Vector2 LocalMin, Vector2 LocalMax, float? MinY, float? MaxY,
    IReadOnlyList<MapSurfaceRole> Roles, IReadOnlyList<string>? SpaceIds, MapQueryLimits Limits)
{
    public Vector2 LocalMin { get; init; } = ValidateInitial(LocalMin, LocalMax, MinY, MaxY, Roles, SpaceIds, Limits);
    public IReadOnlyList<MapSurfaceRole> Roles { get; init; } = Array.AsReadOnly(Roles.ToArray());
    public IReadOnlyList<string>? SpaceIds { get; init; } = SpaceIds is null ? null : Array.AsReadOnly(SpaceIds.ToArray());

    static Vector2 ValidateInitial(Vector2 min, Vector2 max, float? minY, float? maxY,
        IReadOnlyList<MapSurfaceRole> roles, IReadOnlyList<string>? spaces, MapQueryLimits limits)
    {
        ValidateValues(min, max, minY, maxY, roles, spaces, limits);
        return min;
    }
    internal void Validate() => ValidateValues(LocalMin, LocalMax, MinY, MaxY, Roles, SpaceIds, Limits);
    static void ValidateValues(Vector2 min, Vector2 max, float? minY, float? maxY,
        IReadOnlyList<MapSurfaceRole> roles, IReadOnlyList<string>? spaces, MapQueryLimits limits)
    {
        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(max.X) || !float.IsFinite(max.Y) ||
            min.X > max.X || min.Y > max.Y ||
            (minY is { } low && !float.IsFinite(low)) || (maxY is { } high && !float.IsFinite(high)) || minY > maxY ||
            roles is null || roles.Any(r => !Enum.IsDefined(r)) || spaces?.Any(string.IsNullOrWhiteSpace) == true || limits is null ||
            limits.MaxCandidatePatches < 0 || limits.MaxPageReads < 0 || limits.MaxInspectedFaces < 0 ||
            limits.MaxSupportIntersections < 0 || limits.MaxRecordReads < 0 || limits.MaxRecordDepth < 0)
            throw new ArgumentException("invalid surface scope bounds or limits");
    }
    public string Digest
    {
        get
        {
            Validate();
            return MapCanonical.HashHex(w =>
            {
                w.WriteStartArray(); w.WriteStringValue("kemap/scope-request/1");
                w.WriteNumberValue(Frame.X); w.WriteNumberValue(Frame.Z);
                foreach (float value in new[] { LocalMin.X, LocalMin.Y, LocalMax.X, LocalMax.Y })
                    w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                foreach (float? value in new[] { MinY, MaxY })
                    if (value is { } y) w.WriteNumberValue(BitConverter.SingleToInt32Bits(y)); else w.WriteNullValue();
                w.WriteStartArray(); foreach (var role in Roles.Distinct().OrderBy(r => r)) w.WriteNumberValue((byte)role); w.WriteEndArray();
                if (SpaceIds is null) w.WriteNullValue();
                else { w.WriteStartArray(); foreach (string id in SpaceIds.Distinct().OrderBy(s => s, StringComparer.Ordinal)) w.WriteStringValue(id); w.WriteEndArray(); }
                foreach (int limit in new[] { Limits.MaxCandidatePatches, Limits.MaxInspectedFaces, Limits.MaxSupportIntersections,
                    Limits.MaxPageReads, Limits.MaxRecordReads, Limits.MaxRecordDepth }) w.WriteNumberValue(limit);
                w.WriteEndArray();
            });
        }
    }
    internal MapExactRect WorldRectangle() => new(
        MapExactValue.FromSingle(LocalMin.X).Add(new((long)Frame.X * (long)WorldFrame.Grid, 1)),
        MapExactValue.FromSingle(LocalMin.Y).Add(new((long)Frame.Z * (long)WorldFrame.Grid, 1)),
        MapExactValue.FromSingle(LocalMax.X).Add(new((long)Frame.X * (long)WorldFrame.Grid, 1)),
        MapExactValue.FromSingle(LocalMax.Y).Add(new((long)Frame.Z * (long)WorldFrame.Grid, 1)));
}

internal readonly record struct MapExactRect(MapExactValue MinX, MapExactValue MinZ, MapExactValue MaxX, MapExactValue MaxZ)
{
    internal bool Touches(MapExactRect b) => MinX.CompareTo(b.MaxX) <= 0 && MaxX.CompareTo(b.MinX) >= 0 &&
        MinZ.CompareTo(b.MaxZ) <= 0 && MaxZ.CompareTo(b.MinZ) >= 0;
}

/// <summary>Exact rectangle conversion shared by window reads, queries and reverse-knowledge checks.</summary>
internal static class MapSurfaceRanges
{
    internal static MapCellRect Cells(MapExactRect world, MapLatticeFrame frame)
    {
        MapExactValue unit = frame.CellUnitMetres.Exact();
        MapExactValue minZ = world.MinZ, maxZ = world.MaxZ;
        if (frame.RowDirection == MapRowDirection.NegativeZ) (minZ, maxZ) = (maxZ.Negate(), minZ.Negate());
        try
        {
            return new(checked(world.MinX.Divide(unit).Floor() - 1), checked(minZ.Divide(unit).Floor() - 1),
                checked(world.MaxX.Divide(unit).Ceiling() + 1), checked(maxZ.Divide(unit).Ceiling() + 1));
        }
        catch (OverflowException) { throw new MapExactOverflowException(); }
    }
    internal static MapSlotRect Slots(MapCellRect cells) => new(Floor(cells.MinX, 64), Floor(cells.MinZ, 64),
        checked(Floor(checked(cells.MaxXExclusive - 1), 64) + 1), checked(Floor(checked(cells.MaxZExclusive - 1), 64) + 1));
    internal static long Floor(long value, int divisor) => value / divisor - (value % divisor < 0 ? 1 : 0);
    internal static MapCellRect Rectangle(MapSurfacePatch patch)
    {
        try
        {
            long x = checked(patch.Key.SlotX * 64 + patch.CellMinX), z = checked(patch.Key.SlotZ * 64 + patch.CellMinZ);
            return new(x, z, checked(x + patch.Width), checked(z + patch.Depth));
        }
        catch (OverflowException) { throw new MapExactOverflowException(); }
    }
    internal static MapExactRect World(MapCellRect cells, MapLatticeFrame frame)
    {
        MapExactXz a = frame.WorldXz(MapLatticeAddress.Corner(cells.MinX, cells.MinZ));
        MapExactXz b = frame.WorldXz(MapLatticeAddress.Corner(cells.MaxXExclusive, cells.MaxZExclusive));
        return new(a.X, a.Z.CompareTo(b.Z) <= 0 ? a.Z : b.Z, b.X, a.Z.CompareTo(b.Z) <= 0 ? b.Z : a.Z);
    }
}
