using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Crafting.CurrencyWorld;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// The executor, and the ONE authored currency spec 20 phase 5's acceptance clause asks for: spec 10.4's
/// whetstone extended to four primitives, with a target guard, a step guard that skips, and a consumed
/// definition.
/// <para>
/// The executor is the one place a target guard set, the ordered steps, each step's guard set and a game
/// operation are composed, so the three properties spec 10.4 names about that encoding are asserted here
/// rather than at the plan: a step whose guard set is false is SKIPPED, a target guard failing refuses
/// everything and consumes nothing, and a refused craft leaves the durable bytes exactly where they were.
/// </para>
/// <para>
/// Every registry and snapshot a fact builds is its own, so nothing here writes process-global state.
/// </para>
/// </summary>
public sealed class CraftExecutorTests
{
    /// <summary>The currency whose one step reaches for a primitive that moves two slots.</summary>
    const int TwoSlotCurrency = 30;

    /// <summary>The currency whose one step names a game operation nothing registered.</summary>
    const int MissingCurrency = 31;

    /// <summary>The currency whose one step draws a mod, which is the executor's generator drawing.</summary>
    const int DrawCurrency = 32;

    [Fact]
    public void The_polishing_kit_runs_its_four_primitives_and_writes_ONE_canonical_payload()
    {
        CraftWorld world = PolishingWorld();
        CraftPlan plan = Plan(world, PolishingKit);
        byte[] stored = Target(quality: 5, durability: 30);

        CraftWorkingCopy copy = world.Open(stored);
        CraftOutcome outcome = Executor(world, Source()).Apply(plan, ref copy);

        Assert.False(outcome.IsRefused);
        Assert.Equal(4, outcome.StepsRun);
        Assert.Equal(0, outcome.StepsSkipped);
        Assert.True(copy.TryEncode(out byte[] crafted));

        // Repair in full, polish by its delta, one gem socket appended, the mirrored bit set.
        Assert.Equal((120ul, 120ul), Pair(crafted, InstancePropertyKind.Durability));
        Assert.Equal((ulong)(5 + PolishDelta), Scalar(crafted, InstancePropertyKind.Quality));
        Assert.Equal([GemSocket], SocketTypes(crafted));
        Assert.Equal(1ul << MirroredBit, Scalar(crafted, InstancePropertyKind.Flags));

        // Everything the currency did not name is carried through untouched, and the result is canonical
        // because the working copy re-encodes through the one encoder the format has.
        Assert.Equal(60ul, Scalar(crafted, InstancePropertyKind.ItemLevel));
        Assert.Equal([1], Affixes(crafted).ConvertAll(static affix => affix.ModId));
        Assert.True(ItemInstancePayload.IsCanonical(crafted));

        // The plan carries what one run spends, which is the currency row rather than a rule in code.
        Assert.Equal(PolishFlask, plan.ConsumesDefinitionId);
        Assert.Equal(1, plan.ConsumesCount);
    }

    [Fact]
    public void A_step_whose_guard_set_is_false_is_SKIPPED_rather_than_refusing_the_whole_craft()
    {
        // Spec 10.4's first property: the whetstone repairs an item already at quality 20 instead of
        // refusing to touch it, which is the difference between a step guard and a precondition.
        CraftWorld world = PolishingWorld();
        CraftWorkingCopy copy = world.Open(Target(quality: PolishCeiling + 1, durability: 30));
        CraftOutcome outcome = Executor(world, Source()).Apply(Plan(world, PolishingKit), ref copy);

        Assert.False(outcome.IsRefused);
        Assert.True(outcome.Skipped(1));
        Assert.False(outcome.Ran(1));
        Assert.Equal(3, outcome.StepsRun);
        Assert.Equal(1, outcome.StepsSkipped);

        // The three steps around it ran, and the polish left the quality exactly where it was.
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal((ulong)(PolishCeiling + 1), Scalar(crafted, InstancePropertyKind.Quality));
        Assert.Equal((120ul, 120ul), Pair(crafted, InstancePropertyKind.Durability));
        Assert.Equal([GemSocket], SocketTypes(crafted));
    }

    [Fact]
    public void A_TARGET_guard_failing_refuses_the_whole_craft_and_leaves_the_durable_bytes_UNTOUCHED()
    {
        // Spec 10.4's second property. The kit's target guard is IsIdentified(1), so an unidentified item
        // refuses before any step runs and spends nothing.
        CraftWorld world = PolishingWorld();
        byte[] stored = Target(identified: false, quality: 5, durability: 30);
        byte[] before = [.. stored];

        CraftWorkingCopy copy = world.Open(stored);
        CraftOutcome outcome = Executor(world, Source()).Apply(Plan(world, PolishingKit), ref copy);

        Assert.True(outcome.IsRefused);
        Assert.Equal(CraftGuardEvaluator.Failure(CraftGuardKind.IsIdentified), outcome.Refusal);
        Assert.Equal(0, outcome.StepsRun);
        Assert.Equal(0, outcome.StepsSkipped);

        // A refusal at any step discards the builder, so there is no partial craft and no rollback path
        // to get wrong.
        Assert.True(copy.IsRefused);
        Assert.False(copy.TryEncode(out byte[] crafted));
        Assert.Empty(crafted);
        Assert.Equal(before, stored);
    }

    [Fact]
    public void An_unregistered_game_operation_is_refused_AT_USE_with_a_counter()
    {
        // OWNER DECISION 7 and spec 10.5's deliberate softening of the fail-closed rule. A server that
        // shipped without an operation is a deploy mismatch, loud at the first use, and a boot failure
        // would take every player down for one unusable currency row.
        CraftWorld world = World(MissingRows);
        var executor = Executor(world, Source());
        Assert.Equal(0, executor.UnregisteredOperationRefusals);

        CraftWorkingCopy copy = world.Open(Target());
        CraftOutcome outcome = executor.Apply(Plan(world, MissingCurrency), ref copy);

        Assert.True(outcome.IsRefused);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.OperationUnregistered, FirstGameOperation + 5),
            outcome.Refusal);
        Assert.Equal(1, executor.UnregisteredOperationRefusals);
        Assert.False(copy.TryEncode(out _));

        // The counter is what a host reads for telemetry, so a second use moves it again.
        CraftWorkingCopy second = world.Open(Target());
        _ = executor.Apply(Plan(world, MissingCurrency), ref second);
        Assert.Equal(2, executor.UnregisteredOperationRefusals);
    }

    [Fact]
    public void The_executors_generator_draws_from_the_EXECUTORS_own_random_source()
    {
        // The executor holds the source by constructor for the same reason the generator does, and it
        // builds ONE generator over that same source, so a craft that draws leaves the caller's stream
        // where the craft left it rather than somewhere a second source went.
        CraftWorld world = World(DrawRows);
        var recording = new RecordingRandomSource(new SeededRandomSource(915));

        CraftWorkingCopy copy = world.Open(Target(rarityId: RareRarity));
        Assert.False(Executor(world, recording).Apply(Plan(world, DrawCurrency), ref copy).IsRefused);

        Assert.NotEmpty(recording.Calls);
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal(2, Affixes(crafted).Count);
    }

    [Fact]
    public void A_corrupted_target_is_refused_ahead_of_the_FIRST_writing_step()
    {
        // Standing rule 1 of spec 10.3, which no currency authors and none can opt out of. The executor
        // asks it before any step runs, so a corrupted item refuses at the ask rather than at the write
        // the first step would have ended in.
        CraftWorld world = PolishingWorld();
        CraftWorkingCopy copy = world.Open(Target(flags: 1u << CraftStandingRules.CorruptedFlagBit));
        CraftOutcome outcome = Executor(world, Source()).Apply(Plan(world, PolishingKit), ref copy);

        Assert.True(outcome.IsRefused);
        Assert.Equal(CraftGuardEvaluator.Failure(CraftGuardKind.IsCorruptible), outcome.Refusal);
        Assert.Equal(0, outcome.StepsRun);
        Assert.False(copy.TryEncode(out _));
    }

    [Fact]
    public void A_step_naming_a_TWO_SLOT_primitive_is_refused_because_a_plan_carries_no_second_slot()
    {
        // Socket and Unsocket are the only primitives that touch TWO slots, and the second slot is an item
        // the CALLER holds. A currency_step row carries four integers and no item, so a plan cannot name
        // one, and a step reaching for either is refused at use rather than silently doing nothing.
        CraftWorld world = World(TwoSlotRows);
        CraftWorkingCopy copy = world.Open(Target());
        CraftOutcome outcome = Executor(world, Source()).Apply(Plan(world, TwoSlotCurrency), ref copy);

        Assert.True(outcome.IsRefused);
        Assert.Equal(
            new CraftRefusal(CraftRefusalKind.StepNeedsSecondSlot, (int)CraftPrimitive.Socket),
            outcome.Refusal);
        Assert.False(copy.TryEncode(out _));
    }

    /// <summary>One currency whose only step names a game operation nothing registered.</summary>
    static IEnumerable<ContentRow> MissingRows(ContentTypeRegistry registry)
        =>
        [
            Currency(registry, MissingCurrency, "absent_bench", maxSteps: 1),
            Step(registry, 311, "absent_bench_1", MissingCurrency, sort: 1, operation: FirstGameOperation + 5),
        ];

    /// <summary>One currency whose only step reaches for the two slot primitive.</summary>
    static IEnumerable<ContentRow> TwoSlotRows(ContentTypeRegistry registry)
        =>
        [
            Currency(registry, TwoSlotCurrency, "socket_bench", maxSteps: 1),
            Step(
                registry,
                301,
                "socket_bench_1",
                TwoSlotCurrency,
                sort: 1,
                operation: (int)CraftPrimitive.Socket,
                a: 0,
                b: GemSocket),
        ];

    /// <summary>One currency that adds a random mod through the executor's own generator.</summary>
    static IEnumerable<ContentRow> DrawRows(ContentTypeRegistry registry)
        =>
        [
            Currency(registry, DrawCurrency, "draw_bench", maxSteps: 1),
            Step(
                registry,
                321,
                "draw_bench_1",
                DrawCurrency,
                sort: 1,
                operation: (int)CraftPrimitive.AddRandomMod,
                a: ModContentType.SuffixKind),
        ];

    /// <summary>A source that answers the bottom of every range, which the four primitive steps never touch.</summary>
    static ScriptedRandomSource Source() => new([]);

    /// <summary>One scalar field's value, or 0 for a field the crafted item does not carry.</summary>
    static ulong Scalar(ReadOnlyMemory<byte> payload, ushort kind)
    {
        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(Body(payload, kind).Span, ref offset, out ulong value, out _));
        return value;
    }

    /// <summary>One two varint field's values, which is kind 5's current and maximum.</summary>
    static (ulong First, ulong Second) Pair(ReadOnlyMemory<byte> payload, ushort kind)
    {
        ReadOnlySpan<byte> body = Body(payload, kind).Span;
        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(body, ref offset, out ulong first, out _));
        Assert.True(ContentVarint.TryReadUInt64(body, ref offset, out ulong second, out _));
        return (first, second);
    }
}
