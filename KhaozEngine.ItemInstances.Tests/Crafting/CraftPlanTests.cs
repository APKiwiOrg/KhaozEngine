using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Tests.ItemInstances.Generation;
using Xunit;
using KhaozEngine.Tests.ItemInstances.Content;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Crafting.CurrencyWorld;
using static KhaozEngine.Tests.ItemInstances.Generation.GenerationWorld;

namespace KhaozEngine.Tests.ItemInstances.Crafting;

/// <summary>
/// Currency RESOLUTION, spec 10.4: the three content types turning into one immutable
/// <see cref="CraftPlan"/> at boot, and the four encoding properties that resolution has to read the way
/// the rows were authored.
/// <para>
/// Every registry and snapshot a fact builds is its own, so nothing here writes process-global state.
/// </para>
/// </summary>
public sealed class CraftPlanTests
{
    [Fact]
    public void A_currency_resolves_into_a_plan_ONCE_at_boot_and_never_inside_a_tick()
    {
        CraftWorld world = PolishingWorld();
        var counting = new CountingSnapshot(world.Snapshot);
        var index = new CraftPlanIndex();

        index.Build(counting);
        int readAtBoot = counting.RowsRead;
        Assert.True(readAtBoot > 0, "The build read no rows at all, so the fact reads nothing.");

        // Every later ask is a dictionary lookup over the plan the boot built. A lazy build inside a tick
        // is a latency spike, and a plan derived from content is immutable for the version.
        for (int tick = 0; tick < 4; tick++)
        {
            Assert.True(index.TryGetPlan(PolishingKit, out CraftPlan? plan));
            Assert.Equal(4, plan.Steps.Length);
        }

        Assert.Equal(readAtBoot, counting.RowsRead);

        // And a process holds exactly ONE resolution, because a new content version becomes active at a
        // restart rather than through a swap.
        Assert.Throws<InvalidOperationException>(() => index.Build(counting));
    }

    [Fact]
    public void A_plans_steps_are_in_SORT_order_and_a_duplicate_sort_never_reached_the_pack()
    {
        // The rows are authored with their ids ASCENDING and their sorts descending, so an index reading
        // them in id order would answer the reverse of the authored order.
        CraftPlan plan = Plan(
            World(registry =>
            [
                Currency(registry, 8, "shuffled", maxSteps: 3),
                Step(registry, 81, "shuffled_3", 8, sort: 3, operation: 13),
                Step(registry, 82, "shuffled_1", 8, sort: 1, operation: 11),
                Step(registry, 83, "shuffled_2", 8, sort: 2, operation: 12),
            ]),
            8);

        Assert.Equal([1, 2, 3], plan.Steps.ToArray().Select(static step => step.Sort));
        Assert.Equal([82, 83, 81], plan.Steps.ToArray().Select(static step => step.StepId));

        // A duplicate sort is KEC0110 at publish, so no pack carries one, and resolution fails the boot
        // closed rather than picking one of the two orders.
        CraftWorld duplicated = World(registry =>
        [
            Currency(registry, 9, "twinned", maxSteps: 2),
            Step(registry, 91, "twinned_a", 9, sort: 1, operation: 11),
            Step(registry, 92, "twinned_b", 9, sort: 1, operation: 12),
        ]);

        Assert.Contains("92", InstanceValidationFixtures.Only(InstanceValidationFixtures.Band(duplicated.Snapshot), "KEC0110").Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Index(duplicated));
    }

    [Fact]
    public void A_guard_with_no_currency_step_id_is_a_TARGET_guard()
    {
        CraftPlan plan = Plan(PolishingWorld(), PolishingKit);

        CraftGuard target = Assert.Single(plan.TargetGuards.ToArray());
        Assert.Equal(new CraftGuard(CraftGuardKind.IsIdentified, 1, 0), target);

        // One type told apart by one empty reference, so the guard naming a step is in that step's set and
        // in no other, and the target set holds only the one that named nothing.
        Assert.DoesNotContain(
            plan.Steps.ToArray().SelectMany(static step => step.Guards.ToArray()),
            guard => guard.Kind == CraftGuardKind.IsIdentified);
    }

    [Fact]
    public void A_guard_naming_a_step_is_evaluated_immediately_before_THAT_step()
    {
        CraftWorld world = PolishingWorld();
        CraftPlan plan = Plan(world, PolishingKit);

        // The authored guard names step 72, which is the polish step at sort 2, and it lands on that step
        // and on no other.
        for (int index = 0; index < plan.Steps.Length; index++)
        {
            CraftPlanStep step = plan.Steps.Span[index];
            Assert.Equal(step.StepId == 72 ? 1 : 0, step.Guards.Length);
        }

        Assert.Equal(
            new CraftGuard(CraftGuardKind.QualityBetween, 0, PolishCeiling),
            plan.Steps.Span[1].Guards.Span[0]);

        // And it is evaluated BEFORE that step rather than at the end: an item already at the ceiling
        // skips the polish and still takes the three steps around it.
        CraftWorkingCopy copy = world.Open(Target(quality: PolishCeiling + 1));
        CraftOutcome outcome = Executor(world, Source()).Apply(plan, ref copy);

        Assert.False(outcome.IsRefused);
        Assert.True(outcome.Skipped(1));
        Assert.Equal(3, outcome.StepsRun);
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal((ulong)(PolishCeiling + 1), Scalar(crafted, InstancePropertyKind.Quality));
    }

    [Fact]
    public void An_operation_below_1024_resolves_to_a_PRIMITIVE_and_1024_or_above_to_the_registry()
    {
        CraftPlan plan = Plan(
            World(registry =>
            [
                Currency(registry, 10, "mixed", maxSteps: 2),
                Step(registry, 101, "mixed_1", 10, sort: 1, operation: (int)CraftPrimitive.SetQuality),
                Step(registry, 102, "mixed_2", 10, sort: 2, operation: FirstGameOperation),
            ]),
            10);

        CraftPlanStep primitive = plan.Steps.Span[0];
        Assert.False(primitive.IsGameOperation);
        Assert.Equal(CraftPrimitive.SetQuality, primitive.Primitive);

        CraftPlanStep game = plan.Steps.Span[1];
        Assert.True(game.IsGameOperation);
        Assert.Equal(FirstGameOperation, game.Operation);

        // The two vocabularies share the step list ON PURPOSE, which is the whole reason the registry
        // exists, and the split is the number rather than a second field.
        Assert.Equal(FirstGameOperation, CurrencyStepContentType.FirstGameOperation);
        Assert.False(CurrencyStepContentType.IsGameOperation(FirstGameOperation - 1));
    }

    [Fact]
    public void A_selector_parameter_occupies_TWO_slots_its_kind_then_its_parameter()
    {
        // RemoveMod takes a selector, so its first TWO parameters are the selector's kind and then that
        // kind's own parameter. Authored as ByModId(2), it takes the entry naming mod 2 and leaves mod 1.
        CraftWorld world = World(registry =>
        [
            Currency(registry, 11, "extractor", maxSteps: 1),
            Step(
                registry,
                111,
                "extractor_1",
                11,
                sort: 1,
                operation: (int)CraftPrimitive.RemoveMod,
                a: (int)CraftSelectorKind.ByModId,
                b: 2),
        ]);

        CraftPlan plan = Plan(world, 11);
        Assert.Equal(
            [(int)CraftSelectorKind.ByModId, 2, 0, 0],
            plan.Steps.Span[0].Parameters.ToArray());

        var builder = new ItemInstancePayloadBuilder();
        _ = builder.AddScalar(InstancePropertyKind.ItemLevel, 60);
        _ = builder.AddAffixes(
            InstancePropertyKind.Affixes,
            [new InstanceAffix(1, 1, RollPosition.Bottom), new InstanceAffix(2, 1, RollPosition.Bottom)]);

        CraftWorkingCopy copy = world.Open(builder.ToArray());
        Assert.False(Executor(world, Source()).Apply(plan, ref copy).IsRefused);
        Assert.True(copy.TryEncode(out byte[] crafted));
        Assert.Equal([1], GenerationWorld.Affixes(crafted).Select(static affix => affix.ModId));
    }

    [Fact]
    public void A_currency_consuming_NO_definition_carries_no_currency_fields_at_all()
    {
        // A bench that costs nothing is an ordinary currency row with an empty consumes_definition_id, and
        // a COUNT beside no item names nothing at all, so resolution drops it rather than carrying a
        // number an executor would have to remember not to read.
        CraftPlan free = Plan(
            World(registry =>
            [
                Currency(registry, 12, "free_bench", consumes: null, consumesCount: 3, maxSteps: 1),
                Step(registry, 121, "free_bench_1", 12, sort: 1, operation: (int)CraftPrimitive.Identify),
            ]),
            12);

        Assert.True(free.IsFree);
        Assert.Equal(0, free.ConsumesDefinitionId);
        Assert.Equal(0, free.ConsumesCount);

        // The paid one carries both, which is what makes the empty pair a fact rather than a default.
        CraftPlan paid = Plan(PolishingWorld(), PolishingKit);
        Assert.False(paid.IsFree);
        Assert.Equal(PolishFlask, paid.ConsumesDefinitionId);
        Assert.Equal(1, paid.ConsumesCount);
    }

    [Fact]
    public void A_currency_naming_a_dead_operation_number_fails_the_boot_CLOSED()
    {
        // The row codec already refuses the gap between 14 and 1,024 on both sides, so resolution meeting
        // one is a row built in code rather than a row a pack carried. It throws for the same reason the
        // candidate tables do: a partial index answers plausible wrong numbers on a tick.
        CraftWorld world = World(registry =>
        [
            Currency(registry, 13, "gap", maxSteps: 1),
            Step(registry, 131, "gap_1", 13, sort: 1, operation: CurrencyStepContentType.MaxPrimitiveOperation + 1),
        ]);

        Assert.Throws<InvalidOperationException>(() => Index(world));
    }

    [Fact]
    public void A_guard_naming_a_step_of_ANOTHER_currency_fails_the_boot_CLOSED()
    {
        // KEC0100 is what stops this reaching a pack, and resolution is the second door rather than the
        // only one: a guard seated on nothing would silently never run.
        CraftWorld world = World(registry =>
        [
            Currency(registry, 14, "left", maxSteps: 1),
            Currency(registry, 15, "right", maxSteps: 1),
            Step(registry, 141, "left_1", 14, sort: 1, operation: (int)CraftPrimitive.Identify),
            Step(registry, 151, "right_1", 15, sort: 1, operation: (int)CraftPrimitive.Identify),
            Guard(registry, 152, "right_reaches_left", 15, stepId: 141),
        ]);

        Assert.Contains(InstanceValidationFixtures.Band(world.Snapshot), static finding => finding.Code == "KEC0100");
        Assert.Throws<InvalidOperationException>(() => Index(world));
    }

    /// <summary>One scalar field's value, read the way a payload fact reads one.</summary>
    static ulong Scalar(ReadOnlyMemory<byte> payload, ushort kind)
    {
        int offset = 0;
        Assert.True(ContentVarint.TryReadUInt64(Body(payload, kind).Span, ref offset, out ulong value, out _));
        return value;
    }

    /// <summary>A source that answers the bottom of every range, which no fact here draws from.</summary>
    static ScriptedRandomSource Source() => new([]);
}
