namespace KhaozEngine.Windowing;

// Null means the boundary did not execute. A measured zero remains a valid duration.
public record struct ShadowStageProbeTicks
{
    public long Frequency { get; set; }
    public int TimestampReads { get; set; }
    public long? ScenePrefix { get; set; }
    public long? PointRequests { get; set; }
    public long? PointPreparation { get; set; }
    public long? RigidIndexBuild { get; set; }
    public long? SlotLookup { get; set; }
    public long? StaticSignatures { get; set; }
    public long? RigidSignatureQuery { get; set; }
    public long? RigidSignatureFold { get; set; }
    public long? FoliageIndexBuild { get; set; }
    public long? FoliageSignatureQuery { get; set; }
    public long? FoliageSignatureFold { get; set; }
    public long? RowSelection { get; set; }
    public long? RowPreparation { get; set; }
    public long? RowRecording { get; set; }
    public long? RigidRowQuery { get; set; }
    public long? FoliageRowQuery { get; set; }
    public long? ReceiverPublication { get; set; }
    public long? CascadeFit { get; set; }
    public long? DirectionalPreparation { get; set; }
    public long? DirectionalSelection { get; set; }
    public long? DirectionalRecording { get; set; }
    public long? DirectionalCommit { get; set; }
}
