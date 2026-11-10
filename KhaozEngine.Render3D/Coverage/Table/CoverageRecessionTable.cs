using System;

namespace KhaozEngine.Render3D;

/// <summary>Immutable offline-verified recession rows sharing one representative array and a sorted state index.</summary>
public sealed class CoverageRecessionTable
{
    readonly int[] representatives;
    readonly float[] recessions;
    readonly CoverageStateEntry[] states;

    // Only Create and the strict binary reader may transfer newly owned, validated arrays here.
    internal CoverageRecessionTable(CoverageRecessionIdentity identity, int[] representatives,
        float[] recessions, CoverageStateEntry[] states)
    {
        Identity = identity;
        this.representatives = representatives;
        this.recessions = recessions;
        this.states = states;
    }

    public CoverageRecessionIdentity Identity { get; }
    public int PositionGroupCount => representatives.Length;
    public int EntryCount => recessions.Length / representatives.Length;
    public ReadOnlySpan<int> Representatives => representatives;
    public ReadOnlySpan<CoverageStateEntry> States => states;

    /// <summary>Validate and snapshot all input spans. State records are canonically sorted by unsigned key.</summary>
    /// <remarks>Zero entries and empty state inventories are allowed. Representative upper bounds require actual host topology.</remarks>
    public static CoverageRecessionTable Create(CoverageRecessionIdentity identity, ReadOnlySpan<int> representatives,
        ReadOnlySpan<float> entryMajorRecessions, ReadOnlySpan<CoverageStateEntry> states)
    {
        ValidateDigest(identity.HostSha256, nameof(identity.HostSha256));
        ValidateDigest(identity.HostMapSha256, nameof(identity.HostMapSha256));
        ValidateDigest(identity.ConfigurationSha256, nameof(identity.ConfigurationSha256));
        ValidateDigest(identity.RefinementSha256, nameof(identity.RefinementSha256));
        ValidateDigest(identity.ArithmeticSha256, nameof(identity.ArithmeticSha256));
        ValidateDigest(identity.StateSchemaSha256, nameof(identity.StateSchemaSha256));
        if (representatives.IsEmpty)
            throw new CoverageTableDataException(CoverageTableRefusal.ZeroGroups, "A table requires position groups.");
        if (entryMajorRecessions.Length % representatives.Length != 0)
            throw new CoverageTableDataException(CoverageTableRefusal.IncompleteRows, "The scalar array contains an incomplete row.")
            { GroupIndex = entryMajorRecessions.Length % representatives.Length, EntryIndex = entryMajorRecessions.Length / representatives.Length };
        int entryCount = entryMajorRecessions.Length / representatives.Length;
        CoverageRecessionTableBinary.GetByteLength(representatives.Length, entryCount, states.Length);
        for (int group = 0; group < representatives.Length; group++)
            ValidateRepresentative(representatives[group], group);
        for (int i = 0; i < entryMajorRecessions.Length; i++)
            ValidateScalar(BitConverter.SingleToUInt32Bits(entryMajorRecessions[i]), i % representatives.Length, i / representatives.Length);

        CoverageStateEntry[] ownedStates = states.ToArray();
        Array.Sort(ownedStates, static (a, b) => a.Key.Value.CompareTo(b.Key.Value));
        for (int i = 0; i < ownedStates.Length; i++)
            ValidateState(ownedStates[i], i == 0 ? null : ownedStates[i - 1].Key, entryCount, i);
        return new(identity, representatives.ToArray(), entryMajorRecessions.ToArray(), ownedStates);
    }

    /// <summary>Borrow one immutable scalar row. An invalid entry index is a caller parameter error.</summary>
    public ReadOnlySpan<float> Entry(int entryIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, EntryCount);
        return recessions.AsSpan(entryIndex * representatives.Length, representatives.Length);
    }

    /// <summary>Select an entry without allocation. Missing keys and explicit holes both return false and -1.</summary>
    public bool TrySelect(CoverageStateKey key, out int entryIndex)
    {
        entryIndex = -1;
        int low = 0, high = states.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            CoverageStateEntry state = states[middle];
            if (state.Key.Value < key.Value) low = middle + 1;
            else if (state.Key.Value > key.Value) high = middle - 1;
            else
            {
                entryIndex = state.EntryIndex;
                return entryIndex >= 0;
            }
        }
        return false;
    }

    static void ValidateDigest(string? digest, string member)
    {
        bool valid = digest is { Length: 64 };
        if (valid)
            foreach (char character in digest!)
                if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f')) { valid = false; break; }
        if (!valid)
            throw new CoverageTableDataException(CoverageTableRefusal.MalformedDigest, "An identity member is not a lowercase SHA-256 digest.")
            { MemberName = member };
    }

    internal static void ValidateRepresentative(int representative, int group, string? identity = null, int? offset = null)
    {
        if (representative < -1)
            throw new CoverageTableDataException(CoverageTableRefusal.InvalidRepresentative, "A representative is below -1.")
            { Identity = identity, ByteOffset = offset, GroupIndex = group, Representative = representative };
    }

    internal static void ValidateScalar(uint word, int group, int entry, string? identity = null, int? offset = null)
    {
        // Inspect the word, so denormal modes cannot turn a negative subnormal into an accepted zero.
        CoverageTableRefusal? reason = (word & 0x7f800000u) == 0x7f800000u ? CoverageTableRefusal.NonFiniteRecession
            : (word & 0x80000000u) != 0 && (word & 0x7fffffffu) != 0 ? CoverageTableRefusal.NegativeRecession : null;
        if (reason.HasValue)
            throw new CoverageTableDataException(reason.Value, "A recession must be finite and nonnegative, allowing either zero sign.")
            { Identity = identity, ByteOffset = offset, GroupIndex = group, EntryIndex = entry, ScalarWord = word };
    }

    internal static void ValidateState(CoverageStateEntry state, CoverageStateKey? previous, int entryCount, int index,
        string? identity = null, int? offset = null)
    {
        CoverageTableRefusal? reason = previous == state.Key ? CoverageTableRefusal.DuplicateKey
            : previous.HasValue && previous.Value.Value > state.Key.Value ? CoverageTableRefusal.UnsortedKeys
            : state.EntryIndex < -1 || state.EntryIndex >= entryCount ? CoverageTableRefusal.InvalidEntryIndex : null;
        if (reason.HasValue)
            throw new CoverageTableDataException(reason.Value, "A state record has an invalid key order or entry index.")
            {
                Identity = identity,
                ByteOffset = offset.HasValue && reason == CoverageTableRefusal.InvalidEntryIndex ? offset + 8 : offset,
                Key = state.Key,
                EntryIndex = state.EntryIndex,
                StateIndex = index,
            };
    }
}
