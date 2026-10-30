namespace KhaozEngine.Windowing;

public readonly record struct ShadowStageProbeQueryCounts(int Calls, long ExactTests, long Accepted, long SortInputs);

// Mutable only while scene-owned. Every bridge and saved sample copies this value.
public record struct ShadowStageProbeCounts
{
    public bool HasPointRequests { get; set; }
    public bool PointAtlasAvailable { get; set; }
    public bool DirectionalActive { get; set; }
    public bool DirectionalRendered { get; set; }
    public bool DirectionalSkipped { get; set; }
    public bool RigidIndexBuilt { get; set; }
    public bool FoliageIndexBuilt { get; set; }
    public int StaticRequests { get; set; }
    public int DynamicRequests { get; set; }
    public int TouchAttempts { get; set; }
    public int TouchHits { get; set; }
    public int AcquireAttempts { get; set; }
    public int AcquireSuccesses { get; set; }
    public int AcquireRefusals { get; set; }
    public int Signatures { get; set; }
    public int SignatureMismatches { get; set; }
    public int SelectedStaticRows { get; set; }
    public int SelectedDynamicRows { get; set; }
    public int SelectedTransientRows { get; set; }
    public int PointSlotsInUse { get; set; }
    public int PointShadowedLights { get; set; }
    public int PackedRows { get; set; }
    public int CompletedStaticRebuilds { get; set; }
    public int CompletedDynamicRenders { get; set; }
    public int TransientDemand { get; set; }
    public int PointFaceDrawCalls { get; set; }
    public int PointFoliageFaceDrawCalls { get; set; }
    public int PointDynamicSkinnedFaceDrawCalls { get; set; }
    public int PointTransientSkinnedFaceDrawCalls { get; set; }
    public int RigidIndexPopulation { get; set; }
    public int RigidOversizePopulation { get; set; }
    public int FoliageSources { get; set; }
    public int FoliageRanges { get; set; }
    public int FoliageSelectedProxies { get; set; }
    public int DirectionalCascadeCount { get; set; }
    public int DirectionalInvalidationCasters { get; set; }
    public int DirectionalMaskTests { get; set; }
    public int DirectionalSelectedSpans { get; set; }
    public int DirectionalSelectedInstances { get; set; }
    public int DirectionalRigidDrawCalls { get; set; }
    public int DirectionalSkinnedDrawCalls { get; set; }
    public int DirectionalFoliageDrawCalls { get; set; }
    public long PointRigidIssuedInstances { get; set; }
    public long PointFoliageIssuedInstances { get; set; }
    public long FoliageStreamProxies { get; set; }
    public ShadowStageProbeQueryCounts RigidSignatureQueries { get; set; }
    public ShadowStageProbeQueryCounts RigidRowQueries { get; set; }
    public ShadowStageProbeQueryCounts FoliageSignatureQueries { get; set; }
    public ShadowStageProbeQueryCounts FoliageRowQueries { get; set; }
    public ShadowStageFoliageBuildOrigin FoliageBuildOrigin { get; set; }
}
