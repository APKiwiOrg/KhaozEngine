using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Catalog;

/// <summary>
/// One line of rolled loot: the item's definition id, the rolled count, and the id of the table the line came
/// from, which is the one thing a caller cannot reconstruct after a nested draw.
/// </summary>
/// <param name="ItemId">The item's definition id.</param>
/// <param name="Count">The rolled count, between the entry's <c>min_count</c> and <c>max_count</c>.</param>
/// <param name="TableId">The table the LINE came from, which is the nested table after a recursion.</param>
public readonly record struct LootDraw(int ItemId, int Count, int TableId);

/// <summary>
/// The ONE implementation of the composition rule of spec 3.5. The rule is the engine's, so the code that
/// runs it is the engine's: every consumer would otherwise have written it again, and the first table using
/// both <c>guaranteed</c> and <c>roll_count</c> would have had two answers.
/// <para>
/// <b>The draw order is fixed and is part of the contract.</b> Every <c>guaranteed</c> entry in <c>sort</c>
/// order FIRST, each rolling its own <c>chance_bp</c> independently, then <c>roll_count</c> weighted picks
/// over the NON-guaranteed entries, each pick one <c>NextInt(0, total)</c> and one binary search over the
/// prefix-summed array of spec 9.4. A <c>nested_table</c> entry recurses at the point it is drawn and a
/// <c>required_tags</c> entry draws uniformly from its precomputed candidate array. Writing the order down is
/// the point: it is exactly the interaction two independent reimplementations would have got differently.
/// </para>
/// <para>
/// <b><c>guaranteed</c> is a field of <c>loot_entry</c>, settled here.</b> Spec 3.5 carried it on
/// <c>loot_table</c> in its schema table and read it per entry in its prose, and per entry is what the section
/// is written around: the goblin drops its bread on its own chance AND its coin purse out of a weighted draw,
/// which is two shapes in one table. A table-level flag could not express that and would make
/// <c>roll_count</c> meaningless on any table that set it. A guaranteed entry is out of the weighted pool
/// entirely, which the index does by giving it zero width in the prefix sums.
/// </para>
/// <para>
/// <b>A weighted pick does not roll <c>chance_bp</c>.</b> A pick that won a weighted draw has already had its
/// chance, which is its share of the pool, and rolling again would be a second gate nothing authored asked
/// for. <c>chance_bp</c> is read for a GUARANTEED entry and nowhere else, which is what "each rolling its own
/// <c>chance_bp</c> independently" means. A chance at or above 10,000 is a certainty and consumes no draw, and
/// one at or below zero drops nothing and consumes no draw, so a table of certainties does not advance the
/// stream.
/// </para>
/// <para>
/// <b>It does not create anything.</b> No instance, no ground stack, no inventory write, no event and no
/// journal. It reads content and a random source and returns numbers, which is what lets a generator take its
/// output as an input without the two depending on each other. The journal event a game records is the GAME's:
/// the caller passes the <see cref="LootDraw.TableId"/> it got back into whatever event it writes.
/// </para>
/// <para>
/// <b>It allocates nothing.</b> It writes into a caller-supplied span, walks the flat arrays the load-time
/// index already built, and recurses on the stack. Budget P9 is under 100 ns for one weighted draw over a 200
/// entry table.
/// </para>
/// </summary>
public sealed class LootRoller
{
    /// <summary>
    /// How deep a nested draw may go below the table it started at, so at most
    /// <c>MaxNestedDepth + 1</c> tables contribute to one roll.
    /// <para>
    /// <c>KEC0024</c> refuses a cycle through <c>nested_table</c> at publish, so a published pack cannot reach
    /// this. The cap is the second lock, because a roll runs over bytes a pack STORE handed the process and a
    /// hostile or hand-edited pack must not be able to run a server out of stack. A nested entry at the cap is
    /// REFUSED: it draws nothing, the rest of the table still rolls, and the refusal is silent because a roll
    /// is a tick-rate operation with no error channel and the validator is where a bad pack is reported.
    /// </para>
    /// </summary>
    public const int MaxNestedDepth = 16;

    /// <summary>The basis-point denominator every chance in the system is out of, contracts 13.2.</summary>
    const int BasisPointScale = 10_000;

    readonly ContentRuntime _runtime;
    readonly IRandomSource _random;

    /// <summary>
    /// Builds a roller over one loaded version and one random source.
    /// </summary>
    /// <param name="runtime">The active version, whose loot index carries the resolved entries.</param>
    /// <param name="random">
    /// The draw source (contracts 14.4). A constructor parameter, never an ambient static and never a default,
    /// so a test rolls a seeded table and asserts exact drops.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public LootRoller(ContentRuntime runtime, IRandomSource random)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(random);
        _runtime = runtime;
        _random = random;
    }

    /// <summary>
    /// Rolls one table and returns how many lines were written. A destination too small is FILLED and the
    /// return is the span's length, so a caller sizes up rather than silently losing drops.
    /// </summary>
    /// <param name="tableId">The <c>loot_table</c> to roll. A table this version does not carry draws nothing.</param>
    /// <param name="destination">Where the lines are written.</param>
    public int Roll(int tableId, Span<LootDraw> destination)
    {
        TryRoll(tableId, destination, out int written);
        return written;
    }

    /// <summary>
    /// The same roll, reporting whether the whole of it fitted. False means the destination filled and the
    /// roll stopped at the first line that did not fit, so nothing was drawn for the lines nobody got: a
    /// caller that sizes up and rolls the same seeded source again gets the whole table.
    /// </summary>
    /// <param name="tableId">The <c>loot_table</c> to roll.</param>
    /// <param name="destination">Where the lines are written.</param>
    /// <param name="written">How many lines were written, which is the span's length on an overflow.</param>
    /// <returns>True when the roll finished, false when the destination filled first.</returns>
    public bool TryRoll(int tableId, Span<LootDraw> destination, out int written)
    {
        written = 0;
        return RollTable(tableId, destination, ref written, 0);
    }

    /// <summary>
    /// One weighted pick: the smallest index whose RUNNING total is above the roll. It is the search spec 9.4
    /// budgets the draw at, and a zero-width entry (a guaranteed one, or one whose weight clamped to zero) can
    /// never be the answer because the running total does not move across it.
    /// </summary>
    /// <param name="prefixWeights">The table's running totals, ascending.</param>
    /// <param name="roll">A draw in <c>[0, total)</c>.</param>
    internal static int Pick(ReadOnlySpan<int> prefixWeights, int roll)
    {
        int low = 0;
        int high = prefixWeights.Length - 1;
        while (low < high)
        {
            int middle = (int)(((uint)low + (uint)high) >> 1);
            if (roll < prefixWeights[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    /// <summary>The whole rule for one table, which is the guaranteed pass and then the weighted pass.</summary>
    /// <returns>False when the destination filled, which stops every level of the recursion.</returns>
    bool RollTable(int tableId, Span<LootDraw> destination, ref int written, int depth)
    {
        ContentLootIndex loot = _runtime.Indexes.Loot;
        ReadOnlySpan<ContentLootEntry> entries = loot.Entries(tableId);
        if (entries.IsEmpty)
        {
            return true;
        }

        // Pass one: every guaranteed entry, in the sort order the index already put them in, each rolling its
        // own chance independently of every other entry.
        for (int i = 0; i < entries.Length; i++)
        {
            ContentLootEntry entry = entries[i];
            if (!entry.Guaranteed || !RollChance(entry.ChanceBasisPoints))
            {
                continue;
            }

            if (!Emit(tableId, i, entry, destination, ref written, depth))
            {
                return false;
            }
        }

        // Pass two: roll_count picks over the pool, which the guaranteed entries are not in. A pool total of
        // zero means there is nothing to pick from, which is a table of nothing but guaranteed entries.
        int total = loot.TotalWeight(tableId);
        if (total <= 0)
        {
            return true;
        }

        ReadOnlySpan<int> prefixWeights = loot.PrefixWeights(tableId);
        int picks = loot.RollCount(tableId);
        for (int pick = 0; pick < picks; pick++)
        {
            int index = Pick(prefixWeights, _random.NextInt(0, total));
            if (!Emit(tableId, index, entries[index], destination, ref written, depth))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One drawn entry, resolved the one way it names its draw (<c>KEC0023</c> refuses every other count): an
    /// item line, a recursion, or a uniform draw from the precomputed candidates. An entry naming none of the
    /// three draws nothing, because the validator is where that is reported.
    /// </summary>
    bool Emit(
        int tableId,
        int entryIndex,
        in ContentLootEntry entry,
        Span<LootDraw> destination,
        ref int written,
        int depth)
    {
        if (entry.NestedTableId > 0)
        {
            // At the point it is drawn, so the nested table's lines land between this table's own.
            return depth >= MaxNestedDepth || RollTable(entry.NestedTableId, destination, ref written, depth + 1);
        }

        int itemId = entry.ItemId;
        if (itemId <= 0)
        {
            ReadOnlySpan<int> candidates = _runtime.Indexes.Loot.Candidates(tableId, entryIndex);
            if (candidates.IsEmpty)
            {
                return true;
            }

            itemId = candidates[_random.NextInt(0, candidates.Length)];
        }

        int count = RollCount(entry.MinCount, entry.MaxCount);
        if (count <= 0)
        {
            // A line of nothing is not a drop. The count came from the row, so this is an authoring defect the
            // validator owns rather than a refusal the roll can report.
            return true;
        }

        if (written >= destination.Length)
        {
            return false;
        }

        destination[written++] = new LootDraw(itemId, count, tableId);
        return true;
    }

    /// <summary>A chance in basis points, with both certainties answered without drawing.</summary>
    bool RollChance(int chanceBasisPoints)
    {
        if (chanceBasisPoints >= BasisPointScale)
        {
            return true;
        }

        return chanceBasisPoints > 0 && _random.NextInt(0, BasisPointScale) < chanceBasisPoints;
    }

    /// <summary>
    /// The line's count. A fixed range draws nothing, and a maximum below the minimum is the minimum rather
    /// than an empty range, because <c>NextInt</c> refuses one and a roll has no error channel.
    /// </summary>
    int RollCount(int minCount, int maxCount)
    {
        if (maxCount <= minCount)
        {
            return minCount;
        }

        // maxCount is INCLUSIVE, and the one value NextInt cannot take an exclusive bound for is int.MaxValue.
        int bound = maxCount == int.MaxValue ? maxCount : maxCount + 1;
        return _random.NextInt(minCount, bound);
    }
}
