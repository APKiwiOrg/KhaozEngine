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
    private const int GroupSlots = 256;

    private readonly SyntheticContent content;
    private readonly ModCandidateTables tables;
    private readonly RareNameTables names;
    private readonly IRandomSource random;
    private readonly InstanceIdAllocator allocator;

    private readonly int[] affixMod = new int[MaximumAffixes];
    private readonly int[] affixTier = new int[MaximumAffixes];
    private readonly ushort[] affixPosition = new ushort[MaximumAffixes];
    private readonly int[] nameWords = new int[RareNameTables.Positions];
    private readonly int[] groupStamp = new int[GroupSlots];
    private readonly int[] groupCount = new int[GroupSlots];
    private readonly int[] rarityCumulative;
    private readonly byte[] payloadBuffer = new byte[InstancePayload.MaximumPayloadBytes];
    private readonly byte[] affixBody = new byte[MaximumAffixes * 8];
    private readonly byte[] socketBody = new byte[64];
    private readonly byte[] nameBody = new byte[32];
    private int[] prefixIndex = new int[1_024];
    private int[] prefixCumulative = new int[1_024];
    private int[] suffixIndex = new int[1_024];
    private int[] suffixCumulative = new int[1_024];
    private int generation;

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
    internal long LastPoolSize { get; private set; }

    /// <summary>Every candidate the filter pass of step 6 has looked at, which is what budget 5's cost is made of.</summary>
    internal long CandidateVisits { get; private set; }

    internal GenerationResult Generate(in GenerationContext context)
    {
        int baseIndex = context.BaseIndex;
        int band = tables.BandOf(context.ItemLevel);                                   // step 1
        ModCandidate[] pool = tables.Merge(baseIndex, band);                            // steps 2 and 6's input
        LastPoolSize = pool.Length;
        EnsureScratch(pool.Length);

        int rarityId = context.ForcedRarityId != 0 ? context.ForcedRarityId : RollRarity(baseIndex);   // step 3
        int rarityIndex = rarityId - 1;
        int minimum = content.RarityMinAffixes[rarityIndex];
        int maximum = content.RarityMaxAffixes[rarityIndex];
        int count = random.NextInt(minimum, maximum + 1);                               // step 4
        int prefixCap = content.RarityMaxPrefixes[rarityIndex];
        int suffixCap = content.RarityMaxSuffixes[rarityIndex];

        generation++;
        int placed = 0;
        int prefixesPlaced = 0;
        int suffixesPlaced = 0;
        for (int pick = 0; pick < count; pick++)
        {
            CandidateVisits += pool.Length;
            int prefixCount = 0;
            int suffixCount = 0;
            int prefixTotal = 0;
            int suffixTotal = 0;
            for (int index = 0; index < pool.Length; index++)                           // step 6, one pass
            {
                ModCandidate candidate = pool[index];
                int modIndex = candidate.ModId - 1;
                if (content.ModLegacy[modIndex]) continue;
                if (IsPlaced(candidate.ModId, placed)) continue;
                int group = content.ModGroup[modIndex];
                if (group != 0 && groupStamp[group & (GroupSlots - 1)] == generation && groupCount[group & (GroupSlots - 1)] >= 1) continue;
                if (content.ModKind[modIndex] == 1)
                {
                    prefixTotal += candidate.Weight;
                    prefixIndex[prefixCount] = index;
                    prefixCumulative[prefixCount++] = prefixTotal;
                }
                else
                {
                    suffixTotal += candidate.Weight;
                    suffixIndex[suffixCount] = index;
                    suffixCumulative[suffixCount++] = suffixTotal;
                }
            }

            bool prefixOpen = prefixesPlaced < prefixCap && prefixCount > 0;
            bool suffixOpen = suffixesPlaced < suffixCap && suffixCount > 0;
            int openTotal = (prefixOpen ? prefixCount : 0) + (suffixOpen ? suffixCount : 0);
            int kindDraw = random.NextInt(0, Math.Max(openTotal, 1));                   // step 5
            bool takePrefix = prefixOpen && (!suffixOpen || kindDraw < prefixCount);

            int[] chosenIndex = takePrefix ? prefixIndex : suffixIndex;
            int[] chosenCumulative = takePrefix ? prefixCumulative : suffixCumulative;
            int chosenCount = takePrefix ? prefixCount : suffixCount;
            int chosenTotal = chosenCount == 0 ? 0 : chosenCumulative[chosenCount - 1];

            if (openTotal == 0 || chosenCount == 0 || chosenTotal == 0)
            {
                _ = random.NextInt(0, 1);                                               // step 8's two discards
                _ = random.NextRollPosition();
                continue;
            }

            int weightDraw = random.NextInt(0, chosenTotal);                            // step 7
            int slot = LowerBound(chosenCumulative, chosenCount, weightDraw);
            ModCandidate chosen = pool[chosenIndex[slot]];
            ushort position = random.NextRollPosition();                                // step 8

            affixMod[placed] = chosen.ModId;
            affixTier[placed] = chosen.TierOrdinal;
            affixPosition[placed] = position;
            placed++;
            if (takePrefix) prefixesPlaced++;
            else suffixesPlaced++;
            int chosenGroup = content.ModGroup[chosen.ModId - 1];
            if (chosenGroup != 0)
            {
                int groupSlot = chosenGroup & (GroupSlots - 1);
                if (groupStamp[groupSlot] != generation)
                {
                    groupStamp[groupSlot] = generation;
                    groupCount[groupSlot] = 0;
                }

                groupCount[groupSlot]++;
            }
        }

        SortAffixes(placed);                                                            // step 9
        int wordCount = RollName(baseIndex, content.RarityNameWords[rarityIndex]);       // step 10
        int payloadLength = Assemble(context, rarityId, placed, wordCount);              // steps 11 and 12
        var payload = new byte[payloadLength];
        payloadBuffer.AsSpan(0, payloadLength).CopyTo(payload);
        long instanceId = payloadLength == 0 ? 0 : allocator.Next();                     // step 13
        return new GenerationResult(content.BaseIdOf(baseIndex), instanceId, payload, rarityId, placed);
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

    private bool IsPlaced(int modId, int placed)
    {
        for (int affix = 0; affix < placed; affix++)
            if (affixMod[affix] == modId) return true;
        return false;
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

    private void EnsureScratch(int poolLength)
    {
        if (prefixIndex.Length >= poolLength) return;
        prefixIndex = new int[poolLength];
        prefixCumulative = new int[poolLength];
        suffixIndex = new int[poolLength];
        suffixCumulative = new int[poolLength];
    }

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
