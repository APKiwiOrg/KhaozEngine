using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Step 10 of spec 9.4 needs, per base, the words of each name position weighted against the base's
/// tags through 8.3's first-tag-wins rule. That is the same shape as the mod merge, so it is memoized
/// the same way, by tag signature, bounded at the same 4,096 entries. The spec does not name this
/// table, so it is the spike's own precomputation and is reported as such.
/// </summary>
internal sealed class RareNameTables
{
    private const int MemoCapacity = 4_096;
    internal const int Positions = 3;

    private readonly SyntheticContent content;
    private readonly Dictionary<int, LinkedListNode<Entry>> memo = new();
    private readonly LinkedList<Entry> order = new();

    internal RareNameTables(SyntheticContent content) => this.content = content;

    internal int MemoHits { get; private set; }
    internal int MemoMisses { get; private set; }

    internal Entry For(int baseIndex)
    {
        int signature = content.BaseTagSignature[baseIndex];
        if (memo.TryGetValue(signature, out LinkedListNode<Entry>? node))
        {
            MemoHits++;
            order.Remove(node);
            order.AddLast(node);
            return node.Value;
        }

        MemoMisses++;
        Entry built = Build(baseIndex, signature);
        if (memo.Count == MemoCapacity)
        {
            LinkedListNode<Entry>? oldest = order.First;
            if (oldest is not null)
            {
                memo.Remove(oldest.Value.Signature);
                order.RemoveFirst();
            }
        }

        LinkedListNode<Entry> added = order.AddLast(built);
        memo.Add(signature, added);
        return built;
    }

    private Entry Build(int baseIndex, int signature)
    {
        var wordIds = new int[Positions][];
        var cumulative = new int[Positions][];
        int tagStart = content.BaseTagStart[baseIndex];
        int tagCount = content.BaseTagCount[baseIndex];
        var ids = new List<int>(64);
        var weights = new List<int>(64);
        for (int position = 0; position < Positions; position++)
        {
            ids.Clear();
            weights.Clear();
            int running = 0;
            for (int word = 0; word < content.NameWordCount; word++)
            {
                if (content.WordPosition[word] != position + 1) continue;
                int weight = 0;
                for (int tag = 0; tag < tagCount && weight == 0; tag++)
                    weight = content.WordWeight[(word * content.TagCount) + content.BaseTags[tagStart + tag] - 1];
                if (weight == 0) continue;
                running += weight;
                ids.Add(word + 1);
                weights.Add(running);
            }

            wordIds[position] = ids.ToArray();
            cumulative[position] = weights.ToArray();
        }

        return new Entry(signature, wordIds, cumulative);
    }

    internal sealed record Entry(int Signature, int[][] WordIds, int[][] Cumulative);
}
