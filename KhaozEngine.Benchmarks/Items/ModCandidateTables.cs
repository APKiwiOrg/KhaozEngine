using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// Spec 9.2's table shape. Tag-kind-band tables are built once at boot as struct-of-arrays with the
/// per bucket cumulative weight beside the packed (mod id, tier ordinal) key, a legacy tier never
/// enters one, and the OVERLAP between the tables a base's tags select is precomputed per (tag
/// signature, kind, band, tag position) as a suppression list. The union of those tables is never
/// materialised, at roll time or at boot, which is what removes the merge and the memo the merge
/// needed. The base count contributes almost nothing, because a base never enters a table and only its
/// tag list does.
/// </summary>
internal sealed class ModCandidateTables
{
    internal const int KindCount = 2;

    /// <summary>A packed entry is <c>(mod id &lt;&lt; TierBits) | tier ordinal</c>, so ascending packed order IS (mod id, tier ordinal) order.</summary>
    internal const int TierBits = 4;

    internal const int TierMask = (1 << TierBits) - 1;

    private readonly SyntheticContent content;
    private readonly int[] bandBoundaries;
    private readonly int[] entryPacked;
    private readonly int[] entryCumulative;
    private readonly int[] bucketStart;
    private readonly int[] bucketLength;
    private readonly int[] groupStart;
    private readonly int[] groupMembers;
    private readonly int[] signatureTagStart;
    private readonly byte[] signatureTagCount;
    private readonly int[] suppressStart;
    private readonly int[] suppressCount;
    private readonly int[] suppressWeight;
    private ushort[] suppressIndex = new ushort[1 << 16];
    private int suppressUsed;

    private ModCandidateTables(
        SyntheticContent content,
        int[] bandBoundaries,
        int[] entryPacked,
        int[] entryCumulative,
        int[] bucketStart,
        int[] bucketLength,
        int[] groupStart,
        int[] groupMembers,
        int[] signatureTagStart,
        byte[] signatureTagCount,
        int headers)
    {
        this.content = content;
        this.bandBoundaries = bandBoundaries;
        this.entryPacked = entryPacked;
        this.entryCumulative = entryCumulative;
        this.bucketStart = bucketStart;
        this.bucketLength = bucketLength;
        this.groupStart = groupStart;
        this.groupMembers = groupMembers;
        this.signatureTagStart = signatureTagStart;
        this.signatureTagCount = signatureTagCount;
        BandCount = bandBoundaries.Length - 1;
        suppressStart = new int[headers];
        suppressCount = new int[headers];
        suppressWeight = new int[headers];
    }

    internal int BandCount { get; }

    internal long TableEntries => entryPacked.Length;

    internal long SuppressedEntries => suppressUsed;

    /// <summary>Tables plus suppression lists plus the group and band indexes, which is what a boot holds for good.</summary>
    internal long ResidentBytes
        => (((long)entryPacked.Length + entryCumulative.Length + bucketStart.Length + bucketLength.Length
            + groupStart.Length + groupMembers.Length + signatureTagStart.Length
            + suppressStart.Length + suppressCount.Length + suppressWeight.Length) * sizeof(int))
            + ((long)suppressIndex.Length * sizeof(ushort))
            + signatureTagCount.Length;

    /// <summary>
    /// A (tag signature, kind, band) whose live count or live weight disagrees with a reference merge
    /// of the same tables. It must be zero: the suppression lists ARE the merge, precomputed.
    /// </summary>
    internal int ConsistencyFailures { get; private set; }

    internal int[] EntryPacked => entryPacked;

    internal int[] EntryCumulative => entryCumulative;

    internal ushort[] SuppressIndex => suppressIndex;

    internal static ModCandidateTables Build(SyntheticContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        int[] boundaries = BuildBands(content);
        int bandCount = boundaries.Length - 1;
        int bucketCount = content.TagCount * KindCount * bandCount;
        var lengths = new int[bucketCount];
        int tierCount = content.ModCount * SyntheticContent.TiersPerMod;

        for (int tier = 0; tier < tierCount; tier++)
        {
            int modIndex = tier / SyntheticContent.TiersPerMod;
            if (content.ModLegacy[modIndex]) continue;
            int kind = content.ModKind[modIndex] - 1;
            int firstBand = BandOf(boundaries, content.TierLevelMin[tier]);
            int lastBand = BandOf(boundaries, content.TierLevelMax[tier]);
            for (int weight = 0; weight < SyntheticContent.WeightsPerTier; weight++)
            {
                int slot = (tier * SyntheticContent.WeightsPerTier) + weight;
                if (content.TierWeightValue[slot] == 0) continue;
                int tagIndex = content.TierWeightTag[slot] - 1;
                for (int band = firstBand; band <= lastBand; band++)
                    lengths[BucketOf(tagIndex, kind, band, bandCount)]++;
            }
        }

        var starts = new int[bucketCount];
        int running = 0;
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            starts[bucket] = running;
            running += lengths[bucket];
        }

        var packed = new int[running];
        var cumulative = new int[running];
        var fill = new int[bucketCount];
        for (int tier = 0; tier < tierCount; tier++)
        {
            int modIndex = tier / SyntheticContent.TiersPerMod;
            if (content.ModLegacy[modIndex]) continue;
            int kind = content.ModKind[modIndex] - 1;
            int entry = ((modIndex + 1) << TierBits) | ((tier % SyntheticContent.TiersPerMod) + 1);
            int firstBand = BandOf(boundaries, content.TierLevelMin[tier]);
            int lastBand = BandOf(boundaries, content.TierLevelMax[tier]);
            for (int weight = 0; weight < SyntheticContent.WeightsPerTier; weight++)
            {
                int slot = (tier * SyntheticContent.WeightsPerTier) + weight;
                int value = content.TierWeightValue[slot];
                if (value == 0) continue;
                int tagIndex = content.TierWeightTag[slot] - 1;
                for (int band = firstBand; band <= lastBand; band++)
                {
                    int bucket = BucketOf(tagIndex, kind, band, bandCount);
                    int position = starts[bucket] + fill[bucket];
                    packed[position] = entry;
                    cumulative[position] = (fill[bucket] == 0 ? 0 : cumulative[position - 1]) + value;
                    fill[bucket]++;
                }
            }
        }

        BuildGroupIndex(content, out int[] groupStart, out int[] groupMembers);
        BuildSignatureTags(content, out int[] signatureTagStart, out byte[] signatureTagCount);
        int headers = content.DistinctTagSignatures * KindCount * bandCount * SyntheticContent.MaximumBaseTags;
        var tables = new ModCandidateTables(
            content, boundaries, packed, cumulative, starts, lengths, groupStart, groupMembers,
            signatureTagStart, signatureTagCount, headers);
        tables.BuildSuppression();
        return tables;
    }

    /// <summary>
    /// Every distinct <c>item_level_min</c> and <c>item_level_max + 1</c>, sorted. Within one band no
    /// tier's gate changes, so the live tier set is constant across it, and the count is the authored
    /// level curve rather than a number this design picks.
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

    /// <summary>
    /// Group id to the mod ids in it, so enforcing <c>max_per_item</c> is a lookup rather than a scan.
    /// A legacy mod is left out, because it is in no table to exclude from.
    /// </summary>
    private static void BuildGroupIndex(SyntheticContent content, out int[] groupStart, out int[] groupMembers)
    {
        int maximum = 0;
        for (int modIndex = 0; modIndex < content.ModCount; modIndex++)
            if (!content.ModLegacy[modIndex] && content.ModGroup[modIndex] > maximum) maximum = content.ModGroup[modIndex];

        var counts = new int[maximum + 1];
        for (int modIndex = 0; modIndex < content.ModCount; modIndex++)
        {
            int group = content.ModGroup[modIndex];
            if (group == 0 || content.ModLegacy[modIndex]) continue;
            counts[group]++;
        }

        groupStart = new int[maximum + 2];
        int running = 0;
        for (int group = 0; group <= maximum; group++)
        {
            groupStart[group] = running;
            running += counts[group];
        }

        groupStart[maximum + 1] = running;
        groupMembers = new int[running];
        var fill = new int[maximum + 1];
        for (int modIndex = 0; modIndex < content.ModCount; modIndex++)
        {
            int group = content.ModGroup[modIndex];
            if (group == 0 || content.ModLegacy[modIndex]) continue;
            groupMembers[groupStart[group] + fill[group]++] = modIndex + 1;
        }
    }

    /// <summary>A tag signature's ordered tag list, taken off the first base carrying it.</summary>
    private static void BuildSignatureTags(SyntheticContent content, out int[] tagStart, out byte[] tagCount)
    {
        tagStart = new int[content.DistinctTagSignatures];
        tagCount = new byte[content.DistinctTagSignatures];
        for (int signature = 0; signature < tagCount.Length; signature++) tagStart[signature] = -1;
        for (int baseIndex = 0; baseIndex < content.BaseCount; baseIndex++)
        {
            int signature = content.BaseTagSignature[baseIndex];
            if (tagStart[signature] >= 0) continue;
            tagStart[signature] = content.BaseTagStart[baseIndex];
            tagCount[signature] = content.BaseTagCount[baseIndex];
        }
    }

    internal ReadOnlySpan<int> GroupMembers(int group)
        => group <= 0 || group + 1 >= groupStart.Length
            ? ReadOnlySpan<int>.Empty
            : groupMembers.AsSpan(groupStart[group], groupStart[group + 1] - groupStart[group]);

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

    private static int BucketOf(int tagIndex, int kind, int band, int bandCount)
        => (((tagIndex * KindCount) + kind) * bandCount) + band;

    internal int BucketOf(int tagId, int kind, int band) => BucketOf(tagId - 1, kind, band, BandCount);

    internal int BucketStartAt(int bucket) => bucketStart[bucket];

    internal int BucketLengthAt(int bucket) => bucketLength[bucket];

    internal int BucketTotalAt(int bucket)
    {
        int length = bucketLength[bucket];
        return length == 0 ? 0 : entryCumulative[bucketStart[bucket] + length - 1];
    }

    internal int HeaderOf(int signature, int kind, int band, int tagPosition)
        => ((((signature * KindCount) + kind) * BandCount) + band) * SyntheticContent.MaximumBaseTags + tagPosition;

    internal int SuppressStartAt(int header) => suppressStart[header];

    internal int SuppressCountAt(int header) => suppressCount[header];

    internal int SuppressWeightAt(int header) => suppressWeight[header];

    /// <summary>
    /// The overlap pass, once per (tag signature, kind, band). It is the k-way merge the roll used to
    /// run, kept at BOOT and used to record only what it discards: the entries a later tag repeats,
    /// which 8.3's first-tag-wins rule drops. The merged count and weight it computes on the way are
    /// checked against the same numbers derived from the suppression lists, so a wrong list cannot ship
    /// silently.
    /// </summary>
    private void BuildSuppression()
    {
        int widest = 0;
        for (int bucket = 0; bucket < bucketLength.Length; bucket++)
            if (bucketLength[bucket] > widest) widest = bucketLength[bucket];
        if (widest > ushort.MaxValue) throw new InvalidOperationException("A tag table is too wide for a 16 bit entry index.");

        var scratch = new ushort[SyntheticContent.MaximumBaseTags][];
        for (int tag = 0; tag < scratch.Length; tag++) scratch[tag] = new ushort[widest];
        var scratchCount = new int[SyntheticContent.MaximumBaseTags];
        var scratchWeight = new int[SyntheticContent.MaximumBaseTags];
        Span<int> buckets = stackalloc int[SyntheticContent.MaximumBaseTags];
        for (int signature = 0; signature < signatureTagCount.Length; signature++)
        {
            int tagStart = signatureTagStart[signature];
            int tagCount = signatureTagCount[signature];
            for (int band = 0; band < BandCount; band++)
            {
                for (int kind = 0; kind < KindCount; kind++)
                {
                    for (int tag = 0; tag < SyntheticContent.MaximumBaseTags; tag++)
                        buckets[tag] = tag < tagCount ? BucketOf(content.BaseTags[tagStart + tag], kind, band) : -1;
                    ScanOverlap(buckets, scratch, scratchCount, scratchWeight, out int mergedCount, out int mergedWeight);
                    int liveCount = 0;
                    int liveWeight = 0;
                    for (int tag = 0; tag < tagCount; tag++)
                    {
                        int header = HeaderOf(signature, kind, band, tag);
                        suppressStart[header] = suppressUsed;
                        suppressCount[header] = scratchCount[tag];
                        suppressWeight[header] = scratchWeight[tag];
                        Append(scratch[tag], scratchCount[tag]);
                        liveCount += bucketLength[buckets[tag]] - scratchCount[tag];
                        liveWeight += BucketTotalAt(buckets[tag]) - scratchWeight[tag];
                    }

                    if (liveCount != mergedCount || liveWeight != mergedWeight) ConsistencyFailures++;
                }
            }
        }

        if (suppressUsed == suppressIndex.Length) return;
        var trimmed = new ushort[suppressUsed];
        Array.Copy(suppressIndex, trimmed, suppressUsed);
        suppressIndex = trimmed;
    }

    /// <summary>
    /// Four scalar cursors, one advance per matching cursor, and the earliest cursor holding the key is
    /// the winner 8.3 keeps. Every other cursor on that key records the entry as suppressed, which
    /// includes a table repeating a key of its own.
    /// </summary>
    private void ScanOverlap(
        Span<int> buckets,
        ushort[][] scratch,
        int[] scratchCount,
        int[] scratchWeight,
        out int mergedCount,
        out int mergedWeight)
    {
        int[] source = entryPacked;
        int start0 = buckets[0] < 0 ? 0 : bucketStart[buckets[0]];
        int start1 = buckets[1] < 0 ? 0 : bucketStart[buckets[1]];
        int start2 = buckets[2] < 0 ? 0 : bucketStart[buckets[2]];
        int start3 = buckets[3] < 0 ? 0 : bucketStart[buckets[3]];
        int end0 = buckets[0] < 0 ? 0 : start0 + bucketLength[buckets[0]];
        int end1 = buckets[1] < 0 ? 0 : start1 + bucketLength[buckets[1]];
        int end2 = buckets[2] < 0 ? 0 : start2 + bucketLength[buckets[2]];
        int end3 = buckets[3] < 0 ? 0 : start3 + bucketLength[buckets[3]];
        int cursor0 = start0;
        int cursor1 = start1;
        int cursor2 = start2;
        int cursor3 = start3;
        int key0 = cursor0 < end0 ? source[cursor0] : int.MaxValue;
        int key1 = cursor1 < end1 ? source[cursor1] : int.MaxValue;
        int key2 = cursor2 < end2 ? source[cursor2] : int.MaxValue;
        int key3 = cursor3 < end3 ? source[cursor3] : int.MaxValue;
        for (int tag = 0; tag < SyntheticContent.MaximumBaseTags; tag++)
        {
            scratchCount[tag] = 0;
            scratchWeight[tag] = 0;
        }

        mergedCount = 0;
        mergedWeight = 0;
        int previous = -1;
        while (true)
        {
            int best = Math.Min(Math.Min(key0, key1), Math.Min(key2, key3));
            if (best == int.MaxValue) break;
            bool wanted = best != previous;
            previous = best;
            if (key0 == best)
            {
                int weight = WeightAt(cursor0, start0);
                if (wanted)
                {
                    wanted = false;
                    mergedCount++;
                    mergedWeight += weight;
                }
                else
                {
                    scratch[0][scratchCount[0]++] = (ushort)(cursor0 - start0);
                    scratchWeight[0] += weight;
                }

                cursor0++;
                key0 = cursor0 < end0 ? source[cursor0] : int.MaxValue;
            }

            if (key1 == best)
            {
                int weight = WeightAt(cursor1, start1);
                if (wanted)
                {
                    wanted = false;
                    mergedCount++;
                    mergedWeight += weight;
                }
                else
                {
                    scratch[1][scratchCount[1]++] = (ushort)(cursor1 - start1);
                    scratchWeight[1] += weight;
                }

                cursor1++;
                key1 = cursor1 < end1 ? source[cursor1] : int.MaxValue;
            }

            if (key2 == best)
            {
                int weight = WeightAt(cursor2, start2);
                if (wanted)
                {
                    wanted = false;
                    mergedCount++;
                    mergedWeight += weight;
                }
                else
                {
                    scratch[2][scratchCount[2]++] = (ushort)(cursor2 - start2);
                    scratchWeight[2] += weight;
                }

                cursor2++;
                key2 = cursor2 < end2 ? source[cursor2] : int.MaxValue;
            }

            if (key3 == best)
            {
                int weight = WeightAt(cursor3, start3);
                if (wanted)
                {
                    mergedCount++;
                    mergedWeight += weight;
                }
                else
                {
                    scratch[3][scratchCount[3]++] = (ushort)(cursor3 - start3);
                    scratchWeight[3] += weight;
                }

                cursor3++;
                key3 = cursor3 < end3 ? source[cursor3] : int.MaxValue;
            }
        }
    }

    private int WeightAt(int index, int bucketFirst)
        => entryCumulative[index] - (index == bucketFirst ? 0 : entryCumulative[index - 1]);

    private void Append(ushort[] entries, int count)
    {
        if (count == 0) return;
        if (suppressUsed + count > suppressIndex.Length)
        {
            int grown = Math.Max(suppressIndex.Length * 2, suppressUsed + count);
            var replacement = new ushort[grown];
            Array.Copy(suppressIndex, replacement, suppressUsed);
            suppressIndex = replacement;
        }

        Array.Copy(entries, 0, suppressIndex, suppressUsed, count);
        suppressUsed += count;
    }
}
