using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>One line of rolled loot: the item, the count, and the table the line came from.</summary>
public readonly record struct LootDraw(int ItemId, int Count, int TableId);

/// <summary>
/// The weighted draw of spec section 3.5, measured by budget P9: <c>NextInt(0, total)</c> plus a binary
/// search over the prefix summed array of section 9.4, writing into a caller supplied span so a roll
/// allocates nothing.
/// <para>
/// The seam contracts 14.4 names is <c>IRandomSource</c>, which does not exist in the tree yet, so the
/// spike takes <c>DeterministicRng</c> by constructor, which is the same discipline one layer down.
/// </para>
/// </summary>
public sealed class LootRoller
{
    private readonly ContentIndexes _indexes;
    private readonly DeterministicRng _random;

    public LootRoller(ContentIndexes indexes, DeterministicRng random)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(random);
        _indexes = indexes;
        _random = random;
    }

    /// <summary>Draws one weighted pick from a prefix summed range. This is the loop P9 times.</summary>
    public static int Pick(ReadOnlySpan<int> prefixWeights, int roll)
    {
        int low = 0;
        int high = prefixWeights.Length - 1;
        while (low < high)
        {
            int middle = (low + high) >> 1;
            if (roll < prefixWeights[middle]) high = middle;
            else low = middle + 1;
        }
        return low;
    }

    public int Roll(int tableId, Span<LootDraw> destination)
    {
        if ((uint)tableId >= (uint)_indexes.LootTableStart.Length) return 0;
        int start = _indexes.LootTableStart[tableId];
        int count = _indexes.LootTableCount[tableId];
        if (count == 0) return 0;
        ReadOnlySpan<int> prefix = _indexes.LootEntryPrefixWeight.AsSpan(start, count);
        int total = prefix[count - 1];
        if (total <= 0) return 0;
        int written = 0;
        while (written < destination.Length)
        {
            int slot = start + Pick(prefix, _random.Next(total));
            destination[written] = new LootDraw(_indexes.LootEntryItem[slot], 1, tableId);
            written++;
            break;
        }
        return written;
    }
}
