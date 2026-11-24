using System;

namespace KhaozEngine.Attachments;

internal enum ChainContactQuery
{
    SegmentPlacement, SegmentOpening, SegmentVelocity, RoundingRecheck, FixedEdge,
    SheetCurrent, SheetOpening, SheetVelocity, SheetDiagonal, FaceBarrelResidual,
    BandFilter, BandVisit, OpeningSlotsCleared,
}

internal sealed class ChainContactCounts
{
    private readonly long[] _counts = new long[(int)ChainContactQuery.OpeningSlotsCleared + 1];

    [ThreadStatic]
    internal static ChainContactCounts? Active;

    internal long this[ChainContactQuery kind] => _counts[(int)kind];

    internal long SegmentAxis => this[ChainContactQuery.SegmentPlacement] + this[ChainContactQuery.SegmentOpening] +
        this[ChainContactQuery.SegmentVelocity] + this[ChainContactQuery.RoundingRecheck] + this[ChainContactQuery.FixedEdge];

    internal long TriangleAxis => this[ChainContactQuery.SheetCurrent] + this[ChainContactQuery.SheetOpening] +
        this[ChainContactQuery.SheetVelocity];

    internal static void Record(ChainContactQuery kind, int count = 1)
    {
        if (Active is { } active)
            active._counts[(int)kind] += count;
    }
}
