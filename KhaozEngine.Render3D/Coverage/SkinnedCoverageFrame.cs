using System;

namespace KhaozEngine.Render3D;

/// <summary>Opaque scene, frame, actor and preparation identity. Staging alone never presents a surface.</summary>
public readonly struct SkinnedCoverageFrame
{
    internal SkinnedCoverageFrame(CoveragePreparation preparation, ulong generation, ulong revision)
    { Preparation = preparation; Generation = generation; Revision = revision; }
    internal CoveragePreparation? Preparation { get; }
    internal ulong Generation { get; }
    internal ulong Revision { get; }
}

/// <summary>Opaque evidence for a frozen material slice of one rigid equipment part.</summary>
public readonly struct CoverageEquipmentDraw
{
    internal CoverageEquipmentDraw(CoverageEquipmentStage stage, ulong generation, ulong revision, int slice)
    { Stage = stage; Generation = generation; Revision = revision; Slice = slice; }
    internal CoverageEquipmentStage? Stage { get; }
    internal ulong Generation { get; }
    internal ulong Revision { get; }
    internal int Slice { get; }
}

/// <summary>Refuses a foreign, stale or changed coverage descriptor before any queue or palette write.</summary>
public sealed class CoverageFrameStateException : InvalidOperationException
{
    public CoverageFrameStateException(CoverageFailure failure) : base("Coverage descriptor does not match its live frozen preparation.")
    {
        if (failure.IsSuccess) throw new ArgumentException("A refusal requires a failure.", nameof(failure));
        Failure = failure;
    }
    public CoverageFailure Failure { get; }
    public CoverageFailureCode Reason => Failure.Code;
}
