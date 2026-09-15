using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// One resolved <c>loot_entry</c> as the index holds it, spec 3.5 and 9.4. Every number a draw needs is here,
/// so a roll reads this and the candidate span and touches no row body.
/// </summary>
/// <param name="EntryId">The entry's own definition id, which fixes ties and names the row in a log.</param>
/// <param name="ItemId">The item this entry draws, or 0 when it is a nested or a tag draw.</param>
/// <param name="NestedTableId">The table this entry recurses into, or 0.</param>
/// <param name="PrefixWeight">
/// The RUNNING weight total through this entry, so a weighted draw is one <c>NextInt(0, total)</c> and one
/// binary search rather than a summation per roll.
/// </param>
/// <param name="ChanceBasisPoints">The entry's own chance, out of 10,000.</param>
/// <param name="MinCount">The smallest count a drawn line carries.</param>
/// <param name="MaxCount">The largest count a drawn line carries.</param>
/// <param name="Sort">The authored order, which fixes the order guaranteed entries roll in.</param>
public readonly record struct ContentLootEntry(
    int EntryId,
    int ItemId,
    int NestedTableId,
    int PrefixWeight,
    int ChanceBasisPoints,
    int MinCount,
    int MaxCount,
    int Sort);

/// <summary>
/// The loot candidate arrays of spec 9.4, fourth of the engine's four: per <c>loot_table</c>, its resolved
/// entry list with the weights PREFIX SUMMED, plus the candidate item ids a <c>required_tags</c> entry draws
/// uniformly from.
/// <para>
/// The prefix sum is the one that matters for the tick: a weighted draw over n entries becomes one
/// <c>NextInt(0, total)</c> and one binary search over an <c>int[]</c>, with no allocation and no per-roll
/// summation. Everything here is a flat array sliced per table, so no table owns a collection of its own.
/// </para>
/// <para>
/// Entries come back ordered by <c>sort</c> then by entry id, which is the order spec 3.5 fixes for the
/// guaranteed pass and a deterministic one for the weighted pass. A weight below zero is CLAMPED to zero
/// rather than refused, because a negative weight would make the prefix array non-monotonic and unsearchable,
/// and refusing it here would be a load failure where the validator already has a finding. The running total
/// saturates at <see cref="int.MaxValue"/> for the same reason.
/// </para>
/// <para>
/// A <c>required_tags</c> entry resolves at load into a candidate array (spec 3.5): every LIVE item carrying
/// every listed tag, ascending by id, retired rows excluded, because a retired item is out of play and a drop
/// that offered one would be a drop nobody can use. The tag index is built before this one, which is what
/// makes the resolution one intersection of sorted spans.
/// </para>
/// </summary>
public sealed class ContentLootIndex
{
    /// <summary>The index of a version carrying no loot table.</summary>
    public static readonly ContentLootIndex Empty = new(0, [], [], [], [], [], [], [], [], []);

    readonly int[] _start;
    readonly int[] _count;
    readonly int[] _rollCount;
    readonly bool[] _guaranteed;
    readonly ContentLootEntry[] _entries;

    /// <summary>
    /// The running totals again, as a bare <c>int[]</c>. It duplicates
    /// <see cref="ContentLootEntry.PrefixWeight"/> at 4 bytes an entry, on purpose: spec 9.4 budgets a draw
    /// at ONE binary search over an <c>int[]</c>, and searching a struct array on one of its fields needs a
    /// comparer and loses the contiguous compare.
    /// </summary>
    readonly int[] _prefixWeights;
    readonly int[] _candidateStart;
    readonly int[] _candidateCount;
    readonly int[] _candidates;

    internal ContentLootIndex(
        int tableCount,
        int[] start,
        int[] count,
        int[] rollCount,
        bool[] guaranteed,
        ContentLootEntry[] entries,
        int[] prefixWeights,
        int[] candidateStart,
        int[] candidateCount,
        int[] candidates)
    {
        TableCount = tableCount;
        _start = start;
        _count = count;
        _rollCount = rollCount;
        _guaranteed = guaranteed;
        _entries = entries;
        _prefixWeights = prefixWeights;
        _candidateStart = candidateStart;
        _candidateCount = candidateCount;
        _candidates = candidates;
    }

    /// <summary>How many loot tables the version carries an entry-bearing row for.</summary>
    public int TableCount { get; }

    /// <summary>How many entries one table has, and 0 for a table this version does not carry.</summary>
    public int EntryCount(int tableId) => Covers(tableId) ? _count[tableId] : 0;

    /// <summary>How many weighted picks a roll over this table draws.</summary>
    public int RollCount(int tableId) => Covers(tableId) ? _rollCount[tableId] : 0;

    /// <summary>Whether this table's entries roll their own chance independently, spec 3.5.</summary>
    public bool IsGuaranteed(int tableId) => Covers(tableId) && _guaranteed[tableId];

    /// <summary>The table's total weight, which is the last prefix and the exclusive bound of a draw.</summary>
    public int TotalWeight(int tableId)
    {
        ReadOnlySpan<int> weights = PrefixWeights(tableId);
        return weights.IsEmpty ? 0 : weights[^1];
    }

    /// <summary>
    /// The table's RUNNING weight totals, ascending, which a draw binary searches. One span, no copy.
    /// </summary>
    public ReadOnlySpan<int> PrefixWeights(int tableId)
        => Covers(tableId) ? _prefixWeights.AsSpan(_start[tableId], _count[tableId]) : default;

    /// <summary>The table's resolved entries, in <c>sort</c> then id order.</summary>
    public ReadOnlySpan<ContentLootEntry> Entries(int tableId)
        => Covers(tableId) ? _entries.AsSpan(_start[tableId], _count[tableId]) : default;

    /// <summary>
    /// The item ids a tag-filtered entry draws uniformly from, ascending, or empty for an entry that names
    /// its draw another way.
    /// </summary>
    /// <param name="tableId">The table the entry belongs to.</param>
    /// <param name="entryIndex">The entry's position within <see cref="Entries"/>.</param>
    public ReadOnlySpan<int> Candidates(int tableId, int entryIndex)
    {
        if (!Covers(tableId) || (uint)entryIndex >= (uint)_count[tableId])
        {
            return default;
        }

        int slot = _start[tableId] + entryIndex;
        return _candidates.AsSpan(_candidateStart[slot], _candidateCount[slot]);
    }

    /// <summary>The managed bytes this index holds, for the memory line of spec 9.2.</summary>
    public long ApproximateBytes()
        => ((long)(_start.Length + _count.Length + _rollCount.Length) * 4)
            + _guaranteed.LongLength
            + ((long)_entries.Length * 32)
            + ((long)(_prefixWeights.Length + _candidateStart.Length + _candidateCount.Length) * 4)
            + ((long)_candidates.Length * 4);

    bool Covers(int tableId) => (uint)tableId < (uint)_count.Length && _count[tableId] > 0;
}
