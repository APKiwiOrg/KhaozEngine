using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Generation;

/// <summary>
/// Spec 17 row 6, which is three claims and not one: weights are proportional over a large seeded run,
/// roll positions are uniform with both ends of a tier's range reachable, and the draw COUNT is a function
/// of the affix count alone, including an item whose pool empties mid roll.
/// <para>
/// <b>Every tolerance here is an INTEGER bound.</b> A chi squared style bound stated as COUNTS keeps
/// contracts 13.4 true inside the assertion itself, and a test that computes a float tolerance to assert an
/// integer system passes review and then teaches the next reader the wrong lesson. The bound is
/// <c>5 * isqrt(expected) + 5</c>, five standard deviations of a binomial rounded down through an integer
/// square root, which is wide enough that a fixed seed never makes it flap and narrow enough that a draw
/// ignoring its weights misses it by an order of magnitude.
/// </para>
/// <para>
/// <b>What row 6 does NOT test, so nobody later concludes the seeded run proved the production one.</b> A
/// distribution run here is driven by <see cref="SeededRandomSource"/>, and the source a hosted server runs
/// is <see cref="CryptographicRandomSource"/>. The two are DIFFERENT IMPLEMENTATIONS of contracts 14.1 and
/// nothing measured on one is evidence about the other. They differ in their entropy: the seeded source
/// draws from <c>DeterministicRng</c>, which is reproducible by design and therefore predictable to anyone
/// who learns the seed, while the cryptographic source draws every value from the OS. A crafting roll a
/// player can predict is the edge contracts 14.2 exists to close, and no statistical fact about a
/// reproducible stream can close it.
/// </para>
/// <para>
/// Contracts 14.2's own prose says the seeded source uses MODULO and the cryptographic one uses rejection
/// sampling. The shipped <see cref="SeededRandomSource"/> does not: its bounded draw is Lemire rejection
/// sampling over <c>NextULong</c> rather than the wrapped <c>DeterministicRng.Next(int)</c>, so BOTH
/// implementations are unbiased on a bounded draw and the contract's stated difference is narrower than the
/// code's. That is https://github.com/APKiwiOrg/KhaozEngine/issues/975, filed rather than fixed here,
/// because the code is the safer of the two readings and the prose is what is behind.
/// </para>
/// <para>
/// Every registry and every generator a fact builds is its OWN, so nothing here writes process-global state
/// and no <c>DisableParallelization</c> collection is needed.
/// </para>
/// </summary>
public class GeneratorDistributionTests
{
    /// <summary>How many magic items the weight fact rolls. Every expected count below is derived from it.</summary>
    const int WeightRuns = 70_000;

    /// <summary>
    /// How many rare items the position fact rolls. Four affixes each is about 280,000 positions, which is
    /// what makes a specific end of the 0 to 65,535 range a near certainty rather than a coin toss.
    /// </summary>
    const int PositionRuns = 70_000;

    /// <summary>The dagger's prefix pool: sharp at three tiers, heavy and strong at one each.</summary>
    const int DaggerPrefixEntries = 5;

    /// <summary>The dagger's suffix pool: keen and swift.</summary>
    const int DaggerSuffixEntries = 2;

    /// <summary>The dagger's whole prefix weight, which one prefix draw is taken over.</summary>
    const int DaggerPrefixWeight = 335;

    /// <summary>The dagger's whole suffix weight.</summary>
    const int DaggerSuffixWeight = 500;

    /// <summary>The rarity the extra fact authors, asking for more affixes than the dagger's pool can serve.</summary>
    const int GrandRarity = 3;

    /// <summary>How many affixes that rarity asks for.</summary>
    const int GrandAffixes = 7;

    [Fact]
    public void Each_candidates_share_of_a_large_seeded_run_is_its_weight_over_the_pool_total()
    {
        // The dagger carries the metal tag alone, so its pool is one tag position with no overlap to
        // suppress and every share below is readable off the authored weights. Magic asks for exactly one
        // affix, so one roll is one observation.
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(915));
        var counts = new Dictionary<int, int>();
        for (int run = 0; run < WeightRuns; run++)
        {
            GenerationResult result = generator.Generate(Magic(GenerationWorld.Dagger));
            InstanceAffix affix = Assert.Single(GenerationWorld.Affixes(result.Payload));
            counts[Entry(affix)] = counts.TryGetValue(Entry(affix), out int held) ? held + 1 : 1;
        }

        // The kind is drawn first, weighted by the LIVE COUNT of each kind, and the mod is drawn second,
        // weighted by its weight inside that kind. So a candidate's share is the product of the two, which
        // is what these seven expectations are.
        AssertShare(counts, modId: 1, tier: 1, DaggerPrefixEntries, 100, DaggerPrefixWeight);
        AssertShare(counts, modId: 1, tier: 2, DaggerPrefixEntries, 50, DaggerPrefixWeight);
        AssertShare(counts, modId: 1, tier: 3, DaggerPrefixEntries, 25, DaggerPrefixWeight);
        AssertShare(counts, modId: 3, tier: 1, DaggerPrefixEntries, 70, DaggerPrefixWeight);
        AssertShare(counts, modId: 5, tier: 1, DaggerPrefixEntries, 90, DaggerPrefixWeight);
        AssertShare(counts, modId: 2, tier: 1, DaggerSuffixEntries, 300, DaggerSuffixWeight);
        AssertShare(counts, modId: 4, tier: 1, DaggerSuffixEntries, 200, DaggerSuffixWeight);

        // Nothing outside the pool was ever drawn, which is the other half of "proportional": the unique's
        // line carries no weight row anywhere and mod 6 must therefore never appear.
        Assert.Equal(7, counts.Count);
        Assert.DoesNotContain(6, counts.Keys);

        // And the bound DISCRIMINATES. Sharp tier 3 is the thinnest weight in the pool, so a draw that
        // ignored weights and picked uniformly over the five prefix entries would put it near 10,000
        // against an expectation near 3,700, which is far outside its own tolerance.
        long thin = Expected(DaggerPrefixEntries, 25, DaggerPrefixWeight);
        long uniform = Expected(DaggerPrefixEntries, DaggerPrefixWeight / DaggerPrefixEntries, DaggerPrefixWeight);
        Assert.True(
            uniform - thin > Tolerance(thin),
            FormattableString.Invariant($"A uniform draw would land {uniform - thin} away, inside the {Tolerance(thin)} bound."));
    }

    [Fact]
    public void The_tolerance_is_stated_as_an_INTEGER_bound_rather_than_a_float()
    {
        // Contracts 13.4 applied to the ASSERTION rather than to the code under it. No member of this class
        // takes, returns or holds a float or a double anywhere, so no tolerance here can have been computed
        // in floating point.
        const BindingFlags Everything = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (MethodInfo method in typeof(GeneratorDistributionTests).GetMethods(Everything))
        {
            Assert.False(IsFloating(method.ReturnType), method.Name);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(IsFloating(parameter.ParameterType), method.Name + "." + parameter.Name);
            }
        }

        foreach (FieldInfo field in typeof(GeneratorDistributionTests).GetFields(Everything))
        {
            Assert.False(IsFloating(field.FieldType), field.Name);
        }

        // The integer square root the bound is built on is exact, which is what makes the bound a real
        // number of counts rather than a value that happens to look like one. For every sample n, the
        // answer squared is at or below n and the next integer squared is above it.
        foreach (long sample in new long[] { 0, 1, 2, 3, 4, 8, 9, 15, 16, 14_925, 65_535, 1_000_000 })
        {
            long root = IntegerSquareRoot(sample);
            Assert.True(root * root <= sample, sample.ToString(CultureInfo.InvariantCulture));
            Assert.True((root + 1) * (root + 1) > sample, sample.ToString(CultureInfo.InvariantCulture));
        }

        // And the bound itself is monotone and never zero, so a rare cell still gets a usable window.
        Assert.Equal(5, Tolerance(0));
        Assert.True(Tolerance(100) < Tolerance(10_000));
    }

    [Fact]
    public void Roll_positions_are_uniform_and_BOTH_ENDS_of_a_tier_range_are_reachable()
    {
        // Contracts 6.4's worked table, through the ONE place the formula lives. The whole table, including
        // the equal bounds row and the negative range row, is pinned by
        // ItemGeneratorTests.The_roll_position_formula_is_contracts_6_4s_worked_table_and_lives_in_ONE_place
        // and is not re-derived here: these three rows are the ends and the midpoint, which is what the
        // reachability half of row 6 is about.
        Assert.Equal(10, RollPosition.Resolve(RollPosition.Bottom, 10, 40));
        Assert.Equal(25, RollPosition.Resolve(32_768, 10, 40));
        Assert.Equal(40, RollPosition.Resolve(RollPosition.Top, 10, 40));

        // A generator handed the two ends WRITES the two ends, so nothing between the draw and kind 131
        // narrows the range. This is the deterministic half of the claim and it needs no run at all.
        var scripted = new ScriptedRandomSource([0], [RollPosition.Bottom, RollPosition.Top]);
        List<InstanceAffix> ends = GenerationWorld.Affixes(
            GenerationWorld.Generator(scripted).Generate(Rare(GenerationWorld.Greatsword)).Payload);
        Assert.Contains(ends, affix => affix.Position == RollPosition.Bottom);
        Assert.Contains(ends, affix => affix.Position == RollPosition.Top);

        // The statistical half. A seeded run's positions are spread over sixteen equal buckets of 4,096
        // each, and BOTH ends of the range are reached rather than approached.
        ItemGenerator generator = GenerationWorld.Generator(new SeededRandomSource(915));
        const int Buckets = 16;
        const int BucketWidth = (RollPosition.Top / Buckets) + 1;
        var histogram = new int[Buckets];
        int total = 0;
        bool bottom = false;
        bool top = false;
        for (int run = 0; run < PositionRuns; run++)
        {
            foreach (InstanceAffix affix in GenerationWorld.Affixes(
                generator.Generate(Rare(GenerationWorld.Greatsword)).Payload))
            {
                histogram[affix.Position / BucketWidth]++;
                total++;
                bottom |= affix.Position == RollPosition.Bottom;
                top |= affix.Position == RollPosition.Top;
            }
        }

        long perBucket = total / Buckets;
        for (int bucket = 0; bucket < Buckets; bucket++)
        {
            Assert.True(
                Math.Abs(histogram[bucket] - perBucket) <= Tolerance(perBucket),
                FormattableString.Invariant(
                    $"Bucket {bucket} held {histogram[bucket]} of {total} positions against {perBucket} plus or minus {Tolerance(perBucket)}."));
        }

        Assert.True(bottom, FormattableString.Invariant($"No position 0 in {total} rolled positions."));
        Assert.True(top, FormattableString.Invariant($"No position {RollPosition.Top} in {total} rolled positions."));
    }

    [Fact]
    public void The_draw_count_is_a_function_of_the_affix_count_including_a_pool_that_empties()
    {
        // The formula is one count draw, three draws a pick, and one draw a name position. It is a function
        // of the REQUESTED affix count and of nothing else, which is spec 9.3's reproducibility contract:
        // an item that runs dry mid roll must consume exactly what an item that does not consumes.
        var survives = new RecordingRandomSource(new SeededRandomSource(915));
        GenerationResult full = GenerationWorld.Generator(survives).Generate(Rare(GenerationWorld.Greatsword));
        Assert.Equal(DrawCount(4, names: 2), survives.Calls.Count);
        Assert.Equal(full.RequestedAffixCount, full.AffixCount);

        // The same base and the same level, under a rarity asking for seven. The dagger's pool holds four
        // distinct mods at most, because sharp's whole run leaves with one placement and heavy and strong
        // share a group capped at one, so three picks find nothing at all.
        var dries = new RecordingRandomSource(new SeededRandomSource(915));
        GenerationResult dry = Grand(dries).Generate(
            new GenerationContext(GenerationWorld.Dagger, 60, GrandRarity, 0, 0));
        Assert.Equal(DrawCount(GrandAffixes, names: 2), dries.Calls.Count);
        Assert.Equal(GrandAffixes, dry.RequestedAffixCount);
        Assert.Equal(4, dry.AffixCount);
        Assert.True(dry.AffixCount < dry.RequestedAffixCount);

        // And the three picks that found nothing still DREW. A dry pick is the kind draw on an empty open
        // total, then the two discards, and all three are Skip rather than NextInt(0, 1): a one-wide range
        // consumes NOTHING from the stream, so a discard written that way is no discard at all.
        int tail = 1 + (3 * 4);
        for (int pick = 4; pick < GrandAffixes; pick++)
        {
            int at = tail + ((pick - 4) * 3);
            Assert.Equal("skip", dries.Calls[at]);
            Assert.Equal("skip", dries.Calls[at + 1]);
            Assert.Equal("position", dries.Calls[at + 2]);
        }
    }

    /// <summary>The calls one roll makes: the count draw, three a pick, and one a name position.</summary>
    static int DrawCount(int requested, int names) => 1 + (3 * requested) + names;

    /// <summary>A generator over the authored world plus the rarity that asks for more than it can serve.</summary>
    static ItemGenerator Grand(IRandomSource random)
    {
        ContentTypeRegistry registry = GenerationWorld.World();
        List<ContentRow> rows = GenerationWorld.Rows(registry);
        rows.Add(RarityRule(
            registry,
            GrandRarity,
            "grand",
            minAffixes: GrandAffixes,
            maxAffixes: GrandAffixes,
            maxPrefixes: GrandAffixes,
            maxSuffixes: GrandAffixes,
            nameWordPositions: 2));
        ContentSnapshot candidate = Content.InstanceContentTypeFixtures.Snapshot(registry, [.. rows]);
        return new ItemGenerator(
            GenerationTables.Build(ModCandidateTables.Build(candidate), candidate),
            random,
            GenerationWorld.FreshAllocator());
    }

    /// <summary>One candidate's observed count against its weight's share of the pool.</summary>
    static void AssertShare(
        IReadOnlyDictionary<int, int> counts,
        int modId,
        int tier,
        int kindEntries,
        int weight,
        int kindWeight)
    {
        long expected = Expected(kindEntries, weight, kindWeight);
        long bound = Tolerance(expected);
        int observed = counts.TryGetValue(ModCandidateTables.Pack(modId, tier), out int held) ? held : 0;
        Assert.True(
            Math.Abs(observed - expected) <= bound,
            FormattableString.Invariant(
                $"Mod {modId} tier {tier} came up {observed} times in {WeightRuns} rolls against {expected} plus or minus {bound}."));
    }

    /// <summary>
    /// The count one candidate is expected to take, in integers throughout: the run count times its kind's
    /// live entry count times its own weight, over the whole open entry count times its kind's weight. The
    /// division floors, and the tolerance below covers the fraction it dropped.
    /// </summary>
    static long Expected(int kindEntries, int weight, int kindWeight)
        => (long)WeightRuns * kindEntries * weight
            / ((long)(DaggerPrefixEntries + DaggerSuffixEntries) * kindWeight);

    /// <summary>
    /// The integer bound an observed count may miss its expectation by: five standard deviations of a
    /// binomial, bounded above by the square root of the expectation, plus five so a rare cell still has a
    /// window. Every term is an integer (contracts 13.4).
    /// </summary>
    static long Tolerance(long expected) => (5 * IntegerSquareRoot(expected)) + 5;

    /// <summary>The largest integer whose square is at or below <paramref name="value"/>, by Newton.</summary>
    static long IntegerSquareRoot(long value)
    {
        if (value < 2)
        {
            return value < 0 ? 0 : value;
        }

        long guess = value;
        long next = (guess + 1) / 2;
        while (next < guess)
        {
            guess = next;
            next = (guess + (value / guess)) / 2;
        }

        return guess;
    }

    /// <summary>Whether a type is one of the two the determinism rule forbids on a value path.</summary>
    static bool IsFloating(Type type) => type == typeof(float) || type == typeof(double);

    /// <summary>One affix as its packed (mod id, tier ordinal) key, which is the table's own key.</summary>
    static int Entry(in InstanceAffix affix) => ModCandidateTables.Pack(affix.ModId, affix.Tier);

    /// <summary>A forced magic, which places exactly one affix and rolls no name.</summary>
    static GenerationContext Magic(int baseId) => new(baseId, 60, GenerationWorld.MagicRarity, 0, 0);

    /// <summary>A forced rare at item level 60, which asks for four affixes and a two word name.</summary>
    static GenerationContext Rare(int baseId) => new(baseId, 60, GenerationWorld.RareRarity, 0, 0);
}
