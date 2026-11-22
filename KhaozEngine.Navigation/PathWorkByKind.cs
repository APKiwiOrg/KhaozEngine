namespace KhaozEngine.Navigation;

/// <summary>Paid operations across search, profile admission and straightening. Indexed only by their test recorder.</summary>
internal enum PathWorkKind
{
    Layer,
    Snap,
    MemberBox,
    Member,
    CertificateLabel,
    CertificateProgress,
    Wrap,
    Seed,
    Pop,
    SearchProgress,
    Neighbor,
    Link,
    Parent,
    Reverse,
    Decode,
    Allocate,
    Copy,
    EndpointStart,
    FootprintRow,
    FootprintColumn,
    EndpointLayer,
    SegmentStart,
    CellCrossing,
    ValidateFeet,
    Candidate,
    SideOffsets,
    KeepAnchor,
    StraighteningAllocate,
    StraighteningCopy,
    Count,
}

/// <summary>Caller-owned test counters. Reuse the same instance for every measured advance of one request.</summary>
internal sealed class PathWorkByKind
{
    readonly long[] _units = new long[(int)PathWorkKind.Count];

    internal long this[PathWorkKind kind] => _units[(int)kind];

    internal long Total
    {
        get
        {
            long total = 0;
            foreach (long units in _units) total += units;
            return total;
        }
    }

    internal void Add(PathWorkKind kind, int units) => _units[(int)kind] += units;
}
