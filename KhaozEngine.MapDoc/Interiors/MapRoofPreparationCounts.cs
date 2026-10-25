namespace KhaozEngine.MapDoc.Interiors;

/// <summary>Count-only construction evidence. Contains no source identities, geometry or policy payloads.</summary>
public sealed class MapRoofPreparationCounts
{
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
    public long ValidationRawPieces { get; internal set; }
    public long ValidationVertices { get; internal set; }
    public long PeakValidationCandidates { get; internal set; }
    public long PeakValidationUncovered { get; internal set; }
    public long ValidationPeakBytes { get; internal set; }
    public long RetainedBytes { get; internal set; }
}
