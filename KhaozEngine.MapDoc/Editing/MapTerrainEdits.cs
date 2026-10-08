using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

public abstract record MapTerrainEdit(string Label);
public sealed record MapSetCornerHeights(MapPatchKey Patch, int CornerX, int CornerZ, int Width, int Depth,
    IReadOnlyList<int> Values) : MapTerrainEdit("Set corner heights");
public sealed record MapSmoothCorners(MapPatchKey Patch, int CornerX, int CornerZ, int Width, int Depth,
    int Iterations) : MapTerrainEdit("Smooth corners");
public sealed record MapSetCells(MapPatchKey Patch, int CellX, int CellZ, int Width, int Depth,
    IReadOnlyList<MapSurfaceCell> Cells) : MapTerrainEdit("Set cells");
public sealed record MapSetPresence(MapPatchKey Patch, IReadOnlyList<int> SlotCells,
    bool Present) : MapTerrainEdit("Set presence");
public sealed record MapReassignCornerOwner(IReadOnlyList<MapCornerOwnerChange> Changes)
    : MapTerrainEdit("Reassign corner owner");
public sealed record MapRecordAnchorMove(string RecordId, MapPatchKey From, MapPatchKey To);
public sealed record MapReanchorRecords(IReadOnlyList<MapRecordAnchorMove> Moves) : MapTerrainEdit("Reanchor records");
public sealed record MapReplaceTopology(IReadOnlyList<MapSurfacePatch> UpsertPatches,
    IReadOnlyList<MapPatchKey> RemovePatches, IReadOnlyList<MapSurfaceRef> UpsertSurfaces,
    IReadOnlyList<string> RemoveSurfaces) : MapTerrainEdit("Replace topology");
public sealed record MapConvertFinePatch(MapFinePatchRequest Request) : MapTerrainEdit("Convert fine patch");
public sealed record MapCompositeEdit(IReadOnlyList<MapTerrainEdit> Edits) : MapTerrainEdit("Composite terrain edit");
public sealed record MapTerrainEditResult(MapSurfaceSet Candidate, MapNativeWriteSet WriteSet, MapNativeEditEffects Effects);

/// <summary>Pure terrain preparation. Every edit and its dependencies are checked before returning a candidate.</summary>
public static class MapTerrainEdits
{
    internal const MapNativeInvalidation Geometry = MapNativeInvalidation.Terrain | MapNativeInvalidation.Physics |
        MapNativeInvalidation.Nav | MapNativeInvalidation.Residency;
    internal const MapNativeInvalidation All = Geometry | MapNativeInvalidation.Material;

    public static MapTerrainEditResult Prepare(MapSurfaceSet surfaces, MapTerrainEdit edit)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        ArgumentNullException.ThrowIfNull(edit);
        RequireIterations(edit);
        try
        {
            MapSurfaceSet candidate = surfaces.Clone();
            MapNativeInvalidation invalidates = Apply(ref candidate, edit);
            RequireValid(MapTopologyReferenceValidator.Validate(candidate.Refs, candidate.Patches.Values));
            MapScopedSurfaces view = MapScopedSurfaces.CompleteView(candidate);
            foreach (MapSurfacePatch patch in candidate.Patches.Values)
                RequireValid(MapSeamValidator.ValidateCornerDependencies(patch, view));
            foreach (MapSurfaceSeam seam in candidate.AllRecords().OfType<MapSurfaceSeam>())
                RequireValid(MapSeamValidator.Validate(seam, view));
            return MapTerrainEditChanges.Describe(surfaces, candidate, invalidates);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("terrain edit is not representable", error);
        }
    }

    static void RequireIterations(MapTerrainEdit edit)
    {
        if (edit is MapSmoothCorners smooth && smooth.Iterations is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(smooth.Iterations), "Smoothing requires 1 to 64 iterations.");
        if (edit is MapCompositeEdit composite)
        {
            ArgumentNullException.ThrowIfNull(composite.Edits);
            foreach (MapTerrainEdit child in composite.Edits)
            {
                ArgumentNullException.ThrowIfNull(child);
                RequireIterations(child);
            }
        }
    }

    static MapNativeInvalidation Apply(ref MapSurfaceSet candidate, MapTerrainEdit edit)
    {
        switch (edit)
        {
            case MapSetCornerHeights heights:
                SetHeights(Patch(candidate, heights.Patch), heights);
                return Geometry;
            case MapSmoothCorners smooth:
                Smooth(candidate, smooth);
                return Geometry;
            case MapSetCells cells:
                return SetCells(Patch(candidate, cells.Patch), cells);
            case MapSetPresence presence:
                SetPresence(Patch(candidate, presence.Patch), presence);
                return Geometry;
            case MapReassignCornerOwner owners:
                MapTerrainTopologyEdits.Reassign(candidate, owners);
                return Geometry;
            case MapReanchorRecords anchors:
                MapTerrainTopologyEdits.Reanchor(candidate, anchors);
                return MapNativeInvalidation.Residency;
            case MapReplaceTopology topology:
                MapTerrainTopologyEdits.Replace(candidate, topology);
                return All;
            case MapConvertFinePatch conversion:
                candidate = MapFinePatchConversion.Convert(candidate, conversion.Request).Candidate;
                return All;
            case MapCompositeEdit composite:
                MapNativeInvalidation invalidates = MapNativeInvalidation.None;
                foreach (MapTerrainEdit child in composite.Edits) invalidates |= Apply(ref candidate, child);
                return invalidates;
            default:
                throw new MapDocumentException("unknown terrain edit type");
        }
    }

    internal static MapSurfacePatch Patch(MapSurfaceSet set, MapPatchKey key) =>
        set.Patches.TryGetValue(key, out MapSurfacePatch? patch) ? patch
            : throw new MapDocumentException($"missing terrain patch '{key}'");

    internal static void RequireValid(IReadOnlyList<string> findings)
    {
        if (findings.Count != 0) throw new MapDocumentException(findings[0]);
    }

    static void RequireRegion(int x, int z, int width, int depth, int limitX, int limitZ)
    {
        if (x < 0 || z < 0 || width < 1 || depth < 1 || (long)x + width > limitX || (long)z + depth > limitZ)
            throw new MapDocumentException("terrain edit region outside patch rectangle");
    }

    static void SetHeights(MapSurfacePatch patch, MapSetCornerHeights edit)
    {
        ArgumentNullException.ThrowIfNull(edit.Values);
        RequireRegion(edit.CornerX, edit.CornerZ, edit.Width, edit.Depth, patch.Width + 1, patch.Depth + 1);
        if (edit.Values.Count != edit.Width * edit.Depth) throw new MapDocumentException("terrain height value count differs");
        for (int z = 0; z < edit.Depth; z++)
            for (int x = 0; x < edit.Width; x++)
                patch.Heights[(edit.CornerZ + z) * (patch.Width + 1) + edit.CornerX + x] = edit.Values[z * edit.Width + x];
    }

    static MapNativeInvalidation SetCells(MapSurfacePatch patch, MapSetCells edit)
    {
        ArgumentNullException.ThrowIfNull(edit.Cells);
        RequireRegion(edit.CellX, edit.CellZ, edit.Width, edit.Depth, patch.Width, patch.Depth);
        if (edit.Cells.Count != edit.Width * edit.Depth) throw new MapDocumentException("terrain cell value count differs");
        MapNativeInvalidation invalidates = MapNativeInvalidation.None;
        for (int z = 0; z < edit.Depth; z++)
            for (int x = 0; x < edit.Width; x++)
            {
                int index = (edit.CellZ + z) * patch.Width + edit.CellX + x;
                MapSurfaceCell before = patch.Cells[index], after = edit.Cells[z * edit.Width + x];
                if (before.Cut != after.Cut || before.Rotation != after.Rotation || before.Topology != after.Topology ||
                    before.Flags != after.Flags) invalidates |= All;
                else if (before.Underlay != after.Underlay || before.Overlay != after.Overlay)
                    invalidates |= MapNativeInvalidation.Terrain | MapNativeInvalidation.Material;
                patch.Cells[index] = after;
            }
        return invalidates;
    }

    static void SetPresence(MapSurfacePatch patch, MapSetPresence edit)
    {
        ArgumentNullException.ThrowIfNull(edit.SlotCells);
        foreach (int cell in edit.SlotCells)
        {
            if (cell is < 0 or >= 4096) throw new MapDocumentException("presence slot cell outside patch rectangle");
            patch.SetPresent(cell % 64 - patch.CellMinX, cell / 64 - patch.CellMinZ, edit.Present);
        }
    }

    static void Smooth(MapSurfaceSet set, MapSmoothCorners edit)
    {
        MapSurfacePatch patch = Patch(set, edit.Patch);
        RequireRegion(edit.CornerX, edit.CornerZ, edit.Width, edit.Depth, patch.Width + 1, patch.Depth + 1);
        int stride = edit.Width + 2;
        var previous = new int[stride * (edit.Depth + 2)];
        for (int z = 0; z < edit.Depth + 2; z++)
            for (int x = 0; x < stride; x++)
                previous[z * stride + x] = Halo(set, patch, edit.CornerX + x - 1, edit.CornerZ + z - 1);
        var next = (int[])previous.Clone();
        for (int pass = 0; pass < edit.Iterations; pass++)
        {
            for (int z = 1; z <= edit.Depth; z++)
                for (int x = 1; x <= edit.Width; x++)
                {
                    long sum = 0;
                    for (int dz = -1; dz <= 1; dz++)
                        for (int dx = -1; dx <= 1; dx++) sum += previous[(z + dz) * stride + x + dx];
                    next[z * stride + x] = checked((int)Math.Round(sum / 9.0, MidpointRounding.AwayFromZero));
                }
            (previous, next) = (next, previous);
        }
        for (int z = 0; z < edit.Depth; z++)
            for (int x = 0; x < edit.Width; x++)
                patch.Heights[(edit.CornerZ + z) * (patch.Width + 1) + edit.CornerX + x] = previous[(z + 1) * stride + x + 1];
    }

    static int Halo(MapSurfaceSet set, MapSurfacePatch patch, int x, int z)
    {
        if (x >= 0 && x <= patch.Width && z >= 0 && z <= patch.Depth) return patch.Height(x, z);
        MapLatticeAddress origin = patch.CornerAddress(0, 0);
        long absoluteX = checked(origin.X + x), absoluteZ = checked(origin.Z + z);
        int? height = null;
        foreach (MapSurfacePatch neighbour in set.Patches.Values.Where(p => p.Key.SurfaceId == patch.Key.SurfaceId && p.Key != patch.Key))
        {
            MapLatticeAddress start = neighbour.CornerAddress(0, 0);
            Int128 nx = (Int128)absoluteX - start.X, nz = (Int128)absoluteZ - start.Z;
            if (nx < 0 || nx > neighbour.Width || nz < 0 || nz > neighbour.Depth) continue;
            if (height.HasValue) throw new MapDocumentException("smoothing halo corner is ambiguous");
            height = neighbour.Height((int)nx, (int)nz);
        }
        return height ?? throw new MapDocumentException("smoothing halo corner is outside the surface or unloaded");
    }
}
