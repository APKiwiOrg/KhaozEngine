using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The BUILD half of the candidate tables: three row sets in, the bands, the buckets and the group index
/// out. It is a scan of indexed rows rather than a decode of a blob per mod, which is what keeps a build of
/// the owner's 2,000 mods inside budget 9's 500 ms.
/// </summary>
public sealed partial class ModCandidateTables
{
    /// <summary>The <c>mod</c> schema positions, named so a schema change lands in one place.</summary>
    const int ModKindField = 0;
    const int ModGroupIdField = 1;
    const int ModLegacyField = 2;

    /// <summary>The <c>mod_tier</c> schema positions.</summary>
    const int TierModIdField = 0;
    const int TierOrdinalField = 1;
    const int TierItemLevelMinField = 2;
    const int TierItemLevelMaxField = 3;

    /// <summary>The <c>mod_tier_weight</c> schema positions.</summary>
    const int WeightTierIdField = 0;
    const int WeightTagIdField = 1;
    const int WeightValueField = 2;

    /// <summary>The <c>mod_group</c> schema position.</summary>
    const int GroupMaxPerItemField = 0;

    /// <summary>
    /// Builds the whole table set from one loaded version. The input is three row sets for the tables
    /// themselves (<c>mod</c> for its kind, group and legacy flag, <c>mod_tier</c> for its ordinal and its
    /// two level bounds, <c>mod_tier_weight</c> for its tag and weight), plus the <c>item</c> rows the tag
    /// signature intern reads and the <c>mod_group</c> rows the group index reads for its count. All five
    /// are ordinary ROWS, which is what an <see cref="IContentLoadIndex"/> is explicitly permitted to read.
    /// <para>
    /// <b>A server whose pack omits the ServerOnly weight type builds no tables at all</b>, which is exactly
    /// what a client does, so the generator is off a client by construction rather than behind a flag.
    /// </para>
    /// </summary>
    /// <param name="snapshot">The loaded version, read for rows and for no other index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// A tier ordinal is past <see cref="MaxTierOrdinal"/>, a base's tag list is past
    /// <see cref="MaxGenerationTagPositions"/>, a bucket would hold more than
    /// <see cref="MaxBucketEntries"/> entries, or a bucket's weights sum past <see cref="int.MaxValue"/>.
    /// Each one fails the BOOT closed rather than handing back a table that quietly answers wrong.
    /// </exception>
    public static ModCandidateTables Build(IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        GenerationTagSignature signatures = GenerationTagSignature.Build(snapshot, MaxGenerationTagPositions);
        ModRows mods = ReadMods(snapshot);
        GroupRows groups = ReadGroups(snapshot);
        WeightRows weights = ReadWeights(snapshot);
        TierRows tiers = ReadTiers(snapshot, mods, weights);
        var tables = new ModCandidateTables(BuildBuckets(signatures, mods, groups, weights, tiers));
        tables.BuildOverlap();
        return tables;
    }

    /// <summary>
    /// Every live <c>mod</c> row, ascending by id, with the kind it carries, the group it belongs to and
    /// whether it is legacy. The rows arrive ascending, so the id column is a binary search and nothing here
    /// sorts.
    /// </summary>
    static ModRows ReadMods(IContentSnapshot snapshot)
    {
        IReadOnlyList<ContentRow> rows = snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.ModTypeId));
        var ids = new List<int>(rows.Count);
        var kinds = new List<int>(rows.Count);
        var groups = new List<int>(rows.Count);
        var legacy = new List<bool>(rows.Count);
        var distinctKinds = new SortedSet<int>();

        foreach (ContentRow row in rows)
        {
            if (row.IsRetired)
            {
                continue;
            }

            // A kind is an int key for a bucket column and the engine fixes only two of its meanings, so
            // there is no ceiling to enforce and nothing is dropped for carrying a kind above 2. That is the
            // whole point of indexing a bucket by the kind's POSITION.
            int kind = Clamp(InstanceContentChecks.Number(row, ModKindField) ?? 0);
            bool isLegacy = (InstanceContentChecks.Number(row, ModLegacyField) ?? 0) != 0;
            int group = isLegacy ? 0 : Clamp(InstanceContentChecks.Number(row, ModGroupIdField) ?? 0);

            ids.Add(row.Id);
            kinds.Add(kind);
            groups.Add(group < 0 ? 0 : group);
            legacy.Add(isLegacy);
            distinctKinds.Add(kind);
        }

        var kindSet = new int[distinctKinds.Count];
        distinctKinds.CopyTo(kindSet);
        return new ModRows(ids.ToArray(), kinds.ToArray(), groups.ToArray(), legacy.ToArray(), kindSet);
    }

    /// <summary>
    /// Every live <c>mod_group</c> row's count, ascending by group id. It is the other half of the group
    /// index: the members answer "which other mods", and this answers "how many of them one item may carry".
    /// </summary>
    static GroupRows ReadGroups(IContentSnapshot snapshot)
    {
        IReadOnlyList<ContentRow> rows = snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.ModGroupTypeId));
        var ids = new List<int>(rows.Count);
        var counts = new List<int>(rows.Count);

        foreach (ContentRow row in rows)
        {
            if (row.IsRetired)
            {
                continue;
            }

            ids.Add(row.Id);
            counts.Add(Clamp(InstanceContentChecks.Number(row, GroupMaxPerItemField) ?? 0));
        }

        return new GroupRows(ids.ToArray(), counts.ToArray());
    }

    /// <summary>
    /// Every live <c>mod_tier_weight</c> row, ordered by (tier id, row id), so one tier's weights are a
    /// contiguous run found by binary search and their order inside it is the authored row order.
    /// </summary>
    static WeightRows ReadWeights(IContentSnapshot snapshot)
    {
        IReadOnlyList<ContentRow> rows =
            snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.ModTierWeightTypeId));
        var keys = new List<long>(rows.Count);
        var tiers = new List<int>(rows.Count);
        var tags = new List<int>(rows.Count);
        var values = new List<long>(rows.Count);

        foreach (ContentRow row in rows)
        {
            if (row.IsRetired)
            {
                continue;
            }

            long tierId = InstanceContentChecks.Number(row, WeightTierIdField) ?? 0;
            long tagId = InstanceContentChecks.Number(row, WeightTagIdField) ?? 0;
            if (tierId is < 1 or > int.MaxValue || tagId is < 1 or > int.MaxValue)
            {
                continue;
            }

            // A weight at or below zero never spawns, which is the same answer the load-time clamp of a
            // negative one gives, so neither reaches a table and neither needs a rule at the draw.
            long weight = InstanceContentChecks.Number(row, WeightValueField) ?? 0;
            if (weight <= 0)
            {
                continue;
            }

            keys.Add(((long)(int)tierId << 32) + tiers.Count);
            tiers.Add((int)tierId);
            tags.Add((int)tagId);
            values.Add(weight);
        }

        long[] order = keys.ToArray();
        int[] tierColumn = tiers.ToArray();
        int[] tagColumn = tags.ToArray();
        long[] valueColumn = values.ToArray();
        var slots = new int[order.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i;
        }

        Array.Sort(order, slots);
        var sortedTiers = new int[slots.Length];
        var sortedTags = new int[slots.Length];
        var sortedValues = new long[slots.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            sortedTiers[i] = tierColumn[slots[i]];
            sortedTags[i] = tagColumn[slots[i]];
            sortedValues[i] = valueColumn[slots[i]];
        }

        return new WeightRows(sortedTiers, sortedTags, sortedValues);
    }

    /// <summary>
    /// Every <c>mod_tier</c> row that can put an entry in a table, ordered by PACKED key. Ascending packed
    /// order is (mod id, tier ordinal) order, so walking the tiers in it is what makes every bucket sorted
    /// by construction rather than by a sort pass of its own, and what makes the result independent of the
    /// order rows arrived in.
    /// <para>
    /// The BANDS come from every live tier row with a legal gate, which is spec 9.2 item 1 verbatim, so a
    /// tier whose mod is legacy or does not resolve still contributes its boundaries while contributing no
    /// entry.
    /// </para>
    /// </summary>
    static TierRows ReadTiers(IContentSnapshot snapshot, in ModRows mods, in WeightRows weights)
    {
        IReadOnlyList<ContentRow> rows = snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.ModTierTypeId));
        var boundaries = new SortedSet<int> { ModTierContentType.MinItemLevel, ModTierContentType.MaxItemLevel + 1 };
        var keys = new List<long>();
        var packed = new List<int>();
        var kindPositions = new List<int>();
        var lowest = new List<int>();
        var highest = new List<int>();
        var weightStart = new List<int>();
        var weightCount = new List<int>();

        foreach (ContentRow row in rows)
        {
            if (row.IsRetired)
            {
                continue;
            }

            long ordinal = InstanceContentChecks.Number(row, TierOrdinalField) ?? 0;
            if (ordinal > MaxTierOrdinal)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"{InstanceContentFindings.TierOrdinal}: mod tier {row.Id} carries ordinal {ordinal}, over the packed key's ceiling of {MaxTierOrdinal}. The candidate table packs a tier ordinal into {TierBits} bits beside the mod id, so an ordinal above the ceiling would alias onto another tier of the same mod."));
            }

            long low = InstanceContentChecks.Number(row, TierItemLevelMinField) ?? 0;
            long high = InstanceContentChecks.Number(row, TierItemLevelMaxField) ?? 0;
            if (low > high || low < ModTierContentType.MinItemLevel || high > ModTierContentType.MaxItemLevel)
            {
                // An empty gate, or one outside the legal item level range, is live at no item level at all,
                // so it contributes neither a band boundary nor an entry. A publish reports it as KEC0101.
                continue;
            }

            boundaries.Add((int)low);
            boundaries.Add((int)high + 1);

            // An ordinal below 1 is a tier no stored affix entry can name, so it is in no table. It is
            // SKIPPED rather than refused, unlike the ceiling above, because it aliases nothing.
            if (ordinal < ModTierContentType.MinOrdinal)
            {
                continue;
            }

            long modId = InstanceContentChecks.Number(row, TierModIdField) ?? 0;
            int modSlot = modId is < 1 or > int.MaxValue
                ? -1
                : GenerationTagSignature.IndexOf(mods.Ids, (int)modId);
            if (modSlot < 0 || mods.Legacy[modSlot])
            {
                continue;
            }

            if (mods.Ids[modSlot] > MaxModId)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Mod {mods.Ids[modSlot]} is over the packed key's mod id ceiling of {MaxModId}. The candidate table packs the mod id above {TierBits} bits of tier ordinal, so a larger id cannot be held in one int."));
            }

            int run = LowerBound(weights.TierIds, row.Id);
            int length = 0;
            while (run + length < weights.TierIds.Length && weights.TierIds[run + length] == row.Id)
            {
                length++;
            }

            if (length == 0)
            {
                // A tier with NO weight row can never spawn, which is legal and is how a tier reachable only
                // through crafting is authored.
                continue;
            }

            int entry = Pack(mods.Ids[modSlot], (int)ordinal);
            keys.Add(((long)entry << 32) + row.Id);
            packed.Add(entry);
            kindPositions.Add(GenerationTagSignature.IndexOf(mods.Kinds, mods.Kind[modSlot]));
            lowest.Add((int)low);
            highest.Add((int)high);
            weightStart.Add(run);
            weightCount.Add(length);
        }

        var bands = new int[boundaries.Count];
        boundaries.CopyTo(bands);

        long[] order = keys.ToArray();
        var slots = new int[order.Length];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = i;
        }

        Array.Sort(order, slots);
        var sorted = new TierRows(
            bands,
            new int[slots.Length],
            new int[slots.Length],
            new int[slots.Length],
            new int[slots.Length],
            new int[slots.Length],
            new int[slots.Length]);
        for (int i = 0; i < slots.Length; i++)
        {
            int from = slots[i];
            sorted.Packed[i] = packed[from];
            sorted.KindPosition[i] = kindPositions[from];
            sorted.LevelMin[i] = lowest[from];
            sorted.LevelMax[i] = highest[from];
            sorted.WeightStart[i] = weightStart[from];
            sorted.WeightCount[i] = weightCount[from];
        }

        return sorted;
    }

    /// <summary>
    /// The counting pass, the prefix sums and the fill, which together are spec 9.2 item 2. Three things are
    /// folded in here rather than tested per candidate: the kind, which IS the bucket, the legacy flag,
    /// which kept the tier out of <see cref="ReadTiers"/> entirely, and the running weight.
    /// </summary>
    static BuiltTables BuildBuckets(
        GenerationTagSignature signatures,
        in ModRows mods,
        in GroupRows groups,
        in WeightRows weights,
        in TierRows tiers)
    {
        int bandCount = tiers.Bands.Length - 1;
        var distinctTags = new SortedSet<int>();
        for (int tier = 0; tier < tiers.Packed.Length; tier++)
        {
            for (int slot = 0; slot < tiers.WeightCount[tier]; slot++)
            {
                distinctTags.Add(weights.TagIds[tiers.WeightStart[tier] + slot]);
            }
        }

        var tagIds = new int[distinctTags.Count];
        distinctTags.CopyTo(tagIds);
        int bucketCount = tagIds.Length * mods.Kinds.Length * bandCount;
        var lengths = new int[bucketCount];
        var firstBand = new int[tiers.Packed.Length];
        var lastBand = new int[tiers.Packed.Length];

        for (int tier = 0; tier < tiers.Packed.Length; tier++)
        {
            firstBand[tier] = BandOf(tiers.Bands, tiers.LevelMin[tier]);
            lastBand[tier] = BandOf(tiers.Bands, tiers.LevelMax[tier]);
            for (int slot = 0; slot < tiers.WeightCount[tier]; slot++)
            {
                int tag = GenerationTagSignature.IndexOf(tagIds, weights.TagIds[tiers.WeightStart[tier] + slot]);
                for (int band = firstBand[tier]; band <= lastBand[tier]; band++)
                {
                    lengths[BucketAt(tag, tiers.KindPosition[tier], band, mods.Kinds.Length, bandCount)]++;
                }
            }
        }

        var starts = new int[bucketCount];
        int running = 0;
        for (int bucket = 0; bucket < bucketCount; bucket++)
        {
            if (lengths[bucket] > MaxBucketEntries)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The candidate bucket for tag {tagIds[bucket / (mods.Kinds.Length * bandCount)]}, mod kind {mods.Kinds[(bucket / bandCount) % mods.Kinds.Length]} and band {bucket % bandCount} holds {lengths[bucket]} entries, over the ceiling of {MaxBucketEntries}. The overlap lists are 16 bit indices into a bucket, so a wider one cannot be addressed by one."));
            }

            starts[bucket] = running;
            running += lengths[bucket];
        }

        var packed = new int[running];
        var cumulative = new int[running];
        var fill = new int[bucketCount];
        for (int tier = 0; tier < tiers.Packed.Length; tier++)
        {
            for (int slot = 0; slot < tiers.WeightCount[tier]; slot++)
            {
                int index = tiers.WeightStart[tier] + slot;
                int tag = GenerationTagSignature.IndexOf(tagIds, weights.TagIds[index]);
                for (int band = firstBand[tier]; band <= lastBand[tier]; band++)
                {
                    int bucket = BucketAt(tag, tiers.KindPosition[tier], band, mods.Kinds.Length, bandCount);
                    int at = starts[bucket] + fill[bucket];

                    // The running total is summed in a long and stored in an int, which is safe exactly
                    // because KEC0113 bounds the rows sharing one tag and a bucket is a SUBSET of those. A
                    // version where that does not hold is refused here rather than saturating silently.
                    long total = (fill[bucket] == 0 ? 0L : cumulative[at - 1]) + weights.Values[index];
                    if (total > int.MaxValue)
                    {
                        throw new InvalidOperationException(FormattableString.Invariant(
                            $"{InstanceContentFindings.WeightBucketOverflow}: the candidate bucket for tag {weights.TagIds[index]}, mod kind {mods.Kinds[tiers.KindPosition[tier]]} and band {band} sums to {total}, past the int ceiling of {int.MaxValue}. A cumulative column that saturates changes every probability in the bucket."));
                    }

                    packed[at] = tiers.Packed[tier];
                    cumulative[at] = (int)total;
                    fill[bucket]++;
                }
            }
        }

        BuildGroups(mods, groups, out int[] groupIds, out int[] groupStart, out int[] groupMembers, out int[] groupMaxPerItem);
        return new BuiltTables
        {
            BandBoundaries = tiers.Bands,
            Kinds = mods.Kinds,
            TagIds = tagIds,
            EntryPacked = packed,
            EntryCumulative = cumulative,
            BucketStart = starts,
            BucketLength = lengths,
            ModIds = mods.Ids,
            ModKind = mods.Kind,
            ModGroup = mods.Group,
            GroupIds = groupIds,
            GroupStart = groupStart,
            GroupMembers = groupMembers,
            GroupMaxPerItem = groupMaxPerItem,
            Signatures = signatures,
        };
    }

    /// <summary>
    /// Group id to the mod ids in it, ascending, so enforcing a group's count is a lookup rather than a
    /// scan. A legacy mod is left out, because it is in no table to exclude from.
    /// </summary>
    static void BuildGroups(
        in ModRows mods,
        in GroupRows groups,
        out int[] groupIds,
        out int[] groupStart,
        out int[] groupMembers,
        out int[] groupMaxPerItem)
    {
        var distinct = new SortedSet<int>();
        int members = 0;
        for (int mod = 0; mod < mods.Ids.Length; mod++)
        {
            if (mods.Group[mod] <= 0 || mods.Legacy[mod])
            {
                continue;
            }

            distinct.Add(mods.Group[mod]);
            members++;
        }

        groupIds = new int[distinct.Count];
        distinct.CopyTo(groupIds);
        groupMaxPerItem = new int[groupIds.Length];
        for (int group = 0; group < groupIds.Length; group++)
        {
            int row = GenerationTagSignature.IndexOf(groups.Ids, groupIds[group]);

            // A group its own row does not resolve keeps a count of zero, which is what a publish already
            // refuses as KEC0100 on the mod and as KEC0105 on a count below one.
            groupMaxPerItem[group] = row < 0 ? 0 : groups.MaxPerItem[row];
        }

        var counts = new int[groupIds.Length];
        for (int mod = 0; mod < mods.Ids.Length; mod++)
        {
            if (mods.Group[mod] > 0 && !mods.Legacy[mod])
            {
                counts[GenerationTagSignature.IndexOf(groupIds, mods.Group[mod])]++;
            }
        }

        groupStart = new int[groupIds.Length + 1];
        int running = 0;
        for (int group = 0; group < groupIds.Length; group++)
        {
            groupStart[group] = running;
            running += counts[group];
        }

        groupStart[^1] = running;
        groupMembers = new int[members];
        var fill = new int[groupIds.Length];

        // The mods are ascending by id already, so each group's member run comes out ascending too.
        for (int mod = 0; mod < mods.Ids.Length; mod++)
        {
            if (mods.Group[mod] <= 0 || mods.Legacy[mod])
            {
                continue;
            }

            int group = GenerationTagSignature.IndexOf(groupIds, mods.Group[mod]);
            groupMembers[groupStart[group] + fill[group]++] = mods.Ids[mod];
        }
    }

    /// <summary>The band one item level falls in, over a boundary array the instance does not hold yet.</summary>
    static int BandOf(int[] boundaries, int itemLevel)
    {
        int low = 0;
        int high = boundaries.Length - 2;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (boundaries[middle] <= itemLevel)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low < 0 ? 0 : low;
    }

    static int BucketAt(int tagPosition, int kindPosition, int band, int kindCount, int bandCount)
        => (((tagPosition * kindCount) + kindPosition) * bandCount) + band;

    /// <summary>The first index of a value in an ascending column, or where it would go.</summary>
    static int LowerBound(int[] ascending, int value)
    {
        int low = 0;
        int high = ascending.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (ascending[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>A row's number as an int, saturating rather than wrapping on a value no schema can hold.</summary>
    static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    /// <summary>The live <c>mod</c> rows as columns, ascending by id.</summary>
    readonly struct ModRows(int[] ids, int[] kind, int[] group, bool[] legacy, int[] kinds)
    {
        public int[] Ids { get; } = ids;

        public int[] Kind { get; } = kind;

        public int[] Group { get; } = group;

        public bool[] Legacy { get; } = legacy;

        /// <summary>The distinct kinds, ascending, which is the kind axis of the bucket space.</summary>
        public int[] Kinds { get; } = kinds;
    }

    /// <summary>The live <c>mod_group</c> rows as columns, ascending by id.</summary>
    readonly struct GroupRows(int[] ids, int[] maxPerItem)
    {
        public int[] Ids { get; } = ids;

        public int[] MaxPerItem { get; } = maxPerItem;
    }

    /// <summary>The live <c>mod_tier_weight</c> rows as columns, ordered by (tier id, row id).</summary>
    readonly struct WeightRows(int[] tierIds, int[] tagIds, long[] values)
    {
        public int[] TierIds { get; } = tierIds;

        public int[] TagIds { get; } = tagIds;

        public long[] Values { get; } = values;
    }

    /// <summary>The tiers that can put an entry in a table, ordered by packed key, plus the band boundaries.</summary>
    readonly struct TierRows(
        int[] bands,
        int[] packed,
        int[] kindPosition,
        int[] levelMin,
        int[] levelMax,
        int[] weightStart,
        int[] weightCount)
    {
        public int[] Bands { get; } = bands;

        public int[] Packed { get; } = packed;

        public int[] KindPosition { get; } = kindPosition;

        public int[] LevelMin { get; } = levelMin;

        public int[] LevelMax { get; } = levelMax;

        public int[] WeightStart { get; } = weightStart;

        public int[] WeightCount { get; } = weightCount;
    }

    /// <summary>Everything the constructor takes, named rather than positional so a column cannot swap.</summary>
    sealed class BuiltTables
    {
        public int[] BandBoundaries { get; init; } = [];

        public int[] Kinds { get; init; } = [];

        public int[] TagIds { get; init; } = [];

        public int[] EntryPacked { get; init; } = [];

        public int[] EntryCumulative { get; init; } = [];

        public int[] BucketStart { get; init; } = [];

        public int[] BucketLength { get; init; } = [];

        public int[] ModIds { get; init; } = [];

        public int[] ModKind { get; init; } = [];

        public int[] ModGroup { get; init; } = [];

        public int[] GroupIds { get; init; } = [];

        public int[] GroupStart { get; init; } = [];

        public int[] GroupMembers { get; init; } = [];

        public int[] GroupMaxPerItem { get; init; } = [];

        public GenerationTagSignature Signatures { get; init; } = GenerationTagSignature.Empty;
    }
}
