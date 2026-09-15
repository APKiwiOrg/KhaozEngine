using System;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct GenerationContext(int BaseIndex, int ItemLevel, int ForcedRarityId, int Quality);

internal readonly record struct GenerationResult(int BaseId, long InstanceId, byte[] Payload, int RarityId, int AffixCount);

/// <summary>
/// Spec 9.4's thirteen steps, in order, because the ORDER of the draws is the reproducibility contract.
/// It takes its <see cref="IRandomSource"/> in the constructor and holds it, which is contracts 14.4.
/// The only per-generation allocation is the payload buffer: every working array is scratch the
/// generator owns.
/// </summary>
internal sealed class SpikeItemGenerator
{
    private const int MaximumAffixes = 8;

    /// <summary>One slot per (kind, tag position), which is the shape every per roll counter has.</summary>
    private const int Slots = ModCandidateTables.KindCount * SyntheticContent.MaximumBaseTags;

    /// <summary>Excluded runs held per slot. A placement excludes one run per slot, and its group excludes one more per member.</summary>
    private const int MaximumExcludedRuns = 64;

    private readonly SyntheticContent content;
    private readonly ModCandidateTables tables;
    private readonly RareNameTables names;
    private readonly IRandomSource random;
    private readonly InstanceIdAllocator allocator;

    private readonly int[] affixMod = new int[MaximumAffixes];
    private readonly int[] affixTier = new int[MaximumAffixes];
    private readonly ushort[] affixPosition = new ushort[MaximumAffixes];
    private readonly int[] nameWords = new int[RareNameTables.Positions];
    private readonly int[] rarityCumulative;
    private readonly byte[] payloadBuffer = new byte[InstancePayload.MaximumPayloadBytes];
    private readonly byte[] affixBody = new byte[MaximumAffixes * 8];
    private readonly byte[] socketBody = new byte[64];
    private readonly byte[] nameBody = new byte[32];
    private readonly int[] slotStart = new int[Slots];
    private readonly int[] slotLength = new int[Slots];
    private readonly int[] slotLiveWeight = new int[Slots];
    private readonly int[] slotSuppressStart = new int[Slots];
    private readonly int[] slotSuppressCount = new int[Slots];
    private readonly int[] runFirst = new int[Slots * MaximumExcludedRuns];
    private readonly int[] runLast = new int[Slots * MaximumExcludedRuns];
    private readonly int[] runWeight = new int[Slots * MaximumExcludedRuns];
    private readonly int[] runCount = new int[Slots];
    private readonly int[] liveCount = new int[ModCandidateTables.KindCount];
    private int tagCount;

    internal SpikeItemGenerator(
        SyntheticContent content,
        ModCandidateTables tables,
        RareNameTables names,
        IRandomSource random,
        InstanceIdAllocator allocator)
    {
        this.content = content;
        this.tables = tables;
        this.names = names;
        this.random = random;
        this.allocator = allocator;
        rarityCumulative = new int[content.RarityCount];
    }

    internal int ContentVersion { get; init; } = 7;

    /// <summary>Live candidates the roll drew from, both kinds, which is the pool 9.2 never materialises.</summary>
    internal long LastPoolSize { get; private set; }

    /// <summary>Dead entries walked by step 7's shift, which is what a pick costs beyond its binary search.</summary>
    internal long DeadEntriesWalked { get; private set; }

    /// <summary>A pick that could not seat a run because the exclusion list was full. It must stay at zero.</summary>
    internal long ExclusionOverflows { get; private set; }

    internal GenerationResult Generate(in GenerationContext context)
    {
        int baseIndex = context.BaseIndex;
        int band = tables.BandOf(context.ItemLevel);                                   // step 1
        OpenPool(baseIndex, band);                                                      // step 6's tables, no merge

        int rarityId = context.ForcedRarityId != 0 ? context.ForcedRarityId : RollRarity(baseIndex);   // step 3
        int rarityIndex = rarityId - 1;
        int minimum = content.RarityMinAffixes[rarityIndex];
        int maximum = content.RarityMaxAffixes[rarityIndex];
        int count = random.NextInt(minimum, maximum + 1);                               // step 4
        int prefixCap = content.RarityMaxPrefixes[rarityIndex];
        int suffixCap = content.RarityMaxSuffixes[rarityIndex];

        int placed = 0;
        int prefixesPlaced = 0;
        int suffixesPlaced = 0;
        for (int pick = 0; pick < count; pick++)
        {
            int prefixLive = liveCount[0];
            int suffixLive = liveCount[1];
            bool prefixOpen = prefixesPlaced < prefixCap && prefixLive > 0;
            bool suffixOpen = suffixesPlaced < suffixCap && suffixLive > 0;
            int openTotal = (prefixOpen ? prefixLive : 0) + (suffixOpen ? suffixLive : 0);
            int kindDraw = random.NextInt(0, Math.Max(openTotal, 1));                   // step 5
            bool takePrefix = prefixOpen && (!suffixOpen || kindDraw < prefixLive);
            int kind = takePrefix ? 0 : 1;
            int liveWeight = 0;
            for (int tag = 0; tag < tagCount; tag++) liveWeight += slotLiveWeight[(kind * SyntheticContent.MaximumBaseTags) + tag];

            if (openTotal == 0 || liveWeight <= 0)
            {
                _ = random.NextInt(0, 1);                                               // step 8's two discards
                _ = random.NextRollPosition();
                continue;
            }

            int weightDraw = random.NextInt(0, liveWeight);                             // step 7
            int entry = Resolve(kind, weightDraw);
            int modId = entry >> ModCandidateTables.TierBits;
            ushort position = random.NextRollPosition();                                // step 8

            affixMod[placed] = modId;
            affixTier[placed] = entry & ModCandidateTables.TierMask;
            affixPosition[placed] = position;
            placed++;
            if (takePrefix) prefixesPlaced++;
            else suffixesPlaced++;

            Exclude(modId);
            int group = content.ModGroup[modId - 1];
            if (group == 0) continue;
            foreach (int member in tables.GroupMembers(group)) Exclude(member);
        }

        SortAffixes(placed);                                                            // step 9
        int wordCount = RollName(baseIndex, content.RarityNameWords[rarityIndex]);       // step 10
        int payloadLength = Assemble(context, rarityId, placed, wordCount);              // steps 11 and 12
        var payload = new byte[payloadLength];
        payloadBuffer.AsSpan(0, payloadLength).CopyTo(payload);
        long instanceId = payloadLength == 0 ? 0 : allocator.Next();                     // step 13
        return new GenerationResult(content.BaseIdOf(baseIndex), instanceId, payload, rarityId, placed);
    }

    /// <summary>
    /// Step 2 of 9.4: the base's tag tables for this band, one per (kind, tag position), with the
    /// precomputed overlap already deducted. Nothing is merged and nothing is copied, so a roll starts
    /// at a fixed cost in the base's tag count rather than one in the size of its candidate pool.
    /// </summary>
    private void OpenPool(int baseIndex, int band)
    {
        int signature = content.BaseTagSignature[baseIndex];
        int tagStart = content.BaseTagStart[baseIndex];
        tagCount = content.BaseTagCount[baseIndex];
        long pool = 0;
        for (int kind = 0; kind < ModCandidateTables.KindCount; kind++)
        {
            int live = 0;
            for (int tag = 0; tag < SyntheticContent.MaximumBaseTags; tag++)
            {
                int slot = (kind * SyntheticContent.MaximumBaseTags) + tag;
                runCount[slot] = 0;
                if (tag >= tagCount)
                {
                    slotLength[slot] = 0;
                    slotLiveWeight[slot] = 0;
                    slotSuppressCount[slot] = 0;
                    continue;
                }

                int bucket = tables.BucketOf(content.BaseTags[tagStart + tag], kind, band);
                int header = tables.HeaderOf(signature, kind, band, tag);
                slotStart[slot] = tables.BucketStartAt(bucket);
                slotLength[slot] = tables.BucketLengthAt(bucket);
                slotSuppressStart[slot] = tables.SuppressStartAt(header);
                slotSuppressCount[slot] = tables.SuppressCountAt(header);
                slotLiveWeight[slot] = tables.BucketTotalAt(bucket) - tables.SuppressWeightAt(header);
                live += slotLength[slot] - slotSuppressCount[slot];
            }

            liveCount[kind] = live;
            pool += live;
        }

        LastPoolSize = pool;
    }

    /// <summary>
    /// Step 7's draw over a pool with the overlap and the exclusions SUBTRACTED rather than filtered.
    /// The draw picks a tag position by its live weight, then walks that table's dead entries in index
    /// order to shift the draw back into the table's own cumulative array before the binary search. The
    /// landing entry is live by construction: consecutive dead entries share one shifted position, so a
    /// dead entry is either wholly behind the draw or wholly ahead of it.
    /// </summary>
    private int Resolve(int kind, int draw)
    {
        int slot = kind * SyntheticContent.MaximumBaseTags;
        while (draw >= slotLiveWeight[slot])
        {
            draw -= slotLiveWeight[slot];
            slot++;
        }

        int[] cumulative = tables.EntryCumulative;
        ushort[] suppressed = tables.SuppressIndex;
        int start = slotStart[slot];
        int suppressCursor = slotSuppressStart[slot];
        int suppressEnd = suppressCursor + slotSuppressCount[slot];
        int runCursor = slot * MaximumExcludedRuns;
        int runEnd = runCursor + runCount[slot];
        int shift = 0;
        int walked = 0;
        while (true)
        {
            int suppressIndex = suppressCursor < suppressEnd ? suppressed[suppressCursor] : int.MaxValue;
            int runIndex = runCursor < runEnd ? runFirst[runCursor] : int.MaxValue;
            if (suppressIndex == int.MaxValue && runIndex == int.MaxValue) break;
            walked++;
            if (suppressIndex < runIndex)
            {
                int before = suppressIndex == 0 ? 0 : cumulative[start + suppressIndex - 1];
                if (before - shift > draw) break;
                shift += cumulative[start + suppressIndex] - before;
                suppressCursor++;
                continue;
            }

            int runBefore = runIndex == 0 ? 0 : cumulative[start + runIndex - 1];
            if (runBefore - shift > draw) break;
            shift += runWeight[runCursor];
            int last = runLast[runCursor];
            while (suppressCursor < suppressEnd && suppressed[suppressCursor] < last) suppressCursor++;
            runCursor++;
        }

        DeadEntriesWalked += walked;
        return tables.EntryPacked[start + LowerBound(cumulative, start, slotLength[slot], draw + shift)];
    }

    /// <summary>
    /// Removes every tier of one mod from every tag table of its kind BEFORE the next draw, which is
    /// 9.3's filter-before-draw rule. The tiers of a mod are contiguous in a table, because it is sorted
    /// by the packed (mod id, tier ordinal) key, so one run covers them all, and an entry the overlap
    /// already suppressed is not deducted twice.
    /// </summary>
    private void Exclude(int modId)
    {
        int kind = content.ModKind[modId - 1] - 1;
        int[] packed = tables.EntryPacked;
        int[] cumulative = tables.EntryCumulative;
        ushort[] suppressed = tables.SuppressIndex;
        int target = modId << ModCandidateTables.TierBits;
        for (int tag = 0; tag < tagCount; tag++)
        {
            int slot = (kind * SyntheticContent.MaximumBaseTags) + tag;
            int length = slotLength[slot];
            if (length == 0) continue;
            int start = slotStart[slot];
            int first = LowerBoundKey(packed, start, length, target);
            if (first >= length || (packed[start + first] >> ModCandidateTables.TierBits) != modId) continue;
            int last = first + 1;
            while (last < length && (packed[start + last] >> ModCandidateTables.TierBits) == modId) last++;

            int runCursor = slot * MaximumExcludedRuns;
            int runs = runCount[slot];
            int position = runs;
            bool seated = false;
            for (int run = 0; run < runs; run++)
            {
                if (runFirst[runCursor + run] == first)
                {
                    seated = true;
                    break;
                }

                if (runFirst[runCursor + run] > first)
                {
                    position = run;
                    break;
                }
            }

            if (seated) continue;
            if (runs == MaximumExcludedRuns)
            {
                ExclusionOverflows++;
                continue;
            }

            int weight = cumulative[start + last - 1] - (first == 0 ? 0 : cumulative[start + first - 1]);
            int suppressStart = slotSuppressStart[slot];
            int suppressEnd = suppressStart + slotSuppressCount[slot];
            int suppressedWeight = 0;
            int suppressedEntries = 0;
            for (int index = suppressStart; index < suppressEnd; index++)
            {
                int entry = suppressed[index];
                if (entry < first) continue;
                if (entry >= last) break;
                suppressedWeight += cumulative[start + entry] - (entry == 0 ? 0 : cumulative[start + entry - 1]);
                suppressedEntries++;
            }

            for (int run = runs; run > position; run--)
            {
                runFirst[runCursor + run] = runFirst[runCursor + run - 1];
                runLast[runCursor + run] = runLast[runCursor + run - 1];
                runWeight[runCursor + run] = runWeight[runCursor + run - 1];
            }

            runFirst[runCursor + position] = first;
            runLast[runCursor + position] = last;
            runWeight[runCursor + position] = weight;
            runCount[slot] = runs + 1;
            slotLiveWeight[slot] -= weight - suppressedWeight;
            liveCount[kind] -= last - first - suppressedEntries;
        }
    }

    private int RollRarity(int baseIndex)
    {
        int tagStart = content.BaseTagStart[baseIndex];
        int tagCount = content.BaseTagCount[baseIndex];
        int running = 0;
        for (int rarity = 0; rarity < content.RarityCount; rarity++)
        {
            int weight = 0;
            for (int tag = 0; tag < tagCount && weight == 0; tag++)
                weight = content.RarityWeight[(rarity * content.TagCount) + content.BaseTags[tagStart + tag] - 1];
            running += weight;
            rarityCumulative[rarity] = running;
        }

        if (running == 0) return 1;
        int draw = random.NextInt(0, running);
        return LowerBound(rarityCumulative, content.RarityCount, draw) + 1;
    }

    private int RollName(int baseIndex, int positions)
    {
        if (positions == 0) return 0;
        RareNameTables.Entry entry = names.For(baseIndex);
        int placed = 0;
        for (int position = 0; position < positions && position < RareNameTables.Positions; position++)
        {
            int[] cumulative = entry.Cumulative[position];
            if (cumulative.Length == 0)
            {
                _ = random.NextInt(0, 1);
                continue;
            }

            int draw = random.NextInt(0, cumulative[^1]);
            nameWords[placed++] = entry.WordIds[position][LowerBound(cumulative, cumulative.Length, draw)];
        }

        return placed;
    }

    private int Assemble(in GenerationContext context, int rarityId, int affixCount, int wordCount)
    {
        Span<byte> destination = payloadBuffer;
        Span<byte> scalar = stackalloc byte[Varint.MaximumBytes64 * 2];
        int written = 0;

        int scalarLength = Varint.Write(scalar, (ulong)(uint)context.ItemLevel);
        written += InstancePayload.WriteField(destination[written..], InstanceKinds.ItemLevel, scalar[..scalarLength]);

        if (context.Quality > 0)
        {
            scalarLength = Varint.Write(scalar, (ulong)(uint)context.Quality);
            written += InstancePayload.WriteField(destination[written..], InstanceKinds.Quality, scalar[..scalarLength]);
        }

        if (content.BaseHasDurability[context.BaseIndex])
        {
            scalarLength = Varint.Write(scalar, 100);
            scalarLength += Varint.Write(scalar[scalarLength..], 100);
            written += InstancePayload.WriteField(destination[written..], InstanceKinds.Durability, scalar[..scalarLength]);
        }

        scalar[0] = (byte)rarityId;
        written += InstancePayload.WriteField(destination[written..], InstanceKinds.Rarity, scalar[..1]);

        if (affixCount > 0)
        {
            Span<byte> body = affixBody;
            body[0] = (byte)affixCount;
            int bodyLength = 1;
            for (int affix = 0; affix < affixCount; affix++)
                bodyLength += InstancePayload.WriteAffixEntry(body[bodyLength..], affixMod[affix], (byte)affixTier[affix], affixPosition[affix]);
            written += InstancePayload.WriteField(destination[written..], InstanceKinds.Affixes, body[..bodyLength]);
        }

        int sockets = content.BaseSocketCount[context.BaseIndex];
        if (sockets > 0)
        {
            Span<byte> body = socketBody;
            int bodyLength = Varint.Write(body, (ulong)(uint)sockets);
            for (int socket = 0; socket < sockets; socket++)
            {
                bodyLength += Varint.Write(body[bodyLength..], (ulong)(uint)(socket + 1));
                bodyLength += Varint.Write(body[bodyLength..], 0);
                bodyLength += Varint.Write(body[bodyLength..], 0);
                bodyLength += Varint.Write(body[bodyLength..], 0);
            }

            written += InstancePayload.WriteField(destination[written..], InstanceKinds.Sockets, body[..bodyLength]);
        }

        if (wordCount > 0)
        {
            Span<byte> body = nameBody;
            int bodyLength = Varint.Write(body, (ulong)(uint)rarityId);
            body[bodyLength++] = (byte)wordCount;
            for (int word = 0; word < wordCount; word++)
                bodyLength += Varint.Write(body[bodyLength..], (ulong)(uint)nameWords[word]);
            written += InstancePayload.WriteField(destination[written..], InstanceKinds.RareName, body[..bodyLength]);
        }

        return written;
    }

    private void SortAffixes(int count)
    {
        for (int outer = 1; outer < count; outer++)
        {
            int mod = affixMod[outer];
            int tier = affixTier[outer];
            ushort position = affixPosition[outer];
            int inner = outer - 1;
            while (inner >= 0 && affixMod[inner] > mod)
            {
                affixMod[inner + 1] = affixMod[inner];
                affixTier[inner + 1] = affixTier[inner];
                affixPosition[inner + 1] = affixPosition[inner];
                inner--;
            }

            affixMod[inner + 1] = mod;
            affixTier[inner + 1] = tier;
            affixPosition[inner + 1] = position;
        }
    }

    /// <summary>The first index whose cumulative weight exceeds the draw, which is the weighted pick of 9.3.</summary>
    private static int LowerBound(int[] cumulative, int count, int draw)
    {
        int low = 0;
        int high = count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (cumulative[middle] <= draw) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    /// <summary>The same, over one bucket of the flat table.</summary>
    private static int LowerBound(int[] cumulative, int start, int count, int draw)
    {
        int low = 0;
        int high = count - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (cumulative[start + middle] <= draw) low = middle + 1;
            else high = middle;
        }

        return low;
    }

    /// <summary>The first index whose packed key is at or past the target, which finds a mod's run of tiers.</summary>
    private static int LowerBoundKey(int[] packed, int start, int count, int target)
    {
        int low = 0;
        int high = count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (packed[start + middle] < target) low = middle + 1;
            else high = middle;
        }

        return low;
    }
}

/// <summary>
/// Contracts 6.2's scheme: <c>(node &lt;&lt; 48) | counter</c>, counter from 1, never recycled, with a
/// block reserved before any id in it is issued. The spike persists nothing, so it models the reserve
/// as the block boundary alone.
/// </summary>
internal sealed class InstanceIdAllocator
{
    private const long BlockSize = 4_096;
    private readonly long nodePrefix;
    private long counter;
    private long reservedThrough;

    internal InstanceIdAllocator(ushort node, long firstCounter = 1)
    {
        nodePrefix = (long)node << 48;
        counter = firstCounter - 1;
        reservedThrough = firstCounter - 1;
    }

    internal long Reservations { get; private set; }

    internal long Next()
    {
        counter++;
        if (counter > reservedThrough)
        {
            reservedThrough += BlockSize;
            Reservations++;
        }

        return nodePrefix | counter;
    }
}
