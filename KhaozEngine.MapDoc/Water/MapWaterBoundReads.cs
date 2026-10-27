using KhaozEngine.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>The faces of one bound over a world rectangle. <see cref="Status"/> is
/// <see cref="MapRefinementStatus.Complete"/> when every chunk with faces was read,
/// <see cref="MapRefinementStatus.MissingGeometry"/> when some geometry was unavailable, which leaves its area without
/// faces while the faces that were read remain, with <see cref="Gap"/> naming the first such reason, and a read's own
/// <c>CapacityExceeded</c>, <c>NotRepresentable</c> or <c>Invalid</c> otherwise. <see cref="Frame"/> is the lattice the
/// faces were read on, null when the bound's surface is unavailable.</summary>
internal sealed record MapWaterBoundRead(MapRefinementStatus Status, string? Detail, MapLatticeFrame? Frame,
    IReadOnlyList<MapBoundFace> Faces, IReadOnlyList<MapLegacyCellTag> Compatibility, IReadOnlyList<MapPatchKey> PatchesRead,
    string? SurfaceId, string? Gap);

/// <summary>Reads one bound over a world rectangle in chunks that each stay within one <see cref="MapRefinementLimits"/>
/// read. Chunks are cut in the bound surface's own lattice, one 64-cell slot at a time so each read touches one patch,
/// and sized by a preflight over the released per-cell face counts of surface cells and opening cells, so a call never
/// passes the per-bound source face or cell limit. A single cell whose faces pass the limit refuses as
/// <c>CapacityExceeded</c> before any read.</summary>
internal static class MapWaterBoundReads
{
    internal static void ChargeTraversal(MapScopedSurfaces view, string surfaceId, MapExactXz min, MapExactXz max, MapWaterWorkMeter meter)
    {
        var range = MapEffectiveGeometry.CellRange(view, surfaceId, min, max);
        if (range.MaxXExclusive <= range.MinX || range.MaxZExclusive <= range.MinZ) return;
        var slots = MapSurfaceRanges.Slots(range);
        meter.ChargeBoundTraversal(((BigInteger)slots.MaxXExclusive - slots.MinX) * ((BigInteger)slots.MaxZExclusive - slots.MinZ),
            ((BigInteger)range.MaxXExclusive - range.MinX) * ((BigInteger)range.MaxZExclusive - range.MinZ));
    }
    internal static MapWaterBoundRead Read(MapScopedSurfaces view, MapSpaceFootprint footprint, MapBoundRef bound,
        MapExactXz min, MapExactXz max, MapRefinementLimits limits, MapWaterWorkMeter meter, bool upper)
    {
        var admission = view.Scope.SceneAdmission ?? MapSceneAdmission.Current;
        using var admitted = admission?.Enter();
        using var preparation = admission?.BeginPreparation();
        if (bound.Kind == MapBoundKind.OpenTop) throw new ArgumentException("an open top has no faces", nameof(bound));
        MapHorizontalOpening? opening = null;
        if (bound.Kind == MapBoundKind.HorizontalOpening)
        {
            if (bound.Opening is not { } reference ||
                !view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus status, borrow: true) ||
                record is not MapHorizontalOpening found)
                return Unread(null, $"opening '{bound.Opening?.Id}' is unavailable");
            opening = found;
        }
        string? surfaceId = opening?.Patch.SurfaceId ?? bound.SurfaceId ??
            (bound.Kind == MapBoundKind.LegacyExteriorV1 ? footprint.Lattice.SurfaceId : null);
        MapSurfaceRef? surface = view.Surfaces.FirstOrDefault(s => s.Id == surfaceId);
        if (surface is null) return Unread(surfaceId, $"surface '{surfaceId}' is unavailable");

        preparation?.Reserve(128);
        var faces = new List<MapBoundFace>();
        var compatibility = new List<MapLegacyCellTag>();
        var read = new List<MapPatchKey>();
        string? gap = null;
        MapCellRect range = MapLatticeRanges.CellRange(surface.Frame, min, max);
        if (range.MaxXExclusive > range.MinX && range.MaxZExclusive > range.MinZ)
        {
            BigInteger slots = ((BigInteger)SlotOf(range.MaxXExclusive - 1) - SlotOf(range.MinX) + 1) *
                ((BigInteger)SlotOf(range.MaxZExclusive - 1) - SlotOf(range.MinZ) + 1);
            BigInteger cells = ((BigInteger)range.MaxXExclusive - range.MinX) * ((BigInteger)range.MaxZExclusive - range.MinZ);
            meter.ChargeBoundTraversal(slots, cells);
        }
        int limit = limits.MaxSourceFacesPerBound;
        for (long sz = SlotOf(range.MinZ); sz <= SlotOf(range.MaxZExclusive - 1) && range.MaxZExclusive > range.MinZ; sz++)
            for (long sx = SlotOf(range.MinX); sx <= SlotOf(range.MaxXExclusive - 1) && range.MaxXExclusive > range.MinX; sx++)
            {
                var key = new MapPatchKey(surface.Id, sx, sz);
                if (opening is not null && key != opening.Patch) continue;
                if (!view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status))
                {
                    gap ??= $"patch {key}: {status}";
                    continue;
                }
                if (status == MapPatchStatus.KnownEmpty) continue;
                if (patch is null)
                {
                    gap ??= $"patch {key}: {status} without payload";
                    continue;
                }
                Grow(read, 1, preparation);
                read.Add(key);
                long x0 = Math.Max(range.MinX, sx * MapPatchKey.SlotCells), x1 = Math.Min(range.MaxXExclusive, (sx + 1) * MapPatchKey.SlotCells);
                long z0 = Math.Max(range.MinZ, sz * MapPatchKey.SlotCells), z1 = Math.Min(range.MaxZExclusive, (sz + 1) * MapPatchKey.SlotCells);
                foreach (Chunk chunk in Chunks(x0, z0, x1, z1, limit, (x, z) => Faces(surface, patch, opening, x, z)))
                {
                    // BoundFaces copies value faces out of its local and transformed compiled products.
                    // Their receipts, and the read/index envelope, end before the next chunk starts.
                    using var chunkPreparation = admission?.BeginPreparation();
                    if (chunk.Faces < 0) return Failure(MapRefinementStatus.CapacityExceeded, "water bound read: source faces in one cell", surface, read);
                    // A face-free chunk can still carry legacy compatibility coverage, which only a legacy recipe tags.
                    if (chunk.Faces == 0 && bound.Kind != MapBoundKind.LegacyExteriorV1) continue;
                    meter.ChargeRead(chunk.Faces);
                    MapExactXz a = surface.Frame.WorldXz(MapLatticeAddress.Corner(chunk.X0, chunk.Z0));
                    MapExactXz b = surface.Frame.WorldXz(MapLatticeAddress.Corner(chunk.X1, chunk.Z1));
                    MapBoundFaceSet set = MapCommonRefinement.BoundFaces(view, footprint, bound,
                        new(MapSpaceGeometry.Min(a.X, b.X), MapSpaceGeometry.Min(a.Z, b.Z)),
                        new(MapSpaceGeometry.Max(a.X, b.X), MapSpaceGeometry.Max(a.Z, b.Z)), limits);
                    if (set.Status == MapRefinementStatus.MissingGeometry)
                    {
                        gap ??= set.Detail ?? "missing geometry";
                        continue;
                    }
                    if (set.Status != MapRefinementStatus.Complete) return Failure(set.Status, set.Detail, surface, read);
                    Grow(faces, set.Faces.Count, preparation);
                    faces.AddRange(set.Faces);
                    Grow(compatibility, set.Compatibility.Count, preparation);
                    compatibility.AddRange(set.Compatibility);
                    meter.CountFaces(upper, set.Faces.Count);
                }
            }
        preparation?.Complete(checked(128L + (long)faces.Capacity * Unsafe.SizeOf<MapBoundFace>() +
            (long)compatibility.Capacity * Unsafe.SizeOf<MapLegacyCellTag>() + (long)read.Capacity * Unsafe.SizeOf<MapPatchKey>()));
        return new(gap is null ? MapRefinementStatus.Complete : MapRefinementStatus.MissingGeometry, gap, surface.Frame, faces.AsReadOnly(),
            compatibility.AsReadOnly(), read.AsReadOnly(), surface.Id, gap);
    }

    static void Grow<T>(List<T> values, int additional, MapPreparationScope? preparation)
    {
        int count = checked(values.Count + additional);
        if (count <= values.Capacity) return;
        int previous = values.Capacity;
        int capacity = Math.Max(count, checked(Math.Max(4, previous * 2)));
        long bytes = checked((long)capacity * Unsafe.SizeOf<T>());
        preparation?.Reserve(bytes);
        values.Capacity = capacity;
        preparation?.Release(checked((long)previous * Unsafe.SizeOf<T>()));
    }

    /// <summary>The released face count of one lattice cell of the bound: an opening counts only its own cells.</summary>
    static long Faces(MapSurfaceRef surface, MapSurfacePatch patch, MapHorizontalOpening? opening, long x, long z)
    {
        int slot = MapLowerCellClassifier.SlotCell(x, z);
        if (opening is null) return MapSurfaceCompiler.CountFaces(surface, patch, slot);
        return opening.SlotCells.Contains(slot) ? MapBoundFaceContext.OpeningFaceCount(patch, slot) : 0;
    }

    /// <summary>One chunk of cells from (<see cref="X0"/>, <see cref="Z0"/>) to the exclusive (<see cref="X1"/>,
    /// <see cref="Z1"/>) with its counted faces, or a negative count for one cell that alone passes the limit.</summary>
    readonly record struct Chunk(long X0, long Z0, long X1, long Z1, long Faces);

    /// <summary>Cuts the cell range into chunks of whole rows while both the cells and the faces of a chunk stay within
    /// <paramref name="limit"/>, splitting a row that alone passes it into runs of cells.</summary>
    static IEnumerable<Chunk> Chunks(long x0, long z0, long x1, long z1, int limit, Func<long, long, long> faces)
    {
        long width = x1 - x0;
        long start = z0, cells = 0, total = 0;
        for (long z = z0; z < z1; z++)
        {
            long[] row = new long[width];
            long rowFaces = 0;
            for (long x = x0; x < x1; x++) rowFaces += row[x - x0] = faces(x, z);
            if (!CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.RowCells", "MaxSourceFacesPerBound", 0, width, limit, width > limit, CapacityDemandLifetime.Transient) &&
                !CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.RowFaces", "MaxSourceFacesPerBound", 0, rowFaces, limit, rowFaces > limit, CapacityDemandLifetime.Transient))
            {
                if (CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.ChunkCells", "MaxSourceFacesPerBound", cells, width, limit, cells + width > limit, CapacityDemandLifetime.Transient) ||
                    CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.ChunkFaces", "MaxSourceFacesPerBound", total, rowFaces, limit, total + rowFaces > limit, CapacityDemandLifetime.Transient))
                {
                    if (cells > 0) yield return new(x0, start, x1, z, total);
                    start = z;
                    cells = total = 0;
                }
                cells += width;
                total += rowFaces;
                continue;
            }
            if (cells > 0) yield return new(x0, start, x1, z, total);
            cells = total = 0;
            start = z + 1;
            long runStart = x0, runFaces = 0;
            for (long x = x0; x < x1; x++)
            {
                long cellFaces = row[x - x0];
                if (CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.CellFaces", "MaxSourceFacesPerBound", 0, cellFaces, limit, cellFaces > limit, CapacityDemandLifetime.Transient))
                {
                    yield return new(x, z, x + 1, z + 1, -1);
                    yield break;
                }
                if (x > runStart && (CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.RunCells", "MaxSourceFacesPerBound", x - runStart, 1, limit, x - runStart + 1 > limit, CapacityDemandLifetime.Transient) ||
                    CapacityDemandDiagnostics.Check(null, "MapWaterBoundReads.RunFaces", "MaxSourceFacesPerBound", runFaces, cellFaces, limit, runFaces + cellFaces > limit, CapacityDemandLifetime.Transient)))
                {
                    yield return new(runStart, z, x, z + 1, runFaces);
                    runStart = x;
                    runFaces = 0;
                }
                runFaces += cellFaces;
            }
            yield return new(runStart, z, x1, z + 1, runFaces);
        }
        if (cells > 0) yield return new(x0, start, x1, z1, total);
    }

    static long SlotOf(long cell) => cell >= 0 ? cell / MapPatchKey.SlotCells : -((-cell + MapPatchKey.SlotCells - 1) / MapPatchKey.SlotCells);

    static MapWaterBoundRead Unread(string? surfaceId, string gap) => new(MapRefinementStatus.MissingGeometry, gap, null,
        Array.Empty<MapBoundFace>(), Array.Empty<MapLegacyCellTag>(), Array.Empty<MapPatchKey>(), surfaceId, gap);

    static MapWaterBoundRead Failure(MapRefinementStatus status, string? detail, MapSurfaceRef surface, List<MapPatchKey> read) =>
        new(status, detail, surface.Frame, Array.Empty<MapBoundFace>(), Array.Empty<MapLegacyCellTag>(), read.AsReadOnly(), surface.Id, null);
}
