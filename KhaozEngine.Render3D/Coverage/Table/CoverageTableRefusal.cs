namespace KhaozEngine.Render3D;

/// <summary>Stable recession table format 1 refusal reasons.</summary>
public enum CoverageTableRefusal
{
    /// <summary>The four-byte magic is not KERT.</summary>
    InvalidMagic,
    /// <summary>The unsigned format version is not 1.</summary>
    UnsupportedVersion,
    /// <summary>An identity member is not 64 lowercase hexadecimal characters.</summary>
    MalformedDigest,
    /// <summary>At least one position group is required.</summary>
    ZeroGroups,
    /// <summary>The programmatic scalar array ends in an incomplete entry row.</summary>
    IncompleteRows,
    /// <summary>A count or checked payload length exceeds the managed binary asset capacity.</summary>
    LengthOverflow,
    /// <summary>The input ends before the header or its declared payload is complete.</summary>
    Truncated,
    /// <summary>Bytes follow the complete declared payload.</summary>
    TrailingData,
    /// <summary>A representative is below -1. Topology-dependent upper bounds belong to host binding.</summary>
    InvalidRepresentative,
    /// <summary>A recession word encodes infinity or NaN.</summary>
    NonFiniteRecession,
    /// <summary>A recession word is negative and nonzero, including a negative subnormal.</summary>
    NegativeRecession,
    /// <summary>A state key appears more than once.</summary>
    DuplicateKey,
    /// <summary>Binary state records are not in strictly increasing unsigned-key order.</summary>
    UnsortedKeys,
    /// <summary>A state entry index is outside [-1, EntryCount).</summary>
    InvalidEntryIndex,
}

/// <summary>Malformed recession table data with structured rule and record context.</summary>
public sealed class CoverageTableDataException : MalformedDataException
{
    /// <summary>Create a refusal with a developer-facing message.</summary>
    public CoverageTableDataException(CoverageTableRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The stable rule that refused the data.</summary>
    public CoverageTableRefusal Reason { get; }
    /// <summary>Caller-supplied binary asset identity. Null for programmatic creation.</summary>
    public string? Identity { get; init; }
    /// <summary>Zero-based offending field offset, first missing byte or first trailing byte. Null without binary input.</summary>
    public int? ByteOffset { get; init; }
    /// <summary>Refused identity member, or count member for a capacity refusal.</summary>
    public string? MemberName { get; init; }
    /// <summary>Refused unsigned wire version.</summary>
    public uint? FormatVersion { get; init; }
    /// <summary>Position group containing the invalid representative or scalar, or the first missing scalar.</summary>
    public int? GroupIndex { get; init; }
    /// <summary>Scalar row index, or offending state record's entry value.</summary>
    public int? EntryIndex { get; init; }
    /// <summary>Offending state's opaque key, when available in full.</summary>
    public CoverageStateKey? Key { get; init; }
    /// <summary>State record index. Programmatic creation reports the canonical sorted index.</summary>
    public int? StateIndex { get; init; }
    /// <summary>Offending representative value.</summary>
    public int? Representative { get; init; }
    /// <summary>Exact invalid binary32 scalar word.</summary>
    public uint? ScalarWord { get; init; }
    /// <summary>Refused count or computed byte length, when representable.</summary>
    public long? Count { get; init; }
    /// <summary>Inclusive managed asset capacity for a length refusal.</summary>
    public long? Limit { get; init; }
}
