namespace KhaozEngine.Render3D;

/// <summary>Explicit presentation on a late coverage refusal.</summary>
public enum CoverageFallback { UncoveredCurrentPose }

/// <summary>Lifecycle of a staged coverage request.</summary>
public enum CoveragePresentation { Pending, Covered, UncoveredFallback, Dispatched = 3 }

/// <summary>Developer result of the owning scene's one presentation decision.</summary>
public readonly record struct CoverageFrameOutcome(CoveragePresentation Presentation, CoverageFailure Failure);

/// <summary>Opaque scene, frame, actor and preparation identity. Staging is not drawable certification.</summary>
public readonly struct SkinnedCoverageFrame
{
    internal SkinnedCoverageFrame(CoveragePreparation preparation, ulong generation)
    { Preparation = preparation; Generation = generation; }
    internal CoveragePreparation? Preparation { get; }
    internal ulong Generation { get; }
}

/// <summary>Opaque evidence for one frozen material slice of one equipment instance in one frame.</summary>
public readonly struct CoverageEquipmentDraw
{
    internal CoverageEquipmentDraw(CoverageEquipmentStage stage, ulong generation, int slice)
    { Stage = stage; Generation = generation; Slice = slice; }
    internal CoverageEquipmentStage? Stage { get; }
    internal ulong Generation { get; }
    internal int Slice { get; }
}
