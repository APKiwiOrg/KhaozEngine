using System;

namespace KhaozEngine.Attachments;

internal enum ChainContactQuery
{
    SegmentPlacement, SegmentOpening, SegmentVelocity, RoundingRecheck, FixedEdge,
    SheetCurrent, SheetOpening, SheetVelocity, SheetDiagonal, FaceBarrelResidual,
    BandFilter, BandVisit, OpeningSlotsCleared, ChainReach, BendCosines, FirstPassReuses, ThickBandSkips, ThickEdgeQueries, WidthSafety, ConflictCheck,
}

internal sealed class ChainContactCounts
{
    private readonly long[] _counts = new long[(int)ChainContactQuery.ConflictCheck + 1];

    private readonly long[] _segmentPasses = new long[ChainSegmentContact.ContactPasses + 1];
    private readonly long[] _bandPasses = new long[ChainSheetContact.MaxPasses + 1];

    internal ReadOnlySpan<long> SegmentPasses => _segmentPasses;
    internal ReadOnlySpan<long> BandPasses => _bandPasses;

    internal void RecordSegmentPasses(int passes) => _segmentPasses[passes]++;
    internal void RecordBandPasses(int visits) => _bandPasses[visits]++;

    [ThreadStatic]
    internal static ChainContactCounts? Active;

    internal long this[ChainContactQuery kind] => _counts[(int)kind];

    internal long SegmentAxis => this[ChainContactQuery.SegmentPlacement] + this[ChainContactQuery.SegmentOpening] +
        this[ChainContactQuery.SegmentVelocity] + this[ChainContactQuery.RoundingRecheck] +
        this[ChainContactQuery.FixedEdge] + this[ChainContactQuery.ThickEdgeQueries] + this[ChainContactQuery.WidthSafety] + this[ChainContactQuery.ConflictCheck];

    internal long TriangleAxis => this[ChainContactQuery.SheetCurrent] + this[ChainContactQuery.SheetOpening] +
        this[ChainContactQuery.SheetVelocity];

    internal static void Record(ChainContactQuery kind, int count = 1)
    {
        if (Active is { } active)
            active._counts[(int)kind] += count;
    }
}
