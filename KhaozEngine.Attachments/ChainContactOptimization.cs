using System;

namespace KhaozEngine.Attachments;

[Flags]
internal enum ChainContactOptimization
{
    OpeningTriangle = 1 << 0,
    BandCandidates = 1 << 1,
    FixedEdge = 1 << 2,
    CleanBands = 1 << 3,
    CurrentTriangle = 1 << 4,
    VelocityTriangle = 1 << 5,
    TriangleCull = 1 << 6,
    SegmentCull = 1 << 7,
    OpeningStamp = 1 << 8,
    Diagonal = 1 << 9,
    FaceParameter = 1 << 10,
    AxisCull = 1 << 11,
    RoundingMargin = 1 << 12,
    RoundingReuse = 1 << 13,
    PerTriangleRevision = 1 << 14,
    FirstPassCandidates = 1 << 15,
    BendCosine = 1 << 16,
    All = (1 << 17) - 1,
    PassTolerance = 1 << 17,
    ThickSheet = 1 << 18,
}

internal static class ChainContactOptimizations
{
    [ThreadStatic]
    internal static ChainContactOptimization DisabledForTests;

    internal static bool Use(ChainContactOptimization optimization) => (DisabledForTests & optimization) == 0;
}
