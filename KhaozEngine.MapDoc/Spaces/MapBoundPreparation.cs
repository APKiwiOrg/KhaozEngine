using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Face-free, per-bound preflight. Only a surviving footprint cell yields context demands.</summary>
internal sealed class MapBoundPreparation
{
    readonly MapScopedSurfaces _view;
    readonly MapRefinementLimits _limits;
    readonly MapBoundFaceWork? _work;
    readonly MapBoundFaceWork? _validationWork;
    readonly Dictionary<MapPatchKey, MapValidatedSurfacePatch> _validated = new();
    readonly MapBoundFaceContext? _context;

    internal MapBoundPreparation(MapScopedSurfaces view, MapRefinementLimits limits, MapBoundFaceWork? work)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(limits);
        _view = view;
        _limits = limits;
        _work = work;
        _validationWork = work;
    }

    internal MapBoundPreparation(MapBoundFaceContext context, MapRefinementLimits limits)
        : this(context.View, limits, null)
    {
        _context = context;
        _validationWork = context.Work;
    }

    internal IEnumerable<MapValidatedSurfacePatch> ValidatedPatches => _validated.Values;

    internal void RequireView(MapScopedSurfaces view)
    {
        if (!ReferenceEquals(view, _view)) throw new ArgumentException("context view does not match", nameof(view));
    }

    internal sealed record Plan(MapRefinementStatus Status, IReadOnlyList<MapCellDemand> Demands,
        IReadOnlyList<MapLegacyCellTag> Compatibility, MapRecordRef? Opening, string? Detail);

    internal MapExactXz[] Rectangle(MapSpaceFootprint footprint, int cell)
    {
        MapSurfaceRef? lattice = Surface(footprint.Lattice.SurfaceId);
        if (lattice is null) throw new MapDocumentException($"missing footprint lattice '{footprint.Lattice.SurfaceId}'");
        return MapLatticeRanges.CellRect(lattice.Frame, footprint.Lattice, cell);
    }

    internal Plan Bound(MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz min, MapExactXz max)
    {
        if (bound != footprint.Lower && bound != footprint.Upper)
            throw new ArgumentException("bound must belong to footprint", nameof(bound));
        if (bound.Kind == MapBoundKind.OpenTop) throw new ArgumentException("open top has no bound faces", nameof(bound));
        if (min.X.CompareTo(max.X) >= 0 || min.Z.CompareTo(max.Z) >= 0) return Failure(MapRefinementStatus.Invalid, "cell");
        bool lower = bound == footprint.Lower;
        if (!lower && bound.Kind == MapBoundKind.LegacyExteriorV1) return Failure(MapRefinementStatus.Invalid, "legacy recipe: upper");
        if (bound.Kind == MapBoundKind.HorizontalOpening) return Opening(bound, min, max);
        if (bound.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.Ceiling or MapBoundKind.LegacyExteriorV1))
            return Failure(MapRefinementStatus.Invalid, "bound kind");
        MapSurfaceRef? surface = Surface(bound.SurfaceId);
        if (surface is null)
        {
            if (lower && bound.Kind == MapBoundKind.LegacyExteriorV1)
            {
                MapLowerCellClassification classification = MapLowerCellClassifier.Classify(_view, footprint, 0, 0);
                return ClassificationFailure(classification);
            }
            return Failure(MapRefinementStatus.MissingGeometry, $"surface '{bound.SurfaceId}': missing");
        }
        MapCellRect range = MapLatticeRanges.CellRange(surface.Frame, min, max);
        if (!WithinCellBudget(range)) return Failure(MapRefinementStatus.CapacityExceeded, "source faces: cell range");
        var demands = new List<MapCellDemand>();
        var compatibility = new List<MapLegacyCellTag>();
        long count = 0;
        foreach (var (x, z) in Cells(range))
        {
            MapPatchKey key = MapPatchKey.ForCell(surface.Id, x, z);
            int slot = MapLowerCellClassifier.SlotCell(x, z);
            if (lower)
            {
                MapLowerCellClassification classification = MapLowerCellClassifier.Classify(_view, footprint, x, z);
                if (classification.Class is MapLowerCellClass.Unavailable or MapLowerCellClass.InvalidRecipe)
                    return ClassificationFailure(classification);
                if (classification.Legacy is { } tag) compatibility.Add(tag);
                if (classification.Class != MapLowerCellClass.Physical) continue;
            }
            if (!_view.TryAcquiredPatch(key, out MapSurfacePatch? patch, out MapPatchStatus status))
                return Failure(MapRefinementStatus.MissingGeometry, $"patch {key}: {status}");
            if (status == MapPatchStatus.KnownEmpty) continue;
            if (patch is null) return Failure(MapRefinementStatus.MissingGeometry, $"patch {key}: {status} without payload");
            _ = ValidatedPatch(surface, patch);
            long faces = MapSurfaceCompiler.CountFaces(surface, patch, slot);
            count = checked(count + faces);
            if (_work is not null) _work.BoundFacesCounted += faces;
            if (count > _limits.MaxSourceFacesPerBound) return Failure(MapRefinementStatus.CapacityExceeded, "source faces");
            if (faces != 0) demands.Add(new(key, slot));
        }
        return new(MapRefinementStatus.Complete, demands.AsReadOnly(), compatibility.AsReadOnly(), null, null);
    }

    Plan Opening(MapBoundRef bound, MapExactXz min, MapExactXz max)
    {
        if (bound.Opening is not { } reference) return Failure(MapRefinementStatus.MissingGeometry, "opening: missing reference");
        if (!_view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus recordStatus, borrow: true) ||
            record is not MapHorizontalOpening opening)
            return Failure(MapRefinementStatus.MissingGeometry, $"opening '{reference.Id}' {reference.Anchor}: {recordStatus}");
        MapSurfaceRef? surface = Surface(opening.Patch.SurfaceId);
        if (surface is null) return Failure(MapRefinementStatus.MissingGeometry, $"opening '{reference.Id}': missing surface");
        MapCellRect range = MapLatticeRanges.CellRange(surface.Frame, min, max);
        if (!WithinCellBudget(range)) return Failure(MapRefinementStatus.CapacityExceeded, "source faces: cell range");
        if (!_view.TryAcquiredPatch(opening.Patch, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
            return Failure(MapRefinementStatus.MissingGeometry, $"opening '{reference.Id}' {opening.Patch}: {status}");
        _ = ValidatedPatch(surface, patch);
        var demands = new List<MapCellDemand>();
        long count = 0;
        foreach (int slot in opening.SlotCells.Order())
        {
            MapExactXz[] rect = MapLatticeRanges.CellRect(surface.Frame, opening.Patch, slot);
            if (rect[0].X.CompareTo(max.X) >= 0 || rect[1].X.CompareTo(min.X) <= 0 ||
                rect[0].Z.CompareTo(max.Z) >= 0 || rect[1].Z.CompareTo(min.Z) <= 0) continue;
            int faces = MapBoundFaceContext.OpeningFaceCount(patch, slot);
            count = checked(count + faces);
            if (_work is not null) _work.BoundFacesCounted += faces;
            if (count > _limits.MaxSourceFacesPerBound) return Failure(MapRefinementStatus.CapacityExceeded, "source faces");
            demands.Add(new(opening.Patch, slot));
        }
        return new(MapRefinementStatus.Complete, demands.AsReadOnly(), Array.Empty<MapLegacyCellTag>(), reference, null);
    }

    bool WithinCellBudget(MapCellRect range)
    {
        try
        {
            long count = checked(checked(range.MaxXExclusive - range.MinX) * checked(range.MaxZExclusive - range.MinZ));
            return count <= _limits.MaxSourceFacesPerBound;
        }
        catch (OverflowException) { return false; }
    }

    static IEnumerable<(long X, long Z)> Cells(MapCellRect range)
    {
        for (long z = range.MinZ; z < range.MaxZExclusive; z++)
            for (long x = range.MinX; x < range.MaxXExclusive; x++) yield return (x, z);
    }

    internal MapValidatedSurfacePatch ValidatedPatch(MapSurfaceRef surface, MapSurfacePatch patch)
    {
        if (_context is not null) return _context.ValidatedPatch(patch.Key);
        if (_validated.TryGetValue(patch.Key, out MapValidatedSurfacePatch? validated)) return validated;
        validated = new(surface, patch, _validationWork);
        // Retain evidence for every borrowed input visited by preflight, including face-free bounds.
        // The context's patch budget limits demanded compilation masks, not validation evidence.
        _validated.Add(patch.Key, validated);
        return validated;
    }
    MapSurfaceRef? Surface(string? id) => _view.Surfaces.FirstOrDefault(s => s.Id == id);
    static Plan ClassificationFailure(MapLowerCellClassification classification) => Failure(
        classification.Class == MapLowerCellClass.InvalidRecipe ? MapRefinementStatus.Invalid : MapRefinementStatus.MissingGeometry,
        classification.Detail);
    static Plan Failure(MapRefinementStatus status, string? detail) =>
        new(status, Array.Empty<MapCellDemand>(), Array.Empty<MapLegacyCellTag>(), null, detail);
}
