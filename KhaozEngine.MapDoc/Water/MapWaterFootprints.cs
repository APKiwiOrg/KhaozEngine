using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>One maximal row-run rectangle of a space's footprint cells that share a lattice frame and both bounds.
/// Cell bounds are lattice cells with exclusive maxima, and <see cref="Min"/> and <see cref="Max"/> are its exact world
/// corners. <see cref="Footprint"/> is the record that owns the rectangle's first cell, one representative of the
/// merged group for bound face reads.</summary>
internal sealed record MapWaterFootprintRect(string SpaceId, MapSpaceFootprint Footprint, MapLatticeFrame Frame,
    long CellMinX, long CellMinZ, long CellMaxX, long CellMaxZ, MapExactXz Min, MapExactXz Max, MapBoundRef Lower, MapBoundRef Upper);

/// <summary>Reads a space's acquired footprints near a window and merges their cells into rectangles.</summary>
internal static class MapWaterFootprints
{
    /// <summary>The footprint rectangles of <paramref name="spaceId"/>, including its aliases' footprints, read only from
    /// footprints whose 64-cell slot meets the closed window. Cells group by (lattice frame, lower bound, upper bound)
    /// and each group merges across slots by the greedy row-run rule: rows in ascending Z, each row's maximal X runs
    /// extend the rectangle opened on the row before when its X span is identical, else open a new one.
    /// <paramref name="complete"/> is false when the view cannot prove it holds every footprint the window could
    /// meet, or when a footprint whose slot meets the window has an unavailable space record or lattice surface.
    /// Proving the window held needs a <see cref="MapAcquireStatus.Complete"/> acquisition with a complete witness,
    /// every surface role, no height filter, no space filter that excludes <paramref name="spaceId"/>, and a scope
    /// rectangle that holds the window, so one unloaded patch anywhere in the scope leaves every window incomplete.
    /// Compile views must be acquired to match. A footprint whose slot misses the window is never resolved, so its
    /// space record is neither read nor able to clear <paramref name="complete"/>.</summary>
    internal static IReadOnlyList<MapWaterFootprintRect> Of(MapScopedSurfaces view, string spaceId, MapExactXz windowMin,
        MapExactXz windowMax, out bool complete, out int cellsInspected, out IReadOnlyList<MapPatchKey> patchesRead) =>
        Of(view, spaceId, windowMin, windowMax, out complete, out cellsInspected, out patchesRead, null);

    /// <summary><see cref="Of(MapScopedSurfaces, string, MapExactXz, MapExactXz, out bool, out int, out IReadOnlyList{MapPatchKey})"/>
    /// charging each matching footprint's cells to <paramref name="meter"/> before they are merged.</summary>
    internal static IReadOnlyList<MapWaterFootprintRect> Of(MapScopedSurfaces view, string spaceId, MapExactXz windowMin,
        MapExactXz windowMax, out bool complete, out int cellsInspected, out IReadOnlyList<MapPatchKey> patchesRead,
        MapWaterWorkMeter? meter)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(spaceId);
        complete = HoldsWindow(view, spaceId, windowMin, windowMax);
        cellsInspected = 0;
        var read = new SortedSet<MapPatchKey>();
        var owners = new Dictionary<MapRecordRef, string?>();
        var groups = new List<Group>();
        foreach (MapSpaceFootprint footprint in MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>()
            .OrderBy(f => f.Id, StringComparer.Ordinal))
        {
            // A footprint whose lattice surface is unknown cannot be placed, so it may meet the window.
            MapSurfaceRef? lattice = view.Surfaces.FirstOrDefault(s => s.Id == footprint.Lattice.SurfaceId);
            if (lattice is null)
            {
                complete = false;
                continue;
            }
            if (!SlotMeets(lattice.Frame, footprint.Lattice, windowMin, windowMax)) continue;
            string? owner = Owner(footprint.Space);
            if (owner is null)
            {
                complete = false;
                continue;
            }
            if (owner != spaceId) continue;
            meter?.ChargeFootprintCells(footprint.SlotCells.Count);
            cellsInspected = checked(cellsInspected + footprint.SlotCells.Count);
            read.Add(footprint.Lattice);
            Group? group = groups.FirstOrDefault(g => g.Frame == lattice.Frame && g.Lower == footprint.Lower && g.Upper == footprint.Upper);
            if (group is null) groups.Add(group = new(lattice.Frame, footprint.Lower, footprint.Upper));
            foreach (int cell in footprint.SlotCells)
            {
                long x = checked(footprint.Lattice.SlotX * MapPatchKey.SlotCells + cell % MapPatchKey.SlotCells);
                long z = checked(footprint.Lattice.SlotZ * MapPatchKey.SlotCells + cell / MapPatchKey.SlotCells);
                group.Cells.TryAdd((z, x), footprint);
            }
        }
        patchesRead = Array.AsReadOnly(read.ToArray());
        return Array.AsReadOnly(groups.SelectMany(g => Merge(spaceId, g)).ToArray());

        string? Owner(MapRecordRef space)
        {
            if (space.Id == spaceId) return spaceId;
            if (owners.TryGetValue(space, out string? known)) return known;
            string? owner;
            try { owner = MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, space)); }
            catch (MapDocumentException) { owner = null; }
            owners.Add(space, owner);
            return owner;
        }
    }

    /// <summary>The cells of one (frame, lower, upper) group in row order, each with the first footprint that owns it.</summary>
    sealed class Group(MapLatticeFrame frame, MapBoundRef lower, MapBoundRef upper)
    {
        internal MapLatticeFrame Frame { get; } = frame;
        internal MapBoundRef Lower { get; } = lower;
        internal MapBoundRef Upper { get; } = upper;
        internal SortedDictionary<(long Z, long X), MapSpaceFootprint> Cells { get; } = new();
    }

    static IEnumerable<MapWaterFootprintRect> Merge(string spaceId, Group group)
    {
        var rects = new List<(long MinX, long MinZ, long MaxX, long MaxZ, MapSpaceFootprint Footprint)>();
        var open = new List<int>();
        var next = new List<int>();
        long row = long.MinValue;
        var cells = group.Cells.ToArray();
        for (int i = 0; i < cells.Length;)
        {
            long z = cells[i].Key.Z;
            // Only rectangles whose last row is the one directly before this row can extend.
            if (row == long.MinValue || z != checked(row + 1)) open.Clear();
            next.Clear();
            while (i < cells.Length && cells[i].Key.Z == z)
            {
                long start = cells[i].Key.X;
                MapSpaceFootprint first = cells[i].Value;
                long end = start;
                for (i++; i < cells.Length && cells[i].Key.Z == z && cells[i].Key.X == checked(end + 1); i++) end++;
                long maxX = checked(end + 1);
                int match = open.FindIndex(r => rects[r].MinX == start && rects[r].MaxX == maxX);
                if (match >= 0)
                {
                    int index = open[match];
                    rects[index] = rects[index] with { MaxZ = checked(z + 1) };
                    open.RemoveAt(match);
                    next.Add(index);
                }
                else
                {
                    rects.Add((start, z, maxX, checked(z + 1), first));
                    next.Add(rects.Count - 1);
                }
            }
            (open, next) = (next, open);
            row = z;
        }
        foreach (var r in rects)
        {
            var (min, max) = World(group.Frame, r.MinX, r.MinZ, r.MaxX, r.MaxZ);
            yield return new(spaceId, r.Footprint, group.Frame, r.MinX, r.MinZ, r.MaxX, r.MaxZ, min, max, group.Lower, group.Upper);
        }
    }

    /// <summary>True when the acquisition is complete with no height, role or space filter that could hide a
    /// footprint of <paramref name="spaceId"/>, and its scope holds the window.</summary>
    static bool HoldsWindow(MapScopedSurfaces view, string spaceId, MapExactXz windowMin, MapExactXz windowMax) =>
        HoldsEverything(view) && (view.Scope.SpaceIds is not { } ids || ids.Contains(spaceId)) && ScopeHolds(view, windowMin, windowMax);

    /// <summary>True when the view proves that no patch exists at <paramref name="key"/>: the acquisition holds every
    /// patch of its scope with no filter, and either the key's surface is listed and its slot lies inside the scope,
    /// or the surface is unlisted in a view that lists every declared surface. A scoped acquisition lists only the
    /// surfaces it captured, so there an unlisted surface proves nothing.</summary>
    internal static bool ProvesAbsent(MapScopedSurfaces view, MapPatchKey key)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!HoldsEverything(view) || view.Scope.SpaceIds is not null) return false;
        if (view.Surfaces.FirstOrDefault(s => s.Id == key.SurfaceId) is not { } surface) return view.ListsEverySurface;
        long x = checked(key.SlotX * MapPatchKey.SlotCells), z = checked(key.SlotZ * MapPatchKey.SlotCells);
        var (min, max) = World(surface.Frame, x, z, checked(x + MapPatchKey.SlotCells), checked(z + MapPatchKey.SlotCells));
        return ScopeHolds(view, min, max);
    }

    /// <summary>True when the acquisition is complete with a complete witness, every surface role and no height filter.</summary>
    static bool HoldsEverything(MapScopedSurfaces view)
    {
        MapSurfaceScope scope = view.Scope;
        return view.Status == MapAcquireStatus.Complete && view.Witness.Complete && scope.MinY is null && scope.MaxY is null &&
            Enum.GetValues<MapSurfaceRole>().All(scope.Roles.Contains);
    }

    static bool ScopeHolds(MapScopedSurfaces view, MapExactXz windowMin, MapExactXz windowMax)
    {
        MapSurfaceScope scope = view.Scope;
        MapExactXz min = new MapFramePoint(scope.Frame, new Vector3(scope.LocalMin.X, 0, scope.LocalMin.Y)).ExactWorldXz();
        MapExactXz max = new MapFramePoint(scope.Frame, new Vector3(scope.LocalMax.X, 0, scope.LocalMax.Y)).ExactWorldXz();
        return min.X.CompareTo(windowMin.X) <= 0 && min.Z.CompareTo(windowMin.Z) <= 0 &&
            windowMax.X.CompareTo(max.X) <= 0 && windowMax.Z.CompareTo(max.Z) <= 0;
    }

    static bool SlotMeets(MapLatticeFrame frame, MapPatchKey slot, MapExactXz windowMin, MapExactXz windowMax)
    {
        long x = checked(slot.SlotX * MapPatchKey.SlotCells), z = checked(slot.SlotZ * MapPatchKey.SlotCells);
        var (min, max) = World(frame, x, z, checked(x + MapPatchKey.SlotCells), checked(z + MapPatchKey.SlotCells));
        return min.X.CompareTo(windowMax.X) <= 0 && windowMin.X.CompareTo(max.X) <= 0 &&
            min.Z.CompareTo(windowMax.Z) <= 0 && windowMin.Z.CompareTo(max.Z) <= 0;
    }

    /// <summary>The exact world corners of a lattice cell range, ordered by world coordinate so a negative-Z frame
    /// still yields its world minimum first.</summary>
    static (MapExactXz Min, MapExactXz Max) World(MapLatticeFrame frame, long minX, long minZ, long maxX, long maxZ)
    {
        MapExactXz a = frame.WorldXz(MapLatticeAddress.Corner(minX, minZ));
        MapExactXz b = frame.WorldXz(MapLatticeAddress.Corner(maxX, maxZ));
        return (new(MapSpaceGeometry.Min(a.X, b.X), MapSpaceGeometry.Min(a.Z, b.Z)),
            new(MapSpaceGeometry.Max(a.X, b.X), MapSpaceGeometry.Max(a.Z, b.Z)));
    }
}
