using System;

namespace KhaozEngine.Windowing;

// Private archive bridge. Numeric modes are part of schema 3.
public enum ShadowStageProbeMode { Off = 0, Counts = 1, Timings = 2 }
public enum ShadowStageProbeStatus
{
    Off, Valid, IncompleteCallback, MissingProducer, NotRendered, DuplicateProducer,
    IdentityMismatch, ModeMismatch, InvalidClock, ConfigurationChanged,
}
public enum ShadowStageFoliageBuildOrigin { None, Signature, RowPreparation, RowRecording }

public readonly record struct ShadowStageProbeRequest(Guid Epoch, long Sequence, ShadowStageProbeMode Mode);

public readonly record struct ShadowStageProbeConfiguration(bool HudTiming, int DirectionalMode,
    bool TerrainCastsShadows, bool CascadeCulling, bool GpuSkinning, int CascadeMergeGap,
    int DirectionalResolution, int CascadeCount, bool PointEnabled, int PointFaceResolution,
    int PointRows, int TransientRows, int StaticBudget, int DynamicBudget, int PointFilter);

public readonly record struct ShadowStageProbeSnapshot(ShadowStageProbeRequest Request,
    ShadowStageProbeStatus Status, ShadowStageProbeConfiguration Configuration,
    ShadowStageProbeCounts? Counters, ShadowStageProbeTicks? Timings);

internal readonly record struct ShadowStageCallbackResult(ShadowStageProbeRequest Request,
    bool Completed, int ProducerCount, ShadowStageProbeSnapshot? Snapshot);

internal readonly record struct ShadowStageProbeRow(ShadowStageProbeRequest Request,
    ShadowStageProbeStatus Status, ShadowStageCallbackResult Callback);
