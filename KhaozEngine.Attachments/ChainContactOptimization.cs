using System;

namespace KhaozEngine.Attachments;

[Flags]
internal enum ChainContactOptimization
{
    SegmentCull = 1 << 0,
    OpeningStamp = 1 << 1,
    AxisCull = 1 << 2,
    RoundingMargin = 1 << 3,
    RoundingReuse = 1 << 4,
    FirstPassCandidates = 1 << 5,
    BendCosine = 1 << 6,
    PassTolerance = 1 << 7,
    WidthNearSolids = 1 << 8,
    SheetOpening = 1 << 9,
    All = SegmentCull | OpeningStamp | AxisCull | RoundingMargin | RoundingReuse | FirstPassCandidates | BendCosine | WidthNearSolids | SheetOpening,
}

internal static class ChainContactOptimizations
{
    [ThreadStatic]
    internal static ChainContactOptimization DisabledForTests;

    internal static bool Use(ChainContactOptimization optimization) => Use(DisabledForTests, optimization);

    internal static bool Use(ChainContactOptimization disabled, ChainContactOptimization optimization) =>
        (disabled & optimization) == 0;
}
