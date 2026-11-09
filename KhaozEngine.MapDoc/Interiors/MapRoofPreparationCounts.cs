using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Count-only construction evidence. Contains no source identities, geometry or policy payloads.</summary>
public sealed class MapRoofPreparationCounts
{
    /// <summary>Borrowed-input controls only. Later owners admit their own stage capacities.</summary>
    public long HeaderBytes { get; internal set; }
    public bool CompleteAggregatePredictionAvailable => false;
    public MapPreparationStageCounts Membership { get; } = new();
    public MapPreparationStageCounts Header { get; } = new();
    public MapPreparationStageCounts Validation { get; } = new();
    public MapPreparationStageCounts Planner { get; } = new();
    public MapPreparationStageCounts Construction { get; } = new();
    public MapPreparationStageCounts Publication { get; } = new();
    public long UniqueTargetSlots { get; internal set; }
    public long FootprintTargetSlots { get; internal set; }
    public long IndoorTargetSlots { get; internal set; }
    public long PortalTargetSlots { get; internal set; }
    public long FaceFreeTargetSlots { get; internal set; }
    public long PhysicalFaces { get; internal set; }
    public long SpanFaces { get; internal set; }
    public long ChartFaces { get; internal set; }
    public long OpeningFaces { get; internal set; }
    public long BoundAssociations { get; internal set; }
    public long FailedBoundAssociations { get; internal set; }
    public long PlannerBytes { get; internal set; }
    public long PeakPatchBytes { get; internal set; }
    public long PeakCompilerBytes { get; internal set; }
    public long NormalizedCoefficientBytes { get; internal set; }
    public long Coefficients { get; internal set; }
    public long Polygons { get; internal set; }
    public long PolygonCorners { get; internal set; }
    public long Triangles { get; internal set; }
    public long SpanTriangles { get; internal set; }
    public long LegacyTriangles { get; internal set; }
    public long OpeningTriangles { get; internal set; }
    public long PortalTriangles { get; internal set; }
    public long UniqueCellProducts { get; internal set; }
    public long UniqueFootprintPolygons { get; internal set; }
    public long CoefficientPoolPages { get; internal set; }
    public long CoefficientPoolCapacity { get; internal set; }
    public long SideTableRows { get; internal set; }
    public long SideTableBytes { get; internal set; }
    public long FootprintCacheHits { get; internal set; }
    public long FrozenBoundRows { get; internal set; }
    public long FrozenBoundCapacityBytes { get; internal set; }
    public long SharedCapacityBytes { get; internal set; }
    public long PrivateCapacityBytes { get; internal set; }
    public long FaceKeyRows { get; internal set; }
    public long GeometryArenaPages { get; internal set; }
    public long GeometryArenaCapacityBytes { get; internal set; }
    public long ValidationRawPieces { get; internal set; }
    public long ValidationVertices { get; internal set; }
    public long PeakValidationCandidates { get; internal set; }
    public long PeakValidationUncovered { get; internal set; }
    public long ValidationPeakBytes { get; internal set; }
    public long RetainedBytes { get; internal set; }
}
