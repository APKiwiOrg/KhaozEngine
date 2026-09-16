using System;

namespace KhaozEngine.Catalog;

/// <summary>
/// One resolved <c>loot_entry</c> as the index holds it, spec 3.5 and 9.4. Every number a draw needs is here,
/// so a roll reads this and the candidate span and touches no row body.
/// </summary>
/// <param name="EntryId">The entry's own definition id, which fixes ties and names the row in a log.</param>
/// <param name="ItemId">The item this entry draws, or 0 when it is a nested or a tag draw.</param>
/// <param name="NestedTableId">The table this entry recurses into, or 0.</param>
/// <param name="Guaranteed">
/// Whether the entry rolls its own chance before the weighted draw instead of competing in it. A guaranteed
/// entry contributes NO weight, so <see cref="PrefixWeight"/> does not move across one.
/// </param>
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
    bool Guaranteed,
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
/// guaranteed pass and a deterministic one for the weighted pass.
/// </para>
/// <para>
/// <b>The prefix sums run over the NON-GUARANTEED entries only.</b> <c>guaranteed</c> is a field of
/// <c>loot_entry</c> (see <see cref="LootEntryContentType.GuaranteedField"/>), a guaranteed entry rolls its own
/// chance rather than competing in the draw, and so it contributes zero to the running total. The array stays
/// one array and the draw stays one binary search: a zero-width entry can never be the answer, because a
/// search for the first running total ABOVE the roll steps straight over it. <see cref="TotalWeight"/> is
/// therefore the weighted pool's total and the exclusive bound of a pick, not the sum of every authored
/// weight. A weight below zero is CLAMPED to zero
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
/// <para>
/// <b>A RETIRED <c>loot_entry</c> or <c>loot_table</c> row is not indexed at all</b>, for the same reason and
/// on the same authority (spec 3.9): a retired definition has left play and its row stays in the version so a
/// stored stack still decodes. A retired entry contributes no weight and cannot be drawn, and a retired table
/// answers as one this version does not carry, which is <see cref="RollCount"/> 0 and no entries, so its
/// guaranteed entries never fire and an entry nesting into it draws nothing. Nothing else would catch it: a
/// retired entry naming a retired item is the ORDINARY shape of a retirement, so every validator check skips
/// a retired row deliberately.
/// </para>
/// <para>
/// <b>A second row under an id another row already took contributes once.</b> The first row in id order wins,
/// exactly as it wins the type table's own lookup, for both types. The duplicate is <c>KEC0036</c> on the
/// publish side, and the index holds the line for a pack that reached the process without the validator,
/// which would otherwise roll a pool wider than anything authored.
/// </para>
/// </summary>
public sealed class ContentLootIndex
{
    /// <summary>The index of a version carrying no loot table.</summary>
    public static readonly ContentLootIndex Empty = new(0, [], [], [], [], [], [], [], []);

    readonly int[] _start;
    readonly int[] _count;
    readonly int[] _rollCount;
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

    /// <summary>
    /// The table's weighted pool total, which is the last prefix and the exclusive bound of a pick. Guaranteed
    /// entries are not in it, so a table of nothing but guaranteed entries answers 0 and takes no pick.
    /// </summary>
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
            + ((long)_entries.Length * 36)
            + ((long)(_prefixWeights.Length + _candidateStart.Length + _candidateCount.Length) * 4)
            + ((long)_candidates.Length * 4);

    bool Covers(int tableId) => (uint)tableId < (uint)_count.Length && _count[tableId] > 0;
}
