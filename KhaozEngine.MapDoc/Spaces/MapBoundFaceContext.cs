using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>One immutable, view-bound compilation of the union of demanded slot cells.</summary>
internal sealed class MapBoundFaceContext
{
    readonly IReadOnlyDictionary<MapPatchKey, Entry> _entries;
    readonly IReadOnlyDictionary<MapRecordRef, IReadOnlyList<MapBoundFace>> _openings;
    internal MapScopedSurfaces View { get; }

    MapBoundFaceContext(MapScopedSurfaces view, Dictionary<MapPatchKey, Entry> entries,
        Dictionary<MapRecordRef, IReadOnlyList<MapBoundFace>> openings)
    {
        View = view;
        _entries = new ReadOnlyDictionary<MapPatchKey, Entry>(entries);
        _openings = new ReadOnlyDictionary<MapRecordRef, IReadOnlyList<MapBoundFace>>(openings);
    }

    sealed record Entry(MapSlotCellMask Mask, MapCompiledPatch Compiled,
        IReadOnlyDictionary<int, IReadOnlyList<MapBoundFace>> Cells);
    sealed class Pending
    {
        internal readonly ulong[] Words = new ulong[64];
        internal long PhysicalFaces;
        internal MapSurfaceRef Surface { get; }
        internal MapSurfacePatch Patch { get; }
        internal Pending(MapSurfaceRef surface, MapSurfacePatch patch)
        {
            Surface = surface;
            Patch = patch;
        }
        internal bool Contains(int slot) => (Words[slot / 64] & (1UL << (slot % 64))) != 0;
        internal void Add(int slot) => Words[slot / 64] |= 1UL << (slot % 64);
        internal IEnumerable<int> Cells()
        {
            for (int word = 0; word < Words.Length; word++)
                for (int bit = 0; bit < 64; bit++)
                    if ((Words[word] & (1UL << bit)) != 0) yield return word * 64 + bit;
        }
    }

    sealed record PendingOpening(MapHorizontalOpening Record, SortedSet<int> Cells);

    internal static MapBoundFaceContext? Prepare(MapScopedSurfaces view, IEnumerable<MapCellDemand> demands,
        int maxPatches, long maxFaces, MapBoundFaceWork? work, out string? refusal) =>
        PrepareBounds(view, demands.Select(d => new MapBoundFaceDemand(d, null)), maxPatches, maxFaces, work, out refusal);

    internal static MapBoundFaceContext? PrepareBounds(MapScopedSurfaces view, IEnumerable<MapBoundFaceDemand> demands,
        int maxPatches, long maxFaces, MapBoundFaceWork? work, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(demands);
        if (maxPatches < 0 || maxFaces < 0) throw new ArgumentOutOfRangeException(nameof(maxPatches));
        if (work is not null) work.ContextsPrepared++;
        refusal = null;
        var pending = new SortedDictionary<MapPatchKey, Pending>();
        var pendingOpenings = new Dictionary<MapRecordRef, PendingOpening>();
        long count = 0;
        foreach (MapBoundFaceDemand boundDemand in demands)
        {
            // Drain phase 1 after refusal. Do not allocate masks or count any further context work.
            if (refusal is not null) continue;
            MapCellDemand demand = boundDemand.Cell;
            if (demand.SlotCell is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(demands));
            if (!pending.TryGetValue(demand.Patch, out Pending? entry))
            {
                if (pending.Count >= maxPatches) { refusal = "context patches"; continue; }
                MapSurfaceRef? surface = view.Surfaces.FirstOrDefault(s => s.Id == demand.Patch.SurfaceId);
                if (surface is null || !view.TryAcquiredPatch(demand.Patch, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
                    throw new MapDocumentException($"context demand patch {demand.Patch}: unavailable");
                MapSurfaceCompiler.Validate(surface, patch);
                entry = new(surface, patch);
                pending.Add(demand.Patch, entry);
            }
            bool freshCell = !entry.Contains(demand.SlotCell);
            long faces = freshCell ? MapSurfaceCompiler.CountFaces(entry.Surface, entry.Patch, demand.SlotCell) : 0;
            entry.PhysicalFaces = checked(entry.PhysicalFaces + faces);
            PendingOpening? requested = null;
            MapHorizontalOpening? opening = null;
            bool freshOpeningCell = false;
            if (boundDemand.Opening is { } reference)
            {
                pendingOpenings.TryGetValue(reference, out requested);
                if (requested is not null) opening = requested.Record;
                else if (view.TryRecord(reference, out MapTopologyRecord? record, out _, borrow: true) &&
                    record is MapHorizontalOpening found) opening = found;
                if (opening is null || opening.Patch != demand.Patch || !opening.SlotCells.Contains(demand.SlotCell))
                    throw new MapDocumentException("context demand opening is unavailable or does not own the cell");
                freshOpeningCell = requested is null || !requested.Cells.Contains(demand.SlotCell);
                if (freshOpeningCell) faces = checked(faces + OpeningFaceCount(entry.Patch, demand.SlotCell));
            }
            if (!freshCell && !freshOpeningCell) continue;
            try { count = checked(count + faces); }
            catch (OverflowException) { refusal = "context faces"; continue; }
            if (work is not null) work.ContextFacesCounted = count;
            if (count > maxFaces || entry.PhysicalFaces > MapSurfaceCompiler.MaxFacesPerPatch)
            { refusal = "context faces"; continue; }
            entry.Add(demand.SlotCell);
            if (freshOpeningCell)
            {
                // Each stored opening cell has charged plane faces. No unrelated owner is compiled.
                if (requested is null)
                {
                    requested = new(opening!, new());
                    pendingOpenings.Add(boundDemand.Opening!, requested);
                }
                requested.Cells.Add(demand.SlotCell);
            }
        }
        if (refusal is not null) return null;
        var entries = new Dictionary<MapPatchKey, Entry>();
        var openings = new Dictionary<MapRecordRef, IReadOnlyList<MapBoundFace>>();
        foreach (var (key, pendingEntry) in pending)
        {
            MapSlotCellMask mask = MapSlotCellMask.Of(pendingEntry.Cells());
            MapCompiledPatch compiled;
            try { compiled = MapSurfaceCompiler.Compile(pendingEntry.Surface, pendingEntry.Patch, mask, MapSurfaceCompiler.MaxFacesPerPatch); }
            catch (MapDocumentException error) when (MapCommonRefinement.NotRepresentable(error)) { throw new MapExactOverflowException(); }
            if (work is not null) { work.Compiles++; work.CompiledFaces += compiled.Faces.Count; }
            var cells = compiled.Faces.GroupBy(f => f.Key.Primitive).ToDictionary(g => g.Key,
                g => (IReadOnlyList<MapBoundFace>)Array.AsReadOnly(g.OrderBy(f => f.Key)
                    .Select(f => new MapBoundFace(f.Key, compiled.ExactTriangle(f))).ToArray()));
            entries.Add(key, new(mask, compiled, new ReadOnlyDictionary<int, IReadOnlyList<MapBoundFace>>(cells)));
        }
        foreach (var (reference, requested) in pendingOpenings.OrderBy(p => p.Key.Anchor).ThenBy(p => p.Key.Id, StringComparer.Ordinal))
        {
            Pending entry = pending[requested.Record.Patch];
            MapOpeningPlane plane;
            try
            {
                plane = MapOpeningBoundary.Compile(entry.Surface, entry.Patch,
                requested.Record with { SlotCells = Array.AsReadOnly(requested.Cells.ToArray()) });
            }
            catch (MapDocumentException error) when (MapCommonRefinement.NotRepresentable(error)) { throw new MapExactOverflowException(); }
            IReadOnlyList<MapBoundFace> faces = Array.AsReadOnly(plane.Keys.Select((face, i) => new MapBoundFace(face, plane.ExactTriangles[i])).ToArray());
            openings.Add(reference, faces);
            if (work is not null) work.CompiledFaces += faces.Count;
        }
        return new(view, entries, openings);
    }

    internal void RequireView(MapScopedSurfaces view)
    {
        if (!ReferenceEquals(view, View)) throw new ArgumentException("context view does not match", nameof(view));
    }

    internal IReadOnlyList<MapBoundFace> Faces(MapCellDemand cell)
    {
        if (!_entries.TryGetValue(cell.Patch, out Entry? entry) || !entry.Mask.Contains(cell.SlotCell))
            throw new InvalidOperationException("context demand was not prepared");
        return entry.Cells.TryGetValue(cell.SlotCell, out IReadOnlyList<MapBoundFace>? faces) ? faces : Array.Empty<MapBoundFace>();
    }
    internal MapCompiledPatch Compiled(MapPatchKey key) => _entries.TryGetValue(key, out Entry? entry)
        ? entry.Compiled : throw new InvalidOperationException("context demand patch was not prepared");
    internal IReadOnlyList<MapBoundFace> Opening(MapRecordRef reference) => _openings.TryGetValue(reference, out IReadOnlyList<MapBoundFace>? faces)
        ? faces : throw new InvalidOperationException("context demand opening was not prepared");

    internal static int OpeningFaceCount(MapSurfacePatch patch, int slot)
    {
        int x = slot % 64 - patch.CellMinX, z = slot / 64 - patch.CellMinZ;
        if (slot is < 0 or >= 4096 || x < 0 || x >= patch.Width || z < 0 || z >= patch.Depth || patch.IsPresent(x, z))
            throw new MapDocumentException("opening must name absent cells in its patch");
        Span<MapLatticeTriangle> triangles = stackalloc MapLatticeTriangle[4];
        return MapSurfaceCompiler.Describe(patch, x, z, triangles);
    }
}
