using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Closed geometry producer categories. Consumers retain their own predicate order and status projection.</summary>
internal static class MapGeometryRefusals
{
    internal static bool Representability(MapDocumentException error, bool arithmetic = true) =>
        arithmetic && MapCommonRefinement.NotRepresentable(error) ||
        error.RefinementStatus == MapRefinementStatus.NotRepresentable ||
        error is { Reason: MapDocumentRefusal.ValueNonfinite or MapDocumentRefusal.ValueOutsideRange, Operation: nameof(MapResourceTransform.Freeze) } ||
        error.Reason == MapDocumentRefusal.GeometryNotRepresentable && !PreserveNonArithmeticProjection(error) && error.Operation is not
            (nameof(MapEffectiveGeometry.RefinePolygonWide) or nameof(MapEffectiveGeometry.Normal) or "paint intersection");

    internal static bool Capacity(MapDocumentException error) =>
        error.RefinementStatus == MapRefinementStatus.CapacityExceeded ||
        error is { Reason: MapDocumentRefusal.WorkCapacityExceeded, Budget: "WaterDomainPairChecks" } ||
        error is { RefinementStatus: null, Reason: MapDocumentRefusal.WorkCapacityExceeded,
            Budget: "MaxRecordReads" or "MaxRecordDepth" or "MaxInspectedFaces" or "MaxRefinementFaces" or
                "MaxRefinementVertices" };

    internal static bool MissingGeometry(MapDocumentException error) =>
        error.RefinementStatus is { } status ? status is not (MapRefinementStatus.CapacityExceeded or MapRefinementStatus.NotRepresentable)
            : error.Operation is not (nameof(MapBoundFaceContext.PrepareBounds) or nameof(MapEffectiveGeometry.Patch)) &&
                (error.Reason is MapDocumentRefusal.PatchUnavailable or MapDocumentRefusal.ReferenceMissing or MapDocumentRefusal.AcquisitionIncomplete or
                    MapDocumentRefusal.SpacePrismParentMismatch or MapDocumentRefusal.SpacePrismParentFootprintMissing or MapDocumentRefusal.SupportSpaceMissing ||
                    error is { Reason: MapDocumentRefusal.ReferenceKindMismatch, SubjectKind: not "aperture" });

    internal static bool Invalid(MapDocumentException error) =>
        error.ResolutionStatus == MapResolveStatus.Invalid || error.Reason is
            MapDocumentRefusal.GeometryVertexSequenceInvalid or MapDocumentRefusal.SpaceAliasCycle or MapDocumentRefusal.SpaceParentCycle or
            MapDocumentRefusal.SpacePrismParentCycle or MapDocumentRefusal.SpacePrismSeparationInvalid or MapDocumentRefusal.SpacePortalHeightInvalid or
            MapDocumentRefusal.SpacePortalOpenTopInvalid ||
        error is { Reason: MapDocumentRefusal.ReferenceKindMismatch, SubjectKind: "aperture" };

    internal static bool InvalidPrefix(MapDocumentException error) => !error.InvalidPrefixSuppressed &&
        error.ResolutionStatus != MapResolveStatus.MissingGeometry &&
        (error.ResolutionStatus == MapResolveStatus.Invalid || error.RefinementStatus is null && Invalid(error));

    // These support wrappers originally retained neither an arithmetic cause nor a representability phrase.
    internal static bool PreserveNonArithmeticProjection(MapDocumentException error) => error is
        { Reason: MapDocumentRefusal.GeometryNotRepresentable, MarkerId: not null, Operation: "Sample" } or
        { Reason: MapDocumentRefusal.GeometryNotRepresentable, PlacementId: not null, Operation: "SupportHeight" };

    internal static bool FoliageCapacity(MapDocumentException error) =>
        error is { Reason: MapDocumentRefusal.WorkCapacityExceeded, Budget: "MaxFacesPerPatch" };

    internal static bool FoliageUnavailable(MapDocumentException error) =>
        error.Operation == nameof(MapBoundFaceContext.PrepareBounds) ||
        error is { Reason: MapDocumentRefusal.PatchUnavailable, RecordId: null, Operation: not nameof(MapEffectiveGeometry.Patch) } ||
        error.Reason == MapDocumentRefusal.FoliageChunkUnavailable ||
        error is { Reason: MapDocumentRefusal.AcquisitionIncomplete, AcquireStatus: MapAcquireStatus.Incomplete } or
            { Reason: MapDocumentRefusal.AcquisitionIncomplete, FieldPath: "Scope.Roles" } or
            { Reason: MapDocumentRefusal.AcquisitionContractMismatch, FieldPath: "Patch" or "Unavailable.Patch" } or
            { Reason: MapDocumentRefusal.SchemaEnumUnsupported, FieldPath: "Unavailable.Status" };
}
