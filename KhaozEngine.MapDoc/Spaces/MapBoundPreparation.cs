using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

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
    internal MapWaterWorkMeter? WaterMeter { get; }

    internal MapBoundPreparation(MapScopedSurfaces view, MapRefinementLimits limits, MapBoundFaceWork? work, MapWaterWorkMeter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(limits);
        _view = view;
        _limits = limits;
        _work = work;
        _validationWork = work;
        WaterMeter = meter;
    }

    internal MapBoundPreparation(MapBoundFaceContext context, MapRefinementLimits limits)
        : this(context.View, limits, null, context.WaterMeter)
    {
        _context = context;
        _validationWork = context.Work;
    }

    internal IEnumerable<MapValidatedSurfacePatch> ValidatedPatches => _validated.Values;
    internal string? Refusal { get; private set; }
    internal MapDocumentFinding? Finding { get; private set; }

    internal void RequireView(MapScopedSurfaces view)
    {
        if (!ReferenceEquals(view, _view)) throw new ArgumentException("context view does not match", nameof(view));
    }

    internal sealed record Plan(MapRefinementStatus Status, IReadOnlyList<MapCellDemand> Demands,
        IReadOnlyList<MapLegacyCellTag> Compatibility, MapRecordRef? Opening, string? Detail)
    {
        internal MapDocumentFinding? Finding { get; init; }
    }

    internal MapExactXz[] Rectangle(MapSpaceFootprint footprint, int cell)
    {
        MapSurfaceRef? lattice = Surface(footprint.Lattice.SurfaceId);
        if (lattice is null) throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, $"missing footprint lattice '{footprint.Lattice.SurfaceId}'")
        { SurfaceId = footprint.Lattice.SurfaceId, Patch = footprint.Lattice, RecordId = footprint.Id, SpaceId = footprint.Space.Id };
        if (_view.EffectiveContext is null) return MapLatticeRanges.CellRect(lattice.Frame, footprint.Lattice, cell);
        var bounds = MapEffectiveGeometry.Bounds(MapEffectiveGeometry.CellPolygon(_view, footprint.Lattice, cell));
        return new[] { new MapExactXz(bounds.MinX, bounds.MinZ), new MapExactXz(bounds.MaxX, bounds.MaxZ) };
    }

    internal Plan Bound(MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz min, MapExactXz max)
    {
        if (bound != footprint.Lower && bound != footprint.Upper)
            throw new ArgumentException("bound must belong to footprint", nameof(bound));
        if (bound.Kind == MapBoundKind.OpenTop) throw new ArgumentException("open top has no bound faces", nameof(bound));
        if (min.X.CompareTo(max.X) >= 0 || min.Z.CompareTo(max.Z) >= 0) return Failure(MapRefinementStatus.Invalid, "cell",
            Context(footprint, bound, MapDocumentRefusal.BoundsInvalid, "cell") with
            { FieldPath = min.X.CompareTo(max.X) >= 0 ? "Bounds.X" : "Bounds.Z" });
        bool lower = bound == footprint.Lower;
        if (!lower && bound.Kind == MapBoundKind.LegacyExteriorV1) return Failure(MapRefinementStatus.Invalid, "legacy recipe: upper",
            Context(footprint, bound, MapDocumentRefusal.LegacyExteriorOpenTopRequired, "legacy recipe: upper") with { FieldPath = "Upper.Kind" });
        if (bound.Kind == MapBoundKind.HorizontalOpening) return Opening(footprint, bound, min, max, lower);
        if (bound.Kind is not (MapBoundKind.SupportFloor or MapBoundKind.Ceiling or MapBoundKind.LegacyExteriorV1))
            return Failure(MapRefinementStatus.Invalid, "bound kind",
                Context(footprint, bound, MapDocumentRefusal.SchemaEnumUnsupported, "bound kind") with
                { FieldPath = lower ? "Lower.Kind" : "Upper.Kind", EnumType = typeof(MapBoundKind), ActualInteger = (long)bound.Kind });
        MapSurfaceRef? surface = Surface(bound.SurfaceId);
        if (surface is null)
        {
            if (lower && bound.Kind == MapBoundKind.LegacyExteriorV1)
            {
                MapLowerCellClassification classification = MapLowerCellClassifier.Classify(_view, footprint, 0, 0);
                return ClassificationFailure(classification);
            }
            return Failure(MapRefinementStatus.MissingGeometry, $"surface '{bound.SurfaceId}': missing",
                Context(footprint, bound, MapDocumentRefusal.ReferenceMissing, $"surface '{bound.SurfaceId}': missing") with
                { FieldPath = lower ? "Lower.SurfaceId" : "Upper.SurfaceId" });
        }
        MapCellRect range = MapEffectiveGeometry.CellRange(_view, surface.Id, min, max);
        WaterMeterCharge(surface.Id, min, max);
        if (!WithinCellBudget(range, out long? cells)) return Failure(MapRefinementStatus.CapacityExceeded, "source faces: cell range",
            SourceCapacity(footprint, bound, "source faces: cell range", cells));
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
                return Failure(MapRefinementStatus.MissingGeometry, $"patch {key}: {status}",
                    Context(footprint, bound, MapDocumentRefusal.PatchUnavailable, $"patch {key}: {status}") with
                    { Patch = key, PatchStatus = status, SlotCell = slot });
            if (status == MapPatchStatus.KnownEmpty) continue;
            if (patch is null) return Failure(MapRefinementStatus.MissingGeometry, $"patch {key}: {status} without payload",
                Context(footprint, bound, MapDocumentRefusal.PatchUnavailable, $"patch {key}: {status} without payload") with
                { Patch = key, PatchStatus = status, SlotCell = slot });
            if (WaterMeter is null && ValidatedPatch(surface, patch) is null) return Failure(MapRefinementStatus.CapacityExceeded, Refusal, Finding);
            long faces = MapSurfaceCompiler.CountFaces(surface, patch, slot);
            WaterMeter?.ChargeRead(faces);
            WaterMeter?.CountFaces(!lower, checked((int)faces));
            if (WaterMeter is not null && ValidatedPatch(surface, patch) is null) return Failure(MapRefinementStatus.CapacityExceeded, Refusal, Finding);
            count = checked(count + faces);
            if (_work is not null) _work.BoundFacesCounted += faces;
            if (count > _limits.MaxSourceFacesPerBound) return Failure(MapRefinementStatus.CapacityExceeded, "source faces",
                SourceCapacity(footprint, bound, "source faces", count) with { Patch = key, SlotCell = slot });
            if (faces != 0) demands.Add(new(key, slot));
        }
        return new(MapRefinementStatus.Complete, demands.AsReadOnly(), compatibility.AsReadOnly(), null, null);
    }

    Plan Opening(MapSpaceFootprint footprint, MapBoundRef bound, MapExactXz min, MapExactXz max, bool lower)
    {
        if (bound.Opening is not { } reference) return Failure(MapRefinementStatus.MissingGeometry, "opening: missing reference",
            Context(footprint, bound, MapDocumentRefusal.SchemaValueRequired, "opening: missing reference") with
            { FieldPath = lower ? "Lower.Opening" : "Upper.Opening" });
        bool found = _view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus recordStatus, borrow: true);
        if (!found ||
            record is not MapHorizontalOpening opening)
            return Failure(MapRefinementStatus.MissingGeometry, $"opening '{reference.Id}' {reference.Anchor}: {recordStatus}",
                Context(footprint, bound, !found ? MapDocumentRefusal.PatchUnavailable : MapDocumentRefusal.ReferenceKindMismatch,
                    $"opening '{reference.Id}' {reference.Anchor}: {recordStatus}") with
                { RelatedRecordId = reference.Id, Patch = reference.Anchor, PatchStatus = recordStatus,
                    RecordType = record?.GetType(), ExpectedRecordType = typeof(MapHorizontalOpening) });
        MapSurfaceRef? surface = Surface(opening.Patch.SurfaceId);
        if (surface is null) return Failure(MapRefinementStatus.MissingGeometry, $"opening '{reference.Id}': missing surface",
            Context(footprint, bound, MapDocumentRefusal.ReferenceMissing, $"opening '{reference.Id}': missing surface") with
            { RelatedRecordId = reference.Id, SurfaceId = opening.Patch.SurfaceId, Patch = opening.Patch });
        MapCellRect range = MapEffectiveGeometry.CellRange(_view, surface.Id, min, max);
        WaterMeterCharge(surface.Id, min, max);
        if (!WithinCellBudget(range, out long? cells)) return Failure(MapRefinementStatus.CapacityExceeded, "source faces: cell range",
            SourceCapacity(footprint, bound, "source faces: cell range", cells) with
            { RelatedRecordId = reference.Id, SurfaceId = surface.Id, Patch = opening.Patch });
        if (!_view.TryAcquiredPatch(opening.Patch, out MapSurfacePatch? patch, out MapPatchStatus status) || patch is null)
            return Failure(MapRefinementStatus.MissingGeometry, $"opening '{reference.Id}' {opening.Patch}: {status}",
                Context(footprint, bound, MapDocumentRefusal.PatchUnavailable, $"opening '{reference.Id}' {opening.Patch}: {status}") with
                { RelatedRecordId = reference.Id, SurfaceId = surface.Id, Patch = opening.Patch, PatchStatus = status });
        if (ValidatedPatch(surface, patch) is null) return Failure(MapRefinementStatus.CapacityExceeded, Refusal, Finding);
        var demands = new List<MapCellDemand>();
        long count = 0;
        foreach (int slot in opening.SlotCells.Order())
        {
            var rect = MapEffectiveGeometry.Bounds(MapEffectiveGeometry.CellPolygon(_view, opening.Patch, slot));
            if (rect.MinX.CompareTo(max.X) >= 0 || rect.MaxX.CompareTo(min.X) <= 0 ||
                rect.MinZ.CompareTo(max.Z) >= 0 || rect.MaxZ.CompareTo(min.Z) <= 0) continue;
            int faces = MapBoundFaceContext.OpeningFaceCount(patch, slot);
            WaterMeter?.ChargeRead(faces);
            WaterMeter?.CountFaces(!lower, faces);
            count = checked(count + faces);
            if (_work is not null) _work.BoundFacesCounted += faces;
            if (count > _limits.MaxSourceFacesPerBound) return Failure(MapRefinementStatus.CapacityExceeded, "source faces",
                SourceCapacity(footprint, bound, "source faces", count) with
                { RelatedRecordId = reference.Id, SurfaceId = surface.Id, Patch = opening.Patch, SlotCell = slot });
            demands.Add(new(opening.Patch, slot));
        }
        return new(MapRefinementStatus.Complete, demands.AsReadOnly(), Array.Empty<MapLegacyCellTag>(), reference, null);
    }

    bool WithinCellBudget(MapCellRect range, out long? measured)
    {
        try
        {
            long count = checked(checked(range.MaxXExclusive - range.MinX) * checked(range.MaxZExclusive - range.MinZ));
            measured = count;
            return count <= _limits.MaxSourceFacesPerBound;
        }
        catch (OverflowException) { measured = null; return false; }
    }

    void WaterMeterCharge(string surface, MapExactXz min, MapExactXz max)
    {
        if (WaterMeter is { } meter) MapWaterBoundReads.ChargeTraversal(_view, surface, min, max, meter);
    }

    static IEnumerable<(long X, long Z)> Cells(MapCellRect range)
    {
        for (long z = range.MinZ; z < range.MaxZExclusive; z++)
            for (long x = range.MinX; x < range.MaxXExclusive; x++) yield return (x, z);
    }

    internal MapValidatedSurfacePatch? ValidatedPatch(MapSurfaceRef surface, MapSurfacePatch patch)
    {
        if (_context is not null) return _context.ValidatedPatch(patch.Key);
        if (_validated.TryGetValue(patch.Key, out MapValidatedSurfacePatch? validated)) return validated;
        if (_validated.Count >= _limits.MaxContextPatches)
        {
            Refusal = "context patches";
            Finding = new(MapDocumentRefusal.WorkCapacityExceeded, Refusal)
            { SurfaceId = surface.Id, Patch = patch.Key, Budget = "MaxContextPatches", ActualCount = (long)_validated.Count + 1,
                Limit = _limits.MaxContextPatches };
            return null;
        }
        validated = new(surface, patch, _validationWork);
        // Count every validated bound patch before retaining evidence, including face-free bounds.
        _validated.Add(patch.Key, validated);
        return validated;
    }
    MapSurfaceRef? Surface(string? id) => _view.Surfaces.FirstOrDefault(s => s.Id == id);
    internal void RecordContextCapacity(string budget, long? count, long limit, MapPatchKey patch) =>
        Finding ??= new(MapDocumentRefusal.WorkCapacityExceeded, "context faces")
        { Budget = budget, ActualCount = count, Limit = limit, SurfaceId = patch.SurfaceId, Patch = patch };
    static Plan ClassificationFailure(MapLowerCellClassification classification) => Failure(
        classification.Class == MapLowerCellClass.InvalidRecipe ? MapRefinementStatus.Invalid : MapRefinementStatus.MissingGeometry,
        classification.Detail, classification.Finding);
    static Plan Failure(MapRefinementStatus status, string? detail, MapDocumentFinding? finding) =>
        new(status, Array.Empty<MapCellDemand>(), Array.Empty<MapLegacyCellTag>(), null, detail) { Finding = finding };

    static MapDocumentFinding Context(MapSpaceFootprint footprint, MapBoundRef bound, MapDocumentRefusal reason, string detail) =>
        new(reason, detail) { RecordId = footprint.Id, SpaceId = footprint.Space.Id, SurfaceId = bound.SurfaceId, Patch = footprint.Lattice };
    MapDocumentFinding SourceCapacity(MapSpaceFootprint footprint, MapBoundRef bound, string detail, long? count) =>
        Context(footprint, bound, MapDocumentRefusal.WorkCapacityExceeded, detail) with
        { Budget = "MaxSourceFacesPerBound", ActualCount = count, Limit = _limits.MaxSourceFacesPerBound };
}
