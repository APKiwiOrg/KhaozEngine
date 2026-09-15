using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct ModCandidate(int ModId, int TierOrdinal, int Weight);

/// <summary>
/// Spec 9.2's two levels: tag-band tables built once at boot, and a memoized merge per (tag signature,
/// band). The base count contributes almost nothing, because a base never enters a table and only its
/// tag list does.
/// </summary>
internal sealed class ModCandidateTables
{
    private const int MemoCapacity = 4_096;

    private readonly SyntheticContent content;
    private readonly int[] bandBoundaries;
    private readonly ModCandidate[][] tagBandTables;
    private readonly Dictionary<long, LinkedListNode<MemoEntry>> memo = new();
    private readonly LinkedList<MemoEntry> memoOrder = new();
    private ModCandidate[] mergeScratch = new ModCandidate[1_024];
    private readonly int[] cursors = new int[SyntheticContent.MaximumBaseTags];

    private ModCandidateTables(SyntheticContent content, int[] bandBoundaries, ModCandidate[][] tagBandTables)
    {
        this.content = content;
        this.bandBoundaries = bandBoundaries;
        this.tagBandTables = tagBandTables;
        foreach (ModCandidate[] table in tagBandTables) TableEntries += table.Length;
    }

    internal int BandCount => bandBoundaries.Length - 1;
    internal long TableEntries { get; }
    internal int MemoHits { get; private set; }
    internal int MemoMisses { get; private set; }

    internal static ModCandidateTables Build(SyntheticContent content)
    {
        int[] boundaries = BuildBands(content);
        int bandCount = boundaries.Length - 1;
        int bucketCount = content.TagCount * bandCount;
        var counts = new int[bucketCount];
        int tierCount = content.ModCount * SyntheticContent.TiersPerMod;

        for (int tier = 0; tier < tierCount; tier++)
        {
            int firstBand = BandOf(boundaries, content.TierLevelMin[tier]);
            int lastBand = BandOf(boundaries, content.TierLevelMax[tier]);
            for (int weight = 0; weight < SyntheticContent.WeightsPerTier; weight++)
            {
                int slot = (tier * SyntheticContent.WeightsPerTier) + weight;
                if (content.TierWeightValue[slot] == 0) continue;
                int tagIndex = content.TierWeightTag[slot] - 1;
                for (int band = firstBand; band <= lastBand; band++) counts[(tagIndex * bandCount) + band]++;
            }
        }

        var tables = new ModCandidate[bucketCount][];
        for (int bucket = 0; bucket < bucketCount; bucket++)
            tables[bucket] = counts[bucket] == 0 ? Array.Empty<ModCandidate>() : new ModCandidate[counts[bucket]];
        Array.Clear(counts);

        for (int tier = 0; tier < tierCount; tier++)
        {
            int modId = (tier / SyntheticContent.TiersPerMod) + 1;
            int ordinal = (tier % SyntheticContent.TiersPerMod) + 1;
            int firstBand = BandOf(boundaries, content.TierLevelMin[tier]);
            int lastBand = BandOf(boundaries, content.TierLevelMax[tier]);
            for (int weight = 0; weight < SyntheticContent.WeightsPerTier; weight++)
            {
                int slot = (tier * SyntheticContent.WeightsPerTier) + weight;
                int value = content.TierWeightValue[slot];
                if (value == 0) continue;
                int tagIndex = content.TierWeightTag[slot] - 1;
                var candidate = new ModCandidate(modId, ordinal, value);
                for (int band = firstBand; band <= lastBand; band++)
                {
                    int bucket = (tagIndex * bandCount) + band;
                    tables[bucket][counts[bucket]++] = candidate;
                }
            }
        }

        return new ModCandidateTables(content, boundaries, tables);
    }

    /// <summary>
    /// Every distinct <c>item_level_min</c> and <c>item_level_max + 1</c>, sorted. Within one band no
    /// tier's gate changes, so the live tier set is constant across it.
    /// </summary>
    private static int[] BuildBands(SyntheticContent content)
    {
        var values = new SortedSet<int> { 1, ushort.MaxValue };
        int tierCount = content.ModCount * SyntheticContent.TiersPerMod;
        for (int tier = 0; tier < tierCount; tier++)
        {
            values.Add(content.TierLevelMin[tier]);
            values.Add(content.TierLevelMax[tier] + 1);
        }

        var boundaries = new int[values.Count];
        values.CopyTo(boundaries);
        return boundaries;
    }

    internal int BandOf(int itemLevel) => BandOf(bandBoundaries, itemLevel);

    private static int BandOf(int[] boundaries, int itemLevel)
    {
        int low = 0;
        int high = boundaries.Length - 2;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (boundaries[middle] <= itemLevel) low = middle;
            else high = middle - 1;
        }

        return low;
    }

    /// <summary>
    /// Step 3 of 9.2: the merge of the tables for a base's tags, walked in the base's AUTHORED tag
    /// order, taking the first weight found for each (mod id, tier ordinal) and ignoring later ones.
    /// Memoized by (tag signature, band), bounded at 4,096 entries on an LRU eviction.
    /// </summary>
    internal ModCandidate[] Merge(int baseIndex, int band)
    {
        long key = ((long)content.BaseTagSignature[baseIndex] << 20) | (uint)band;
        if (memo.TryGetValue(key, out LinkedListNode<MemoEntry>? node))
        {
            MemoHits++;
            memoOrder.Remove(node);
            memoOrder.AddLast(node);
            return node.Value.Candidates;
        }

        MemoMisses++;
        ModCandidate[] merged = MergeTables(baseIndex, band);
        if (memo.Count == MemoCapacity)
        {
            LinkedListNode<MemoEntry>? oldest = memoOrder.First;
            if (oldest is not null)
            {
                memo.Remove(oldest.Value.Key);
                memoOrder.RemoveFirst();
            }
        }

        LinkedListNode<MemoEntry> added = memoOrder.AddLast(new MemoEntry(key, merged));
        memo.Add(key, added);
        return merged;
    }

    private ModCandidate[] MergeTables(int baseIndex, int band)
    {
        int tagStart = content.BaseTagStart[baseIndex];
        int tagCount = content.BaseTagCount[baseIndex];
        int upperBound = 0;
        for (int tag = 0; tag < tagCount; tag++)
        {
            cursors[tag] = 0;
            upperBound += TableFor(content.BaseTags[tagStart + tag], band).Length;
        }

        if (mergeScratch.Length < upperBound) mergeScratch = new ModCandidate[Math.Max(upperBound, mergeScratch.Length * 2)];
        int written = 0;
        while (true)
        {
            int bestTag = -1;
            long bestKey = long.MaxValue;
            for (int tag = 0; tag < tagCount; tag++)
            {
                ModCandidate[] table = TableFor(content.BaseTags[tagStart + tag], band);
                if (cursors[tag] >= table.Length) continue;
                ModCandidate candidate = table[cursors[tag]];
                long candidateKey = ((long)candidate.ModId << 12) | (uint)candidate.TierOrdinal;
                if (candidateKey >= bestKey) continue;
                bestKey = candidateKey;
                bestTag = tag;
            }

            if (bestTag < 0) break;
            mergeScratch[written++] = TableFor(content.BaseTags[tagStart + bestTag], band)[cursors[bestTag]];
            for (int tag = 0; tag < tagCount; tag++)
            {
                ModCandidate[] table = TableFor(content.BaseTags[tagStart + tag], band);
                if (cursors[tag] >= table.Length) continue;
                ModCandidate candidate = table[cursors[tag]];
                long candidateKey = ((long)candidate.ModId << 12) | (uint)candidate.TierOrdinal;
                if (candidateKey == bestKey) cursors[tag]++;
            }
        }

        return mergeScratch.AsSpan(0, written).ToArray();
    }

    private ModCandidate[] TableFor(int tagId, int band) => tagBandTables[((tagId - 1) * BandCount) + band];

    private readonly record struct MemoEntry(long Key, ModCandidate[] Candidates);
}
