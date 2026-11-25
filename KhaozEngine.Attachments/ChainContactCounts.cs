using System;

namespace KhaozEngine.Attachments;

internal enum ChainContactQuery
{
    SegmentPlacement, SegmentOpening, SegmentVelocity, RoundingRecheck,
    OpeningSlotsCleared, ChainReach, BendCosines, FirstPassReuses, WidthSafety, ConflictCheck,
    BandFilter, BandCandidate, SheetCurrent, SheetOpening, SheetVelocity, FixedEdge, SheetCorrections, InteriorTailCorrections,
}

internal sealed class ChainContactCounts
{
    private readonly long[] _counts = new long[(int)ChainContactQuery.InteriorTailCorrections + 1];

    private readonly long[] _segmentPasses = new long[ChainSegmentContact.ContactPasses + 1];

    internal ReadOnlySpan<long> SegmentPasses => _segmentPasses;

    internal void RecordSegmentPasses(int passes) => _segmentPasses[passes]++;

    [ThreadStatic]
    internal static ChainContactCounts? Active;

    internal long this[ChainContactQuery kind] => _counts[(int)kind];

    internal long SegmentAxis => this[ChainContactQuery.SegmentPlacement] + this[ChainContactQuery.SegmentOpening] +
        this[ChainContactQuery.SegmentVelocity] + this[ChainContactQuery.RoundingRecheck] +
        this[ChainContactQuery.WidthSafety] + this[ChainContactQuery.ConflictCheck];

    internal void Record(ChainContactQuery kind, int count = 1) => _counts[(int)kind] += count;
}
