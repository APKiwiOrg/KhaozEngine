using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Prepares exactly the cells under a query point, including declared opening owners.</summary>
internal sealed class MapSpacePointBounds
{
    readonly MapScopedSurfaces _view;
    readonly MapBoundPreparation _preparation;
    internal MapBoundPreparation Preparation => _preparation;
    internal sealed record Bound(MapMembershipStatus Status, MapCellDemand? Cell, MapRecordRef? Opening,
        MapLegacyCellTag? Compatibility, string? Detail);

    internal MapSpacePointBounds(MapScopedSurfaces view, MapRefinementLimits limits, MapBoundFaceWork? work)
    {
        _view = view;
        _preparation = new(view, limits, work);
    }

    internal MapSpacePointBounds(MapBoundFaceContext context, MapRefinementLimits limits)
    {
        _view = context.View;
        _preparation = new(context, limits);
    }

    internal Bound Plan(MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz point)
    {
        bool lower = bound == footprint.Lower;
        if (!lower && bound.Kind == MapBoundKind.OpenTop) return new(MapMembershipStatus.Resolved, null, null, null, null);
        if (!lower && bound.Kind == MapBoundKind.LegacyExteriorV1) return Failure(MapMembershipStatus.Invalid, "legacy recipe: upper");
        if (lower && bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1 &&
            !_view.Surfaces.Any(s => s.Id == bound.SurfaceId))
        {
            MapLowerCellClassification missing = MapLowerCellClassifier.Classify(_view, footprint, 0, 0);
            return Failure(missing.Class == MapLowerCellClass.InvalidRecipe ? MapMembershipStatus.Invalid : MapMembershipStatus.MissingGeometry, missing.Detail);
        }
        MapHorizontalOpening? opening = bound.Kind == MapBoundKind.HorizontalOpening
            ? MapSpaceGeometry.Record<MapHorizontalOpening>(_view,
                bound.Opening ?? throw new MapDocumentException("missing geometry: opening reference")) : null;
        string id = opening?.Patch.SurfaceId ?? bound.SurfaceId ?? throw new MapDocumentException("missing geometry: bound surface");
        MapSurfaceRef surface = MapSpaceGeometry.Surface(_view, id);
        var (x, z) = MapSpaceGeometry.Cell(surface.Frame, point);
        MapCellDemand cell = new(MapPatchKey.ForCell(id, x, z), MapLowerCellClassifier.SlotCell(x, z));
        MapLegacyCellTag? compatibility = null;
        if (lower && bound.Kind is MapBoundKind.SupportFloor or MapBoundKind.LegacyExteriorV1)
        {
            MapLowerCellClassification classification = MapLowerCellClassifier.Classify(_view, footprint, x, z);
            if (classification.Class == MapLowerCellClass.InvalidRecipe) return Failure(MapMembershipStatus.Invalid, classification.Detail);
            if (classification.Class == MapLowerCellClass.Unavailable) return Failure(MapMembershipStatus.MissingGeometry, classification.Detail);
            if (classification.Class is MapLowerCellClass.KnownHole or MapLowerCellClass.UntaggedLegacy)
                return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: {classification.Detail ?? cell.Patch.ToString()}");
            compatibility = classification.Legacy;
        }
        else if (opening is null && (lower || bound.Kind != MapBoundKind.Ceiling))
            return Failure(MapMembershipStatus.Invalid, "invalid bound kind");
        if (!_view.TryAcquiredPatch(cell.Patch, out MapSurfacePatch? patch, out MapPatchStatus status))
            return Failure(MapMembershipStatus.MissingGeometry, $"patch {cell.Patch}: {status}");
        if (patch is null) return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: patch {cell.Patch}: {status}");
        int localX = cell.SlotCell % 64 - patch.CellMinX, localZ = cell.SlotCell / 64 - patch.CellMinZ;
        if (localX < 0 || localX >= patch.Width || localZ < 0 || localZ >= patch.Depth)
            return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: patch {cell.Patch} cell {cell.SlotCell}");
        if (_preparation.ValidatedPatch(surface, patch) is null)
            return Failure(MapMembershipStatus.CapacityExceeded, _preparation.Refusal);
        if (opening is not null)
        {
            if (opening.Patch != cell.Patch || !Contains(opening.SlotCells, cell.SlotCell))
                return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: opening '{opening.Id}' cell {cell.SlotCell}");
            _ = MapBoundFaceContext.OpeningFaceCount(patch, cell.SlotCell);
        }
        else if (!patch.IsPresent(localX, localZ))
            return Failure(MapMembershipStatus.MissingGeometry, $"missing bound: patch {cell.Patch} cell {cell.SlotCell}");
        return new(MapMembershipStatus.Resolved, cell, bound.Opening, compatibility, null);
    }

    internal MapCellDemand SourceCell(MapPatchKey key, MapExactXz point)
    {
        MapSurfaceRef surface = MapSpaceGeometry.Surface(_view, key.SurfaceId);
        if (!_view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
            throw new MapDocumentException($"missing geometry: patch {key} ({status})");
        var (x, z) = MapSpaceGeometry.Cell(surface.Frame, point);
        long minX = checked(key.SlotX * 64 + patch.CellMinX), minZ = checked(key.SlotZ * 64 + patch.CellMinZ);
        // A source edge owns its declared patch, including the patch's closed outer rim.
        x = Math.Clamp(x, minX, checked(minX + patch.Width - 1));
        z = Math.Clamp(z, minZ, checked(minZ + patch.Depth - 1));
        _ = _preparation.ValidatedPatch(surface, patch);
        return new(key, MapLowerCellClassifier.SlotCell(x, z));
    }

    internal IEnumerable<MapCellDemand> SourceCells(MapBoundaryChain segment)
    {
        if (segment.SourcePatch is not { } key) yield break;
        MapSurfaceRef surface = MapSpaceGeometry.Surface(_view, key.SurfaceId);
        if (!_view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
            throw new MapDocumentException($"missing geometry: patch {key} ({status})");
        _ = _preparation.ValidatedPatch(surface, patch);
        long minX = checked(key.SlotX * 64 + patch.CellMinX), minZ = checked(key.SlotZ * 64 + patch.CellMinZ);
        long maxX = checked(minX + patch.Width), maxZ = checked(minZ + patch.Depth);
        var cells = new SortedSet<int>();
        foreach (MapChainVertex vertex in segment.Vertices)
        {
            MapLatticeAddress address = vertex.Vertex.Address;
            var x = new MapExactValue(address.X, address.Denominator);
            var z = new MapExactValue(address.Z, address.Denominator);
            // Closed incident cells include the preceding row at an endpoint and the owner of an edge subdivision.
            for (long row = Math.Max(minZ, checked(z.Ceiling() - 1)); row <= Math.Min(maxZ - 1, z.Floor()); row++)
                for (long column = Math.Max(minX, checked(x.Ceiling() - 1)); column <= Math.Min(maxX - 1, x.Floor()); column++)
                    cells.Add(MapLowerCellClassifier.SlotCell(column, row));
        }
        foreach (int cell in cells) yield return new(key, cell);
    }

    internal static MapExactValue? Height(MapBoundFaceContext context, Bound bound, MapExactXz point)
    {
        if (bound.Cell is not { } cell) return null;
        if (bound.Compatibility is not null)
        {
            MapValidatedSurfacePatch patch = context.ValidatedPatch(cell.Patch);
            return MapExactValue.FromSingle(MapLegacyBilinear.HeightMetres(patch.Surface, patch.Patch, point));
        }
        return MapSpaceGeometry.Height(bound.Opening is { } opening ? context.Opening(opening, cell) : context.Faces(cell), point);
    }

    static bool Contains(IReadOnlyList<int> cells, int slot)
    {
        foreach (int cell in cells) if (cell == slot) return true;
        return false;
    }
    static Bound Failure(MapMembershipStatus status, string? detail) => new(status, null, null, null, detail);
}
