using System;
using System.Collections.Generic;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The synthetic content set the spike rolls against: the owner's scale of 50,000 bases and 2,000 mods
/// with the child types spec 8.2 through 8.8 name, built deterministically from one seed so a run is
/// reproducible. Every row set is a flat array, because the generator's table build is a scan of three
/// indexed row sets (spec 9.2) rather than a decode of a blob per mod.
/// </summary>
internal sealed class SyntheticContent
{
    internal const int TiersPerMod = 8;
    internal const int WeightsPerTier = 5;
    internal const int LinesPerTier = 2;
    internal const int MaximumBaseTags = 4;

    private static readonly int[] LevelBreakpoints =
    {
        1, 2, 4, 6, 8, 11, 14, 17, 20, 24, 28, 32, 36, 40, 44, 48,
        52, 56, 60, 64, 68, 72, 75, 78, 81, 84, 87, 90, 93, 96, 98, 100,
    };

    private SyntheticContent(int modCount, int baseCount, int tagCount, int rarityCount, int nameWordCount, int statCount)
    {
        ModCount = modCount;
        BaseCount = baseCount;
        TagCount = tagCount;
        RarityCount = rarityCount;
        NameWordCount = nameWordCount;
        StatCount = statCount;
        ModKind = new byte[modCount];
        ModGroup = new int[modCount];
        ModLegacy = new bool[modCount];
        TierLevelMin = new int[modCount * TiersPerMod];
        TierLevelMax = new int[modCount * TiersPerMod];
        TierWeightTag = new int[modCount * TiersPerMod * WeightsPerTier];
        TierWeightValue = new int[modCount * TiersPerMod * WeightsPerTier];
        TierLineStat = new int[modCount * TiersPerMod * LinesPerTier];
        TierLineCombine = new byte[modCount * TiersPerMod * LinesPerTier];
        TierLineMin = new int[modCount * TiersPerMod * LinesPerTier];
        TierLineMax = new int[modCount * TiersPerMod * LinesPerTier];
        TierLineCount = new byte[modCount * TiersPerMod];
        BaseTagStart = new int[baseCount];
        BaseTagCount = new byte[baseCount];
        BaseTagSignature = new int[baseCount];
        BaseHasDurability = new bool[baseCount];
        BaseSocketCount = new byte[baseCount];
        RarityMinAffixes = new byte[rarityCount];
        RarityMaxAffixes = new byte[rarityCount];
        RarityMaxPrefixes = new byte[rarityCount];
        RarityMaxSuffixes = new byte[rarityCount];
        RarityNameWords = new byte[rarityCount];
        RarityWeight = new int[rarityCount * tagCount];
        WordPosition = new byte[nameWordCount];
        WordWeight = new int[nameWordCount * tagCount];
    }

    internal int ModCount { get; }
    internal int BaseCount { get; }
    internal int TagCount { get; }
    internal int RarityCount { get; }
    internal int NameWordCount { get; }
    internal int StatCount { get; }
    internal byte[] ModKind { get; }
    internal int[] ModGroup { get; }
    internal bool[] ModLegacy { get; }
    internal int[] TierLevelMin { get; }
    internal int[] TierLevelMax { get; }
    internal int[] TierWeightTag { get; }
    internal int[] TierWeightValue { get; }
    internal int[] TierLineStat { get; }
    internal byte[] TierLineCombine { get; }
    internal int[] TierLineMin { get; }
    internal int[] TierLineMax { get; }
    internal byte[] TierLineCount { get; }
    internal int[] BaseTags { get; private set; } = Array.Empty<int>();
    internal int[] BaseTagStart { get; }
    internal byte[] BaseTagCount { get; }
    internal int[] BaseTagSignature { get; }
    internal bool[] BaseHasDurability { get; }
    internal byte[] BaseSocketCount { get; }
    internal byte[] RarityMinAffixes { get; }
    internal byte[] RarityMaxAffixes { get; }
    internal byte[] RarityMaxPrefixes { get; }
    internal byte[] RarityMaxSuffixes { get; }
    internal byte[] RarityNameWords { get; }
    internal int[] RarityWeight { get; }
    internal byte[] WordPosition { get; }
    internal int[] WordWeight { get; }
    internal int DistinctTagSignatures { get; private set; }

    internal int ModIdOf(int modIndex) => modIndex + 1;

    internal int ModIndexOf(int modId) => modId - 1;

    internal int BaseIdOf(int baseIndex) => baseIndex + 1;

    internal static SyntheticContent Build(int seed, int modCount, int baseCount)
    {
        const int tagCount = 64;
        const int rarityCount = 20;
        const int nameWordCount = 200;
        const int statCount = 60;
        var content = new SyntheticContent(modCount, baseCount, tagCount, rarityCount, nameWordCount, statCount);
        var rng = new DeterministicRng(unchecked((ulong)seed * 0x9E3779B97F4A7C15UL) + 0x1234_5678UL);
        content.BuildMods(rng);
        content.BuildBases(rng);
        content.BuildRarities(rng);
        content.BuildNameWords(rng);
        return content;
    }

    private void BuildMods(DeterministicRng rng)
    {
        for (int modIndex = 0; modIndex < ModCount; modIndex++)
        {
            ModKind[modIndex] = (byte)(rng.Next(0, 2) + 1);
            ModGroup[modIndex] = rng.Next(0, 4) == 0 ? rng.Next(1, 200) : 0;
            ModLegacy[modIndex] = rng.Next(0, 100) < 3;
            for (int ordinal = 0; ordinal < TiersPerMod; ordinal++)
            {
                int tier = (modIndex * TiersPerMod) + ordinal;
                int low = rng.Next(0, LevelBreakpoints.Length - 8);
                int high = low + rng.Next(6, Math.Min(LevelBreakpoints.Length - low, 18));
                TierLevelMin[tier] = LevelBreakpoints[low];
                TierLevelMax[tier] = LevelBreakpoints[Math.Min(high, LevelBreakpoints.Length - 1)];
                for (int weight = 0; weight < WeightsPerTier; weight++)
                {
                    int slot = (tier * WeightsPerTier) + weight;
                    TierWeightTag[slot] = rng.Next(1, TagCount + 1);
                    TierWeightValue[slot] = rng.Next(1, 1_001);
                }

                byte lines = (byte)(rng.Next(0, 3) == 0 ? 2 : 1);
                TierLineCount[tier] = lines;
                for (int line = 0; line < lines; line++)
                {
                    int slot = (tier * LinesPerTier) + line;
                    TierLineStat[slot] = rng.Next(1, StatCount + 1);
                    byte combine = (byte)(rng.Next(0, 10) switch
                    {
                        < 6 => 1,
                        < 9 => 2,
                        _ => 3,
                    });
                    TierLineCombine[slot] = combine;
                    int low2 = combine == 1 ? rng.Next(1, 200) : rng.Next(100, 2_000);
                    TierLineMin[slot] = low2;
                    TierLineMax[slot] = low2 + rng.Next(1, 400);
                }
            }
        }
    }

    /// <summary>
    /// Tag lists are AUTHORED, so a catalog of 50,000 bases carries a few hundred distinct tag
    /// signatures rather than 50,000 of them (spec 9.2, "authored, bounded by base count, a few
    /// hundred"). Bases sharing a tag list share its signature and its merge.
    /// </summary>
    private void BuildBases(DeterministicRng rng)
    {
        const int signatureCount = 300;
        var tags = new List<int>(signatureCount * 3);
        var signatureStart = new int[signatureCount];
        var signatureLength = new byte[signatureCount];
        Span<int> scratch = stackalloc int[MaximumBaseTags];
        for (int signature = 0; signature < signatureCount; signature++)
        {
            int count = rng.Next(2, MaximumBaseTags + 1);
            signatureStart[signature] = tags.Count;
            signatureLength[signature] = (byte)count;
            for (int tag = 0; tag < count; tag++)
            {
                int value = rng.Next(1, TagCount + 1);
                for (int prior = 0; prior < tag; prior++)
                    if (scratch[prior] == value) value = (value % TagCount) + 1;
                scratch[tag] = value;
                tags.Add(value);
            }
        }

        for (int baseIndex = 0; baseIndex < BaseCount; baseIndex++)
        {
            int signature = rng.Next(0, signatureCount);
            BaseTagStart[baseIndex] = signatureStart[signature];
            BaseTagCount[baseIndex] = signatureLength[signature];
            BaseTagSignature[baseIndex] = signature;
            BaseHasDurability[baseIndex] = rng.Next(0, 10) < 7;
            BaseSocketCount[baseIndex] = (byte)(rng.Next(0, 10) < 3 ? rng.Next(1, 4) : 0);
        }

        BaseTags = tags.ToArray();
        DistinctTagSignatures = signatureCount;
    }

    private void BuildRarities(DeterministicRng rng)
    {
        for (int rarity = 0; rarity < RarityCount; rarity++)
        {
            byte maximum = (byte)Math.Min(6, 1 + (rarity / 3));
            RarityMinAffixes[rarity] = (byte)Math.Max(1, maximum - 2);
            RarityMaxAffixes[rarity] = maximum;
            RarityMaxPrefixes[rarity] = (byte)Math.Max(1, (maximum + 1) / 2);
            RarityMaxSuffixes[rarity] = (byte)Math.Max(1, (maximum + 1) / 2);
            RarityNameWords[rarity] = (byte)(maximum >= 5 ? 3 : maximum >= 3 ? 2 : 0);
            for (int tag = 0; tag < TagCount; tag++)
                RarityWeight[(rarity * TagCount) + tag] = rng.Next(0, 4) == 0 ? 0 : rng.Next(1, 1_000);
        }
    }

    private void BuildNameWords(DeterministicRng rng)
    {
        for (int word = 0; word < NameWordCount; word++)
        {
            WordPosition[word] = (byte)(rng.Next(1, 4));
            for (int tag = 0; tag < TagCount; tag++)
                WordWeight[(word * TagCount) + tag] = rng.Next(0, 3) == 0 ? 0 : rng.Next(1, 500);
        }
    }
}
