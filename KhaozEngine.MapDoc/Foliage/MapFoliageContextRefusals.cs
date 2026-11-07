using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Retains the generation context's status projection beside its concrete producer findings.</summary>
internal static class MapFoliageContextRefusals
{
    internal static MapFoliageStatus? Classify(Exception error) => error switch
    {
        MapSurfaceCapacityException => MapFoliageStatus.CapacityExceeded,
        MapExactOverflowException or OverflowException => MapFoliageStatus.NotRepresentable,
        MapDocumentException when MapCommonRefinement.NotRepresentable(error) => MapFoliageStatus.NotRepresentable,
        MapDocumentException { Operation: nameof(MapPrefabCapacity) } => MapFoliageStatus.CapacityExceeded,
        MapDocumentException { Reason: MapDocumentRefusal.WorkCapacityExceeded,
            Operation: nameof(MapPrefabPreflight.Count), Budget: not "PrefabExpansionCount" } => MapFoliageStatus.CapacityExceeded,
        // Consumer callbacks can still throw a message-only refusal. Its released projection remains available.
        MapDocumentException { Reason: null, Message: var message } when message.Contains("limit exceeded", StringComparison.Ordinal) =>
            MapFoliageStatus.CapacityExceeded,
        MapDocumentException => MapFoliageStatus.Invalid,
        _ => null,
    };

    internal static MapFoliageContextResult FromException(Exception error, MapFoliageStatus status, MapFoliageContextWork work)
    {
        MapDocumentFinding? finding = error is MapDocumentException document ? MapDocumentFinding.FromException(document)
            : new(status == MapFoliageStatus.CapacityExceeded ? MapDocumentRefusal.WorkCapacityExceeded
                : MapDocumentRefusal.GeometryNotRepresentable, error.Message) { Operation = "FoliageContext" };
        return new(status, null, work)
        {
            Finding = finding is null ? null : finding with { FoliageStatus = status },
            Findings = (error as MapDocumentException)?.Findings,
            Cause = error,
        };
    }

    internal static MapFoliageContextResult FromFindings(IReadOnlyList<MapDocumentFinding> findings, MapFoliageContextWork work) =>
        new(MapFoliageStatus.Invalid, null, work)
        {
            Finding = findings[0] with { FoliageStatus = MapFoliageStatus.Invalid }, Findings = findings,
        };

    internal static MapFoliageContextResult SnapshotChanged(MapDocument document, MapFoliageContextWork work,
        string field, string? expected = null, string? actual = null) => new(MapFoliageStatus.Invalid, null, work)
        {
            Finding = new(MapDocumentRefusal.AcquisitionSnapshotChanged, "the foliage generation context refused this document")
            {
                DocumentId = document.Id, FieldPath = field, ExpectedSnapshot = expected, ActualSnapshot = actual,
                FoliageStatus = MapFoliageStatus.Invalid, Operation = "FoliageContext",
            },
        };
}
