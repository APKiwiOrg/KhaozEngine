using System;
using System.Collections.Generic;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;

namespace KhaozEngine.Tests.ItemInstances.Stats;

/// <summary>
/// Spec 17 row 7, the evaluator determinism row, plus spec 11.3's scope and condition rules. Seven facts
/// for the fold and six for the scope and the conditions, then five more this file adds where the plan's
/// prose names a property with no fact against it.
/// <para>
/// Every fact builds its OWN snapshot and its OWN evaluator, so nothing here writes process-global state
/// and no <c>DisableParallelization</c> collection is needed. Every expected number is a literal worked
/// out by hand from contracts 13.2, never recomputed by a second copy of the formula: an oracle that
/// shares the implementation's arithmetic pins nothing.
/// </para>
/// </summary>
public sealed class ContentStatEvaluatorTests
{
    /// <summary>The one stat nearly every fact folds, id 1.</summary>
    const int Hottest = 1;

    /// <summary>A second stat, for the facts about copying several values at once.</summary>
    const int Second = 2;

    /// <summary>The tag a stat row carries in the scope facts.</summary>
    const int FireTag = 7;

    /// <summary>The tag the evaluation CONTEXT carries in the scope facts.</summary>
    const int SpellTag = 9;

    /// <summary>A tag nothing in the scope facts asks for.</summary>
    const int ColdTag = 11;

    [Fact]
    public void The_More_fold_ORDER_changes_the_answer_and_the_stated_order_is_stable()
    {
        // Base 20, no Flat and no Increased, so the More loop starts at 20. Two More factors, +1 percent
        // and +147.5 percent. Applied in that order: more(20, 100) is 20 and more(20, 14750) is 50.
        // Applied the other way round: more(20, 14750) is 50 and more(50, 100) is 51. One unit apart,
        // which is exactly contracts 13.2's reason for fixing the order.
        var ascending = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        ascending.SetBase(Hottest, 20);
        ascending.AddSource(Key(2, 0, 0), new[] { More(14_750) }, Array.Empty<int>());
        ascending.AddSource(Key(1, 0, 0), new[] { More(100) }, Array.Empty<int>());
        Assert.Equal(50, ascending.Value(Hottest, Context()));

        var swapped = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        swapped.SetBase(Hottest, 20);
        swapped.AddSource(Key(1, 0, 0), new[] { More(14_750) }, Array.Empty<int>());
        swapped.AddSource(Key(2, 0, 0), new[] { More(100) }, Array.Empty<int>());
        Assert.Equal(51, swapped.Value(Hottest, Context()));

        // Stable means the KEY order decides, not the order the sources were handed over, and a second
        // read of the same evaluator answers the same number.
        Assert.Equal(50, ascending.Value(Hottest, Context()));
        ascending.Recompute(Key(1, 0, 0));
        Assert.Equal(50, ascending.Value(Hottest, Context()));
    }

    [Fact]
    public void Add_then_remove_restores_the_prior_value_EXACTLY()
    {
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, 20);
        evaluator.AddSource(Key(1, 0, 0), new[] { More(100) }, Array.Empty<int>());
        int before = evaluator.Value(Hottest, Context());

        evaluator.AddSource(Key(2, 0, 4), new[] { More(14_750), Flat(9) }, Array.Empty<int>());
        Assert.NotEqual(before, evaluator.Value(Hottest, Context()));

        Assert.True(evaluator.RemoveSource(Key(2, 0, 4)));
        Assert.Equal(before, evaluator.Value(Hottest, Context()));
        Assert.Equal(1, evaluator.LineCount);

        // A key the evaluator does not hold is answered false rather than thrown at, and changes nothing.
        Assert.False(evaluator.RemoveSource(Key(2, 0, 4)));
        Assert.Equal(before, evaluator.Value(Hottest, Context()));
    }

    [Fact]
    public void AddSource_under_an_existing_key_REPLACES_in_place_and_keeps_its_position()
    {
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, 20);
        evaluator.AddSource(Key(1, 0, 0), new[] { More(0) }, Array.Empty<int>());
        evaluator.AddSource(Key(2, 0, 0), new[] { More(9_000) }, Array.Empty<int>());
        evaluator.AddSource(Key(3, 0, 0), new[] { More(14_750) }, Array.Empty<int>());
        Assert.Equal(94, evaluator.Value(Hottest, Context()));

        // Kind 2 is re-added. In place it folds between kind 1 and kind 3, which gives 50. Appended after
        // kind 3 it would fold last, which gives 51, so the number itself says where the source sits.
        evaluator.AddSource(Key(2, 0, 0), new[] { More(100) }, Array.Empty<int>());
        Assert.Equal(50, evaluator.Value(Hottest, Context()));
        Assert.Equal(3, evaluator.LineCount);
    }

    [Fact]
    public void Floor_division_rounds_minus_14000_to_minus_1_where_Csharp_slash_gives_0()
    {
        // flat is -7 and increased is 2000, so flat times increased is -14000, a penalty of 1.4 scaled
        // units. floordiv(-14000 + 5000, 10000) is floordiv(-9000, 10000) which is -1, the nearest
        // integer with the tie going up. The trap is silent, so the C# answer is pinned beside it.
        Assert.Equal(0, -9_000 / 10_000);

        var evaluator = new ContentStatEvaluator(OneStat(-1_000, 1_000));
        evaluator.SetBase(Hottest, 0);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(-7), Increased(-8_000) }, Array.Empty<int>());
        Assert.Equal(-1, evaluator.Value(Hottest, Context()));
    }

    [Fact]
    public void Every_divide_in_the_fold_is_floor_division_for_EVERY_sign()
    {
        // The FIRST divide, over the Flat and Increased pool, at both signs. Each expected number is the
        // floor of the true quotient, which is round half up whichever side of zero it lands.
        Assert.Equal(-1, Folded(0, -3, -8_000));    // floordiv(-1000, 10000) is -1, where C# / gives 0
        Assert.Equal(1, Folded(0, 3, -8_000));      // floordiv(11000, 10000) is 1
        Assert.Equal(-5, Folded(0, -3, 8_000));     // floordiv(-49000, 10000) is -5, where C# / gives -4
        Assert.Equal(5, Folded(0, 3, 8_000));       // floordiv(59000, 10000) is 5
        Assert.Equal(-1, Folded(0, -1, 0));         // floordiv(-5000, 10000) is -1, the negative half rounds up
        Assert.Equal(1, Folded(0, 1, 0));           // floordiv(15000, 10000) is 1

        // The SECOND divide, inside the More loop, is the same division at a second call site rather than
        // a second formula, and it is pinned separately because a copy is exactly what would rot.
        Assert.Equal(-3, FoldedMore(-3, 0));        // floordiv(-25000, 10000) is -3, where C# / gives -2
        Assert.Equal(-1, FoldedMore(-3, -8_000));   // -3 folds to -3, then floordiv(-1000, 10000) is -1
        Assert.Equal(-5, FoldedMore(-3, 8_000));    // floordiv(-49000, 10000) is -5, where C# / gives -4
    }

    [Fact]
    public void Intermediate_arithmetic_is_long_and_the_result_is_CHECKED_into_int_before_the_clamp()
    {
        // flat is 1,000,000 and increased is 30,000, so the product is 30,000,000,000. That does not fit
        // in an int: the same multiplication in 32 bits wraps to a negative number, which is the answer
        // this fact exists to keep out of the fold.
        Assert.Equal(-64_771_072, unchecked(1_000_000 * 30_000));

        var evaluator = new ContentStatEvaluator(OneStat(-10_000_000, 10_000_000));
        evaluator.SetBase(Hottest, 1_000_000);
        evaluator.AddSource(Key(1, 0, 0), new[] { Increased(20_000) }, Array.Empty<int>());
        Assert.Equal(3_000_000, evaluator.Value(Hottest, Context()));

        // The boundary itself narrows exactly, which is what makes the checked cast provably safe.
        var boundary = new ContentStatEvaluator(OneStat(int.MinValue, int.MaxValue));
        boundary.SetBase(Hottest, int.MaxValue);
        Assert.Equal(int.MaxValue, boundary.Value(Hottest, Context()));
    }

    [Fact]
    public void A_pathological_modifier_set_saturates_at_the_clamp_rather_than_overflowing()
    {
        // 2,000,000,000 scaled units at +200 percent is 6,000,000,000, which is outside int on its own
        // and far outside the stat's own ceiling. It saturates at the ceiling and throws nothing.
        var high = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        high.SetBase(Hottest, 2_000_000_000);
        high.AddSource(Key(1, 0, 0), new[] { Increased(20_000) }, Array.Empty<int>());
        Assert.Equal(1_000_000, high.Value(Hottest, Context()));

        var low = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        low.SetBase(Hottest, -2_000_000_000);
        low.AddSource(Key(1, 0, 0), new[] { Increased(20_000) }, Array.Empty<int>());
        Assert.Equal(-1_000_000, low.Value(Hottest, Context()));

        // A stack of More factors that would run away is bounded by the same clamp.
        var stacked = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        stacked.SetBase(Hottest, 1_000);
        var lines = new StatModifierLine[16];
        for (int index = 0; index < lines.Length; index++)
        {
            lines[index] = More(1_000_000);
        }

        stacked.AddSource(Key(1, 0, 0), lines, Array.Empty<int>());
        Assert.Equal(1_000_000, stacked.Value(Hottest, Context()));
    }

    [Fact]
    public void A_scope_matches_the_UNION_of_the_context_tags_and_the_STAT_ROWS_own_tags()
    {
        // The stat row carries fire. The line's scope asks for fire AND spell, so it needs the context
        // to supply the half the stat row does not.
        var evaluator = new ContentStatEvaluator(TaggedStat(FireTag));
        evaluator.SetBase(Hottest, 100);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(50, 0, 2) }, new[] { FireTag, SpellTag });

        Assert.Equal(150, evaluator.Value(Hottest, Context(SpellTag)));

        evaluator.Recompute(Key(1, 0, 0));
        Assert.Equal(100, evaluator.Value(Hottest, Context()));

        evaluator.Recompute(Key(1, 0, 0));
        Assert.Equal(100, evaluator.Value(Hottest, Context(ColdTag)));

        evaluator.Recompute(Key(1, 0, 0));
        Assert.Equal(150, evaluator.Value(Hottest, Context(SpellTag, ColdTag)));
    }

    [Fact]
    public void Increased_fire_resistance_applies_with_NO_context_tag_because_the_stat_carries_fire()
    {
        // Spec 11.3's worked example. The scope is fire alone, and the stat row is where fire comes from,
        // so no call site has to know the taxonomy to evaluate fire resistance.
        var evaluator = new ContentStatEvaluator(TaggedStat(FireTag));
        evaluator.SetBase(Hottest, 100);
        evaluator.AddSource(Key(1, 0, 0), new[] { Increased(5_000, 0, 1) }, new[] { FireTag });
        Assert.Equal(150, evaluator.Value(Hottest, Context()));

        // The same line against a stat row carrying no fire needs the context to supply it.
        var untagged = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        untagged.SetBase(Hottest, 100);
        untagged.AddSource(Key(1, 0, 0), new[] { Increased(5_000, 0, 1) }, new[] { FireTag });
        Assert.Equal(100, untagged.Value(Hottest, Context()));

        untagged.Recompute(Key(1, 0, 0));
        Assert.Equal(150, untagged.Value(Hottest, Context(FireTag)));
    }

    [Fact]
    public void An_EMPTY_scope_applies_always_and_costs_one_length_check()
    {
        // Scope length 0, so the scope loop runs zero times whatever the context holds. That is the
        // common case and it is the reason the scope is a range rather than a set to intersect.
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, 100);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(50) }, Array.Empty<int>());

        Assert.Equal(150, evaluator.Value(Hottest, Context()));
        evaluator.Recompute(Key(1, 0, 0));
        Assert.Equal(150, evaluator.Value(Hottest, Context(ColdTag)));
        evaluator.Recompute(Key(1, 0, 0));
        Assert.Equal(150, evaluator.Value(Hottest, Context(FireTag, SpellTag, ColdTag)));
    }

    [Fact]
    public void A_condition_id_of_0_is_unconditional_and_the_engine_defines_NONE_in_v1()
    {
        Assert.Equal(0, IStatConditionRegistry.Unconditional);
        Assert.Equal(1_023, IStatConditionRegistry.EngineBandMaximum);

        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, 100);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(50, 0, 0, IStatConditionRegistry.Unconditional) }, Array.Empty<int>());
        Assert.Equal(150, evaluator.Value(Hottest, Context()));

        // Ids 1 to 1023 are the engine's own band and the engine defines nothing in it, so a line under
        // one of them can never be satisfied and never applies.
        var engineBand = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        engineBand.SetBase(Hottest, 100);
        engineBand.AddSource(
            Key(1, 0, 0),
            new[] { Flat(50, 0, 0, 1), Flat(50, 0, 0, IStatConditionRegistry.EngineBandMaximum) },
            Array.Empty<int>());
        Assert.Equal(100, engineBand.Value(Hottest, Context()));
    }

    [Fact]
    public void The_engine_never_calls_the_registry_for_an_id_in_its_own_0_to_1023_range()
    {
        var registry = new RecordingConditionRegistry();
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000), registry);
        evaluator.SetBase(Hottest, 100);
        evaluator.AddSource(
            Key(1, 0, 0),
            new[]
            {
                Flat(1, 0, 0, 0),
                Flat(2, 0, 0, 1),
                Flat(4, 0, 0, 512),
                Flat(8, 0, 0, 1_023),
                Flat(16, 0, 0, 1_024),
                Flat(32, 0, 0, 2_000),
            },
            Array.Empty<int>());

        // Only the unconditional line and the two game conditions the registry answered true for.
        Assert.Equal(149, evaluator.Value(Hottest, Context()));
        Assert.Equal(new[] { 1_024, 2_000 }, registry.Asked);
    }

    [Fact]
    public void The_condition_registry_arrives_by_CONSTRUCTOR_and_not_on_StatContext()
    {
        // Spec 11.2 put the registry on the context and spec 11.6 put it on the constructor. One
        // dependency with two homes is how two call sites end up with two registries, so contracts 14.4's
        // shape wins and the context carries tags and a mask and nothing else.
        PropertyInfo[] properties = typeof(StatContext).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var names = new List<string>();
        foreach (PropertyInfo property in properties)
        {
            names.Add(property.Name);
            Assert.NotEqual(typeof(IStatConditionRegistry), property.PropertyType);
        }

        Assert.Equal(2, names.Count);
        Assert.Contains(nameof(StatContext.Tags), names);
        Assert.Contains(nameof(StatContext.ConditionMask), names);

        ConstructorInfo constructor = Assert.Single(typeof(ContentStatEvaluator).GetConstructors());
        ParameterInfo[] parameters = constructor.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IContentSnapshot), parameters[0].ParameterType);
        Assert.Equal(typeof(IStatConditionRegistry), parameters[1].ParameterType);
        Assert.True(parameters[1].IsOptional);

        // And the registry it was handed is the one it consults.
        var registry = new RecordingConditionRegistry();
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000), registry);
        evaluator.SetBase(Hottest, 100);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(50, 0, 0, 4_096) }, Array.Empty<int>());
        Assert.Equal(150, evaluator.Value(Hottest, Context()));
        Assert.Equal(new[] { 4_096 }, registry.Asked);
    }

    [Fact]
    public void The_stat_schema_positions_the_evaluator_reads_are_still_where_it_reads_them()
    {
        // The evaluator reads scale, min, max and tags off a stat row by POSITION, and no engine type
        // declares an index constant the way the eighteen instance types do (#968). These literals are
        // the constants ContentStatEvaluator holds, so a schema reorder fails here rather than silently
        // pointing the clamp at a neighbouring int of the same kind.
        ContentFieldSchema schema = StatContentType.CreateSchema();
        Assert.Equal(StatContentType.NameField, schema.Fields[0].Name);
        Assert.Equal(StatContentType.ScaleField, schema.Fields[1].Name);
        Assert.Equal(StatContentType.MinField, schema.Fields[2].Name);
        Assert.Equal(StatContentType.MaxField, schema.Fields[3].Name);
        Assert.Equal(StatContentType.TagsField, schema.Fields[4].Name);
        Assert.Equal(StatContentType.DisplayFormatField, schema.Fields[5].Name);

        // And the values reach the evaluator: the scale is readable and the clamp is the row's own.
        var evaluator = new ContentStatEvaluator(OneStat(-40, 60, scale: 100));
        Assert.Equal(100, evaluator.Scale(Hottest));
        evaluator.SetBase(Hottest, 500);
        Assert.Equal(60, evaluator.Value(Hottest, Context()));
        evaluator.SetBase(Hottest, -500);
        Assert.Equal(-40, evaluator.Value(Hottest, Context()));
    }

    [Fact]
    public void A_warmed_read_and_a_CopyValuesTo_allocate_exactly_ZERO_bytes()
    {
        var evaluator = new ContentStatEvaluator(TwoStats());
        evaluator.SetBase(Hottest, 100);
        evaluator.SetBase(Second, 50);
        evaluator.AddSource(
            Key(1, 0, 0),
            new[] { Flat(20), Increased(1_500), More(2_500), new StatModifierLine(Second, StatCombineKind.Flat, 7, 0, 0, 0) },
            Array.Empty<int>());

        var statIds = new[] { Hottest, Second };
        var destination = new int[2];
        var context = Context(SpellTag);
        StatSourceKey key = Key(1, 0, 0);

        for (int warmup = 0; warmup < 20_000; warmup++)
        {
            evaluator.Recompute(key);
            _ = evaluator.Value(Hottest, context);
            _ = evaluator.Value(Hottest, context);
            evaluator.CopyValuesTo(statIds, destination, context);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 20_000; index++)
        {
            evaluator.Recompute(key);
            _ = evaluator.Value(Hottest, context);
            _ = evaluator.Value(Hottest, context);
            evaluator.CopyValuesTo(statIds, destination, context);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.Equal(173, destination[0]);
        Assert.Equal(57, destination[1]);
    }

    [Fact]
    public void Source_kinds_5_and_6_are_RESERVED_and_the_engine_assigns_neither()
    {
        // Spec 11.4's table: four kinds in v1, 5 and 6 held for passives and buffs, 7 upwards for a game.
        // Reserving them rather than assigning them is what lets a passive tree arrive without moving a
        // single displayed number.
        Assert.Equal(1, StatSourceKey.WornItemKind);
        Assert.Equal(2, StatSourceKey.AffixKind);
        Assert.Equal(3, StatSourceKey.EnchantmentKind);
        Assert.Equal(4, StatSourceKey.SocketedItemKind);
        Assert.Equal(5, StatSourceKey.ReservedPassiveKind);
        Assert.Equal(6, StatSourceKey.ReservedBuffKind);
        Assert.Equal(7, StatSourceKey.FirstGameKind);

        // A source under a reserved kind folds in kind order like any other, with no engine meaning
        // attached to it: kind 5 lands between kind 4 and kind 7.
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, 20);
        evaluator.AddSource(Key(StatSourceKey.FirstGameKind, 0, 0), new[] { More(14_750) }, Array.Empty<int>());
        evaluator.AddSource(Key(StatSourceKey.ReservedPassiveKind, 0, 0), new[] { More(100) }, Array.Empty<int>());
        Assert.Equal(50, evaluator.Value(Hottest, Context()));
    }

    [Fact]
    public void Two_sources_tied_on_kind_and_ordinal_order_by_INSTANCE_ID()
    {
        // The engine's own four kinds cannot tie on kind and ordinal. A game source can, which is why the
        // instance id is in the key at all.
        var ascending = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        ascending.SetBase(Hottest, 20);
        ascending.AddSource(Key(7, 3, 9), new[] { More(14_750) }, Array.Empty<int>());
        ascending.AddSource(Key(7, 3, 5), new[] { More(100) }, Array.Empty<int>());
        Assert.Equal(50, ascending.Value(Hottest, Context()));

        var swapped = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        swapped.SetBase(Hottest, 20);
        swapped.AddSource(Key(7, 3, 5), new[] { More(14_750) }, Array.Empty<int>());
        swapped.AddSource(Key(7, 3, 9), new[] { More(100) }, Array.Empty<int>());
        Assert.Equal(51, swapped.Value(Hottest, Context()));
    }

    [Fact]
    public void Recompute_marks_dirty_and_nothing_else()
    {
        var registry = new RecordingConditionRegistry();
        var evaluator = new ContentStatEvaluator(TwoStats(), registry);
        evaluator.SetBase(Hottest, 100);
        evaluator.SetBase(Second, 100);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(50, 0, 0, 4_096) }, Array.Empty<int>());
        Assert.Equal(150, evaluator.Value(Hottest, Context()));
        Assert.Equal(new[] { 4_096 }, registry.Asked);

        // A cached read folds nothing, so the registry is not consulted again. Spec 11.5: the registry is
        // read at recompute time only, which is what makes a condition over a moving value the game's job
        // to dirty rather than the evaluator's to poll.
        Assert.Equal(150, evaluator.Value(Hottest, Context()));
        Assert.Equal(new[] { 4_096 }, registry.Asked);

        // Marking dirty is all Recompute does. The next read folds again and answers the same number.
        evaluator.Recompute(Key(1, 0, 0));
        Assert.Equal(150, evaluator.Value(Hottest, Context()));
        Assert.Equal(new[] { 4_096, 4_096 }, registry.Asked);

        // A source the evaluator does not hold contributes no line to any stat, so there is nothing of
        // its to dirty and the call is a no-op rather than a refusal.
        evaluator.Recompute(Key(9, 9, 9));
        Assert.Equal(150, evaluator.Value(Hottest, Context()));
        Assert.Equal(new[] { 4_096, 4_096 }, registry.Asked);
    }

    /// <summary>The whole fold for one Flat and one Increased line, which is the first divide alone.</summary>
    static int Folded(int baseValue, int flat, int increasedBasisPoints)
    {
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, baseValue);
        evaluator.AddSource(Key(1, 0, 0), new[] { Flat(flat), Increased(increasedBasisPoints) }, Array.Empty<int>());
        return evaluator.Value(Hottest, Context());
    }

    /// <summary>A base through the first divide and then exactly one More factor, the second divide.</summary>
    static int FoldedMore(int baseValue, int moreBasisPoints)
    {
        var evaluator = new ContentStatEvaluator(OneStat(-1_000_000, 1_000_000));
        evaluator.SetBase(Hottest, baseValue);
        evaluator.AddSource(Key(1, 0, 0), new[] { More(moreBasisPoints) }, Array.Empty<int>());
        return evaluator.Value(Hottest, Context());
    }

    static StatSourceKey Key(byte kind, int ordinal, long instanceId) => new(kind, ordinal, instanceId);

    static StatContext Context(params int[] tags) => new(tags, 0);

    static StatModifierLine Flat(int value, int scopeStart = 0, int scopeLength = 0, int conditionId = 0)
        => new(Hottest, StatCombineKind.Flat, value, scopeStart, scopeLength, conditionId);

    static StatModifierLine Increased(int basisPoints, int scopeStart = 0, int scopeLength = 0, int conditionId = 0)
        => new(Hottest, StatCombineKind.Increased, basisPoints, scopeStart, scopeLength, conditionId);

    static StatModifierLine More(int basisPoints, int scopeStart = 0, int scopeLength = 0, int conditionId = 0)
        => new(Hottest, StatCombineKind.More, basisPoints, scopeStart, scopeLength, conditionId);

    static ContentTypeRegistry Registry()
    {
        var registry = new ContentTypeRegistry();
        EngineContentTypes.Register(registry);
        return registry;
    }

    static ContentRow StatRow(
        ContentTypeRegistry registry,
        int id,
        string key,
        int scale,
        int minimum,
        int maximum,
        int[] tags)
        => RowAt(
            Lookup(registry, EngineContentTypes.StatTypeKey),
            id,
            key,
            Marker(),
            Int(scale),
            Int(minimum),
            Int(maximum),
            tags.Length == 0 ? ContentFieldValue.Absent(ContentFieldKind.TagList) : ContentRowCodecBase.TagListValue(tags),
            Marker());

    static ContentSnapshot OneStat(int minimum, int maximum, int scale = 1)
    {
        ContentTypeRegistry registry = Registry();
        return Snapshot(registry, StatRow(registry, Hottest, "hottest", scale, minimum, maximum, Array.Empty<int>()));
    }

    static ContentSnapshot TaggedStat(params int[] tags)
    {
        ContentTypeRegistry registry = Registry();
        return Snapshot(registry, StatRow(registry, Hottest, "hottest", 1, -1_000_000, 1_000_000, tags));
    }

    static ContentSnapshot TwoStats()
    {
        ContentTypeRegistry registry = Registry();
        return Snapshot(
            registry,
            StatRow(registry, Hottest, "hottest", 1, -1_000_000, 1_000_000, Array.Empty<int>()),
            StatRow(registry, Second, "second", 1, -1_000_000, 1_000_000, Array.Empty<int>()));
    }

    /// <summary>
    /// A game registry that answers true for every id it is asked about and records the asking, which is
    /// what turns "the engine never calls the registry for its own band" into an observable.
    /// </summary>
    sealed class RecordingConditionRegistry : IStatConditionRegistry
    {
        public List<int> Asked { get; } = new();

        public bool Evaluate(int conditionId, in StatContext context)
        {
            Asked.Add(conditionId);
            return true;
        }
    }
}
